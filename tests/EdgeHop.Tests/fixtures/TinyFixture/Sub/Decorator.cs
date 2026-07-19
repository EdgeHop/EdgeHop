namespace TinyFixture;

// Regression coverage this file exists for (see EXPECTED-GRAPH.md):
// 1. It lives in a SUBFOLDER, so its sourceDoc is "Sub/Decorator.cs" — the
//    solution-relative + forward-slash convention assertions are non-vacuous.
// 2. It has a C# 12 PRIMARY CONSTRUCTOR: the ctor gets a Method node, but must get
//    NO CALLS edge from member bodies (its declaring syntax is the whole class).
// 3. It has an auto-PROPERTY typed by a fixture type: one Property node, one
//    CONTAINS edge, one REFERENCES edge — and zero accessor/backing-field nodes.
public class Decorator(Greeter inner)
{
    public Greeter Inner { get; } = inner;

    public string Decorate() => Inner.Greet("deco");
}
