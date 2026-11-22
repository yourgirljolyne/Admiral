namespace Admiral;

/// <summary>
/// The now-playing card (TrackDeck's job, moved here): watches Windows media and rewrites the card when the track
/// or play state changes, and puts the user's own screen back after a set idle time. Driven by <see cref="TickAsync"/>
/// once a second. Same rules as TrackDeck: wait for the metadata to settle, never write more than every 3 seconds.
/// </summary>
internal sealed class Tracker(ScreenController screen)
{
    public const int MinIdleSeconds = 5, MaxIdleSeconds = 24 * 3600;

    private readonly MediaReader _media = new();
    private readonly SemaphoreSlim _busy = new(1, 1);

    private string? _shownKey;        // what's on the keyboard now
    private string? _pendingKey;      // candidate waiting to settle
    private DateTime _pendingSince;
    private DateTime _lastWrite = DateTime.MinValue;
    private DateTime? _idleSince;
    private bool _restored = true;
    private string? _keyboardProblem; // shown instead of the track until fixed

    public bool Enabled { get; set; } = true;
    public bool SpotifyOnly { get => _media.SpotifyOnly; set { _media.SpotifyOnly = value; _shownKey = null; } }
    public bool ShowPaused { get; set { field = value; _shownKey = null; } } = true;
    public bool RestoreWhenIdle { get; set; } = true;
    public int IdleSeconds { get; set => field = Math.Clamp(value, MinIdleSeconds, MaxIdleSeconds); } = 60;
    /// <summary>A reason the card is left to another program (TrackDeck still running), or null.</summary>
    public Func<string?>? TakenBy { get; set; }

    public Action<string>? Log { get; set; }
    public string Status { get; private set; } = "Warming up";
    public event Action? StatusChanged;
    /// <summary>Something the user should hear about once (a keyboard problem), for a tray balloon.</summary>
    public event Action<string>? Notice;

    /// <summary>Write the card again on the next tick.</summary>
    public void Refresh() { _shownKey = null; _lastWrite = DateTime.MinValue; }

    /// <summary>
    /// The user switched screens with FN + arrows. Whatever they picked stays: the idle restore never undoes it
    /// (picking the card while paused used to flip straight back). On the card, it's brought up to date when music plays.
    /// </summary>
    public void OnScreenSwitched(ushort entryId)
    {
        _restored = true;
        if (entryId == ScreenFiles.CardConfig) Refresh();
    }

    public async Task TickAsync()
    {
        if (!Enabled) { SetStatus("Off"); return; }
        if (TakenBy?.Invoke() is { } taken) { SetStatus(taken); return; }
        if (!await _busy.WaitAsync(0)) return;
        try
        {
            NowPlaying? np = null;
            try { np = await _media.ReadAsync(); } catch (Exception e) { Log?.Invoke("media: " + e.Message); }

            // Idle = paused, or nothing playing at all. After the idle time, the user's own screen comes back
            // and stays until music plays again.
            bool idle = np == null || !np.IsPlaying;
            if (idle) _idleSince ??= DateTime.Now; else _idleSince = null;
            bool idleTimeUp = idle && RestoreWhenIdle && DateTime.Now - _idleSince >= TimeSpan.FromSeconds(IdleSeconds);

            if (idleTimeUp)
            {
                _pendingKey = null;
                if (!_restored && DateTime.Now - _lastWrite >= TimeSpan.FromSeconds(3))
                {
                    await RestoreCoreAsync();
                    _lastWrite = DateTime.Now;
                    Log?.Invoke($"idle for {IdleSeconds} s, restored your screen");
                }
                SetStatus(_keyboardProblem ?? (np == null ? "Nothing playing, showing your screen" : $"Paused: {np.Title} (showing your screen)"));
                return;
            }

            if (np == null)
            {
                SetStatus(SpotifyOnly ? "Spotify is quiet. Press play!" : "Nothing playing. Suspiciously quiet.");
                _pendingKey = null;
                return;
            }

            if (!ShowPaused) np = np with { IsPlaying = true };
            string key = np.Key;
            SetStatus(_keyboardProblem ?? $"{(np.IsPlaying ? "Playing" : "Paused")}: {np.Title} - {np.Artist}");
            if (key == _shownKey) { _pendingKey = null; return; }

            // Wait for the metadata to settle (title, then cover, often arrive separately),
            // and never write more often than every 3 seconds.
            if (key != _pendingKey) { _pendingKey = key; _pendingSince = DateTime.Now; return; }
            if (DateTime.Now - _pendingSince < TimeSpan.FromSeconds(1.5)) return;
            if (DateTime.Now - _lastWrite < TimeSpan.FromSeconds(3)) return;

            using var card = CardRenderer.Render(np);
            await screen.ShowAsync(card);
            _shownKey = key;
            _pendingKey = null;
            _lastWrite = DateTime.Now;
            _restored = false;
            if (_keyboardProblem != null) { _keyboardProblem = null; Log?.Invoke("keyboard connected again"); }
            SetStatus($"{(np.IsPlaying ? "Playing" : "Paused")}: {np.Title} - {np.Artist}");
            Log?.Invoke($"shown: {np.Title} - {np.Artist} ({(np.IsPlaying ? "playing" : "paused")})");
        }
        catch (Exception e)
        {
            string msg = e is BragiException ? e.Message : "Keyboard error: " + e.Message;
            if (msg != _keyboardProblem)
            {
                // Say it once (log + notification), not on every retry.
                Log?.Invoke(e is BragiException ? msg : "error: " + e);
                Notice?.Invoke(msg);
            }
            _keyboardProblem = msg;
            SetStatus(msg);
            _lastWrite = DateTime.Now.AddSeconds(7); // retry about every 10 seconds
        }
        finally { _busy.Release(); }
    }

    /// <summary>Puts the previous screen back. Returns null on success, or why it couldn't.</summary>
    public async Task<string?> RestoreAsync()
    {
        await _busy.WaitAsync();
        try { await RestoreCoreAsync(); return null; }
        catch (Exception e) { Log?.Invoke("restore error: " + e.Message); return e.Message; }
        finally { _busy.Release(); }
    }

    private async Task RestoreCoreAsync()
    {
        await screen.RestoreAsync();
        _restored = true;
        _shownKey = null;
    }

    private void SetStatus(string s)
    {
        if (s == Status) return;
        Status = s;
        StatusChanged?.Invoke();
    }
}
