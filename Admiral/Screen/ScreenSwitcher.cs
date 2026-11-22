using System.Buffers.Binary;

namespace Admiral;

/// <summary>
/// FN + arrows for the screen while the keyboard is in software mode. In self-operated mode the firmware walks the
/// active profile's screen list itself; in software mode (SignalRGB streaming) it doesn't, so the screen stays on
/// whatever it showed. The key engine sees FN + arrow in the key report and asks this to step through the same list
/// the way the keyboard would: copy the next entry into the active-screen file (file 62), like SlotDeck's Show.
/// Reads the list fresh on every press, so changes made in SlotDeck or Web Hub count straight away.
/// Writes only file 62; never the profile, the list or the property map.
/// </summary>
internal sealed class ScreenSwitcher(ScreenController screen)
{
    private const ushort ProfileList = 15;
    private const byte LayoutHeader = 55;

    private readonly SemaphoreSlim _busy = new(1, 1);
    private int _queued;

    public Action<string>? Log { get; set; }
    public string Status { get; private set; } = "Waiting for FN + arrow";
    public event Action? StatusChanged;
    /// <summary>Raised after a switch with the entry id now on the screen.</summary>
    public event Action<ushort>? Switched;

    /// <summary>
    /// Steps one screen forward (+1) or back (-1). Presses that arrive while a switch is still being written are
    /// added up and done in one go, so holding or tapping fast never queues a pile of writes.
    /// </summary>
    public async Task StepAsync(int direction)
    {
        Interlocked.Add(ref _queued, direction);
        // A press that lands just as the previous switch finishes is picked up by the outer loop.
        while (Volatile.Read(ref _queued) != 0)
        {
            if (!await _busy.WaitAsync(0)) return;
            try
            {
                while (Interlocked.Exchange(ref _queued, 0) is int steps && steps != 0)
                    await screen.UseAsync(b => SwitchAsync(b, steps));
            }
            catch (Exception e) when (e is BragiException or IOException)
            {
                SetStatus("Couldn't switch: " + e.Message);
            }
            finally { _busy.Release(); }
        }
    }

    private async Task SwitchAsync(Bragi b, int steps)
    {
        var ids = await ReadScreenListAsync(b);
        if (ids is not { Length: > 0 })
        {
            SetStatus("This profile has no screen list (SlotDeck can make one)");
            return;
        }

        // The entries that exist, in list order (a missing one is skipped, like the card before TrackDeck/Admiral ran).
        var entries = new List<(ushort Id, byte[] Entry)>();
        foreach (var id in ids)
            if (await b.ReadFileAsync(id) is { Length: > 0 } e) entries.Add((id, e));
        if (entries.Count == 0) { SetStatus("None of the screens in the list exist"); return; }

        var active = await b.ReadFileAsync(ScreenFiles.ActiveScreen);
        int at = entries.FindIndex(x => Shows(active, x.Entry));
        // Not on any listed screen: forward starts at the first one, back at the last.
        int next = at < 0
            ? (steps > 0 ? steps - 1 : entries.Count + steps)
            : at + steps;
        next = ((next % entries.Count) + entries.Count) % entries.Count;
        if (next == at) return;

        var (nextId, entry) = entries[next];
        await b.WriteFileAsync(ScreenFiles.ActiveScreen, entry);
        SetStatus($"Screen {next + 1} of {entries.Count}{(nextId == ScreenFiles.CardConfig ? " (now-playing card)" : "")}");
        Log?.Invoke($"screen: {(at < 0 ? "?" : (at + 1).ToString())} -> {next + 1} of {entries.Count} (entry {nextId})");
        Switched?.Invoke(nextId);
    }

    /// <summary>True when the active-screen file holds this entry (or points at the same picture, as SlotDeck checks).</summary>
    private static bool Shows(byte[]? active, byte[] entry) =>
        active is { Length: >= 4 } && (active.AsSpan().SequenceEqual(entry) ||
            entry is [ScreenFiles.ConfigTypeImage, 0, _, _, ..] && active[0] == ScreenFiles.ConfigTypeImage && active[1] == 0
            && Le16(active, 2) == Le16(entry, 2));

    /// <summary>The active profile's screen list (null if it has none). Read-only; same reading as SlotDeck.</summary>
    private static async Task<ushort[]?> ReadScreenListAsync(Bragi b)
    {
        var list = await b.ReadFileAsync(ProfileList);
        int count = list is { Length: >= 4 } ? Le16(list, 2) : 0;
        if (list == null || count == 0 || list.Length < 4 + count * 2) return null;
        uint active = await b.GetPropertyAsync(Bragi.PropActiveProfile);
        if (active >= count) return null;
        var p = await b.ReadFileAsync(Le16(list, 4 + (int)active * 2));
        // Profile: header, cookie, file pointers, name length (at 30), name + 0, then 4 pointers; the screen list is last.
        if (p is not { Length: >= 32 }) return null;
        int layoutAt = 33 + Le16(p, 30) + 6;
        if (p.Length < layoutAt + 2) return null;
        ushort layoutId = Le16(p, layoutAt);
        if (layoutId == 0) return null;
        var d = await b.ReadFileAsync(layoutId);
        // 55 0, row count, then per row: count and u16 entry ids. Every row is walked in order, as one list.
        if (d is not { Length: >= 4 } || d[0] != LayoutHeader) return null;
        var ids = new List<ushort>();
        int o = 3;
        for (int row = 0; row < d[2] && o < d.Length; row++)
        {
            int n = d[o++];
            for (int i = 0; i < n && o + 2 <= d.Length; i++, o += 2) ids.Add(Le16(d, o));
        }
        return ids.ToArray();
    }

    private void SetStatus(string s)
    {
        if (s == Status) return;
        Status = s;
        Log?.Invoke("screen: " + s);
        StatusChanged?.Invoke();
    }

    private static ushort Le16(byte[] b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at));
}
