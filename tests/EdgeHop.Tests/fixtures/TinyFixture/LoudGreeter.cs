namespace TinyFixture;

public class LoudGreeter : Greeter
{
    public override string Greet(string name) => base.Greet(name).ToUpper();
}
