using System.Text.Json.Serialization;

namespace EdgeHop.Oxc;

// The stdin/stdout JSON contract with the native edgehop-oxc binary. Field names are the
// camelCase the binary emits/consumes (it is written with serde rename_all = "camelCase").

internal sealed class OxcRequest
{
    [JsonPropertyName("modules")]
    public required IReadOnlyList<OxcModuleInput> Modules { get; init; }
}

internal sealed class OxcModuleInput
{
    [JsonPropertyName("moduleId")] public required string ModuleId { get; init; }
    [JsonPropertyName("sourceDoc")] public required string SourceDoc { get; init; }
    [JsonPropertyName("lang")] public required string Lang { get; init; }
    [JsonPropertyName("source")] public required string Source { get; init; }
}

internal sealed class OxcResponse
{
    [JsonPropertyName("nodes")] public List<OxcNode> Nodes { get; set; } = new();
    [JsonPropertyName("edges")] public List<OxcEdge> Edges { get; set; } = new();
    [JsonPropertyName("interopExports")] public List<OxcInteropExport> InteropExports { get; set; } = new();
    [JsonPropertyName("dotnetCalls")] public List<OxcDotNetCall> DotNetCalls { get; set; } = new();
    [JsonPropertyName("diagnostics")] public List<string> Diagnostics { get; set; } = new();
}

internal sealed class OxcNode
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("sourceDoc")] public string SourceDoc { get; set; } = "";
}

internal sealed class OxcEdge
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("fromId")] public string FromId { get; set; } = "";
    [JsonPropertyName("toId")] public string ToId { get; set; } = "";
    [JsonPropertyName("sourceDoc")] public string SourceDoc { get; set; } = "";
}

/// <summary>A JS symbol callable from C# JS-interop (module-scoped export). Carried through for
/// the cross-tier JS_CALLS matching pass.</summary>
internal sealed class OxcInteropExport
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("moduleId")] public string ModuleId { get; set; } = "";
    [JsonPropertyName("symbolId")] public string SymbolId { get; set; } = "";
    [JsonPropertyName("sourceDoc")] public string SourceDoc { get; set; } = "";
}

/// <summary>A JS → C# interop call site (<c>DotNet.invokeMethod*</c> / instance
/// <c>invokeMethod*</c>). Carried through for the cross-tier JS_INVOKES matching pass.</summary>
internal sealed class OxcDotNetCall
{
    [JsonPropertyName("callerId")] public string CallerId { get; set; } = "";
    [JsonPropertyName("assembly")] public string? Assembly { get; set; }
    [JsonPropertyName("identifier")] public string Identifier { get; set; } = "";
    [JsonPropertyName("isStatic")] public bool IsStatic { get; set; }
    [JsonPropertyName("sourceDoc")] public string SourceDoc { get; set; } = "";
}
