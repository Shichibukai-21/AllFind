# AllFind

A fast, glassy search for Windows 11. It finds apps and programs first and can search the whole PC.

- Hover or click the pinned AllFind icon on the taskbar and it turns into a search box you type straight into.
- Alt+Space opens it from anywhere.
- Right-click any result for Open, Run as administrator, Open file location, Pin to Start, Uninstall, Properties or the full Windows menu.
- Uses about 10–30 MB of memory when idle.

## Install

1. Download **AllFind.exe** from the [latest release](https://github.com/Shichibukai-21/AllFind/releases/latest).
2. Run it. If Windows says "Windows protected your PC", click **More info**, then **Run anyway** (the app isn't code-signed).
3. AllFind installs itself for your user (no admin needed), adds itself to the Start menu and opens a short setup.
4. To use it from the taskbar: open Start, right-click **AllFind** > **Pin to taskbar**, and drag it next to Start.

AllFind updates itself automatically. To remove it, go to Settings > Apps > Installed apps > AllFind > Uninstall.

## Build

Needs only Windows (it uses the C# compiler that ships with .NET Framework 4.8):

```
powershell -ExecutionPolicy Bypass -File src\build.ps1
```

The program is written to `AllFind\AllFind.exe`. Run it with `--portable` to test it without installing.

## Release an update

```
powershell -ExecutionPolicy Bypass -File src\release.ps1 1.0.1 "What changed"
```

This builds, commits, tags, pushes and publishes a GitHub release with the exe. Installed copies check for a new
release 45 seconds after starting and every 6 hours (or right away from the tray menu > Check for updates),
then download it, swap it in and restart quietly once the search panel is closed.
