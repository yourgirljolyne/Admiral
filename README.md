# Admiral

**One background program for the Corsair VANGUARD AIR 99. No iCUE required.**

Admiral sits in the tray and does the work that has to keep running while you use the keyboard:

- **Now-playing card.** What's playing in Windows (Spotify first) on the keyboard screen: cover, title, artist, play/pause. Updated when the track changes, and your own screen comes back after a set idle time. This is what TrackDeck did; Admiral takes over its settings the first time it runs.
- **Custom keys in software mode.** SignalRGB puts the keyboard in software mode for clean live lighting, and in that mode the keyboard stops running your remaps and macros. Admiral plays the ones you made in DeckMapper from the PC, like DeckMapper's background key service did.
- **Screen switching in software mode.** Software mode also stops FN + arrows from switching screens, so the screen gets stuck on one picture. Admiral sees FN + arrows and steps through the profile's screen list itself (right/down forward, left/up back), the same list SlotDeck manages. A custom FN + arrow made in DeckMapper still wins.
- **The dial in software mode.** The keyboard stops running its dial modes too. Admiral plays the profile's dial modes from DeckMapper: volume, scrolling, zoom, switching apps, track control and custom turn/press actions. The keyboard doesn't say which mode it's on, so Admiral keeps the current mode: **FN + dial press** goes to the next one, and the tray menu shows it and can pick one.

In normal (self-operated) mode the keyboard does its keys and screens itself and Admiral only shows the card.

The editors stay separate windows: **DeckMapper** for keys, **SlotDeck** for screen slots. Admiral's tray menu opens them.

> Not affiliated with or endorsed by Corsair. Use at your own risk.

## With the other Deck apps

- **TrackDeck** isn't needed with Admiral. If TrackDeck is running, Admiral leaves the card to it (and says so in the menu); TrackDeck won't start while Admiral runs.
- **DeckMapper 1.2** hands its key service over to Admiral while Admiral runs, and tells Admiral to reload the keys after every Apply. An older DeckMapper with its service on keeps playing the keys itself, and Admiral steps back so no key is typed twice.
- The **SignalRGB plugin** can play the custom keys and the dial itself (its *Custom keys in software mode* setting). While that's on and SignalRGB runs, Admiral greys out its own keys switch and dial modes so nothing is typed twice, and keeps doing the card and FN + arrow screens. Everything else in the menu stays usable.
- Turning on **Start with Windows** in Admiral offers to stop TrackDeck and DeckMapper from starting with Windows too.

## Tray menu

The first three lines say what each part is doing, then the current dial mode. Then: open DeckMapper or SlotDeck, switch each part on or off, the card's options (Spotify only, paused state, restore when idle and after how long, refresh, restore now), Start with Windows, the log, and Exit (puts your own screen back).

## What it writes to the keyboard

- The card: its picture (file 32701), its entry (32700) and the resource map entry, all shared with TrackDeck.
- The active-screen file (62), to show the card, your screen again, or the next screen for FN + arrows.

Nothing else: no profiles, lighting, key assignments or screen lists. Keys are only read.

Settings and the log are in `%LOCALAPPDATA%\Admiral`.

## Build

```
cd Admiral
dotnet publish -c Release -r win-x64 -o dist -p:PublishSingleFile=true -p:SelfContained=false -p:DebugType=none
```

Needs the .NET 10 desktop runtime. `Admiral screen next` / `Admiral screen back` does one FN + arrow step from a console, for testing.

## Shared code

`Device\` (Bragi, Hid, ScreenFiles) and `Keys\` (DeckMapper's key engine and key store) are copies kept in step by hand with the other apps, as before. Admiral uses SlotDeck's image writer, which fixes the one-pixel shift TrackDeck's card had (TrackDeck copied Web Hub's offset bug).
