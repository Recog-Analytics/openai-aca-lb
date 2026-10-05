using openai_loadbalancer.Operations;

namespace openai_loadbalancer.Dashboard.Server;

/// <summary>One closed minute: request outcomes (classified like the web funnel) and the worst deployment state.</summary>
public sealed record SummaryBucket(DateTimeOffset At, long Total, long Served, long Retried, long Failed, long Refused,
    string? Worst, int Unhealthy);
/// <summary>A deployment's worst non-healthy state over <see cref="Seconds"/> seconds; healthy deployments have none.</summary>
public sealed record CompactState(string DeploymentId, string State, int Seconds, bool AccountOpen, bool HalfOpen, double? P95TtfbMs);
public sealed record HistoryRetention(DateTimeOffset SecondsFrom, DateTimeOffset MinutesFrom);
/// <summary>Data of [At - resolution, At).</summary>
public sealed record HistoryBucket(DateTimeOffset At, int Seconds, IReadOnlyList<DashboardRoute> Routes, IReadOnlyList<CompactState> States);
public sealed record HistoryResponse(DateTimeOffset From, DateTimeOffset To, int Resolution, IReadOnlyList<HistoryBucket> Buckets,
    IReadOnlyList<RequestRecord> Requests, IReadOnlyList<MergedDeployment> Deployments);

/// <summary>
/// Bounded in-memory history: per-second route counts and change-only states for an hour, per-minute aggregates for a day,
/// and request events for an hour (every notable one, at most <see cref="NormalPerSecond"/> others per second). Every structure has a hard cap and drops its oldest entries first.
/// Route shapes and their strings are interned, so a tick stores only (shape, count) pairs. Not thread-safe: the store locks.
/// </summary>
public sealed class DashboardHistory
{
    public static readonly TimeSpan SecondRetention = TimeSpan.FromHours(1), MinuteRetention = TimeSpan.FromHours(24);
    public const int MaxSeconds = 3600, MaxSecondRoutes = 200_000, MaxSecondStates = 100_000;
    public const int MaxMinutes = 1440, MaxMinuteRoutes = 400_000, MaxMinuteStates = 100_000;
    public const int MaxRequests = 20_000, MaxShapes = 10_000, MaxStrings = 20_000, MaxDeployments = 5_000, MaxText = 512;
    public const long MaxRequestBytes = 32L << 20;
    public const int NormalPerSecond = 4;
    private static readonly TimeSpan Recent = TimeSpan.FromMinutes(2);
    public static readonly int[] Resolutions = [1, 10, 30, 60, 300, 600, 1800, 3600];

    private readonly Queue<Second> seconds = new();
    private readonly Queue<Minute> minutes = new();
    private readonly Queue<Stored> recent = new(), notable = new(), normal = new();
    private readonly List<Stored> reservoir = [];
    private long reservoirSecond, reservoirSeen, recentBytes;
    private readonly Dictionary<string, CompactState> baseline = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, CompactState> current = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Seen> seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DashboardRoute> shapes = new(StringComparer.Ordinal);
    private readonly Dictionary<(int Status, int Hops), DashboardRoute> overflow = new();
    private readonly Dictionary<string, string> strings = new(StringComparer.Ordinal);
    private OpenMinute? open;
    private int secondRoutes, secondStates, minuteRoutes, minuteStates;
    private long requestBytes;

    public int SecondCount => seconds.Count;
    public int SecondRouteCount => secondRoutes;
    public int SecondStateCount => secondStates;
    public int MinuteCount => minutes.Count;
    public int MinuteRouteCount => minuteRoutes;
    public int MinuteStateCount => minuteStates;
    public int RequestCount => notable.Count + normal.Count;
    public int NotableRequestCount => notable.Count;
    public int ShapeCount => shapes.Count;
    public int DeploymentCount => seen.Count;

    public void Add(DateTimeOffset now, RequestRecord request)
    {
        var stored = new Stored(now, request, Bytes(request));
        recent.Enqueue(stored);
        recentBytes += stored.Bytes;
        PruneRequests(now);
        if (Notable(request))
            Retain(notable, stored);
        else
        {
            // Retention keeps a uniform sample of NormalPerSecond unremarkable requests per second of receipt time.
            reservoirSeen++;
            if (reservoir.Count < NormalPerSecond)
                reservoir.Add(stored);
            else if (Random.Shared.NextInt64(reservoirSeen) is var index && index < NormalPerSecond)
                reservoir[(int)index] = stored;
        }
        PruneRequests(now);
    }

