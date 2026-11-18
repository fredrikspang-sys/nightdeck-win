namespace Nightdeck.Tests;

static class TestHarness
{
    public static int Failed;

    public static void Check(bool ok, string name)
    {
        if (ok) Console.WriteLine("ok   " + name);
        else
        {
            Console.WriteLine("FAIL " + name);
            Failed++;
        }
    }

    public static void Equal<T>(T actual, T expected, string name)
        => Check(EqualityComparer<T>.Default.Equals(actual, expected), name + " (got " + actual + ", expected " + expected + ")");
}

static class Program
{
    static int Main()
    {
        ParserTests.Run();
        ClientTests.Run();
        IntegrationTests.Run();
        CommandTests.Run();
        SleepTests.Run();
        DiscoveryTests.Run();
        GroupTests.Run();
        if (TestHarness.Failed != 0)
        {
            Console.WriteLine(TestHarness.Failed + " failed");
            return 1;
        }
        Console.WriteLine("All tests passed.");
        return 0;
    }
}
