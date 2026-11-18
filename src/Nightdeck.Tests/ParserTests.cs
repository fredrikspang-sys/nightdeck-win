using System.Text;
using Nightdeck.Core;

namespace Nightdeck.Tests;

static class ParserTests
{
    public static void Run()
    {
        const string status = """
            <?xml version="1.0" encoding="UTF-8"?>
            <status etag="abc">
              <state>play</state>
              <pid>7</pid>
              <song>1</song>
              <title1>Hello &amp; Goodbye</title1>
              <name>fallback title</name>
              <title2>An Artist</title2>
              <artist>fallback artist</artist>
              <title3>An Album</title3>
              <album>fallback album</album>
              <volume>40</volume>
              <db>-40.5</db>
              <secs>12</secs>
              <totlen>200</totlen>
              <streamFormat>FLAC 24/96</streamFormat>
              <quality>ignored</quality>
              <shuffle>1</shuffle>
              <repeat>0</repeat>
              <mute>0</mute>
              <sleep>15</sleep>
              <fn>/mnt/music/a.flac</fn>
              <service>LocalMusic</service>
              <image>/Artwork?service=LocalMusic</image>
              <indexing>0</indexing>
            </status>
            """;
        var st = BluOsXml.ParseStatus(status);
        TestHarness.Equal(st.Etag, "abc", "etag");
        TestHarness.Equal(st.Title, "Hello & Goodbye", "title1 beats name and decodes entities");
        TestHarness.Equal(st.Artist, "An Artist", "title2 beats artist");
        TestHarness.Equal(st.Album, "An Album", "title3 beats album");
        TestHarness.Equal(st.Format, "FLAC 24/96", "streamFormat beats quality");
        TestHarness.Equal(st.Volume, 40, "volume");
        TestHarness.Equal(st.Song, 1, "song");
        TestHarness.Equal(st.Shuffle, true, "shuffle");
        TestHarness.Equal(st.Repeat, 0, "repeat queue");
        TestHarness.Equal(st.SleepMinutes, 15, "sleep");
        TestHarness.Equal(st.Image, "/Artwork?service=LocalMusic", "image");
        TestHarness.Equal(st.File, "/mnt/music/a.flac", "fn");

        var fallback = BluOsXml.ParseStatus("<status><name>N</name><artist>A</artist><album>B</album><quality>mp3</quality></status>");
        TestHarness.Equal(fallback.Title, "N", "name fallback");
        TestHarness.Equal(fallback.Artist, "A", "artist fallback");
        TestHarness.Equal(fallback.Album, "B", "album fallback");
        TestHarness.Equal(fallback.Format, "mp3", "quality fallback");
        TestHarness.Equal(fallback.Repeat, -1, "missing repeat");
        TestHarness.Equal(fallback.Song, -1, "missing song");
        TestHarness.Equal(fallback.Shuffle, false, "missing shuffle");
        TestHarness.Equal(BluOsXml.ParseStatus("<status><repeat>9</repeat><sleep></sleep></status>").Repeat, -1, "repeat out of range");
        TestHarness.Equal(BluOsXml.ParseStatus("<status><sleep></sleep></status>").SleepMinutes, 0, "empty sleep");

        var queue = BluOsXml.ParseQueue("""
            <playlist length="2" id="9">
              <song id="0"><art>A &amp; B</art><alb>Alb</alb><title>T</title><fn>F</fn></song>
              <song id="1"><title></title></song>
            </playlist>
            """);
        TestHarness.Equal(queue.Id, "9", "queue id");
        TestHarness.Equal(queue.Length, 2, "queue length");
        TestHarness.Equal(queue.Songs[0].Artist, "A & B", "queue artist entity");
        TestHarness.Equal(queue.Songs[1].Title, "?", "empty title");
        TestHarness.Equal(queue.Songs[1].Album, "", "missing album");

        var meta = BluOsXml.ParseQueueMeta("<playlist><length>4</length><id>9</id></playlist>");
        TestHarness.Equal(meta.Length, 4, "meta length");
        TestHarness.Equal(meta.Id, "9", "meta id");

        var browse = BluOsXml.ParseBrowse("""
            <browse serviceName="Library" searchKey="LocalMusic:Search" nextKey="NEXT:A:3" type="menu">
              <item type="album" text="Abbey" text2="Beatles" browseKey="ALB:1" contextMenuKey="CMA:1" playURL="/Add?playnow=1&amp;album=1"/>
            </browse>
            """);
        TestHarness.Equal(browse.ServiceName, "Library", "serviceName");
        TestHarness.Equal(browse.SearchKey, "LocalMusic:Search", "searchKey");
        TestHarness.Equal(browse.NextKey, "NEXT:A:3", "nextKey");
        TestHarness.Equal(browse.Items.Count, 1, "one item");
        TestHarness.Equal(browse.Items[0].PlayUrl, "/Add?playnow=1&album=1", "playURL decoded");
        TestHarness.Equal(browse.Items[0].Text2, "Beatles", "text2");
        TestHarness.Check(browse.Items[0].IsContainer, "album is a container");

        var presets = BluOsXml.ParsePresets("""<presets prid="0"><preset id="1" name="Radio One" url="x"/></presets>""");
        TestHarness.Equal(presets.Count, 1, "one preset");
        TestHarness.Equal(presets[0].Name, "Radio One", "preset name");
        TestHarness.Equal(BluOsXml.ParseSleepMinutes("<sleep></sleep>"), 0, "sleep element empty");
        TestHarness.Equal(BluOsXml.ParseSleepMinutes("<sleep>30</sleep>"), 30, "sleep element 30");
        TestHarness.Equal(BluOsXml.ErrorDetail("<error><detail>search broke</detail></error>"), "search broke", "error detail");
        TestHarness.Check(BluOsXml.ErrorDetail("<ok/>") == null, "no error");
        TestHarness.Equal(BluOsXml.ParseEntries("<saved><entries>4</entries></saved>"), 4, "entries");

        bool threw = false;
        try { BluOsXml.ParseStatus("<error><detail>nope</detail></error>"); }
        catch (BluOsException ex) { threw = ex.Message == "nope"; }
        TestHarness.Check(threw, "status error throws detail");

        byte[] latinLabel = Encoding.UTF8.GetBytes("""
            <?xml version="1.0" encoding="ISO-8859-1"?>
            <status><title1>Skuggsjá</title1><title2>Ivar</title2></status>
            """);
        var named = BluOsXml.ParseStatus(BluOsXml.Decode(latinLabel));
        TestHarness.Equal(named.Title, "Skuggsjá", "utf-8 title despite latin-1 declaration");
        TestHarness.Equal(named.Artist, "Ivar", "utf-8 artist");

        var bom = new byte[latinLabel.Length + 3];
        bom[0] = 0xEF; bom[1] = 0xBB; bom[2] = 0xBF;
        latinLabel.CopyTo(bom, 3);
        TestHarness.Equal(BluOsXml.ParseStatus(BluOsXml.Decode(bom)).Title, "Skuggsjá", "status bom stripped");

        byte[] errBody = Encoding.UTF8.GetBytes("""<?xml version="1.0"?><error><detail>stopped badly</detail></error>""");
        var errBom = new byte[errBody.Length + 3];
        errBom[0] = 0xEF; errBom[1] = 0xBB; errBom[2] = 0xBF;
        errBody.CopyTo(errBom, 3);
        bool bomError = false;
        try { BluOsXml.EnsureOk(BluOsXml.Decode(errBom)); }
        catch (BluOsException ex) { bomError = ex.Message == "stopped badly"; }
        TestHarness.Check(bomError, "bom error is not success");

        byte[] syncBytes = Encoding.UTF8.GetBytes("""
            <?xml version="1.0" encoding="ISO-8859-1"?>
            <SyncStatus name="Skuggsjá"><slave id="10.0.0.2" port="11000"/></SyncStatus>
            """);
        TestHarness.Equal(BluOsXml.ParseSyncStatus(BluOsXml.Decode(syncBytes)).Name, "Skuggsjá", "sync name utf-8");

        bool dtd = false;
        try { BluOsXml.ParseStatus("""<!DOCTYPE status [<!ENTITY x "a">]><status><title1>&x;</title1></status>"""); }
        catch (BluOsException ex) { dtd = ex.Message == "invalid response"; }
        TestHarness.Check(dtd, "dtd is rejected");

        var fresh = new AppSettings();
        TestHarness.Equal(fresh.Host, "", "default host is empty");
        TestHarness.Equal(fresh.Port, 11000, "default port");
    }
}
