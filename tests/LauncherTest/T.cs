// Tiny assertion helper shared by every group in this suite. Same output shape as the other console
// suites (`  ok   name` / `  FAIL name`), one counter, non-zero exit on any failure.
static class T
{
    public static int Failures { get; private set; }
    public static int Passed { get; private set; }

    public static void Section(string name) => Console.WriteLine($"\n== {name}");

    public static void Check(bool condition, string name)
    {
        if (condition)
        {
            Passed++;
            Console.WriteLine($"  ok   {name}");
            return;
        }
        Failures++;
        Console.WriteLine($"  FAIL {name}");
    }

    public static void Eq<TValue>(TValue expected, TValue actual, string name)
    {
        if (EqualityComparer<TValue>.Default.Equals(expected, actual))
        {
            Passed++;
            Console.WriteLine($"  ok   {name}");
            return;
        }
        Failures++;
        Console.WriteLine($"  FAIL {name}\n       expected {expected}\n       actual   {actual}");
    }

    public static void Seq<TValue>(IEnumerable<TValue> expected, IEnumerable<TValue> actual, string name) =>
        Eq(string.Join(" | ", expected), string.Join(" | ", actual), name);

    // A scratch directory that is removed again, for tests that touch the file system.
    public static string TempDir(string label)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sa-launchertest-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
