using Nightdeck.Core;

namespace Nightdeck;

public static class PlayerList
{
    public static bool TryParse(string text, out string host, out int port)
    {
        host = "";
        port = 11000;
        text = (text ?? "").Trim();
        if (text.Length == 0 || text.Contains('/') || text.Contains('\\')) return false;
        int colon = text.IndexOf(':');
        if (colon >= 0)
        {
            if (text.IndexOf(':', colon + 1) >= 0) return false;
            host = text[..colon].Trim();
            if (!int.TryParse(text[(colon + 1)..].Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out port))
                return false;
        }
        else host = text;
        if (host.Length == 0 || host.Any(char.IsWhiteSpace) || port is < 1 or > 65535) return false;
        return true;
    }

    public static string Format(string host, int port)
        => port == 11000 ? host : host + ":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static void Remember(AppSettings settings, string host, int port)
    {
        host = (host ?? "").Trim();
        if (host.Length == 0 || port is < 1 or > 65535) return;
        foreach (string raw in settings.ManualPlayers)
            if (TryParse(raw, out string known, out int knownPort) && knownPort == port && string.Equals(known, host, StringComparison.OrdinalIgnoreCase))
                return;
        settings.ManualPlayers.Add(Format(host, port));
    }

    public static void SwitchTo(PlayerSession session, string host, int port)
    {
        host = (host ?? "").Trim();
        if (host.Length == 0 || port is < 1 or > 65535) return;
        if (string.Equals(session.Client.Host, host, StringComparison.OrdinalIgnoreCase) && session.Client.Port == port)
            return;
        Remember(session.Settings, session.Client.Host, session.Client.Port);
        session.Settings.Host = host;
        session.Settings.Port = port;
        SettingsStore.Save(session.Settings);
        session.ApplySettings();
    }

    public static List<KnownPlayer> Merge(AppSettings settings, IReadOnlyList<DiscoveredPlayer> discovered)
    {
        var list = new List<KnownPlayer>();
        void Add(string name, string host, int port, bool manual)
        {
            host = (host ?? "").Trim();
            if (host.Length == 0 || port is < 1 or > 65535) return;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Port != port || !string.Equals(list[i].Host, host, StringComparison.OrdinalIgnoreCase)) continue;
                if (list[i].Name.Length == 0 && name.Length > 0)
                    list[i] = new KnownPlayer { Name = name, Host = list[i].Host, Port = port, Manual = list[i].Manual || manual };
                else if (manual && !list[i].Manual)
                    list[i] = new KnownPlayer { Name = list[i].Name.Length > 0 ? list[i].Name : name, Host = list[i].Host, Port = port, Manual = true };
                return;
            }
            list.Add(new KnownPlayer { Name = name, Host = host, Port = port, Manual = manual });
        }

        string activeName = NameOf(discovered, settings.Host, settings.Port);
        Add(activeName, settings.Host, settings.Port, false);
        foreach (string raw in settings.ManualPlayers)
        {
            if (!TryParse(raw, out string host, out int port)) continue;
            Add(NameOf(discovered, host, port), host, port, true);
        }
        foreach (var found in discovered)
            Add(found.Name, found.Host, found.Port, false);
        return list;
    }

    static string NameOf(IReadOnlyList<DiscoveredPlayer> discovered, string host, int port)
    {
        foreach (var found in discovered)
            if (found.Port == port && string.Equals(found.Host, host, StringComparison.OrdinalIgnoreCase) && found.Name.Length > 0)
                return found.Name;
        return "";
    }
}
