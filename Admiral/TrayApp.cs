using System.Drawing.Drawing2D;

namespace Admiral;

/// <summary>
/// The one background program for the VANGUARD AIR 99, with one tray icon. It runs:
/// - the key engine: DeckMapper's custom keys while the keyboard is in software mode (SignalRGB);
/// - the screen switcher: FN + arrows walk the screen list in software mode, where the firmware stops doing it;
/// - the tracker: the now-playing card (what TrackDeck did).
/// DeckMapper and SlotDeck stay the windows where things are edited.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly Settings _settings = Settings.Load();
    private readonly ScreenController _screen = new() { Log = AppLog.Write };
    private readonly KeyEngine _engine = new() { Log = AppLog.Write };
    private readonly ScreenSwitcher _switcher;
    private readonly Tracker _tracker;
    private readonly SynchronizationContext _ui;

    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };

    private readonly ToolStripMenuItem _keysStatus = new() { Enabled = false };
    private readonly ToolStripMenuItem _screenStatus = new() { Enabled = false };
    private readonly ToolStripMenuItem _cardStatus = new() { Enabled = false };
    private readonly ToolStripMenuItem _dialMenu = new("Dial");
    private readonly ToolStripMenuItem _keysItem = new("Play my custom keys in software mode") { CheckOnClick = true };
    private readonly ToolStripMenuItem _screensItem = new("Switch screens with FN + arrows in software mode") { CheckOnClick = true };
    private readonly ToolStripMenuItem _cardItem = new("Now-playing card") { CheckOnClick = true };
    private readonly ToolStripMenuItem _cardMenu = new("Now-playing card options");
    private readonly ToolStripMenuItem _spotifyOnlyItem = new("Spotify only") { CheckOnClick = true };
    private readonly ToolStripMenuItem _showPausedItem = new("Show paused state") { CheckOnClick = true };
    private readonly ToolStripMenuItem _restoreIdleItem = new("Restore my screen when idle") { CheckOnClick = true };
    private readonly ToolStripMenuItem _idleTimeItem = new();
    private readonly ToolStripMenuItem _startupItem = new("Start with Windows") { CheckOnClick = true };

    private bool _exiting;

    public TrayApp()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _switcher = new ScreenSwitcher(_screen) { Log = AppLog.Write };
        _tracker = new Tracker(_screen)
        {
            Log = AppLog.Write,
            TakenBy = Companions.CardTakenBy,
            Enabled = _settings.NowPlaying,
            SpotifyOnly = _settings.SpotifyOnly,
            ShowPaused = _settings.ShowPaused,
            RestoreWhenIdle = _settings.RestoreWhenIdle,
            IdleSeconds = _settings.IdleSeconds,
        };
        _engine.PlayKeys = _settings.PlayKeys;
        _engine.SwitchScreens = _settings.SwitchScreens;
        _engine.KeysTakenBy = Companions.KeysTakenBy;
        _engine.DialModeIndex = _settings.DialMode;
        _engine.DialModeChanged += () => _ui.Post(_ =>
        {
            if (_settings.DialMode != _engine.DialModeIndex) { _settings.DialMode = _engine.DialModeIndex; _settings.Save(); }
            UpdateDialMenu();
        }, null);
        _engine.ScreenStep += dir => Task.Run(() => _switcher.StepAsync(dir));
        _switcher.Switched += id => _tracker.OnScreenSwitched(id);

        _tray = new NotifyIcon { Icon = MakeIcon(), Text = "Admiral", Visible = true, ContextMenuStrip = BuildMenu() };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowMenu(); };
        _tracker.Notice += msg => _ui.Post(_ => _tray.ShowBalloonTip(5000, "Admiral", msg, ToolTipIcon.Info), null);
        foreach (var src in new Action<Action>[] { a => _engine.StatusChanged += a, a => _switcher.StatusChanged += a, a => _tracker.StatusChanged += a })
            src(() => _ui.Post(_ => UpdateStatus(), null));
        UpdateStatus();

        ListenFor(Companions.ReloadKeysEvent, () => { AppLog.Write("DeckMapper wrote new keys, reading them again"); _engine.Reload(); });
        ListenFor(Companions.ShowMenuEvent, () => _ui.Post(_ => ShowMenu(), null));

        _engine.Start();
        _timer.Tick += async (_, _) => await _tracker.TickAsync();
        _timer.Start();
        AppLog.Write("started");
    }

    private ContextMenuStrip BuildMenu()
    {
        _keysItem.Checked = _settings.PlayKeys;
        _screensItem.Checked = _settings.SwitchScreens;
        _cardItem.Checked = _settings.NowPlaying;
        _spotifyOnlyItem.Checked = _settings.SpotifyOnly;
        _showPausedItem.Checked = _settings.ShowPaused;
        _restoreIdleItem.Checked = _settings.RestoreWhenIdle;
        _startupItem.Checked = Settings.StartWithWindows;
        UpdateIdleTimeItem();

        _keysItem.CheckedChanged += (_, _) => { _engine.PlayKeys = _settings.PlayKeys = _keysItem.Checked; _settings.Save(); };
        _screensItem.CheckedChanged += (_, _) => { _engine.SwitchScreens = _settings.SwitchScreens = _screensItem.Checked; _settings.Save(); UpdateStatus(); };
        _cardItem.CheckedChanged += async (_, _) => await SetCardAsync(_cardItem.Checked);
        _spotifyOnlyItem.CheckedChanged += (_, _) => { _tracker.SpotifyOnly = _settings.SpotifyOnly = _spotifyOnlyItem.Checked; _settings.Save(); };
        _showPausedItem.CheckedChanged += (_, _) => { _tracker.ShowPaused = _settings.ShowPaused = _showPausedItem.Checked; _settings.Save(); };
        _restoreIdleItem.CheckedChanged += (_, _) =>
        {
            _tracker.RestoreWhenIdle = _settings.RestoreWhenIdle = _restoreIdleItem.Checked;
            _settings.Save();
            UpdateIdleTimeItem();
        };
        _idleTimeItem.Click += (_, _) => AskIdleSeconds();
        _startupItem.CheckedChanged += (_, _) => SetStartup(_startupItem.Checked);

        _cardMenu.DropDownItems.Add("Refresh card now", null, (_, _) => _tracker.Refresh());
        _cardMenu.DropDownItems.Add("Restore my screen", null, async (_, _) =>
        {
            var error = await _tracker.RestoreAsync();
            _tray.ShowBalloonTip(4000, "Admiral", error == null ? "Your screen is back." : "Couldn't restore your screen: " + error, ToolTipIcon.Info);
        });
        _cardMenu.DropDownItems.Add(new ToolStripSeparator());
        _cardMenu.DropDownItems.AddRange([_spotifyOnlyItem, _showPausedItem, _restoreIdleItem, _idleTimeItem]);

        var menu = new ContextMenuStrip();
        _dialMenu.DropDownOpening += (_, _) => UpdateDialMenu();
        UpdateDialMenu();
        menu.Items.AddRange([_keysStatus, _screenStatus, _cardStatus, _dialMenu, new ToolStripSeparator()]);
        menu.Items.Add("Open DeckMapper (keys)", null, (_, _) => Companions.Open("DeckMapper", _settings, null));
        menu.Items.Add("Open SlotDeck (screen slots)", null, (_, _) => Companions.Open("SlotDeck", _settings, null));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.AddRange([_keysItem, _screensItem, _cardItem, _cardMenu, new ToolStripSeparator(), _startupItem]);
        menu.Items.Add("Open log", null, (_, _) => { try { System.Diagnostics.Process.Start("notepad.exe", AppPaths.LogFile); } catch { } });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit (restore my screen)", null, async (_, _) => await ExitAsync());
        return menu;
    }

    /// <summary>"Dial: mode 2 of 3 (Custom actions)", with the modes to pick from (FN + dial press does the same).</summary>
    private void UpdateDialMenu()
    {
        int count = _engine.DialModeCount, current = _engine.DialModeIndex;
        _dialMenu.Text = count == 0 ? "Dial: Volume" : $"Dial: mode {current + 1} of {count} ({_engine.DialModeName(current)})";
        _dialMenu.DropDownItems.Clear();
        for (int i = 0; i < count; i++)
        {
            int mode = i;
            _dialMenu.DropDownItems.Add(new ToolStripMenuItem($"Mode {i + 1}: {_engine.DialModeName(i)}", null,
                (_, _) => _engine.DialModeIndex = mode) { Checked = i == current });
        }
        if (count > 0) _dialMenu.DropDownItems.Add(new ToolStripSeparator());
        _dialMenu.DropDownItems.Add(new ToolStripMenuItem("In software mode, FN + dial press goes to the next mode") { Enabled = false });
    }

    private void ShowMenu()
    {
        // NotifyIcon only opens its menu on right-click; this is its own (private) way of doing it.
        typeof(NotifyIcon).GetMethod("ShowContextMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.Invoke(_tray, null);
    }

    private void UpdateStatus()
    {
        _keysStatus.Text = "Keys: " + Short(_engine.Status);
        _screenStatus.Text = "Screen: " + Short(!_settings.SwitchScreens ? "FN + arrows off in Admiral"
            : _engine.Active ? _switcher.Status : "The keyboard switches screens itself");
        _cardStatus.Text = "Card: " + Short(_tracker.Status);
        string tip = _engine.Active ? "Admiral: software mode, keys and screen from the PC" : "Admiral: " + _tracker.Status;
        _tray.Text = tip.Length > 63 ? tip[..62] + "…" : tip;
    }

    private static string Short(string s) => s.Length > 70 ? s[..69] + "…" : s;

    private async Task SetCardAsync(bool on)
    {
        _settings.NowPlaying = on;
        _settings.Save();
        _tracker.Enabled = on;
        if (on) { _tracker.Refresh(); return; }
        // Turning the card off puts the user's screen back (only if the card is what's showing).
        if (Companions.CardTakenBy() == null) await _tracker.RestoreAsync();
    }

    private void SetStartup(bool on)
    {
        Settings.StartWithWindows = on;
        if (!on) return;
        var others = Settings.OtherStartups();
        if (others.Count == 0) return;
        var names = string.Join(" and ", others.Select(o => o.Name));
        if (MessageBox.Show(
                $"{names} also start{(others.Count == 1 ? "s" : "")} with Windows, for the background work Admiral does now " +
                $"(the card and the custom keys in software mode).\n\nStop starting {names} with Windows? You can still open them any time.",
                "Admiral", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        foreach (var (name, cmd) in others)
        {
            Settings.RemoveStartup(name);
            AppLog.Write($"removed {name} from start with Windows (was {cmd})");
        }
    }

    private void UpdateIdleTimeItem()
    {
        _idleTimeItem.Text = $"    after {_settings.IdleSeconds} seconds…";
        _idleTimeItem.Enabled = _settings.RestoreWhenIdle;
    }

    private void AskIdleSeconds()
    {
        using var form = new Form
        {
            Text = "Admiral", FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterScreen,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, TopMost = true,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12),
        };
        var label = new Label { Text = "Put my screen back after music has been paused\nor stopped for this many seconds:", AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
        var number = new NumericUpDown { Minimum = Tracker.MinIdleSeconds, Maximum = Tracker.MaxIdleSeconds, Value = Math.Clamp(_settings.IdleSeconds, Tracker.MinIdleSeconds, Tracker.MaxIdleSeconds), Width = 100 };
        var unit = new Label { Text = "seconds", AutoSize = true, Margin = new Padding(6, 6, 0, 0) };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 0, 0, 12) };
        row.Controls.AddRange([number, unit]);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        buttons.Controls.AddRange([cancel, ok]);
        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1 };
        layout.Controls.AddRange([label, row, buttons]);
        form.Controls.Add(layout);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        form.Shown += (_, _) => { number.Select(); number.Select(0, number.Text.Length); };
        if (form.ShowDialog() != DialogResult.OK) return;
        _tracker.IdleSeconds = _settings.IdleSeconds = (int)number.Value;
        _settings.Save();
        UpdateIdleTimeItem();
        AppLog.Write($"idle time set to {_settings.IdleSeconds} s");
    }

    private void ListenFor(string name, Action onSignal)
    {
        var ev = new EventWaitHandle(false, EventResetMode.AutoReset, name);
        ThreadPool.RegisterWaitForSingleObject(ev, (_, _) => onSignal(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _timer.Stop();
        AppLog.Write("exiting");
        await _engine.StopAsync();
        _engine.Dispose();

        // Put the user's screen back, but never let a stuck keyboard keep Admiral alive.
        string? error = null;
        if (_settings.NowPlaying && Companions.CardTakenBy() == null)
        {
            var restore = _tracker.RestoreAsync();
            error = await Task.WhenAny(restore, Task.Delay(15000)) == restore ? await restore : "the keyboard didn't answer in time.";
        }
        await _screen.CloseAsync();

        _tray.Visible = false;
        _tray.Dispose();
        if (error != null)
            MessageBox.Show($"Admiral closed, but couldn't put your screen back:\n{error}\n\n" +
                "Plug in the USB cable, start Admiral again and choose Now-playing card options > Restore my screen.",
                "Admiral", MessageBoxButtons.OK, MessageBoxIcon.Information);
        AppLog.Write("exited");
        ExitThread();
    }

    /// <summary>A navy badge with a gold chevron (drawn here, so no icon file is needed).</summary>
    public static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using (var navy = new SolidBrush(Color.FromArgb(24, 42, 82))) g.FillEllipse(navy, 1, 1, 30, 30);
            using var gold = new Pen(Color.FromArgb(240, 192, 64), 4) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLines(gold, [new PointF(8, 19), new PointF(16, 11), new PointF(24, 19)]);
            g.DrawLines(gold, [new PointF(10, 25), new PointF(16, 19), new PointF(22, 25)]);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
