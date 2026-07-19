namespace BlazorFixture.Shared;

/// <summary>
/// Manual partial half of the Child component: proves the component node's SourceDoc
/// deterministically picks the .razor document ("Child.razor" &lt; "Child.razor.cs"
/// ordinal) while members declared here keep this document.
/// </summary>
public partial class Child
{
    public string Label() => "child";
}
