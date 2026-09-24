# GRID0

One app for every platform: it sets up ZeroTier, joins the GRID0 network
(`8bd5124fd68185ec`), watches the connection with disconnect detection that
covers every case, and can point your Switch emulators at the ZeroTier
adapter for native LAN play.

Built with .NET 8 and Avalonia. One codebase, published self-contained for:

- Windows x64
- Linux x64
- macOS Intel (x64)
- macOS Apple Silicon (arm64)

## What it does

1. **Setup** — Installs ZeroTier if it is missing or dead (official
   installer on Windows/macOS, official install script on Linux), waits for
   the node to come online, and joins the GRID0 network.
2. **Monitor** — After connecting, it keeps watching. Every poll checks the
   full stack: internet reachability, the ZeroTier service, node online
   state, network membership, the OS adapter state, and the managed address
   on that adapter. Any disconnect updates the status dot instead of leaving
   a stale green behind:
   - Gray: working / disconnected (with Reconnect, no silent auto-rejoin)
   - Green: connected
   - Orange: waiting for network authorization
   - Red: error or offline, with the reason spelled out
3. **Configure emulators** — The button scans for running emulators
   (Ryujinx, Ryubing, Kenji-NX, Hyjinx, Eden, Sudachi, Citron NEO, Torzu,
   Suyu, yuzu; Astris is detected only) and edits their network settings to
   use the ZeroTier adapter, with `.bak` backups. Emulators must be closed
   first; the app asks you to close them. LAN mode only, no LDN.

## Build

Pushes to `main` build all four targets in GitHub Actions; download the
artifact for your platform. Or build locally with the .NET 8 SDK:

```bash
dotnet publish GRID0.csproj -c Release -r <rid> --self-contained true -p:PublishSingleFile=true
```

On Windows the app requests administrator rights (ZeroTier install and
service access need it). On Linux, run it with `sudo` if ZeroTier's CLI is
not readable by your user.

## Notes

- Joining the network is not enough: new members still need authorizing on
  the GRID0 network unless it is set to auto-authorize.
- macOS will show its own password prompt during the ZeroTier install.
- Unsigned macOS/Linux builds may need a right-click > Open on first run.
