using System.Net.Http.Json;

namespace openai_loadbalancer.Devkit;

public sealed record TrafficSettings
{
    public double RequestsPerSecond { get; init; } = 2;
    public int Concurrency { get; init; } = 8;
    public string Model { get; init; } = "gpt-4o";
    public double StreamingShare { get; init; } = 0.25;
    public string Zone { get; init; } = "global";
    public bool Enabled { get; init; }

    public void Validate()
    {
        if (!double.IsFinite(RequestsPerSecond) || RequestsPerSecond is < 0.1 or > 100 ||
            Concurrency is < 1 or > 100 || !double.IsFinite(StreamingShare) || StreamingShare is < 0 or > 1 ||
            string.IsNullOrWhiteSpace(Model) || Model.Length > 512 || Zone is not ("eu" or "us" or "global"))
            throw new ArgumentException("Invalid traffic rate, concurrency, model, streaming share, or zone.");
    }
}

public sealed class TrafficGenerator(LbClient lb) : BackgroundService
{
    private TrafficSettings settings = new();
    private long sent;
    private long completed;
    private long failed;
    private int inFlight;

    public object Snapshot() => new
    {
        Settings.RequestsPerSecond, Settings.Concurrency, Settings.Model, Settings.StreamingShare, Settings.Zone, Settings.Enabled,
        sent = Interlocked.Read(ref sent), completed = Interlocked.Read(ref completed), failed = Interlocked.Read(ref failed),
        inFlight = Volatile.Read(ref inFlight)
    };

    public TrafficSettings Settings => Volatile.Read(ref settings);
    public void Update(TrafficSettings value) { value.Validate(); Volatile.Write(ref settings, value); }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pending = new List<Task>();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                pending.RemoveAll(task => task.IsCompleted);
                var current = Settings;
                if (current.Enabled && pending.Count < current.Concurrency)
                    pending.Add(SendAsync(current, stoppingToken));
                await Task.Delay(current.Enabled ? TimeSpan.FromSeconds(1 / current.RequestsPerSecond) : TimeSpan.FromMilliseconds(100), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { await Task.WhenAll(pending); }
    }

    private async Task SendAsync(TrafficSettings current, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref sent);
        Interlocked.Increment(ref inFlight);
        try
        {
            var embeddings = current.Model.StartsWith("text-embedding-", StringComparison.Ordinal);
            using var request = lb.Request(HttpMethod.Post, embeddings ? "/openai/v1/embeddings" : "/openai/v1/chat/completions");
            request.Headers.Add("x-lb-data-zone", current.Zone);
            request.Content = embeddings
                ? JsonContent.Create(new { model = current.Model, input = "Say hello." })
                : JsonContent.Create(new { model = current.Model, stream = Random.Shared.NextDouble() < current.StreamingShare,
                    messages = new[] { new { role = "user", content = "Say hello." } } });
            using var response = await lb.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            await response.Content.CopyToAsync(Stream.Null, cancellationToken);
            if (response.IsSuccessStatusCode) Interlocked.Increment(ref completed);
            else Interlocked.Increment(ref failed);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or IOException)
        {
            Interlocked.Increment(ref failed);
        }
        finally { Interlocked.Decrement(ref inFlight); }
    }
}
