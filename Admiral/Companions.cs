using System.Diagnostics;
using Microsoft.Win32;

namespace Admiral;

/// <summary>
/// The window apps Admiral works with: DeckMapper (keys) and SlotDeck (screen slots) stay the editors, Admiral does
/// the background work. Also tells when an older copy of their background work is still running, so the same thing
/// is never done twice (two key engines would type every custom key twice; two card writers fight over the screen).
/// </summary>
internal static class Companions
{
    /// <summary>DeckMapper 1.2 and later hold this while their own key service plays keys (and leave it to Admiral).</summary>
    public const string DeckMapperKeyService = "DeckMapper.KeyService";
    /// <summary>DeckMapper sets this after writing keys, so Admiral reads them again.</summary>
    public const string ReloadKeysEvent = "Admiral.ReloadKeys";
    public const string InstanceMutex = "Admiral.SingleInstance";
    public const string ShowMenuEvent = "Admiral.ShowMenu";

    private static readonly Version DeckMapperYields = new(1, 2, 0);

    /// <summary>
    /// Why Admiral should leave the keys alone right now, or null. DeckMapper 1.2+ hands its key service over to
    /// Admiral by itself; 1.1 doesn't know Admiral, so while one runs with its service on, Admiral steps back.
    /// </summary>
    public static string? KeysTakenBy()
    {
        if (Mutex.TryOpenExisting(DeckMapperKeyService, out var m)) { m.Dispose(); return "DeckMapper's own key service is playing your keys"; }
        foreach (var p in Process.GetProcessesByName("DeckMapper"))
            using (p)
            {
                try
                {
                    var v = p.MainModule?.FileVersionInfo;
                    if (v != null && new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart) < DeckMapperYields)
                        return $"DeckMapper {v.FileMajorPart}.{v.FileMinorPart} is running and plays the keys itself: update it or close it";
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            }
        return null;
    }

    /// <summary>Why the card is left alone: TrackDeck itself is running.</summary>
    public static string? CardTakenBy()
    {
        if (!Mutex.TryOpenExisting("TrackDeck.SingleInstance", out var m)) return null;
        m.Dispose();
        return "TrackDeck is running and shows the card: exit it to let Admiral do it";
    }

    // ---------------- finding and opening the window apps ----------------

    /// <summary>Starts DeckMapper or SlotDeck, finding it first (and asking once if it can't be found).</summary>
    public static void Open(string app, Settings settings, IWin32Window? owner)
    {
        string? path = app == "DeckMapper" ? settings.DeckMapperPath : settings.SlotDeckPath;
        if (path == null || !File.Exists(path)) path = Find(app);
        if (path == null)
        {
            using var dlg = new OpenFileDialog
            {
                Title = $"Where is {app}.exe?",
                Filter = $"{app}|{app}.exe|Programs|*.exe",
                FileName = $"{app}.exe",
            };
            if (dlg.ShowDialog(owner) != DialogResult.OK) return;
            path = dlg.FileName;
        }
        if (app == "DeckMapper") settings.DeckMapperPath = path; else settings.SlotDeckPath = path;
        settings.Save();
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException)
        {
            MessageBox.Show($"Couldn't start {app}: {e.Message}", "Admiral", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    /// <summary>Next to Admiral, a running copy, or the path in its "start with Windows" entry.</summary>
    private static string? Find(string app)
    {
        var here = Path.Combine(AppContext.BaseDirectory, app + ".exe");
        if (File.Exists(here)) return here;
        foreach (var p in Process.GetProcessesByName(app))
            using (p)
                try { if (p.MainModule?.FileName is { } f && File.Exists(f)) return f; }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (k?.GetValue(app) is string cmd)
        {
            string exe = cmd.StartsWith('"') && cmd.IndexOf('"', 1) is int end and > 0 ? cmd[1..end] : cmd.Split(' ')[0];
            if (File.Exists(exe)) return exe;
        }
        return FindSibling(app);
    }

    /// <summary>
    /// The C:\Admiral layout: each app is a sibling folder of Admiral's own repo. Walks up from Admiral's exe and,
    /// in the first folder that has an &lt;app&gt; folder, picks the best build in it: a published dist\ first,
    /// then bin-test\, then Release, then Debug, the newest of each.
    /// </summary>
    private static string? FindSibling(string app)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var root = new DirectoryInfo(Path.Combine(dir.FullName, app));
            if (!root.Exists || string.Equals(root.FullName.TrimEnd('\\'), AppContext.BaseDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
            List<FileInfo> found;
            try
            {
                found = root.EnumerateFiles(app + ".exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 6, IgnoreInaccessible = true })
                    .Where(f => !Has(f, "obj") && !Has(f, ".git")).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            if (found.Count == 0) continue;
            int Rank(FileInfo f) => Has(f, "dist") ? 0 : Has(f, "bin-test") ? 1 : Has(f, "Release") ? 2 : Has(f, "Debug") ? 3 : 4;
            return found.OrderBy(Rank).ThenByDescending(f => f.LastWriteTimeUtc).First().FullName;
        }
        return null;

        static bool Has(FileInfo f, string folder) =>
            f.FullName.Split(Path.DirectorySeparatorChar).Contains(folder, StringComparer.OrdinalIgnoreCase);
    }
}
