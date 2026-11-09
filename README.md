# ComfyPlacebo's Project Zomboid Memory Tool

A small open-source tool that checks and sets how much RAM Project Zomboid is allowed to use
(the `-Xmx` maximum and `-Xms` minimum settings in `ProjectZomboid64.json`), clears memory
settings from Steam's launch options, installs [ZombieBuddy](https://github.com/zed-0xff/ZombieBuddy)
from the Steam Workshop, and copies the game's `console.txt` log to your clipboard.

**Version 1.2.0**, for Windows and, new in this version, Linux / SteamOS.

| | Windows | Linux / SteamOS (Steam Deck, Steam Machine, Linux PCs) |
|---|---|---|
| Download | [Releases > v1.2.0](../../releases/tag/v1.2.0) | [Releases > v1.2.0](../../releases/tag/v1.2.0) |
| File | `PZMemoryTool.exe` | `PZMemoryTool-1.2.0-linux-steamos.tar.gz` or the bare `PZMemoryTool-linux-steamos` |
| Source | `PZMemoryTool.cs` (one file) | `linux/src/` (six C files) |

**New in 1.2.0:** ZombieBuddy installer; Linux / SteamOS version; log file (F12 copies it); Steam is
opened again after the tool closed it; better detection of the signed-in Steam account; "Restore
backup" keeps ZombieBuddy switched on; the "PZ" program icon on Windows too.

