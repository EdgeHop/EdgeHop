using System.Net.Http.Json;

namespace HttpFixture.Web;

// The client tier: one method per caller-template shape the HTTP pass must handle.
// Which methods get HTTP_CALLS edges (and which deliberately do not) is pinned in
// EXPECTED-GRAPH.md.
public sealed class WidgetApiClient(HttpClient http)
{
    // Literal route -> GET /widgets.
    public async Task<List<string>?> GetWidgetsAsync() =>
        await http.GetFromJsonAsync<List<string>>("/widgets");

    // Interpolation hole matches the {id:int} constraint segment -> GET /widget/{id:int}.
    public async Task<string?> GetWidgetAsync(int id) =>
        await http.GetFromJsonAsync<string>($"/widget/{id}");

    // Literal route, POST verb -> POST /widget.
    public async Task CreateWidgetAsync(string name) =>
        await http.PostAsJsonAsync("/widget", name);

    // Group-prefixed endpoint plus a query string to strip -> DELETE /admin/widget/{id}.
    public async Task DeleteWidgetAsync(int id, bool force) =>
        await http.DeleteAsync($"/admin/widget/{id}?force={force}");

    // Non-constant concat suffix assumed to be a query string -> GET /widgets.
    public async Task<List<string>?> SearchWidgetsAsync(string query) =>
        await http.GetFromJsonAsync<List<string>>("/widgets" + query);

    // Controller endpoint via a plain HttpClient method -> GET /gadget/{id}.
    public async Task<string> GetGadgetAsync(int id) =>
        await http.GetStringAsync($"/gadget/{id}");

    // Verb mismatch: no PUT endpoint is registered -> NO edge.
    public async Task RenameWidgetAsync(int id, string name) =>
        await http.PutAsJsonAsync($"/widget/{id}", name);

    // Unregistered route -> NO edge.
    public async Task<string?> GetUnknownAsync() =>
        await http.GetFromJsonAsync<string>("/nope");
}
