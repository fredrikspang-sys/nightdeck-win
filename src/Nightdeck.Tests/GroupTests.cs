using Nightdeck.Core;
using Nightdeck.Mock;

namespace Nightdeck.Tests;

static class GroupTests
{
    public static void Run() => RunAsync().GetAwaiter().GetResult();

    static async Task RunAsync()
    {
        var parsed = BluOsXml.ParseSyncStatus("""
            <SyncStatus name="Living Room" modelName="NODE">
              <slave id="10.1.1.1" port="11001"/>
              <slave id="10.1.1.2"/>
              <slave id="" port="5"/>
            </SyncStatus>
            """);
        TestHarness.Equal(parsed.Name, "Living Room", "sync name");
        TestHarness.Equal(parsed.Slaves.Count, 2, "two slaves, empty id skipped");
        TestHarness.Equal(parsed.Slaves[0].Id, "10.1.1.1", "slave 0 id");
        TestHarness.Equal(parsed.Slaves[0].Port, 11001, "slave 0 port");
        TestHarness.Equal(parsed.Slaves[1].Id, "10.1.1.2", "slave 1 id");
        TestHarness.Equal(parsed.Slaves[1].Port, 11000, "missing port defaults");
        var badPort = BluOsXml.ParseSyncStatus("<SyncStatus name=\"N\"><slave id=\"h\" port=\"abc\"/></SyncStatus>");
        TestHarness.Equal(badPort.Slaves[0].Port, 11000, "non-int port defaults");

        bool threw = false;
        try { BluOsXml.ParseSyncStatus("<error><detail>nope</detail></error>"); }
        catch (BluOsException ex) { threw = ex.Message.Contains("nope", StringComparison.Ordinal); }
        TestHarness.Check(threw, "sync error throws nope");

        using var a = new MockPlayer();
        using var b = new MockPlayer();
        a.Name = "Room & Den";
        a.Start();
        b.Start();
        await using var client = new BluOsClient("127.0.0.1", a.Port);
        await using var other = new BluOsClient("127.0.0.1", b.Port);
        try
        {
            await client.AddSlaveAsync("127.0.0.1", b.Port);
            var sa = await client.GetSyncStatusAsync();
            TestHarness.Equal(sa.Name, "Room & Den", "A sync name");
            TestHarness.Check(sa.Slaves.Exists(s => s.Id == "127.0.0.1" && s.Port == b.Port), "A has B");
            var sb = await other.GetSyncStatusAsync();
            TestHarness.Equal(sb.Name, "Mock", "B sync name");
            TestHarness.Equal(sb.Slaves.Count, 0, "B sync empty");

            await client.RemoveSlaveAsync("127.0.0.1", b.Port);
            sa = await client.GetSyncStatusAsync();
            TestHarness.Equal(sa.Slaves.Count, 0, "A dropped slave");
            await client.RemoveSlaveAsync("127.0.0.1", b.Port);
            sa = await client.GetSyncStatusAsync();
            TestHarness.Equal(sa.Slaves.Count, 0, "second remove stays empty");

            string query = a.LastQuery("/AddSlave");
            TestHarness.Check(query.Contains("slave=127.0.0.1", StringComparison.Ordinal), "add query slave");
            TestHarness.Check(query.Contains("port=" + b.Port, StringComparison.Ordinal), "add query port");
            TestHarness.Equal(b.Count("/AddSlave"), 0, "B got no AddSlave");
            TestHarness.Equal(b.Count("/RemoveSlave"), 0, "B got no RemoveSlave");

            await client.AddSlaveAsync("127.0.0.1", b.Port);
            a.FailGroup = true;
            bool refused = false;
            try { await client.AddSlaveAsync("10.0.0.7", 11000); }
            catch (BluOsException ex) { refused = ex.Message.Contains("refused", StringComparison.Ordinal); }
            TestHarness.Check(refused, "FailGroup refuses");
            sa = await client.GetSyncStatusAsync();
            TestHarness.Check(sa.Slaves.Count == 1 && sa.Slaves[0].Id == "127.0.0.1" && sa.Slaves[0].Port == b.Port, "membership unchanged");
            a.FailGroup = false;

            await client.AddSlaveAsync("10.0.0.8", 11000);
            int addsOnA = a.Count("/AddSlave");
            client.SetEndpoint("127.0.0.1", b.Port);
            await client.AddSlaveAsync("10.0.0.9", 11000);
            TestHarness.Equal(b.Count("/AddSlave"), 1, "switch: B one add");
            TestHarness.Equal(a.Count("/AddSlave"), addsOnA, "switch: A adds stay");

            client.SetEndpoint("127.0.0.1", a.Port);
            sa = await client.GetSyncStatusAsync();
            TestHarness.Check(sa.Slaves.Exists(s => s.Id == "10.0.0.8" && s.Port == 11000), "A kept 10.0.0.8");
            TestHarness.Check(!sa.Slaves.Exists(s => s.Id == "10.0.0.9"), "A has no 10.0.0.9");
            client.SetEndpoint("127.0.0.1", b.Port);
            sb = await client.GetSyncStatusAsync();
            TestHarness.Check(sb.Slaves.Exists(s => s.Id == "10.0.0.9" && s.Port == 11000), "B has 10.0.0.9");
            TestHarness.Check(!sb.Slaves.Exists(s => s.Id == "10.0.0.8"), "B has no 10.0.0.8");

            int statusB = b.Count("/Status");
            int statusA = a.Count("/Status");
            client.SetEndpoint("127.0.0.1", b.Port);
            var status = await client.GetStatusAsync();
            TestHarness.Equal(status.Title, "Track 1", "status on B succeeds");
            TestHarness.Equal(b.Count("/Status"), statusB + 1, "status hit B");
            TestHarness.Equal(a.Count("/Status"), statusA, "status did not hit A");

            int addsBefore = a.Count("/AddSlave") + b.Count("/AddSlave");
            bool emptyHost = false;
            try { await client.AddSlaveAsync("  ", 1); }
            catch (BluOsException) { emptyHost = true; }
            TestHarness.Check(emptyHost, "empty host throws");
            TestHarness.Equal(a.Count("/AddSlave") + b.Count("/AddSlave"), addsBefore, "empty host sends nothing");

            Quiet(a, "A");
            Quiet(b, "B");
        }
        catch (Exception ex)
        {
            TestHarness.Check(false, "group test threw " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    static void Quiet(MockPlayer mock, string who)
    {
        TestHarness.Equal(mock.Count("/Volume"), 0, who + " no /Volume");
        TestHarness.Equal(mock.Count("/Stop"), 0, who + " no /Stop");
        TestHarness.Equal(mock.Count("/Play"), 0, who + " no /Play");
        TestHarness.Equal(mock.Count("/Pause"), 0, who + " no /Pause");
        TestHarness.Equal(mock.Count("/Skip"), 0, who + " no /Skip");
        TestHarness.Equal(mock.Count("/Browse"), 0, who + " no /Browse");
        TestHarness.Equal(mock.Count("/Playlist"), 0, who + " no /Playlist");
        TestHarness.Equal(mock.Count("/Add"), 0, who + " no /Add");
    }
}
