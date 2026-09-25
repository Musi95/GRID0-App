# GRID0 client

one app for windows, linux and mac. installs zerotier, joins the GRID0 network,
and watches your connection so you actually know when it drops. also sets up
your switch emulators for LAN play.

## what it does

- **setup** installs zerotier if you don't have it, joins the GRID0 network
- **watch** keeps an eye on the connection and tells you why it broke instead
  of leaving a fake green dot behind
- **emulators** points your emulators at the zerotier adapter. close them
  first, it backs up your configs before touching anything

## get it

every push to main builds all four (windows, linux, intel mac, arm mac) — grab
yours from the actions tab. or build it yourself with the .NET 8 SDK:

```bash
dotnet publish GRID0.csproj -c Release -r <rid> --self-contained true
```

## stuff to know

- joining isn't enough, new members still need to get authorized on the network
- windows will ask for admin, mac will ask for your password. that's normal
- if your OS complains on first run it's because the build isn't signed —
  right-click > open gets around it
- LAN mode only, no LDN
