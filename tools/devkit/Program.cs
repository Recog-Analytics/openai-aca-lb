namespace openai_loadbalancer.Devkit;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var configuration = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json").AddEnvironmentVariables().AddCommandLine(args).Build();
        var contentRoot = Path.GetFullPath(configuration["Devkit:ContentRoot"] ?? AppContext.BaseDirectory);
        var scenario = Scenario.Load(Path.Combine(contentRoot, "config/scenario.yaml"));
        await using var app = DevkitHost.Create(scenario, contentRoot,
            configuration["Devkit:LbUrl"] ?? "http://localhost:5080", configuration["Devkit:CallerKey"] ??
            throw new ArgumentException("Devkit caller key is missing."));
        await app.RunAsync();
    }
}
