# ModRelay

ModRelay is a lightweight Windows tray app that watches FFXIV mod downloads, optionally upgrades older mod packs with TexTools, and relays them to Penumbra.

## Features

- watches one or more download folders
- supports `.ttmp`, `.ttmp2`, `.pmp`, `.pcp`, `.zip`, `.7z`, and `.rar`
- recursively finds mod packages in archives and offers a checkbox multi-selection, or extracts all automatically
- hands every selected package to Penumbra's native importer instead of modifying its mod directory
- runs older mod packs through `ConsoleTools.exe /upgrade`
- never silently installs the original when a required upgrade fails
- keeps a persistent queue while Penumbra or the game is unavailable
- optional notifications, automatic cleanup, and Windows startup
- a persistent in-settings update banner for official GitHub releases, with a safe link to the release page
- optional per-user file associations for supported mod files
- no telemetry, ads, plugin system, tutorial loop, or built-in updater

## Setup

1. Start `ModRelay.exe`. The settings window opens on first launch.
2. Check the watched download folders.
3. In Penumbra, enable **HTTP API** under **Settings → Advanced**. The default port is `42069`.
4. For Dawntrail upgrades, install [FFXIV TexTools](https://github.com/TexTools/FFXIV_TexTools_UI/releases). ModRelay detects `ConsoleTools.exe` automatically, or you can select it manually.
5. Changes are saved automatically. Close the window with **X**; ModRelay continues running in the Windows notification area.

Double-click the tray icon to open settings. Closing the window with **X** keeps ModRelay in the tray and shows a reminder. The dark tray menu provides settings, manual import, pause, a Penumbra connection check, logs, resources, and exit actions.

## Why ModRelay?

[Penumbra Mod Forwarder](https://github.com/Sebane1/PenumbraModForwarder) demonstrated how useful a small hand-off tool can be, while [Atomos](https://github.com/CouncilOfTsukuyomi/Atomos) explored a broader automated download workflow. ModRelay was inspired by both ideas, but was built independently to combine the parts needed for this workflow in one focused app:

- a truly portable folder with settings stored beside the executable
- confirmed imports through Penumbra's supported HTTP API, with safe retry behavior
- optional TexTools upgrades for older mod packs before import
- nested multi-mod archive discovery with an explicit selection step
- clear success and failure feedback that remains visible while a game is running
- a compact, mixed-DPI-friendly Windows interface without telemetry or account setup

ModRelay is an independent project and is not affiliated with or endorsed by Penumbra Mod Forwarder, Atomos, Penumbra, or TexTools. No source code or assets from the two forwarding tools are included.

## Safe defaults

- Existing files in a download folder are **not** imported retroactively on first launch.
- If Penumbra is unavailable, the mod remains in a persistent retry queue.
- Import failures and unconfirmed imports always show a notification and keep the source file, even when success notifications are disabled.
- If TexTools fails, ModRelay asks before sending an unchanged original to Penumbra.
- Files produced by ModRelay are ignored by its own watcher, preventing duplicate imports and `_dt_dt` conversion loops.
- Portable settings are stored as `settings.json` next to `ModRelay.exe`.
- Logs and the pending retry queue are stored in the adjacent `data` folder.
- Personal settings and runtime data are excluded from source control and release archives.
- Release builds embed their official repository and version; development builds never query an unknown update server.

## Development

ModRelay requires the .NET 9 SDK and Windows.

```powershell
dotnet build ModRelay.sln
dotnet test ModRelay.sln
dotnet publish src\ModRelay.App\ModRelay.App.csproj -c Release -r win-x64 --self-contained true
```

The published Windows x64 build is self-contained and does not require a separate .NET runtime installation. Extract the `ModRelay` folder anywhere writable and run `ModRelay.exe`; `settings.json` is created inside that folder on first launch.

## License

MIT — see [LICENSE](LICENSE). Distributed dependencies and runtime notices are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
