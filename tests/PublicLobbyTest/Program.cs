// Public-lobby suite entry point. Same shape as the other tests/* console suites: each section is
// a static method that calls Check/Eq; the process exits non-zero when any check failed.
// Sections that need Postgres (Testcontainers) are skipped with a WARN line when Docker is
// unreachable, so the suite still exercises the pure-logic sections on a box without Docker.

static partial class Program
{
    static int _failures;

    static async Task<int> Main(string[] args)
    {
        Console.WriteLine("PublicLobbyTest");
        // WP0.0: empty suite — packages fill in sections below.
        await Task.CompletedTask;
        Console.WriteLine(_failures == 0 ? "ALL PASS" : $"{_failures} FAILURE(S)");
        return _failures == 0 ? 0 : 1;
    }

    static void Check(bool cond, string what)
    {
        if (cond)
            Console.WriteLine($"  ok   {what}");
        else
        {
            _failures++;
            Console.WriteLine($"  FAIL {what}");
        }
    }

    static void Eq<T>(T expected, T actual, string what)
    {
        if (EqualityComparer<T>.Default.Equals(expected, actual))
            Console.WriteLine($"  ok   {what}");
        else
        {
            _failures++;
            Console.WriteLine($"  FAIL {what}: expected {expected}, got {actual}");
        }
    }
}
