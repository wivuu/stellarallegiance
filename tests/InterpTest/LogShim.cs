// Headless stand-in for the client's Log (client/scripts/Log.cs wraps GD.Print, which needs the engine).
public static class Log
{
    public static void Print(string message) => Console.WriteLine(message);

    public static void Err(string message) => Console.Error.WriteLine(message);

    public static void Warn(string message) => Console.Error.WriteLine(message);
}
