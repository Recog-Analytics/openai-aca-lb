namespace openai_loadbalancer.Dashboard.Server;

public static class Program
{
    public static void Main(string[] args) => DashboardHost.Create(args).Run();
}
