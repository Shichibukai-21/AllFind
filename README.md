# AllFind

A fast, glassy search for Windows 11. It finds apps and programs first and can search the whole PC.

- Hover or click the pinned AllFind icon on the taskbar and it turns into a search box you type straight into.
- Alt+Space opens it from anywhere.
- Right-click any result for Open, Run as administrator, Open file location, Pin to Start, Uninstall, Properties or the full Windows menu.
- Uses about 10–30 MB of memory when idle.

## Build

Needs only Windows (it uses the C# compiler that ships with .NET Framework 4.8):

```
powershell -ExecutionPolicy Bypass -File src\build.ps1
```

The program is written to `AllFind\AllFind.exe`.
