# ComfyPlacebo's Project Zomboid Memory Tool

A small open-source Windows tool that sets how much RAM Project Zomboid is allowed to use
(the `-Xmx` line in `ProjectZomboid64.json`).

## What it does

1. Checks that Project Zomboid (`ProjectZomboid64.exe`) is not running. If it is, it asks you
   to close the game and click **Proceed** (or **Cancel**). After Proceed it checks again;
   if the game is still running it waits 5 seconds, checks once more, and keeps asking until
   the game is closed or you cancel. The same check runs again before saving.
2. Looks for `ProjectZomboid64.json` in
   `C:\Program Files (x86)\Steam\steamapps\common\ProjectZomboid`.
3. If it isn't there, it asks Steam where the game is installed (reads Steam's registry
   entries, all your Steam library folders, and Project Zomboid's app manifest). No questions asked.
4. Only if Steam can't point to it either, it offers to search your drives or let you pick the file.
5. Checks the file really is Project Zomboid's (correct name, Zomboid main class, an `-Xmx` argument).
6. Reads your total installed memory from Windows and shows the allowed choices:

   | Installed RAM | Choices | Warnings / notes |
   |---|---|---|
   | 32 GB or more | 5 GB up to RAM − 2 GB, recommended 8–12 GB | Above 10 GB: "for advanced users" |
   | 16 GB – under 32 GB | 5 GB up to RAM − 2 GB, recommended 8 GB | Above 8 GB: "not recommended" |
   | 12 GB – under 16 GB | 6 GB or 8 GB only | Consider upgrading to 16 GB; try 6 GB first, then 8 GB |
   | 8 GB – under 12 GB | 5 GB or 6 GB only | Unsupported, upgrade to 16 GB; try 5 GB first, then 6 GB |
   | under 8 GB | locked (only Close works) | Not advised for heavily modded servers |

   You can never choose less than 5 GB, or more than your installed RAM minus 2 GB.

7. A **Support ComfyPlacebo on Ko-fi** link above the Save button opens https://ko-fi.com/comfyplacebo in your browser.
8. **Save** writes the new value. The first save makes `ProjectZomboid64.json.bak` next to the
   original; **Restore backup** puts it back.

## Look and feel

The window follows your Windows settings automatically, and updates live if you change them:

- **Dark / light mode** (Settings > Personalization > Colors > "Choose your app mode"),
  including a dark title bar on Windows 10 1809+ and Windows 11.
- **Accent colour** for the Save / Proceed buttons and the tip text (adjusted so it stays readable).
- **High Contrast** themes: uses your High Contrast colours exactly.
- **Display scaling** (125%, 150%, 200%...): text and layout stay sharp instead of blurry.
- **Windows UI font** (Segoe UI on Windows 10/11).

The ComfyPlacebo's banner keeps its own colours. Pop-up message boxes are standard Windows
message boxes, which Windows itself always shows in light mode.

## Is it safe?

All the code is in one file, `PZMemoryTool.cs`. It makes no network connections (the Ko-fi link just opens your web browser when you click it) and changes no
registry settings (it only reads Steam's entries to find the game). It writes only
`ProjectZomboid64.json` and its `.bak` backup. Because the game folder is under Program Files,
Windows may ask for administrator permission when you save.

## Building it yourself

Double-click `build.bat`. It uses the C# compiler that already ships with Windows
(.NET Framework 4.x), so nothing needs to be downloaded. The result is `PZMemoryTool.exe`.

## Changing the rules

The memory tiers, minimum allocation, wait time and default folder are in the `Settings`
class near the top of `PZMemoryTool.cs`. The banner (logo, colours, star speed) is in the
`BannerRenderer` class.

Note: Steam's "Verify integrity of game files" or a game update can reset the JSON file;
just run the tool again.
