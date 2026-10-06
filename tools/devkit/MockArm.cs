namespace openai_loadbalancer.Devkit;

public static class MockArm
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/subscriptions/{subscription}/locations", (string subscription, HttpContext context, ScenarioState state) =>
            Valid(context, subscription, state, "2022-12-01") ? Results.Ok(new
            {
                value = state.Scenario.Accounts.DistinctBy(account => account.Region).Select(account =>
                    new { name = account.Region, metadata = new { geographyGroup = account.Geography } })
            }) : Results.NotFound());
        app.MapGet("/subscriptions/{subscription}/providers/Microsoft.CognitiveServices/accounts", Accounts);
        app.MapGet("/subscriptions/{subscription}/resourceGroups/{group}/providers/Microsoft.CognitiveServices/accounts",
            (string subscription, string group, HttpContext context, ScenarioState state, AccountListeners listeners) =>
                group.Equals(state.Scenario.ResourceGroup, StringComparison.OrdinalIgnoreCase)
                    ? Accounts(subscription, context, state, listeners) : Results.NotFound());
        app.MapGet("/subscriptions/{subscription}/resourceGroups/{group}/providers/Microsoft.CognitiveServices/accounts/{account}/deployments",
            (string subscription, string group, string account, HttpContext context, ScenarioState state) =>
            {
                if (!Valid(context, subscription, state, "2024-10-01") ||
                    !group.Equals(state.Scenario.ResourceGroup, StringComparison.OrdinalIgnoreCase)) return Results.NotFound();
                var snapshot = state.Snapshot().FirstOrDefault(item => item.Name.Equals(account, StringComparison.OrdinalIgnoreCase));
                if (snapshot == null) return Results.NotFound();
                return Results.Ok(new { value = snapshot.Deployments.Select(item => new
                {
                    id = state.AccountId(snapshot.Name) + "/deployments/" + item.Name,
                    name = item.Name, sku = new { name = item.Sku, capacity = item.Capacity },
                    properties = new { provisioningState = "Succeeded", model = new { name = item.Model, version = item.Version, format = "OpenAI" } }
                }) });
            });
    }

    private static IResult Accounts(string subscription, HttpContext context, ScenarioState state, AccountListeners listeners) =>
        Valid(context, subscription, state, "2024-10-01") ? Results.Ok(new
        {
            value = state.Scenario.Accounts.Select(account => new { id = state.AccountId(account.Name), name = account.Name,
                kind = "OpenAI", location = account.Region, properties = new { endpoint = listeners.Endpoint(account.Name).AbsoluteUri } })
        }) : Results.NotFound();

    private static bool Valid(HttpContext context, string subscription, ScenarioState state, string version) =>
        subscription.Equals(state.Scenario.Subscription, StringComparison.OrdinalIgnoreCase) &&
        context.Request.Query["api-version"] == version;
}
