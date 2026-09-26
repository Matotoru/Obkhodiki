# Stage 1 — Tray app core (acceptance criteria)

Windows tray app (C#/.NET 8) that runs zapret `winws.exe` using Flowseal
(zapret-discord-youtube) strategies and keeps them up to date.

## Acceptance criteria

1. Strategies are imported from Flowseal `general*.bat` files: only the `winws.exe`
   argument list is extracted (continuation `^` joined, quotes removed, quoted values
   with spaces kept as one arg). Unparsable files are reported, not silently used.
2. Placeholders `%BIN%`, `%LISTS%`, `%GameFilterTCP%`, `%GameFilterUDP%`, `%GameFilter%`
   are resolved; unknown placeholders fail loudly (Flowseal format change detection).
   `*-user.txt` lists resolve to the app's user data dir so updates never wipe them.
3. Game filter modes Disabled/Tcp/Udp/All behave like Flowseal (disabled = dummy port 12,
   default range 1024-65535). IPSet is always "loaded" (full list), never "any".
4. Update: latest release from GitHub API (`Flowseal/zapret-discord-youtube`), zip asset,
   installed into `versions/<ver>`, validated (winws.exe present, ≥1 parsable strategy,
   no zip-slip), then made active. Failed install keeps the previous version active.
   Old versions are cleaned when not locked.
5. Auto-select: each strategy is started, targets probed over HTTPS (reading up to 64 KB
   to catch the TSPU "16 KB stall"), engine stopped after every trial; best = most targets
   passed, tie → lowest latency; none passing → no pick. Cancellable.
6. Settings persist as JSON; corrupt file → defaults + `.corrupt` backup.
7. Tray menu: enable/disable, strategy list, auto-select, game filter, check updates,
   autostart at logon (elevated scheduled task), open user lists folder, exit.
   Refuses to start when a foreign winws / zapret service is running (WinDivert conflict).