    /// <summary>Stores one tick; returns the summary of the minute this tick closed, if any.</summary>
    public SummaryBucket? Tick(DateTimeOffset now, IReadOnlyList<DashboardRoute> routes, IReadOnlyList<MergedDeployment> deployments)
    {
        Minute? closed = null;
        if (open != null && Ceiling(now, 60) > open.At)
            closed = Close();
        open ??= new(Ceiling(now, 60));

        var counts = new Dictionary<DashboardRoute, long>(ReferenceEqualityComparer.Instance);
        foreach (var route in routes)
        {
            var shape = Shape(route);
            counts[shape] = counts.GetValueOrDefault(shape) + route.Count;
            open.Count(route);
        }
        var states = new Dictionary<string, CompactState>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in deployments)
        {
            var deployment = item.Deployment;
            seen[deployment.Id] = new(seen.TryGetValue(deployment.Id, out var known) ? known.First : now, now, item);
            if (DashboardStore.Severity(deployment.State) > 0)
                states[deployment.Id] = current.TryGetValue(deployment.Id, out var old) && old.State == deployment.State &&
                    old.AccountOpen == deployment.AccountOpen && old.HalfOpen == deployment.HalfOpen && old.P95TtfbMs == deployment.P95TtfbMs
                    ? old : new(Text(deployment.Id)!, Text(deployment.State)!, 1, deployment.AccountOpen, deployment.HalfOpen, deployment.P95TtfbMs);
        }
        var changes = states.Where(pair => !current.TryGetValue(pair.Key, out var old) || !ReferenceEquals(old, pair.Value))
            .Select(pair => new Change(pair.Value.DeploymentId, pair.Value))
            .Concat(current.Where(pair => !states.ContainsKey(pair.Key)).Select(pair => new Change(pair.Key, null))).ToArray();
        current = states;

