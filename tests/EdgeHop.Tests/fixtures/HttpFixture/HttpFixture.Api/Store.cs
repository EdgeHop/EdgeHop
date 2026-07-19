namespace HttpFixture.Api;

// Handler-body targets: the endpoint lambdas call these, so the graph chains
// HTTP_CALLS (client -> MapWidgetEndpoints) with CALLS (MapWidgetEndpoints -> Store.*).
public static class Store
{
    public static List<string> All() => [];

    public static string Get(int id) => $"widget-{id}";

    public static string Add(string name) => name;

    public static bool Remove(int id) => true;
}
