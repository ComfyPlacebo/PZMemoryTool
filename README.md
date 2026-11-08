# ComfyPlacebo's Project Zomboid Memory Tool

A small open-source Windows tool that checks and sets how much RAM Project Zomboid is
allowed to use (the `-Xmx` maximum and `-Xms` minimum settings in `ProjectZomboid64.json`), and
copies the game's `console.txt` log to your clipboard.

**Version 1.1.0**

## What it does

1. **Finds the game.** Looks for `ProjectZomboid64.json` in
   `C:\Program Files (x86)\Steam\steamapps\common\ProjectZomboid`. If it isn't there, it asks
   Steam where the game is installed (Steam's registry entries, all your Steam library folders and
   Project Zomboid's app manifest). Only if that fails too, it offers to search your drives or let
   you pick the file. It checks the file really is Project Zomboid's before using it.
2. **Shows your memory.** Total installed RAM, the current `-Xmx` / `-Xms`, and a recommendation
   for your PC.
3. **Checks Steam's launch options.** Memory settings typed into Steam's launch options for Project
   Zomboid override the game file. The tool shows them as
   "Steam Memory Allocation Launch Option" (signed-in Steam account only).
4. **Lets you choose:**
   - **Maximum allocation (-Xmx)** from the dropdown, within the rules below.
   - **Minimum allocation (-Xms)**: tick the box and pick a value (4 GB up to 1 GB less than the
     maximum), or untick it to save no `-Xms` at all.
5. **Saves.** Writes `-Xms` (if ticked) just before `-Xmx` in the game file and clears any
   `-Xmx` / `-Xms` from Steam's launch options, so only the game file's settings are used.
   The file is read back afterwards to confirm the change.
6. **Console Copier.** The **Copy Console** button copies `console.txt` (from your `Zomboid`
   folder) to the clipboard as a file. Paste it into Discord, a message or a folder with
   right click or Ctrl+V. Works while the game is running.
7. **Refresh.** The circular-arrow button at the top right re-reads the game file, Steam's launch
   options and your memory, keeping your current choices.

### Memory rules

| Installed RAM | Maximum (-Xmx) choices | Minimum (-Xms) | Warnings / notes |
|---|---|---|---|
| 32 GB or more | 5 GB up to RAM − 2 GB, recommended 8–12 GB | ticked, 4 GB by default | Above 10 GB: "for advanced users" |
| 16 GB – under 32 GB | 5 GB up to RAM − 2 GB, recommended 8 GB | ticked, 4 GB by default | Above 8 GB: "not recommended" |
| 12 GB – under 16 GB | 6 GB or 8 GB only | ticked, 4 GB by default | Consider upgrading to 16 GB; try 6 GB first, then 8 GB |
| 8 GB – under 12 GB | 5 GB or 6 GB only | locked off (no `-Xms`) | Not recommended for heavily modded servers, upgrade to 16 GB; try 5 GB first, then 6 GB |
| under 8 GB | locked (only Close and Copy Console work) | – | Not advised for heavily modded servers |

You can never choose a maximum below 5 GB or above your installed RAM minus 2 GB, and the minimum
is always at least 1 GB below the maximum (Java won't start if `-Xms` is larger than `-Xmx`).

### Project Zomboid and Steam must be closed to save

The tool opens normally while the game is running, so you can check your allocation or copy the
console log mid-game. **Save** and **Restore backup** ask you to close Project Zomboid first.
If Steam's launch options contain memory settings, Steam must also be fully closed (otherwise Steam
writes them back): the tool offers **Close Steam for me**, **Proceed** (after closing it yourself)
or **Skip** (save the game file only).

### Backups

- The first save makes `ProjectZomboid64.json.bak` next to the game file.
- Before changing Steam's launch options, the tool backs up Steam's settings file to
  `localconfig.vdf.pzmemorytool.bak`.
- **Restore backup** puts back the original game file and Project Zomboid's original Steam launch
  options (only those; your other Steam settings are left as they are).

## Is it safe?

All the code is in one file, `PZMemoryTool.cs`, which anyone can read. The tool:

- makes no network connections (the Ko-fi link only opens your web browser when you click it),
- reads, but never changes, Windows registry entries (Steam's install location and signed-in
  account, and your dark/light mode and accent colour),
- writes only: `ProjectZomboid64.json` and its `.bak` backup; Project Zomboid's launch options in
  Steam's `localconfig.vdf` (only if they contain `-Xmx` / `-Xms`) and its backup; and a copy of
  `console.txt` in your Temp folder when you use Copy Console.

Because the game folder is under Program Files, Windows may ask for administrator permission when
you save. Windows SmartScreen may warn about the downloaded .exe because it isn't digitally signed;
building it yourself (below) avoids that.

## Building it yourself

Double-click `build.bat`. It uses the C# compiler that already ships with Windows
(.NET Framework 4.x), so nothing needs to be downloaded. It embeds `PZMemoryTool.manifest`
(a short plain-text file telling Windows the tool runs with normal user rights and supports
Windows 10/11). The result is `PZMemoryTool.exe`.

## Look and feel

The window follows your Windows settings automatically, and updates live if you change them:
dark / light mode (including the title bar), accent colour, High Contrast themes, display scaling
(125%, 150%, 200%…) and the Windows UI font. The ComfyPlacebo's banner keeps its own colours.
Pop-up message boxes are standard Windows message boxes, which Windows always shows in light mode.

## Changing the rules

The memory tiers, minimum/maximum limits, wait times and default folder are in the `Settings`
class near the top of `PZMemoryTool.cs`. The banner (logo, colours, star speed) is in the
`BannerRenderer` class.

Note: Steam's "Verify integrity of game files" or a game update can reset the game file;
just run the tool again.

## Support

[Support ComfyPlacebo on Ko-fi](https://ko-fi.com/comfyplacebo)