        var second = new Second(now, counts.Select(pair => new RouteCount(pair.Key, pair.Value)).ToArray(), changes);
        seconds.Enqueue(second);
        secondRoutes += second.Routes.Length;
        secondStates += second.Changes.Length;
        open.Seconds++;
        foreach (var entry in second.Routes)
            open.Routes[entry.Route] = open.Routes.GetValueOrDefault(entry.Route) + entry.Count;
        foreach (var state in states.Values)
            open.States[state.DeploymentId] = open.States.TryGetValue(state.DeploymentId, out var known) ? Merge(known, state) : state;
        Prune(now);
        return closed?.Summary;
    }

    public void Prune(DateTimeOffset now)
    {
        while (seconds.TryPeek(out var second) && (now - second.At >= SecondRetention || seconds.Count > MaxSeconds ||
            secondRoutes > MaxSecondRoutes || secondStates > MaxSecondStates))
        {
            seconds.Dequeue();
            secondRoutes -= second.Routes.Length;
            secondStates -= second.Changes.Length;
            Apply(baseline, second.Changes);
        }
        while (minutes.TryPeek(out var minute) && (now - minute.At >= MinuteRetention || minutes.Count > MaxMinutes ||
            minuteRoutes > MaxMinuteRoutes || minuteStates > MaxMinuteStates))
        {
            minutes.Dequeue();
            minuteRoutes -= minute.Routes.Length;
            minuteStates -= minute.States.Length;
        }
        PruneRequests(now);
        foreach (var id in seen.Where(pair => now - pair.Value.Last >= MinuteRetention).Select(pair => pair.Key).ToArray())
            seen.Remove(id);
        if (seen.Count > MaxDeployments)
            foreach (var id in seen.OrderBy(pair => pair.Value.Last).Take(seen.Count - MaxDeployments).Select(pair => pair.Key).ToArray())
                seen.Remove(id);
    }

    private void Retain(Queue<Stored> queue, Stored stored)
    {
        queue.Enqueue(stored);
        requestBytes += stored.Bytes;
    }

    /// <summary>Flushes the sample of a past second, then drops by age, and over the caps old unremarkable requests before notable ones.</summary>
    private void PruneRequests(DateTimeOffset now)
    {
        var second = now.ToUnixTimeSeconds();
        if (second != reservoirSecond)
        {
            foreach (var item in reservoir.OrderBy(item => item.ReceivedAt))
                Retain(normal, item);
            reservoir.Clear();
            reservoirSeen = 0;
            reservoirSecond = second;
        }
        while (recent.TryPeek(out var item) && (now - item.ReceivedAt >= Recent || recent.Count > MaxRequests || recentBytes > MaxRequestBytes))
        {
            recent.Dequeue();
            recentBytes -= item.Bytes;
        }
        foreach (var queue in new[] { normal, notable })
            while (queue.TryPeek(out var item) && now - item.ReceivedAt >= SecondRetention)
                Drop(queue);
        while (notable.Count + normal.Count > MaxRequests || requestBytes > MaxRequestBytes)
            Drop(normal.Count > 0 ? normal : notable);

        void Drop(Queue<Stored> queue) => requestBytes -= queue.Dequeue().Bytes;
    }

    /// <summary>Retained requests (notable and sampled), oldest first.</summary>
    public IReadOnlyList<RequestRecord> Retained() => notable.Concat(normal).Concat(reservoir).OrderBy(item => item.ReceivedAt)
        .Select(item => item.Request).ToArray();

    public IReadOnlyList<RequestRecord> Requests(DateTimeOffset now, TimeSpan window) =>
        recent.SkipWhile(item => now - item.ReceivedAt >= window).Select(item => item.Request).ToArray();

    public IReadOnlyList<RouteTick> Ticks(DateTimeOffset now, TimeSpan window) =>
        seconds.SkipWhile(second => now - second.At >= window).Select(second => new RouteTick(second.At, Expand(second.Routes))).ToArray();

    public IReadOnlyList<SummaryBucket> Summary() => minutes.Select(minute => minute.Summary).ToArray();

    public HistoryRetention Retention(DateTimeOffset now)
    {
        var secondsFrom = seconds.TryPeek(out var second) ? second.At.AddSeconds(-1) : now;
        return new(secondsFrom, minutes.TryPeek(out var minute) ? minute.At.AddMinutes(-1) : secondsFrom);
    }

    /// <summary>
    /// Buckets of [from, to) at <paramref name="resolution"/> seconds, clamped to retention. Resolutions of a minute or more
    /// read closed minutes when the range starts before the per-second data, and per-second data after the last closed minute.
    /// </summary>
    public HistoryResponse Query(DateTimeOffset now, DateTimeOffset from, DateTimeOffset to, int resolution)
    {
        Prune(now);
        var retention = Retention(now);
        var useMinutes = resolution >= 60 && from < retention.SecondsFrom;
        var earliest = useMinutes ? retention.MinutesFrom : retention.SecondsFrom;
        from = from > earliest ? from : earliest;
        to = to < now ? to : now;
        var buckets = new SortedDictionary<DateTimeOffset, BucketBuilder>();
        BucketBuilder Bucket(DateTimeOffset at)
        {
            var end = Ceiling(at, resolution);
            if (!buckets.TryGetValue(end, out var bucket))
                buckets[end] = bucket = new();
            return bucket;
        }
        var minuteEnd = DateTimeOffset.MinValue;
        if (useMinutes)
            foreach (var minute in minutes)
            {
                minuteEnd = minute.At;
                if (minute.At <= from || minute.At > to)
                    continue;
                Bucket(minute.At).Add(minute.Seconds, minute.Routes, minute.States);
            }
        var states = new Dictionary<string, CompactState>(baseline, StringComparer.OrdinalIgnoreCase);
        foreach (var second in seconds)
        {
            Apply(states, second.Changes);
            if (second.At > from && second.At <= to && second.At > minuteEnd)
                Bucket(second.At).Add(1, second.Routes, states.Values);
        }
        bool InRange(Stored item) => item.ReceivedAt >= from && item.ReceivedAt < to;
        var important = Spread(notable.Where(InRange).ToArray(), 1000);
        var chosen = important.Concat(Spread(normal.Concat(reservoir).Where(InRange).ToArray(), 1000 - important.Count))
            .OrderBy(item => item.ReceivedAt).Select(item => item.Request).ToArray();
        var deployments = seen.Values.Where(item => item.First.AddSeconds(-1) < to && item.Last > from)
            .OrderBy(item => item.Deployment.Deployment.Id, StringComparer.OrdinalIgnoreCase).Select(item => item.Deployment).ToArray();
        return new(from, to, resolution, buckets.Where(pair => pair.Value.Seconds > 0)
            .Select(pair => pair.Value.Build(pair.Key)).ToArray(), chosen, deployments);
    }

    private Minute Close()
    {
        var minute = open!;
        open = null;
        var states = minute.States.Values.ToArray();
        var closed = new Minute(minute.At, minute.Seconds, minute.Routes.Select(pair => new RouteCount(pair.Key, pair.Value)).ToArray(),
            states, new(minute.At, minute.Total, minute.Served, minute.Retried, minute.Failed, minute.Refused,
                states.MaxBy(state => DashboardStore.Severity(state.State))?.State, states.Length));
        minutes.Enqueue(closed);
        minuteRoutes += closed.Routes.Length;
        minuteStates += closed.States.Length;
        if (shapes.Count >= MaxShapes)
            CompactShapes();
        return closed;
    }

    /// <summary>Forgets shapes no retained tick or minute references, so the table holds only live shapes.</summary>
    private void CompactShapes()
    {
        var live = new HashSet<DashboardRoute>(seconds.SelectMany(second => second.Routes).Concat(minutes.SelectMany(minute => minute.Routes))
            .Select(entry => entry.Route), ReferenceEqualityComparer.Instance);
        foreach (var key in shapes.Where(pair => !live.Contains(pair.Value)).Select(pair => pair.Key).ToArray())
            shapes.Remove(key);
    }

    /// <summary>
    /// The interned, count-free shape of a route. Ingest rejects identifiers longer than <see cref="MaxText"/> characters (an ARM
    /// deployment id is about 170), so the cap here only guards memory and never shortens a real id. When the table is full,
    /// a new shape counts under a shared "(other)" shape that keeps the status and attempt count.
    /// </summary>
    private DashboardRoute Shape(DashboardRoute route)
    {
        if (new[] { route.Caller, route.ModelKey, route.Zone, route.PoolKind, route.Pool }.Concat(route.Hops.SelectMany(hop => new[] { hop.DeploymentId, hop.Outcome }))
            .Any(value => value?.Length > MaxText))
            route = new(Cut(route.Caller), Cut(route.ModelKey), Cut(route.Zone), route.Status,
                route.Hops.Select(hop => new DashboardHop(Cut(hop.DeploymentId)!, Cut(hop.Outcome)!)).ToArray(), route.Count, Cut(route.PoolKind), Cut(route.Pool));
        var key = DashboardRoute.KeyOf(route);
        if (shapes.TryGetValue(key, out var shape))
            return shape;
        if (shapes.Count >= MaxShapes)
        {
            var other = (route.Status, route.Hops.Count);
            if (!overflow.TryGetValue(other, out shape))
                overflow[other] = shape = new("(other)", null, null, route.Status, route.Hops.Select(_ => new DashboardHop("(other)", "(other)")).ToArray(), 0);
            return shape;
        }
        return shapes[key] = new(Text(route.Caller), Text(route.ModelKey), Text(route.Zone), route.Status,
            route.Hops.Select(hop => new DashboardHop(Text(hop.DeploymentId)!, Text(hop.Outcome)!)).ToArray(), 0, Text(route.PoolKind), Text(route.Pool));
    }

    private static string? Cut(string? value) => value?.Length > MaxText ? value[..MaxText] : value;

    private string? Text(string? value)
    {
        value = Cut(value);
        if (value == null)
            return null;
        if (strings.TryGetValue(value, out var interned))
            return interned;
        if (strings.Count >= MaxStrings)
            strings.Clear();
        return strings[value] = value;
    }

    private static void Apply(Dictionary<string, CompactState> states, Change[] changes)
    {
        foreach (var change in changes)
            if (change.State == null)
                states.Remove(change.DeploymentId);
            else
                states[change.DeploymentId] = change.State;
    }

    private static CompactState Merge(CompactState known, CompactState next) => known with
    {
        State = DashboardStore.Severity(next.State) > DashboardStore.Severity(known.State) ? next.State : known.State,
        Seconds = known.Seconds + next.Seconds, AccountOpen = known.AccountOpen || next.AccountOpen, HalfOpen = known.HalfOpen || next.HalfOpen,
        P95TtfbMs = known.P95TtfbMs == null ? next.P95TtfbMs : next.P95TtfbMs == null ? known.P95TtfbMs : Math.Max(known.P95TtfbMs.Value, next.P95TtfbMs.Value)
    };

    private static DashboardRoute[] Expand(IEnumerable<RouteCount> routes) => routes.Select(entry => entry.Route with { Count = entry.Count }).ToArray();

    private static bool Notable(RequestRecord request) => request.Status >= 400 || request.Attempts.Count > 1;

    private static IReadOnlyList<T> Spread<T>(T[] items, int count) => items.Length <= count ? items :
        Enumerable.Range(0, count).Select(index => items[(int)((long)index * items.Length / count)]).ToArray();

    /// <summary>The end of the <paramref name="seconds"/>-aligned bucket that holds a tick ending at <paramref name="at"/>.</summary>
    private static DateTimeOffset Ceiling(DateTimeOffset at, int seconds)
    {
        var size = seconds * 1000L;
        return DateTimeOffset.FromUnixTimeMilliseconds((at.ToUnixTimeMilliseconds() + size - 1) / size * size);
    }

    /// <summary>Approximate heap bytes of a request event, for the <see cref="MaxRequestBytes"/> budget.</summary>
    private static long Bytes(RequestRecord request)
    {
        static long Of(params string?[] values) => values.Sum(value => value == null ? 0 : 26 + 2L * value.Length);
        return 160 + Of(request.Id, request.Caller, request.RequestedModel, request.ModelKey, request.Zone, request.Outcome,
            request.PoolKind, request.Pool, request.Operation, request.ApiVersion) + request.Attempts.Sum(attempt => 120 +
            Of(attempt.Deployment, attempt.Account, attempt.Region, attempt.HealthOutcome, attempt.RetryReason, attempt.DeploymentId,
                attempt.ErrorCode, attempt.ErrorMessage, attempt.BackendRequestId));
    }

    private readonly record struct RouteCount(DashboardRoute Route, long Count);
    private readonly record struct Change(string DeploymentId, CompactState? State);
    private readonly record struct Stored(DateTimeOffset ReceivedAt, RequestRecord Request, long Bytes);
    private sealed record Second(DateTimeOffset At, RouteCount[] Routes, Change[] Changes);
    private sealed record Minute(DateTimeOffset At, int Seconds, RouteCount[] Routes, CompactState[] States, SummaryBucket Summary);
    private sealed record Seen(DateTimeOffset First, DateTimeOffset Last, MergedDeployment Deployment);

    private sealed class OpenMinute(DateTimeOffset at)
    {
        public DateTimeOffset At { get; } = at;
        public int Seconds { get; set; }
        public long Total { get; private set; }
        public long Served { get; private set; }
        public long Retried { get; private set; }
        public long Failed { get; private set; }
        public long Refused { get; private set; }
        public Dictionary<DashboardRoute, long> Routes { get; } = new(ReferenceEqualityComparer.Instance);
        public Dictionary<string, CompactState> States { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Classifies like the web funnel: refused (no attempt), served, client error (total only), or failed.</summary>
        public void Count(DashboardRoute route)
        {
            Total += route.Count;
            if (route.Hops.Count == 0)
                Refused += route.Count;
            else if (route.Status < 400)
            {
                Served += route.Count;
                if (route.Hops.Count > 1)
                    Retried += route.Count;
            }
            else if (route.Status >= 500 || route.Status == 429)
                Failed += route.Count;
        }
    }

    private sealed class BucketBuilder
    {
        private readonly Dictionary<DashboardRoute, long> routes = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, CompactState> states = new(StringComparer.OrdinalIgnoreCase);
        public int Seconds { get; private set; }

        public void Add(int seconds, IEnumerable<RouteCount> entries, IEnumerable<CompactState> problems)
        {
            Seconds += seconds;
            foreach (var entry in entries)
                routes[entry.Route] = routes.GetValueOrDefault(entry.Route) + entry.Count;
            foreach (var state in problems)
                states[state.DeploymentId] = states.TryGetValue(state.DeploymentId, out var known) ? Merge(known, state) : state;
        }

        public HistoryBucket Build(DateTimeOffset at) => new(at, Seconds,
            routes.GroupBy(pair => DashboardRoute.KeyOf(pair.Key), StringComparer.Ordinal)
                .Select(group => group.First().Key with { Count = group.Sum(pair => pair.Value) }).ToArray(),
            states.Values.OrderBy(state => state.DeploymentId, StringComparer.OrdinalIgnoreCase).ToArray());
    }
}
