using System.Globalization;
using System.IO;
using Nightdeck.Core;

namespace Nightdeck;

public static class SettingsStore
{
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nightdeck", "config");

    public static AppSettings Load()
    {
        var s = new AppSettings();
        if (!File.Exists(FilePath)) return s;
        foreach (var raw in File.ReadAllLines(FilePath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line[..eq].Trim();
            string value = line[(eq + 1)..].Trim().Trim('"');
            switch (key)
            {
                case "host": s.Host = value; break;
                case "port": if (int.TryParse(value, out int p)) s.Port = p; break;
                case "discovery": s.Discovery = IsOn(value); break;
                case "discovery_timeout":
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double t) && t > 0 && t <= 30)
                        s.DiscoveryTimeoutSeconds = t;
                    break;
                case "sleep_fade":
                    if (int.TryParse(value, out int secs) && secs >= 2 && secs <= 300) { s.SleepFade = true; s.SleepFadeSeconds = secs; }
                    else s.SleepFade = IsOn(value);
                    break;
                case "sleep_end": s.SleepEndStop = value.Equals("stop", StringComparison.OrdinalIgnoreCase); break;
                case "tray": s.ShowTray = IsOn(value); break;
                case "db_offset":
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double db)) s.DbOffset = db;
                    break;
                case "manual":
                    if (value.Length > 0) s.ManualPlayers.Add(value);
                    break;
            }
        }
        if (s.Port <= 0 || s.Port > 65535) s.Port = 11000;
        return s;
    }

    public static void Save(AppSettings s)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var lines = new List<string>
        {
            "host = " + s.Host.Trim(),
            "port = " + s.Port.ToString(CultureInfo.InvariantCulture),
            "discovery = " + (s.Discovery ? "on" : "off"),
            "discovery_timeout = " + s.DiscoveryTimeoutSeconds.ToString("0.##", CultureInfo.InvariantCulture),
            "sleep_fade = " + (s.SleepFade ? s.SleepFadeSeconds.ToString(CultureInfo.InvariantCulture) : "off"),
            "sleep_end = " + (s.SleepEndStop ? "stop" : "pause"),
            "tray = " + (s.ShowTray ? "on" : "off"),
            "db_offset = " + s.DbOffset.ToString("0.##", CultureInfo.InvariantCulture),
        };
        foreach (string manual in s.ManualPlayers)
        {
            string value = manual.Trim();
            if (value.Length > 0) lines.Add("manual = " + value);
        }
        File.WriteAllLines(FilePath, lines);
    }

    static bool IsOn(string value)
        => value.Equals("on", StringComparison.OrdinalIgnoreCase)
        || value.Equals("true", StringComparison.OrdinalIgnoreCase)
        || value == "1";
}
