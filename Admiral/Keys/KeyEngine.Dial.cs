namespace Admiral;

/// <summary>
/// The volume dial in software mode. The keyboard stops running its dial modes then and reports the dial instead:
/// turns as their own notification (type 5, id 0x60, a signed click count), the press as key 129 in the key report.
/// The keyboard doesn't say which of the profile's dial modes is current, and doesn't switch modes itself in software
/// mode, so Admiral keeps the current mode: FN + dial press goes to the next one, and the tray menu can pick one.
/// Built-in modes are played the way they behave on the keyboard; custom modes play their DeckMapper actions.
/// </summary>
internal sealed partial class KeyEngine
{
    private const byte DialReport = 5, DialTurnId = 0x60, DialPressKey = 129;

    // HID usages
    private const ushort LeftCtrl = 224, LeftShift = 225, LeftAlt = 226, Tab = 43, Zero = 39, ArrowRight = 79, ArrowLeft = 80;
    // Consumer usages
    private const ushort VolumeUp = 233, VolumeDown = 234, Mute = 226, NextTrack = 181, PreviousTrack = 182, PlayPause = 205;

    private List<DialMode> _dial = [];
    private int _dialMode;
    private bool _altHeld;
    private CancellationTokenSource? _altRelease;

    /// <summary>Raised when the current dial mode changes (FN + dial press, or a new set of modes from the keyboard).</summary>
    public event Action? DialModeChanged;

    /// <summary>Names of the dial modes, replaced (never changed) on each load, so the tray reads them without a lock.</summary>
    private volatile string[] _dialNames = [];

    public int DialModeCount => _dialNames.Length;

    /// <summary>The current dial mode, 0-based. Kept in range when the modes change. Reading it never waits.</summary>
    public int DialModeIndex
    {
        get => Volatile.Read(ref _dialMode);
        set
        {
            lock (_gate)
            {
                // Before the modes are loaded (at start-up), keep the saved mode; SetDial checks it fits.
                int v = _dial.Count == 0 ? Math.Max(0, value) : Math.Clamp(value, 0, _dial.Count - 1);
                if (v == _dialMode) return;
                _dialMode = v;
                EndSwitchApps();
            }
            Log?.Invoke($"dial: mode {value + 1}");
            DialModeChanged?.Invoke();
        }
    }

    public string DialModeName(int i) => _dialNames is var n && i < n.Length ? n[i] : "Volume";

    /// <summary>Called with the gate held, after loading the profile.</summary>
    private void SetDial(List<DialMode> modes)
    {
        _dial = modes;
        _dialNames = modes.Select(m => DialNames.Name(m.Type)).ToArray();
        if (_dialMode >= modes.Count) _dialMode = 0;
        DialModeChanged?.Invoke();
    }

    private DialMode? Current => _dialMode < _dial.Count ? _dial[_dialMode] : null;

    // ---------------- turns ----------------

    private void OnDialTurn(int clicks)
    {
        if (clicks == 0 || Math.Abs(clicks) > 64) return; // nothing, or not a click count
        bool right = clicks > 0;
        for (int i = 0; i < Math.Abs(clicks); i++) Turn(right);
    }

    private void Turn(bool right)
    {
        var mode = Current;
        switch (mode?.Type ?? 13)
        {
            case 13: TapMedia(right ? VolumeUp : VolumeDown); break;                        // Volume
            case 4: InputSender.Scroll(right ? -1 : 1, sideways: false); break;              // Scroll up / down: right scrolls down
            case 5: InputSender.Scroll(right ? 1 : -1, sideways: true); break;               // Scroll left / right
            case 7 or 12: Chord([LeftCtrl], () => InputSender.Scroll(right ? 1 : -1, false)); break; // Zoom: Ctrl + wheel
            case 6 or 11: SwitchApps(right); break;                                          // Switch apps
            case 3 or 9 or 10: TapKey(right ? ArrowRight : ArrowLeft); break;                // Seek in track
            case 8: TapMedia(right ? NextTrack : PreviousTrack); break;                     // Change track
            case DialNames.Custom: Custom(mode!, right ? DialNames.TurnRight : DialNames.TurnLeft); break;
            // Lighting brightness belongs to SignalRGB in software mode; screen brightness and the rest: nothing to send.
        }
    }

    // ---------------- press ----------------

    private void OnDialPress(bool[] now)
    {
        if (now[FnKey])
        {
            int count = _dial.Count;
            if (count > 1)
            {
                _dialMode = (_dialMode + 1) % count;
                EndSwitchApps();
                Log?.Invoke($"dial: mode {_dialMode + 1} of {count} ({DialNames.Name(_dial[_dialMode].Type)})");
                Task.Run(() => DialModeChanged?.Invoke());
            }
            return;
        }

        var mode = Current;
        KeyAction? action = (mode?.Type ?? 13) switch
        {
            13 => KeyAction.Media(Mute),
            3 or 8 or 9 or 10 => KeyAction.Media(PlayPause),
            7 or 12 => KeyAction.Key(LeftCtrl, Zero),                       // back to 100 %
            DialNames.Custom => mode!.Actions.FirstOrDefault(a => a.Trigger == DialNames.Press)?.Action,
            _ => null,
        };
        if ((mode?.Type ?? 13) is 6 or 11 && _altHeld) { EndSwitchApps(); return; } // pick the app
        if (action != null && Begin(action) is { } held) _held[DialPressKey] = held;
    }

    // ---------------- helpers ----------------

    private void Custom(DialMode mode, byte trigger)
    {
        if (mode.Actions.FirstOrDefault(a => a.Trigger == trigger)?.Action is not { } action) return;
        if (action.Kind == ActionKind.Macro && action.Macro is { } m) { _ = PlayMacroAsync(m, CancellationToken.None); return; }
        if (Begin(action) is { } h) End(h); // a click is a tap: press and let go
    }

    private static void TapMedia(ushort usage)
    {
        InputSender.Media(usage, up: false);
        InputSender.Media(usage, up: true);
    }

    private static void TapKey(ushort hid)
    {
        InputSender.KeyDown(hid);
        InputSender.KeyUp(hid);
    }

    private static void Chord(ushort[] modifiers, Action inner)
    {
        foreach (var k in modifiers) InputSender.KeyDown(k);
        inner();
        for (int i = modifiers.Length - 1; i >= 0; i--) InputSender.KeyUp(modifiers[i]);
    }

    /// <summary>Alt + Tab: Alt stays down while turning, and lets go a second after the last click (or on a press).</summary>
    private void SwitchApps(bool right)
    {
        if (!_altHeld) { InputSender.KeyDown(LeftAlt); _altHeld = true; }
        if (!right) InputSender.KeyDown(LeftShift);
        TapKey(Tab);
        if (!right) InputSender.KeyUp(LeftShift);

        _altRelease?.Cancel();
        var cts = _altRelease = new CancellationTokenSource();
        _ = Task.Delay(1000, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            lock (_gate) if (_altRelease == cts) EndSwitchApps();
        }, TaskScheduler.Default);
    }

    /// <summary>Lets go of Alt if switching apps held it. Called with the gate held.</summary>
    private void EndSwitchApps()
    {
        _altRelease?.Cancel();
        _altRelease = null;
        if (!_altHeld) return;
        _altHeld = false;
        InputSender.KeyUp(LeftAlt);
    }
}
