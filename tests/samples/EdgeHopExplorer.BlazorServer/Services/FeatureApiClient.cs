using System.Net.Http.Json;
using EdgeHopExplorer.BlazorServer.Domain;

namespace EdgeHopExplorer.BlazorServer.Services;

/// <summary>
/// Typed HttpClient over this app's own minimal API. Each verb-named call whose route shape matches
/// a registered endpoint produces a cross-tier HTTP_CALLS edge to
/// <c>FeatureEndpoints.MapFeatureEndpoints</c> — the Web->API boundary no compile-time edge
/// crosses, even though here both tiers happen to live in one project. Because <c>get_callers</c>
/// walks HTTP_CALLS alongside CALLS, a component that calls <see cref="GetAllAsync"/> can be traced
/// through to the endpoint method and on into the catalog.
/// </summary>
public sealed class FeatureApiClient
{
    private readonly HttpClient _http;

    public FeatureApiClient(HttpClient http) => _http = http;

    // GET /api/features/all  ->  HTTP_CALLS
    public async Task<IReadOnlyList<FeatureInfo>> GetAllAsync() =>
        await _http.GetFromJsonAsync<List<FeatureInfo>>("/api/features/all") ?? [];

    // GET /api/features/{name}  ->  HTTP_CALLS (interpolation hole matches the {name} segment)
    public async Task<FeatureInfo?> GetAsync(string name) =>
        await _http.GetFromJsonAsync<FeatureInfo>($"/api/features/{name}");

    // POST /api/features/search  ->  HTTP_CALLS (the POST verb discriminates it from the GETs)
    public async Task<IReadOnlyList<FeatureInfo>> SearchAsync(string term)
    {
        var response = await _http.PostAsJsonAsync("/api/features/search", new SearchRequest(term));
        return await response.Content.ReadFromJsonAsync<List<FeatureInfo>>() ?? [];
    }
}
