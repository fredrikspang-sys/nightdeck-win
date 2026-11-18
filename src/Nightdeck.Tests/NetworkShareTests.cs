using Nightdeck.Core;
using Nightdeck.Mock;

namespace Nightdeck.Tests;

static class NetworkShareTests
{
    // Shape captured read-only from a NAD C390 (BluOS 4.18); addresses and share names are invented.
    const string SharesPageXml = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<settings pageId="networkShares" schemaVersion="28">
	<menuGroup id="networkShares" displayName="Network Shares" icon="/images/settings/ic_networkshares.png" refresh="true">
		<setting id="shares" name="shares" displayName="Current music shares" class="html" value="&lt;style&gt;&#xA;.heading{font-weight:600;margin:0 0 8px 0;}&#xA;#configshareform{font-family:inherit;color:inherit;}&#xA;#configshareform fieldset{border:1px solid rgba(255,255,255,0.15);border-radius:8px;padding:6px 10px;margin:0 0 12px 0;}&#xA;#configshareform .row{display:flex;align-items:flex-start;gap:10px;padding:8px 10px;border-radius:6px;}&#xA;#configshareform .row:hover{background:rgba(255,255,255,0.06);}&#xA;#configshareform input[type=checkbox]{width:18px;height:18px;accent-color:#4a9eff;margin:0;flex:0 0 auto;cursor:pointer;}&#xA;#configshareform label{cursor:pointer;flex:1;overflow-wrap:break-word;}&#xA;#configshareform input[type=submit]{margin-top:8px;padding:10px 18px;border-radius:6px;border:1px solid rgba(255,255,255,0.15);background:#2a2f36;color:inherit;font:inherit;cursor:pointer;}&#xA;#configshareform input[type=submit]:hover{background:#353b44;}&#xA;&lt;/style&gt;&lt;form id=&#34;configshareform&#34; method=&#34;POST&#34; action=&#34;http://10.0.0.42:80/findremoveshares&#34;&gt;&lt;fieldset&gt;&lt;div class=&#34;row&#34;&gt;&lt;input name=&#34;\\10.0.0.5\Media\Music&#34; id=&#34;checkbox1&#34; type=&#34;checkbox&#34; /&gt;&lt;label for=&#34;checkbox1&#34;&gt;Media\Music on 10.0.0.5 (\\10.0.0.5\Media\Music)&lt;/label&gt;&lt;/div&gt;&lt;div class=&#34;row&#34;&gt;&lt;input name=&#34;\\nas.example\Rock &amp;amp; Roll&#34; id=&#34;checkbox2&#34; type=&#34;checkbox&#34; /&gt;&lt;label for=&#34;checkbox2&#34;&gt;Rock &amp;amp; Roll on nas.example (\\nas.example\Rock &amp;amp; Roll)&lt;/label&gt;&lt;/div&gt;&lt;/fieldset&gt;&lt;input type=&#34;submit&#34; name=&#34;remove&#34; value=&#34;Remove selected shares&#34; /&gt;&lt;/form&gt;" readonly="true"></setting>
		<menuGroup id="addShare" displayName="Add shares" url="/cp/AddShare" class="form" buttonText="Add share" notification="Share added" closeOnInvoke="true"></menuGroup>
	</menuGroup>
</settings>
""";

    const string AddShareXml = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<settings pageId="addShare" schemaVersion="28">
	<menuGroup id="addShare" displayName="Add shares" url="/cp/AddShare" class="form" buttonText="Add share" notification="Share added" closeOnInvoke="true">
		<setting id="sharename" name="sharename" displayName="Enter share name (as \\ComputerName\share)" class="text" pattern="^\\\\(.*?)\\(.*)$" patternError="Share URL must be in the form \\computer\share" requiredError="This field is required." required="true"></setting>
		<setting id="username" name="username" displayName="User name (if required)" class="text"></setting>
		<setting id="password" name="password" displayName="Password (if required)" class="text" style="password"></setting>
	</menuGroup>
</settings>
""";

