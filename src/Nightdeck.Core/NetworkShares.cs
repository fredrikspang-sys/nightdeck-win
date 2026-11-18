using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Nightdeck.Core;

/// <summary>One SMB share the player indexes for its music library. The player never returns credentials.</summary>
public sealed record NetworkShare(string Path, string Label);

/// <summary>
/// The player's networkShares settings page: the shares plus the remove form (action, submit field) and
/// the add form URL. Shapes are from a NAD C390 on BluOS 4.18; they are undocumented, so URLs and field
/// names are read from the page instead of being hard-coded.
/// </summary>
public sealed class SharesPage
{
    public IReadOnlyList<NetworkShare> Shares { get; init; } = [];
    public string RemoveAction { get; init; } = "";
    public string RemoveName { get; init; } = "remove";
    public string RemoveValue { get; init; } = "Remove selected shares";
    public string AddUrl { get; init; } = "/cp/AddShare";
    /// <summary>Port that answered /Settings (11001 on a NAD; 11000 answers with a redirect).</summary>
    public int SettingsPort { get; init; }
}

/// <summary>Fields of the player's Add share form (Settings?id=addShare).</summary>
public sealed class AddShareForm
{
    public string Url { get; init; } = "/cp/AddShare";
    public string ShareField { get; init; } = "sharename";
    public string UserField { get; init; } = "username";
    public string PasswordField { get; init; } = "password";
    public string Pattern { get; init; } = @"^\\\\(.*?)\\(.*)$";
    public string PatternError { get; init; } = @"Share URL must be in the form \\computer\share";
    public int SettingsPort { get; init; }

    /// <summary>
    /// Normalises '/' to '\' and checks the player's pattern, plus a non-empty server and share name.
    /// Returns the path to send, or null when it is not a \\server\share path.
    /// </summary>
    public string? Normalize(string input)
    {
        string p = (input ?? "").Trim().Replace('/', '\\');
        if (p.Length < 5 || !p.StartsWith(@"\\", StringComparison.Ordinal) || p[2] == '\\') return null;
        int b = p.IndexOf('\\', 2);
        if (b < 0 || b + 1 >= p.Length || p[b + 1] == '\\') return null;
        try
        {
            if (Pattern.Length > 0 && !Regex.IsMatch(p, Pattern, RegexOptions.None, TimeSpan.FromMilliseconds(200))) return null;
        }
        catch (ArgumentException) { }
        catch (RegexMatchTimeoutException) { return null; }
        return p;
    }
}

public static class ShareXml
{
    static readonly Regex Tag = new(@"<(form|input|label)\b((?:[^>""']|""[^""]*""|'[^']*')*)>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex AttrRx = new(@"([\w:-]+)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.CultureInvariant);
    static readonly Regex LabelRx = new(@"<label\b((?:[^>""']|""[^""]*""|'[^']*')*)>(.*?)</label>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    public static SharesPage ParseSharesPage(string xml, int settingsPort = 0)
    {
        var root = LoadSettings(xml);
        string? html = null;
        string addUrl = "";
        foreach (var el in root.Descendants())
        {
            string id = (string?)el.Attribute("id") ?? "";
            if (el.Name.LocalName == "setting" && id == "shares") html = (string?)el.Attribute("value") ?? "";
            else if (el.Name.LocalName == "menuGroup" && id == "addShare") addUrl = (string?)el.Attribute("url") ?? "";
        }
        if (html == null) throw new BluOsException("the player's share page has an unexpected format (firmware changed?)");

        string action = "", rmName = "", rmValue = "";
        var ids = new List<(string Id, string Path)>();
        foreach (Match m in Tag.Matches(html))
        {
            string kind = m.Groups[1].Value.ToLowerInvariant();
            var a = Attrs(m.Groups[2].Value);
            if (kind == "form") action = a.GetValueOrDefault("action", action);
            else if (kind == "input")
            {
                string type = a.GetValueOrDefault("type", "").ToLowerInvariant();
                string name = a.GetValueOrDefault("name", "");
                if (type == "checkbox" && name.Length > 0) ids.Add((a.GetValueOrDefault("id", ""), name));
                else if (type == "submit" && name.Length > 0) { rmName = name; rmValue = a.GetValueOrDefault("value", ""); }
            }
        }
        if (action.Length == 0) throw new BluOsException("the player's share page has no share form (firmware changed?)");
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in LabelRx.Matches(html))
        {
            string f = Attrs(m.Groups[1].Value).GetValueOrDefault("for", "");
            if (f.Length > 0) labels[f] = WebUtility.HtmlDecode(Regex.Replace(m.Groups[2].Value, "<[^>]*>", "")).Trim();
        }
        var shares = ids.Select(x => new NetworkShare(x.Path, labels.GetValueOrDefault(x.Id, ""))).ToList();
        return new SharesPage
        {
            Shares = shares,
            RemoveAction = action,
            RemoveName = rmName.Length > 0 ? rmName : "remove",
            RemoveValue = rmName.Length > 0 ? rmValue : "Remove selected shares",
            AddUrl = addUrl.Length > 0 ? addUrl : "/cp/AddShare",
            SettingsPort = settingsPort,
        };
    }

