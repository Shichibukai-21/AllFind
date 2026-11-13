# HANDOFF — AllFind (Windows search replacement)

Folder: this session's app-made folder (not a git repo, no branch, no PRs). It is deleted with the session.

## Goal
Fast, glassy Windows search app (apps/exe first, whole-PC on demand) that opens like Start
from the taskbar and costs no more than Windows Search.

## Done (all built, installed and checked on the user's PC)
- `src/AllFind.cs`: index, search engine, glass UI, Start-style slide, first-run setup, tray menu.
  Checked: search 2–26 ms, screenshots of panel and setup.
- `src/Taskbar.cs`: settings (`Cfg`), taskbar search bar after the apps, instant-click layer over the
  pinned icon. Checked: bar follows apps opening/closing; click opens panel in ~13 ms; taskbar UIA tree intact.
- `src/Logo.cs`, `src/IconGen.cs`: mirrored glass logo, sharp multi-size `app.ico`. Checked visually.
- Memory/CPU: idle 8–30 MB working set, ~0 CPU (Windows SearchHost ~140 MB + indexer ~53 MB).
- Installed: `%LOCALAPPDATA%\Programs\AllFind\AllFind.exe`, Start menu shortcut, HKCU Run (`--background`).
  Data and settings in `%LOCALAPPDATA%\AllFind`.

## 2026-11-13 polish (built, installed, checked by screenshots + scripted mouse/keyboard)
- Taskbar (icon mode): hovering the pinned AllFind icon ~0.26 s, or clicking it, grows it into a solid "Type to search"
  pill (300 DIP, covers the next icons while open) and opens the panel with focus; typing is mirrored in the pill and the
  panel's own search row is hidden in that mode. Closing the panel shrinks the pill back. Clicks alternate open/close;
  a click within 0.7 s of a hover-open keeps it open. No hover-open when the icon moves under a still cursor.
- Focus: `ForceForeground` (SetForegroundWindow -> AttachThreadInput -> Alt tap fallback). First open 69 ms, later 15 ms.
- Panel: 760x560 (was 900x620), compact search row, chips, rows, preview (232 wide, hidden when nothing is selected),
  ~15% more see-through glass.
- Right-click (mouse, Apps key, Shift+F10) on rows and top-app tiles: Open, Run as admin, Open with, Open file location,
  Terminal, Copy path, Copy, Properties, plus Windows' own Pin to Start / Uninstall for apps (read from the shell menu by
  English text) and "Show more options" = the full Windows menu (dark via uxtheme #135/#136).
- Fixes: Ctrl+Enter/admin on Store apps no longer launches the app; right-click on the expanded pill no longer opens a
  neighbour's jump list; Recent detects folders with dots.
- Light theme checked with `ALLFIND_THEME=light` (also `dark`), a test-only override.
- Build needs `powershell -ExecutionPolicy Bypass -File srcuild.ps1` on this PC (scripts are disabled).
- Idle after restart: 13 MB, exe 284 KB.

## Not done / unverified
- User's own clicks on the setup toggles not watched; settings now read `style=icon`, `bar=0`
  (was `bar` at end of chat), so the user likely switched to icon-only.
- Source is not saved anywhere permanent.

## Next steps
1. needs user: choose a permanent folder for the source, then move the session there.
2. Rebuild after any change: `powershell -File src\build.ps1` → output `AllFind\AllFind.exe`;
   to install, stop the AllFind process and copy the exe over the installed one, then start it with `--background`.
3. Test light theme and the setup toggles end to end.

## Limits
- Restarting Explorer, changing taskbar/registry settings: ask the user first.
- Debug log: set env `ALLFIND_DEBUG=1` only for test runs.

## Do not redo
- No child window inside Shell_TrayWnd: it broke taskbar accessibility. Use owned popups.
- Windows' own search-box slot never appears on Windows 11 LTSC; the bar sits after the apps instead.
- Apps cannot pin themselves on Windows 11; the pin toggle shows instructions.
- No per-tick taskbar polling: re-measure only on WinEvent changes (idle CPU went 500 ms/20 s → 16 ms/45 s).
- No lowercase copy of the index (memory 434 MB → ~20 MB).
- Build is C# 5 / .NET Framework 4.8 csc: no `$""`, no `?.`; edit files as UTF-8 without BOM.
