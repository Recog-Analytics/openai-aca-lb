namespace openai_loadbalancer.Devkit;

public sealed class LbClient : IDisposable
{
    private readonly string key;
    public HttpClient Http { get; }

    public LbClient(string url, string key)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsLoopback || uri.Scheme != "http")
            throw new ArgumentException("Devkit LB URL must be a loopback HTTP URL.");
        this.key = key;
        Http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            BaseAddress = uri, Timeout = TimeSpan.FromSeconds(125)
        };
    }

    public HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("api-key", key);
        return request;
    }

    public void Dispose() => Http.Dispose();
}