    public static AddShareForm ParseAddShareForm(string xml, int settingsPort = 0)
    {
        var root = LoadSettings(xml);
        string url = "", share = "", user = "", pass = "", pattern = "", perr = "";
        foreach (var el in root.Descendants())
        {
            if (el.Name.LocalName == "menuGroup" && url.Length == 0) url = (string?)el.Attribute("url") ?? "";
            if (el.Name.LocalName != "setting") continue;
            string name = (string?)el.Attribute("name") ?? "";
            if (name.Length == 0) continue;
            string? pt = (string?)el.Attribute("pattern");
            if ((string?)el.Attribute("style") == "password") pass = name;
            else if (share.Length == 0 && (pt != null || user.Length == 0))
            {
                share = name; pattern = pt ?? ""; perr = (string?)el.Attribute("patternError") ?? "";
            }
            else if (user.Length == 0) user = name;
        }
        var d = new AddShareForm();
        return new AddShareForm
        {
            Url = url.Length > 0 ? url : d.Url,
            ShareField = share.Length > 0 ? share : d.ShareField,
            UserField = user.Length > 0 ? user : d.UserField,
            PasswordField = pass.Length > 0 ? pass : d.PasswordField,
            Pattern = pattern.Length > 0 ? pattern : d.Pattern,
            PatternError = perr.Length > 0 ? perr : d.PatternError,
            SettingsPort = settingsPort,
        };
    }

    /// <summary>application/x-www-form-urlencoded, space as '+' (like a browser form post).</summary>
    public static string FormBody(IEnumerable<KeyValuePair<string, string>> pairs)
    {
        var sb = new StringBuilder();
        foreach (var kv in pairs)
        {
            if (sb.Length > 0) sb.Append('&');
            sb.Append(Enc(kv.Key)).Append('=').Append(Enc(kv.Value));
        }
        return sb.ToString();
    }

    static string Enc(string s)
    {
        var sb = new StringBuilder();
        foreach (byte c in Encoding.UTF8.GetBytes(s ?? ""))
        {
            if (c is (>= (byte)'a' and <= (byte)'z') or (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'0' and <= (byte)'9') or (byte)'_' or (byte)'.' or (byte)'-' or (byte)'~' or (byte)'*')
                sb.Append((char)c);
            else if (c == (byte)' ') sb.Append('+');
            else sb.Append('%').Append(c.ToString("X2"));
        }
        return sb.ToString();
    }

    static Dictionary<string, string> Attrs(string s)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in AttrRx.Matches(s))
        {
            string v = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value;
            d.TryAdd(m.Groups[1].Value, WebUtility.HtmlDecode(v));
        }
        return d;
    }

    static XElement LoadSettings(string xml)
    {
        var root = BluOsXml.Load(xml ?? "");
        if (root.Name.LocalName == "error")
        {
            string detail = ((string?)root.Element("detail") ?? "").Trim();
            throw new BluOsException(detail.Length == 0 ? "error" : detail);
        }
        if (root.Name.LocalName != "settings") throw new BluOsException("settings: " + root.Name.LocalName);
        return root;
    }
}
