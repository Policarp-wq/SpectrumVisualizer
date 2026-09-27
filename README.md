# TrackPeek

A compact Windows music overlay written in C# and WPF. It reads the active Windows media session, shows track metadata and playback time, and draws a system-audio spectrum behind the controls.

## Build

Requires the .NET 9 SDK on Windows.

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

Settings are stored per user in `%LOCALAPPDATA%\TrackPeek\settings.json`. The first launch migrates a legacy `settings.json` beside the executable.

Right-click the overlay to open settings. The overlay can start with Windows using the current user's Startup folder; no administrator access is required.
