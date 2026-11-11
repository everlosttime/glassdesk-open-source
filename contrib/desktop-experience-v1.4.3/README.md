# Desktop experience feature patch

This contribution keeps the custom desktop-experience work separate from the
current `work/liquid-glass` history, which has a different source baseline.

`desktop-experience.patch` contains only the 37 feature-related files from the
validated v1.4.3 integration build. It intentionally excludes release output,
user data, images, evidence archives, and unrelated project history.

## Included capabilities

- Desktop status bar with system, battery, network, volume, CPU, memory, and
  optional fan telemetry.
- Glass control center, Wi-Fi connection actions, Bluetooth listing, calendar
  panel, and notification entry point.
- Draggable, compact media capsule with media-session recovery and glass-frame
  handoff while panels move.
- Launchpad and dock integration, fullscreen handling, and liquid-glass frame
  synchronization updates.
- The v1.4.3-style upward conceal/reveal animation for the status bar.

## Integration note

The patch was generated against the collaboration v1.4.3 source baseline, not
the current `work/liquid-glass` branch. That branch has a different commit
history and window architecture, so applying this patch may require resolving
host-integration differences in `AppHost`, Dock, Launcher, Platform, and UI.

## Validation of the source patch

The source build used to generate this patch passed:

```powershell
dotnet build src/GlassDesk.App/GlassDesk.App.csproj -c Release --no-restore
```

with zero warnings and zero errors.
