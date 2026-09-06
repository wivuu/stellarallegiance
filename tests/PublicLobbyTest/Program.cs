// Public-lobby suite entry point. Same shape as the other tests/* console suites: each section is
// a static method that calls Check/Eq; the process exits non-zero when any check failed.
// Sections that need Postgres (Testcontainers) are skipped with a WARN line when Docker is
// unreachable, so the suite still exercises the pure-logic sections on a box without Docker.

// Named `Suite`, not `Program`: LobbyHostFixture needs WebApplicationFactory<Program> to bind
// unambiguously to public-lobby/PublicLobby.cs's `public partial class Program {}` marker (added
// for exactly this purpose), which collides in the global namespace with a same-named type
// declared in this project's own compilation (CS0436) — so this suite's own top-level static
// class is named differently instead of reaching for an extern-alias workaround.
static partial class Suite
{
    static int _failures;

    static async Task<int> Main(string[] args)
    {
        Console.WriteLine("PublicLobbyTest");
        try
        {
            await RunSchemaTestsAsync();
            await RunOrleansTestsAsync();
            await RunAuthTestsAsync();
            await RunProfileTestsAsync();
            await RunJoinTokenTestsAsync();
            await RunListingTestsAsync();
        }
        finally
        {
            // LobbyHostFixture (the real host + co-hosted silo) must shut down while its Postgres
            // connection is still live, so it's disposed BEFORE the container it depends on.
            // Both are started lazily by the first section that needs them; torn down once here
            // regardless of which sections ran or failed.
            await LobbyHostFixture.DisposeAsync();
            await PostgresFixture.DisposeAsync();
        }
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
