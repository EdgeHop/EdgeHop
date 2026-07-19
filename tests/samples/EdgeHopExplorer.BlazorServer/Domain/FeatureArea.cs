namespace EdgeHopExplorer.BlazorServer.Domain;

/// <summary>
/// The EdgeHop capability areas this explorer catalogs.
/// <para>
/// An <c>enum</c> is a <b>NamedType</b> node; each member is a <b>Field</b> node. (A predictable
/// quirk worth knowing: every enum member also emits a REFERENCES edge to its own enum type,
/// because the member's type IS the enum.)
/// </para>
/// </summary>
public enum FeatureArea
{
    Extraction,
    Storage,
    Query,
    Interop,
}