    const string EmptySharesXml = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<settings pageId="networkShares" schemaVersion="28"><menuGroup id="networkShares" displayName="Network Shares"><setting id="shares" name="shares" class="html" value="&lt;form id=&#34;configshareform&#34; method=&#34;POST&#34; action=&#34;http://10.0.0.42:80/findremoveshares&#34;&gt;&lt;fieldset&gt;&lt;/fieldset&gt;&lt;input type=&#34;submit&#34; name=&#34;remove&#34; value=&#34;Remove selected shares&#34; /&gt;&lt;/form&gt;" readonly="true"></setting><menuGroup id="addShare" url="/cp/AddShare" class="form"></menuGroup></menuGroup></settings>
""";

    public static void Run()
    {
        Parse();
        RunAsync().GetAwaiter().GetResult();
    }

    static void Parse()
    {
        var page = ShareXml.ParseSharesPage(SharesPageXml, 11001);
        TestHarness.Equal(page.Shares.Count, 2, "shares: two in the fixture");
        TestHarness.Equal(page.Shares[0].Path, @"\\10.0.0.5\Media\Music", "shares: first path");
        TestHarness.Equal(page.Shares[0].Label, @"Media\Music on 10.0.0.5 (\\10.0.0.5\Media\Music)", "shares: first label");
        TestHarness.Equal(page.Shares[1].Path, @"\\nas.example\Rock & Roll", "shares: &amp; decoded in the path");
        TestHarness.Equal(page.RemoveAction, "http://10.0.0.42:80/findremoveshares", "shares: remove form action");
        TestHarness.Equal(page.RemoveName, "remove", "shares: submit name");
        TestHarness.Equal(page.RemoveValue, "Remove selected shares", "shares: submit value");
        TestHarness.Equal(page.AddUrl, "/cp/AddShare", "shares: add form url");
        TestHarness.Equal(page.SettingsPort, 11001, "shares: settings port kept");

        var empty = ShareXml.ParseSharesPage(EmptySharesXml);
        TestHarness.Equal(empty.Shares.Count, 0, "shares: empty fieldset");

        bool bad = false;
        try { ShareXml.ParseSharesPage("<settings pageId=\"networkShares\"><menuGroup id=\"networkShares\"/></settings>"); }
        catch (BluOsException) { bad = true; }
        TestHarness.Check(bad, "shares: missing form throws");

        var form = ShareXml.ParseAddShareForm(AddShareXml);
        TestHarness.Equal(form.Url, "/cp/AddShare", "add form: url");
        TestHarness.Equal(form.ShareField, "sharename", "add form: share field");
        TestHarness.Equal(form.UserField, "username", "add form: user field");
        TestHarness.Equal(form.PasswordField, "password", "add form: password field");
        TestHarness.Equal(form.Pattern, @"^\\\\(.*?)\\(.*)$", "add form: pattern");
        TestHarness.Equal(form.PatternError, @"Share URL must be in the form \\computer\share", "add form: pattern error");

        TestHarness.Equal(form.Normalize(@"\\nas\Music"), @"\\nas\Music", "path: \\\\nas\\Music ok");
        TestHarness.Equal(form.Normalize(@" \\10.0.0.5\Media\Music "), @"\\10.0.0.5\Media\Music", "path: trimmed, subfolder ok");
        TestHarness.Equal(form.Normalize("//nas/Music"), @"\\nas\Music", "path: slashes become backslashes");
        TestHarness.Equal(form.Normalize(@"nas\Music"), null, "path: missing \\\\ refused");
        TestHarness.Equal(form.Normalize(@"\\nas"), null, "path: missing share refused");
        TestHarness.Equal(form.Normalize(@"\\nas\"), null, "path: empty share refused");
        TestHarness.Equal(form.Normalize(@"\\\Music"), null, "path: empty server refused");

        string body = ShareXml.FormBody([new("sharename", @"\\nas\My Music & Co"), new("password", "p w=&%")]);
        TestHarness.Equal(body, "sharename=%5C%5Cnas%5CMy+Music+%26+Co&password=p+w%3D%26%25", "form body encoding");
    }

    static async Task RunAsync()
    {
        using var mock = new MockPlayer();
        mock.Start();
        await using var client = new BluOsClient("127.0.0.1", mock.Port);
        const string Pw = "s3cr3t-Pw&x";
        try
        {
            var page = await client.GetNetworkSharesAsync();
            TestHarness.Equal(page.Shares.Count, 2, "mock: lists two shares");
            TestHarness.Equal(page.SettingsPort, mock.SettingsPort, "mock: 301 followed to the settings port");
            TestHarness.Equal(page.Shares[1].Path, @"\\nas.example\Rock & Roll", "mock: & path");

            var form = await client.GetAddShareFormAsync();
            TestHarness.Equal(form.SettingsPort, mock.SettingsPort, "mock: add form from the settings port");

            await client.AddNetworkShareAsync(form, @"\\10.0.0.6\Lossless", "fredrik", Pw);
            TestHarness.Check(mock.Shares.Contains(@"\\10.0.0.6\Lossless"), "add: share added");
            TestHarness.Equal(mock.LastShareUser, "fredrik", "add: user name sent");
            TestHarness.Equal(mock.LastSharePasswordLength, Pw.Length, "add: password sent");
            TestHarness.Check(mock.LastShareContentTypeOk, "add: form-urlencoded");
            TestHarness.Equal(mock.Count("POST /cp/AddShare"), 1, "add: one POST");

            string? err = null;
            try { await client.AddNetworkShareAsync(form, @"\\10.0.0.6\Lossless", "", Pw); }
            catch (BluOsException ex) { err = ex.Message; }
            TestHarness.Equal(err, "Share already exists", "add: player <error> becomes the message");
            TestHarness.Check(err == null || !err.Contains(Pw, StringComparison.Ordinal), "add: password not in the error");

            err = null;
            int posts = mock.Count("POST /cp/AddShare");
            try { await client.AddNetworkShareAsync(form, "nas/Music", "", Pw); }
            catch (BluOsException ex) { err = ex.Message; }
            TestHarness.Equal(err, form.PatternError, "add: bad path refused before sending");
            TestHarness.Equal(mock.Count("POST /cp/AddShare"), posts, "add: bad path sends nothing");

            mock.RedirectNextAdd = true;
            await client.AddNetworkShareAsync(form, "//nas.example/Jazz", "", "");
            TestHarness.Check(mock.Shares.Contains(@"\\nas.example\Jazz") && mock.Count("POST /cp/AddShare2") == 1, "add: 307 re-POSTs the form");

            err = null;
            var onSettings = new AddShareForm { Url = "http://127.0.0.1:" + mock.SettingsPort + "/cp/AddShare", SettingsPort = mock.SettingsPort };
            try { await client.AddNetworkShareAsync(onSettings, @"\\10.0.0.7\Classical", "", ""); }
            catch (BluOsException ex) { err = ex.Message; }
            TestHarness.Equal(err, "HTTP Error 404", "add: the settings port has no add form (like the player)");
            TestHarness.Check(!mock.Shares.Contains(@"\\10.0.0.7\Classical"), "add: nothing added on the settings port");

            err = null;
            mock.FailNextAdd = true;
            try { await client.AddNetworkShareAsync(form, @"\\10.0.0.8\Offline", "", Pw); }
            catch (BluOsException ex) { err = ex.Message; }
            TestHarness.Equal(err, "Addshare failed", "add: <error><message> becomes the message");
            TestHarness.Check(!mock.Shares.Contains(@"\\10.0.0.8\Offline"), "add: failed mount adds nothing");

            page = await client.GetNetworkSharesAsync();
            await client.RemoveNetworkSharesAsync(page, [@"\\nas.example\Rock & Roll", @"\\nas.example\Jazz"]);
            TestHarness.Check(!mock.Shares.Contains(@"\\nas.example\Rock & Roll") && !mock.Shares.Contains(@"\\nas.example\Jazz"), "remove: two shares removed");
            TestHarness.Check(mock.Shares.Contains(@"\\10.0.0.5\Media\Music"), "remove: others kept");
            TestHarness.Equal(mock.Count("/sharecfg"), 0, "remove: the 302 done page is not fetched");
            TestHarness.Equal((await client.GetNetworkSharesAsync()).Shares.Count, mock.Shares.Count, "remove: list refreshed");

            bool none = false;
            try { await client.RemoveNetworkSharesAsync(page, []); }
            catch (BluOsException) { none = true; }
            TestHarness.Check(none, "remove: nothing selected throws");

            var cross = new SharesPage { RemoveAction = "http://192.0.2.1:" + mock.WebPort + "/findremoveshares", Shares = page.Shares };
            await client.RemoveNetworkSharesAsync(cross, [@"\\10.0.0.7\Classical"]);
            TestHarness.Check(!mock.Shares.Contains(@"\\10.0.0.7\Classical"), "remove: a foreign host in the action is sent to the player instead");

            TestHarness.Equal((await client.GetStatusAsync()).Indexing, 0, "reindex: idle before");
            await client.ReindexAsync();
            TestHarness.Equal((await client.GetStatusAsync()).Indexing, 1, "reindex: status shows indexing");
            var until = DateTime.UtcNow.AddSeconds(5);
            int indexing = 1;
            while (indexing != 0 && DateTime.UtcNow < until) { await Task.Delay(100); indexing = (await client.GetStatusAsync()).Indexing; }
            TestHarness.Equal(indexing, 0, "reindex: indexing ends");

            TestHarness.Check(!string.Join("\n", mock.Paths).Contains(Pw, StringComparison.Ordinal), "password not in any recorded request line");
        }
        catch (Exception ex)
        {
            TestHarness.Check(false, "network shares threw " + ex.GetType().Name + ": " + ex.Message);
        }
    }
}
