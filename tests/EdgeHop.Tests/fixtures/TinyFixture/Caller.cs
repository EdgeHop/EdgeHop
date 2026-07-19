namespace TinyFixture;

public class Caller
{
    private readonly Greeter _greeter = new Greeter();

    public string CallGreet() => _greeter.Greet("x");
}