**Contents:** [What it does](#what-it-does) · [ZombieBuddy](#zombiebuddy) · [Log file](#log-file) ·
[Windows](#windows) · [Linux / SteamOS](#linux--steamos) · [Steam Deck](#steam-deck) ·
[Memory rules](#memory-rules) · [Steam Deck memory, explained](#steam-deck-memory-explained) ·
[Is it safe?](#is-it-safe) · [Building it yourself](#building-it-yourself)

---

## What it does

1. **Finds the game.** Looks in Steam's default folder, then asks Steam where Project Zomboid is
   installed (all your Steam library folders and Project Zomboid's app manifest). Only if that
   fails, it offers to search your drives or let you pick the file. It checks the file really is
   Project Zomboid's before using it.
2. **Shows your memory.** Total installed RAM and how much of it the system can use, the current
   `-Xmx` / `-Xms`, and a recommendation for your PC.
3. **Checks Steam's launch options.** Memory settings typed into Steam's launch options for Project
   Zomboid override the game file. The tool shows them as "Steam Memory Allocation Launch Option"
   (signed-in Steam account only).
4. **Lets you choose:**
   - **Maximum allocation (-Xmx)** from the dropdown, within the [rules](#memory-rules).
   - **Minimum allocation (-Xms)**: tick the box and pick a value (4 GB up to 1 GB less than the
     maximum), or untick it to save no `-Xms` at all.
5. **Saves.** Writes `-Xms` (if ticked) just before `-Xmx` in the game file and clears any
   `-Xmx` / `-Xms` from Steam's launch options, so only the game file's settings are used.
   The file is read back afterwards to confirm the change.
6. **Console Copier.** **Copy Console** copies `console.txt` to the clipboard as a file (paste it
   into Discord, a message or a folder). **Copy as text** copies its contents as text (paste it
   anywhere you can type). Works while the game is running.
7. **Refresh.** The circular-arrow button at the top right re-reads everything, keeping your
   current choices.
8. **ZombieBuddy.** The ZombieBuddy box installs, updates or removes ZombieBuddy (see [below](#zombiebuddy)).

When the tool closes Steam to change its launch options, it starts Steam again afterwards.

Note: **Alternate Launch** (Steam's second Play option on Windows) runs `ProjectZomboid64.bat`, which has
its own `-Xmx` setting. This tool sets the normal launch (`ProjectZomboid64.json`).

**Project Zomboid and Steam must be closed to save.** The tool opens normally while the game is
running, so you can check your allocation or copy the console log mid-game. **Save** and
**Restore backup** ask you to close Project Zomboid first. If Steam's launch options contain memory
settings, Steam must also be fully closed (otherwise Steam writes them back): the tool offers
**Close Steam for me**, **Proceed** (after closing it yourself) or **Skip** (save the game file only).

**Backups.** The first save makes `ProjectZomboid64.json.bak` next to the game file. Before changing
Steam's launch options, Steam's settings file is backed up to `localconfig.vdf.pzmemorytool.bak`.
**Restore backup** puts back the original game file and Project Zomboid's original Steam launch
options (only those; your other Steam settings are left as they are).

Steam's "Verify integrity of game files" or a game update can reset the game file; just run the tool again.

---

## ZombieBuddy

[ZombieBuddy](https://github.com/zed-0xff/ZombieBuddy) (by Zed, MIT licence) lets Project Zomboid load
Java mods. The **ZombieBuddy** box above the Console Copier opens a window that installs it the same way
ZombieBuddy's own Windows installer does:

1. **Subscribe on the Steam Workshop.** If you aren't subscribed yet, **Subscribe on Workshop** opens the
   page in Steam. The window notices by itself when Steam has downloaded it (stable, or beta if you're
   subscribed to that one; with both, you choose).
2. **Choose where to switch it on.**
   - Windows: **Game file** (normal launch), **Alternate Launch** (`ProjectZomboid64.bat`) and **Steam
     launch options**, all ticked. For Steam, the tool closes Steam, makes the change and opens Steam again.
   - Linux / SteamOS: the game file only. Linux reads `ProjectZomboid64.json` on every start, so Steam's
     launch options aren't needed and Steam is never closed.
3. **Advanced** (click it to open): approval policy (*ask me for each new mod*, the default, or *only load
   mods I already approved*) and how much ZombieBuddy writes to its own log.
4. **Install / Update**, **Uninstall** or **Close**. Install and Uninstall show the list of changes first,
   then a report. Uninstall can also delete your saved mod approvals.

What Install does: copies `ZombieBuddy.jar` (and `zbNative.dll` on Windows) from your Workshop download
into the game folder, and adds `-agentlib:zbNative` (Windows) or `-javaagent:ZombieBuddy.jar` (Linux) to
the places you ticked. ZombieBuddy keeps itself up to date from the Workshop copy after that.

Java mods have full access to your computer. ZombieBuddy asks before it loads each new one: only allow
mods you trust.

## Log file

The tool writes what it detects and does (game file, memory, Steam, ZombieBuddy, every button) to a log:

- Windows: `%LOCALAPPDATA%\PZMemoryTool\log.txt`
- Linux / SteamOS: `~/.local/state/PZMemoryTool/log.txt`

Only two runs are kept: `log.txt` (this run) and `log-previous.txt` (the run before). Each start deletes
the older one, and one run never writes more than 1 MB. Press **F12** (or **View** on a controller) to
copy the log to the clipboard, to paste it into a message when something goes wrong. It contains file
paths, which include your user name. Nothing is ever sent anywhere.

---

## Windows

**What's new in 1.2.0:** ZombieBuddy installer; log file (F12 copies it); Steam is opened again
after the tool closed it; better detection of the signed-in Steam account; the "PZ" program icon.

### Download and run

1. Open [Releases > v1.2.0](../../releases/tag/v1.2.0) and download `PZMemoryTool.exe`.
2. Your browser may warn about the download, because the program is new and not digitally signed:
   - **Edge:** "…isn't commonly downloaded" > hover over it > **⋯** > **Keep** > **Show more** > **Keep anyway**.
   - **Chrome:** "Suspicious download" > **Keep** (or **Download suspicious file**).
   - **Firefox:** usually no warning; if there is one, choose **Allow download**.
3. Double-click `PZMemoryTool.exe`. If Windows shows **"Windows protected your PC"** (SmartScreen),
   click **More info** > **Run anyway**. This appears for programs that aren't signed with a paid
   certificate; it doesn't mean anything was found. Building it yourself (below) avoids it.
4. Because the game folder is under Program Files, Windows may ask for administrator permission
   when you save.

`SHA256.txt` on the release page lets you check the file (in PowerShell: `Get-FileHash PZMemoryTool.exe`).

**Look and feel:** the window follows your Windows settings automatically, and updates live if you
change them: dark / light mode, accent colour, High Contrast themes, display scaling and the Windows
UI font.

---

## Linux / SteamOS

One program file, no installer, nothing else to install. It runs natively (no Proton / Wine) on
SteamOS (Steam Deck, Steam Machine) and on other Linux PCs (64-bit, X11 or Wayland). It looks like
your desktop: it uses your KDE / SteamOS colours (Vapor on SteamOS, Breeze otherwise) and the system
font (Noto Sans), and works with mouse, touch, keyboard and controller.

### Download and run

1. Open [Releases > v1.2.0](../../releases/tag/v1.2.0) and download **one** of:
   - `PZMemoryTool-1.2.0-linux-steamos.tar.gz` (recommended). In Dolphin (the file manager
     in Desktop Mode): right-click it > **Extract** > **Extract archive here**. Open the new
     `PZMemoryTool` folder. The program inside is ready to run.
   - `PZMemoryTool-linux-steamos` (the bare program file). Downloads lose their "can run"
     permission, so first: right-click it > **Properties** > **Permissions** > tick
     **Allow executing file as program** (called **Is executable** in older versions) > **OK**.
     In a terminal: `chmod +x PZMemoryTool-linux-steamos`.
2. Double-click the program. Dolphin may ask what to do with an executable file: choose **Execute**.
3. Your browser usually doesn't warn about Linux downloads. If it does, choose **Keep** / **Allow download**.

`SHA256.txt` on the release page lets you check the files (`sha256sum -c SHA256.txt`).

**Where it writes on Linux:** the game file and its `.bak`; Steam's `localconfig.vdf` (only Project
Zomboid's launch options, only if they contain `-Xmx` / `-Xms`) and its backup;
`~/.config/PZMemoryTool/settings.ini` (one line: your "Add to Steam" answer); a copy of
`console.txt` in `~/.cache/PZMemoryTool/` when you use Copy Console; the [log file](#log-file); for
ZombieBuddy, `ZombieBuddy.jar` in the game folder (and its entry in the game file); and, only for the
Game Mode "Fix it for me" step, a temporary autostart entry that removes itself.

### Controls

| | Controller | Keyboard | Mouse / touch |
|---|---|---|---|
| Move between controls | D-pad or left stick, L1 / R1 | Arrow keys, Tab | — |
| Press | **A** | Enter or Space | Click / tap |
| Back (close a list or dialog) | **B** | Escape | Click outside |
| Change a dropdown value | D-pad left / right | Left / Right | Tap the dropdown |

The clipboard keeps working after you close the tool: a tiny background copy of it hands the console
log over when you paste, and quits as soon as you copy something else.

---

## Steam Deck

### Desktop Mode (recommended for the first run)

Press the **Steam** button > **Power** > **Switch to Desktop**. Download and start the tool as above.
On the first run it asks:

> **Add PZ Memory Tool to Steam?** Then you can open it from your Steam Library, also in Game Mode,
> and use your controller in it.  — **Yes** / **Not now** / **Don't ask again**

**Yes** adds it to your Library under **Non-Steam**. If you move the file later, add it again.
In Desktop Mode the Deck's controls act as a mouse (trackpad / touch) and keyboard unless the tool
is started from Steam; touch always works.

### Game Mode

Open **Library** > **Non-Steam** > **PZ Memory Tool**. Everything works with the controller and touch.

Steam can't be closed in Game Mode, so if Steam's launch options for Project Zomboid contain memory
settings, **Save** offers:

- **Fix it for me**: saves the game file, switches to Desktop Mode, closes Steam there, clears the
  launch options, and switches back to Game Mode by itself (about a minute). In Desktop Mode you see
  a short message with **Return to Game Mode** / **Stay in Desktop Mode** (it returns by itself after 10 s).
- **I'll clear it myself**: saves the game file; then select Project Zomboid in your Library >
  gear icon (**Settings**) > **Properties** > **General** > **Launch Options**, and delete the
  `-Xmx…` / `-Xms…` parts.
- **Skip**: saves the game file only.

### Docked

Docked works like Desktop Mode or Game Mode, whichever you're in. Graphics on an external screen
use more of the shared memory, which is why the tool suggests 8–9 GB rather than 10 GB when docked
(see below).

---

## Memory rules

"Installed" is your total RAM; the tool rounds what the system reports up to a standard size.

### Windows

| Installed RAM | Maximum (-Xmx) choices | Recommended | Minimum (-Xms) | Warnings / notes |
|---|---|---|---|---|
| 32 GB or more | 5 GB up to RAM − 6 GB (Windows 10) or RAM − 7 GB (Windows 11) | 8–12 GB | ticked, 4 GB | Above 10 GB: "for advanced users" |
| 16 GB – under 32 GB | 5 GB up to RAM − 6 GB (Win 10) / RAM − 7 GB (Win 11) | 8 GB | ticked, 4 GB | Above 8 GB: "not recommended" |
| 12 GB – under 16 GB | 6 GB or 8 GB only | 6 GB | ticked, 4 GB | Consider upgrading to 16 GB; try 6 GB first, then 8 GB |
| 8 GB – under 12 GB | 5 GB or 6 GB only | 5 GB | locked off | Not recommended for heavily modded servers; try 5 GB first, then 6 GB |
| under 8 GB | locked (only Close and the Console Copier work) | – | – | Not advised for heavily modded servers |

Examples: 16 GB on Windows 10 → up to 10 GB; on Windows 11 → up to 9 GB. 32 GB → up to 26 / 25 GB.
The extra headroom on 16 GB and up is for Windows itself, Java's own memory outside the `-Xmx`
limit, and everything else running.

### Linux PCs

Same as Windows, with the 16 GB-and-up limit at RAM − 6 GB.

### Steam Machine

16 GB: 8, 9 or 10 GB, recommended 8 GB. Upgraded to more than 16 GB: the normal tiers above, but
never below 8 GB and up to RAM − 6 GB. Minimum (-Xms) as normal.

### Steam Deck

The Deck's 16 GB is shared between the system and graphics, so the choices depend on how much your
Deck sets aside for graphics: the **UMA Frame Buffer Size** (the memory your Steam Deck sets aside
for graphics, a BIOS setting; "UMA buffer" below). No minimum (-Xms) is used on the Deck.

| UMA buffer | Maximum (-Xmx) choices | Recommended | Note |
|---|---|---|---|
| 1 GB (default) | 8, 9, 10 GB | 8 GB | At 9–10 GB: "Graphics share this memory. In Desktop Mode or docked, 8–9 GB is safer. If you notice stutter, go back to 8." |
| 2 GB | 8, 9 GB | 8 GB | Same note at 9 GB |
| 3 GB | 7, 8 GB | 8 GB | Project Zomboid doesn't need a 3 GB UMA buffer; at 1 GB the game has more memory. 8 GB still works. |
| 4 GB or more | 6, 7, 8 GB | 7 GB | Project Zomboid doesn't need that much; setting it back to 1 GB gives 3 GB back to the game. At 8 GB: warning (see below). |

At 8 GB with a 4 GB UMA buffer, the tool shows a shorter version of this warning: *"At 8 GB, only about 0.5 GB of memory is left for
everything else on your Deck. The game may stutter or freeze. Choose 7 GB, or lower the UMA buffer to 1–2 GB."*

## Steam Deck memory, explained

How the numbers above are worked out, so you can check them:

| | UMA 1 GB | UMA 2 GB | UMA 3 GB | UMA 4 GB |
|---|---|---|---|---|
| Installed | 16 GB | 16 GB | 16 GB | 16 GB |
| − UMA buffer (graphics) | 1 GB | 2 GB | 3 GB | 4 GB |
| − firmware / kernel reserve | ~0.5 GB | ~0.5 GB | ~0.5 GB | ~0.5 GB |
| = usable (what the tool shows) | ~14.5 GB | ~13.5 GB | ~12.5 GB | ~11.5 GB |
| − SteamOS + Steam in Game Mode | ~2 GB | ~2 GB | ~2 GB | ~2 GB |
| − Java's own memory outside `-Xmx` | ~1 GB | ~1 GB | ~1 GB | ~1 GB |
| = room for `-Xmx` + spare | ~11.5 GB | ~10.5 GB | ~9.5 GB | ~8.5 GB |
| Spare at 8 GB | ~3.5 GB | ~2.5 GB | ~1.5 GB | **~0.5 GB** |
| Highest choice (spare) | 10 GB (~1.5 GB) | 9 GB (~1.5 GB) | 8 GB (~1.5 GB) | 8 GB (~0.5 GB, warned) |

Desktop Mode (KDE Plasma) uses roughly 1 GB more than Game Mode (on a test Deck in Desktop Mode,
docked, `free -h` showed 14 GiB total and 3.4 GiB used before starting the game). That's why 8–9 GB
is suggested there. SteamOS also has about 8 GB of compressed swap in memory (zram), which softens
short peaks but is slow, so the tool doesn't count on it. You can check your own numbers in
Desktop Mode with `free -h` in Konsole, and your UMA buffer with
`cat /sys/class/drm/card*/device/mem_info_vram_total` (1073741824 = 1 GB).

### Changing the UMA Frame Buffer Size (BIOS)

1. Shut the Deck down completely.
2. Hold **Volume Up** and press **Power**; release when you hear the chime. The boot menu opens.
3. Choose **Setup Utility** > **Advanced** > **UMA Frame Buffer Size**.
4. Set it to **1G** (the default; Project Zomboid doesn't need more) and choose **Exit** > **Save Changes and Exit**.

Some other games ask for a larger UMA buffer; if you keep it at 3–4 GB for them, the tool adjusts
its choices for you.

---

## Is it safe?

All the code is in this repository and anyone can read it. The tool makes no network connections
(the Ko-fi link only opens your browser when you click it) and writes only the files listed above.
On Windows it reads (never changes) Steam's install location, the signed-in Steam account and your
colour settings from the registry.

## How the downloads are built

The release files on the Releases page are built from the source in this branch:

- **Windows:** compiled with the same command as `build.bat`, with `PZMemoryTool.manifest` and the
  `PZMemoryTool.ico` icon built in.
- **Linux / SteamOS:** compiled with raylib 5.5 built in, using Zig's C compiler against an older
  glibc (2.31) so it runs on older Linux and SteamOS versions too (`PZMT_ZIG=1 sh linux/build.sh`).
  `PZMemoryTool --version` shows the version.

Test versions of the next release are built automatically by GitHub Actions on the `preview` branch
([`.github/workflows/preview.yml`](.github/workflows/preview.yml)).

## Building it yourself

**Windows:** double-click `build.bat`. It uses the C# compiler that ships with Windows (.NET
Framework 4.x), so nothing needs to be downloaded, and embeds `PZMemoryTool.manifest` (tells Windows
the tool runs with normal user rights) and the `PZMemoryTool.ico` icon. A self-built exe doesn't trigger SmartScreen on your own PC.

**Linux:** install a C compiler and the X11 / OpenGL development files, then run `sh linux/build.sh`.
It downloads raylib 5.5 once, builds it into the program, and leaves `linux/PZMemoryTool`
(about 1 MB).

```sh
# Debian / Ubuntu
sudo apt install build-essential git libx11-dev libxrandr-dev libxinerama-dev libxcursor-dev libxi-dev libgl1-mesa-dev
# Arch (or SteamOS after "sudo steamos-readonly disable")
sudo pacman -S base-devel git libx11 libxrandr libxinerama libxcursor libxi mesa
sh linux/build.sh
# optional, like the GitHub build (runs on older systems too): pip install ziglang, then
PZMT_ZIG=1 sh linux/build.sh
```

## Changing the rules

Windows: the `Settings` class near the top of `PZMemoryTool.cs` (tiers, limits, wait times); the
banner is in `BannerRenderer`. Linux: the settings at the top of `linux/src/pzmt.h` and
`make_plan()` / `plan_messages()` in `linux/src/core.c`; the banner is in `linux/src/banner.c`.

## Support

[Support ComfyPlacebo on Ko-fi](https://ko-fi.com/comfyplacebo)
