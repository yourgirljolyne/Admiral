using System.Text.Json;
using Microsoft.Win32;

namespace Admiral;

internal static class AppPaths
{
    /// <summary>Settings, log and the screen to put back: %LOCALAPPDATA%\Admiral.</summary>
    public static string Data { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Admiral");

    public static string LogFile => Path.Combine(Data, "log.txt");
}

internal static class AppLog
{
    private static readonly object Gate = new();

    /// <summary>What the tray (UI thread) is busy with, for the watchdog's report.</summary>
    public static volatile string UiDoing = "starting";

    public static void Write(string s)
    {
        // With SignalRGB streaming, every command skips its replies; that's normal and would flood the log.
        if (s.Contains("meant for another program")) return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.Data);
                var fi = new FileInfo(AppPaths.LogFile);
                if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
                File.AppendAllText(AppPaths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {s}{Environment.NewLine}");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Admiral's preferences, kept on this PC (never on the keyboard).</summary>
internal sealed class Settings
{
    public bool PlayKeys { get; set; } = true;
    public bool SwitchScreens { get; set; } = true;
    public bool NowPlaying { get; set; } = true;
    public bool SpotifyOnly { get; set; } = true;
    public bool ShowPaused { get; set; } = true;
    public bool RestoreWhenIdle { get; set; } = true;
    public int IdleSeconds { get; set; } = 60;
    /// <summary>The dial mode Admiral plays in software mode (0-based; the keyboard doesn't report its own).</summary>
    public int DialMode { get; set; }
    /// <summary>Where DeckMapper.exe and SlotDeck.exe are, once found or picked.</summary>
    public string? DeckMapperPath { get; set; }
    public string? SlotDeckPath { get; set; }

    private static string FilePath => Path.Combine(AppPaths.Data, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
        var s = new Settings();
        s.ImportTrackDeck();
        s.Save();
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Data);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* stays for this session */ }
    }

    /// <summary>First run: take over TrackDeck's card settings (settings.txt, key=value lines).</summary>
    private void ImportTrackDeck()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrackDeck", "settings.txt");
            if (!File.Exists(path)) return;
            foreach (var line in File.ReadAllLines(path))
            {
                var parts = line.Split('=', 2);
                if (parts.Length != 2) continue;
                string v = parts[1].Trim();
                switch (parts[0].Trim())
                {
                    case "spotifyOnly": SpotifyOnly = v == "true"; break;
                    case "showPaused": ShowPaused = v == "true"; break;
                    case "restoreWhenIdle": RestoreWhenIdle = v == "true"; break;
                    case "idleSeconds" when int.TryParse(v, out int secs): IdleSeconds = secs; break;
                }
            }
            AppLog.Write("took over TrackDeck's card settings");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // ---------------- start with Windows ----------------

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool StartWithWindows
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue("Admiral") is string;
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue("Admiral", $"\"{Environment.ProcessPath}\"");
            else k.DeleteValue("Admiral", throwOnMissingValue: false);
        }
    }

    /// <summary>Other Deck apps that start with Windows (Run value name and command), whose background work Admiral does now.</summary>
    public static List<(string Name, string Command)> OtherStartups()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        var list = new List<(string, string)>();
        foreach (var name in new[] { "TrackDeck", "DeckMapper" })
            if (k?.GetValue(name) is string cmd) list.Add((name, cmd));
        return list;
    }

    public static void RemoveStartup(string name)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        k.DeleteValue(name, throwOnMissingValue: false);
    }
}
