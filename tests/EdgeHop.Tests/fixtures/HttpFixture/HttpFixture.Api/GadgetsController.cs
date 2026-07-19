using Microsoft.AspNetCore.Mvc;

namespace HttpFixture.Api;

// Attribute-routed controller: the action method itself is the endpoint anchor
// (GET /gadget/{id} — class-level [Route] composed with the [HttpGet] template).
[Route("gadget")]
public sealed class GadgetsController : ControllerBase
{
    [HttpGet("{id}")]
    public string GetById(int id) => Store.Get(id);
}
