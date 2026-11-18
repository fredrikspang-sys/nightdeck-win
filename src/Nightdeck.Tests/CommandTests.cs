using Nightdeck.Core;
using Nightdeck.Mock;

namespace Nightdeck.Tests;

static class CommandTests
{
    public static void Run() => RunAsync().GetAwaiter().GetResult();

    static async Task RunAsync()
    {
        using var mock = new MockPlayer();
        mock.Start();
        await using var client = new BluOsClient("127.0.0.1", mock.Port);
        try
        {
            var status = await client.GetStatusAsync();
            await client.GetQueueAsync();
            if (status.Image.Length > 0)
                await client.GetBytesAsync(status.Image, 100_000);
            TestHarness.Equal(mock.Count("/Volume"), 0, "setup sends no /Volume");
            TestHarness.Equal(mock.Count("/Stop"), 0, "setup sends no /Stop");

            await client.StopAsync();
            TestHarness.Equal((await client.GetStatusAsync()).State, "stop", "stop action stops");
            TestHarness.Equal(mock.Count("/Stop"), 1, "stop action sends /Stop");
            TestHarness.Equal(mock.Count("/Volume"), 0, "stop sends no /Volume");

            await client.GetStatusAsync();
            TestHarness.Equal(mock.Count("/Stop"), 1, "later status sends no /Stop");
            TestHarness.Equal(mock.Count("/Volume"), 0, "later status sends no /Volume");
        }
        catch (Exception ex)
        {
            TestHarness.Check(false, "command test threw " + ex.GetType().Name + ": " + ex.Message);
        }
    }
}
