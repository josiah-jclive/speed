# JSPEED

Record control for live production, by JC Live. One button rolls every recorder in the group,
JSPEED checks each one actually started, and a status graphic goes out through a Blackmagic
UltraStudio so the room can see what's recording.

Runs on Windows (.exe) and macOS (.dmg) from one C# codebase built with [Avalonia](https://avaloniaui.net).

## What it controls

| Recorder | Connection | Record/stop | Timecode | Media left | Format |
| --- | --- | --- | --- | --- | --- |
| Atomos Shogun (Studio II and others) | USB to RS-422 adapter, Sony 9-pin | Yes | Yes | No (not in the protocol) | No (format on the unit) |
| Blackmagic HyperDeck | Ethernet, TCP 9993 | Yes | Yes | Time left per slot | Yes (exFAT or HFS+) |
| AJA Ki Pro | Ethernet, HTTP | Yes, if the unit lists the commands | Yes | Percent free | No (format from its web page) |

## Features

- **Record all / Stop all** with an arm switch (F7, F9, F8). Two seconds after Record all, JSPEED checks
  every recorder and flags any that didn't start, in amber, with the reason.
- **Program output** to an UltraStudio or DeckLink: event name, date, time of day, take length, and a row
  or tile per recorder that fills red while recording. Lays itself out for 1 to 12+ recorders.
- **Media warnings** below a time or percentage you set.
- **Format media** in three steps: choose the drive, type its name to confirm, then press and hold for
  3 seconds. HyperDecks add their own prepare/confirm token on top.
- **Record-dot app icon** that follows the group: white dot when ready, red while recording, amber when something needs attention.
  It changes in the Windows taskbar while JSPEED runs; on macOS the Dock icon stays fixed.
- **Activity log**, also written to a daily file.

Settings and logs live in `%LOCALAPPDATA%\JSPEED` on Windows and `~/Library/Application Support/JSPEED` on macOS.

## Building

### Automatically, with GitHub Actions

Every push runs `.github/workflows/build.yml`, which builds:

- **JSPEED-Windows**: `JSPEED.exe` plus `speed_decklink.dll`. Keep the two files together.
- **JSPEED-macOS-osx-arm64**: `.dmg` for Apple silicon Macs.
- **JSPEED-macOS-osx-x64**: `.dmg` for Intel Macs.

Download them from the run's **Artifacts** section on the repository's Actions page.

The repository holds only text. The first build job runs `tools/prepare_assets.py`, which downloads the
DeckLink SDK headers and fonts (pinned to exact commits) and draws the icons and title-bar logo.

### By hand

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download), CMake and Python 3.10+. First fetch and
generate the assets:

```bash
pip install pillow cairosvg
python tools/prepare_assets.py
```

**Windows** (in a *Developer PowerShell for Visual Studio*, so `midl` and `cl` are available):

```powershell
cmake -S native/speed_decklink -B native/build -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build native/build
dotnet publish src/Speed/Speed.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o out/JSPEED
Copy-Item native/build/speed_decklink.dll out/JSPEED/
```

**macOS:**

```bash
cmake -S native/speed_decklink -B native/build -DCMAKE_BUILD_TYPE=Release
cmake --build native/build
packaging/mac/build-dmg.sh osx-arm64     # or osx-x64 for Intel
```

To run from source while developing: `dotnet run --project src/Speed`.
Without the bridge library, everything works except UltraStudio output, which says why in the app.

## Setup on each machine

- **UltraStudio output:** install Blackmagic Desktop Video. JSPEED uses the DeckLink SDK 12.0 interface,
  which current Desktop Video drivers still support.
- **Shoguns:** an FTDI-based USB to RS-422 adapter needs no driver on macOS or Windows 10/11. Use a cable
  wired as the controller side of Sony 9-pin (TX and RX pairs crossed relative to a deck-to-deck cable),
  and turn on RS-422 remote control in AtomOS. On a Mac, pick the `cu.usbserial-…` port.
- **HyperDecks:** turn on remote control (Setup menu, or JSPEED sends `remote: enable: true` when it connects).
- **Ki Pros:** same network as the computer; nothing to enable beyond its web interface.

## Signing

The builds are ad-hoc signed only.

- **macOS** shows "unidentified developer" the first time. Open System Settings › Privacy & Security and
  choose **Open Anyway**. To avoid this for other people, sign with a Developer ID certificate
  ($99/year Apple Developer Program) and notarize with `xcrun notarytool`.
- **Windows** SmartScreen may warn on first launch. Choose **More info › Run anyway**, or sign the exe and
  dll with a code-signing certificate.

## First hardware test

JSPEED was written without access to the hardware, so check these on the bench before a show:

1. Each recorder connects, and its timecode and state on the card match the unit.
2. Record all starts every recorder; Stop all stops them. Pull one recorder's network or serial cable
   during a take and confirm it turns amber.
3. HyperDeck media time matches the front panel; Ki Pro percent matches its web page.
4. **Ki Pro:** confirm the activity log doesn't say it couldn't find Record and Stop. Models label their
   commands differently; if yours isn't found, send me the output of `http://<ki-pro-ip>/descriptors`.
5. **Format:** try it on a HyperDeck with a scratch drive first.
6. UltraStudio: start output in each format you use and watch the status for dropped frames.

## Layout

```
tools/                prepare_assets.py: fetches SDK headers and fonts, draws icons and logo
src/Speed/            the app (Avalonia, C#)
  Core/               settings, recorder base, group status, log
  Devices/            Shogun (Sony 9-pin), HyperDeck (TCP), Ki Pro (HTTP)
  Output/             program output graphic and DeckLink interop
  Views/              windows and the press-and-hold button
native/speed_decklink/ C++ bridge to the DeckLink SDK (Windows COM, macOS CFPlugin)
packaging/mac/        Info.plist, icon, .dmg script
```

## Licences

- DeckLink SDK headers: Blackmagic Design, Boost Software License 1.0 (`native/speed_decklink/sdk/LICENSE.txt`).
- Geist and Poppins fonts: SIL Open Font License (`src/Speed/Assets/Fonts`).
