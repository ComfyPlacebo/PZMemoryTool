// =============================================================================
//  PZ Memory Tool
//  Adjusts the Java heap size (-Xmx) in Project Zomboid's ProjectZomboid64.json
//
//  Open source - single file, no network access (the Ko-fi link only opens
//  your web browser when you click it), no installer, no registry
//  changes (it only READS Steam's registry entries to find the game, and the
//  Windows dark/light mode and accent colour settings to match them). The only
//  file it writes is ProjectZomboid64.json (after making a .bak backup copy
//  next to it).
//
//  Build: run build.bat on any Windows 10/11 PC (uses the C# compiler that
//  ships with Windows / .NET Framework 4.x).
// =============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PZMemoryTool
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Ui.EnableDpiAwareness();   // crisp text on 125% / 150% / 200% display scaling
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Project Zomboid must not be running while we read/change its settings.
            if (!ProcessGuard.EnsureGameClosed())
                return; // user cancelled

            // When relaunched as administrator we receive the file path as an argument.
            string path = (args.Length > 0 && File.Exists(args[0])) ? args[0] : null;

            if (path == null)
                path = FileLocator.FindJson();

            if (path == null)
                return; // user cancelled

            Application.Run(new MainForm(path));
        }
    }

    // -------------------------------------------------------------------------
    //  Settings - change these to adjust the tool's behaviour
    // -------------------------------------------------------------------------
    static class Settings
    {
        public const string DefaultFolder =
            @"C:\Program Files (x86)\Steam\steamapps\common\ProjectZomboid";
        public const string JsonName = "ProjectZomboid64.json";
        public const string GameProcessName = "ProjectZomboid64"; // ProjectZomboid64.exe
        public const string SteamAppId = "108600";                // Project Zomboid on Steam

        public const int MinAllocGB        = 5;  // never allow less than this
        public const int WindowsReserveGB  = 2;  // always leave this much for Windows
        public const int RecommendedUpgrade = 16;
        public const int GameCheckWaitSeconds = 5;
        public const string KofiUrl = "https://ko-fi.com/comfyplacebo";

        // Memory tiers (installed RAM, in GB)
        public const int Tier32 = 32;  // 32 GB+          : 8 to 12 GB, warn above 10 GB
        public const int Tier16 = 16;  // 16 GB to <32 GB : 8 GB, warn above 8 GB
        public const int Tier12 = 12;  // 12 GB to <16 GB : 6 or 8 GB only
        public const int Tier8  = 8;   //  8 GB to <12 GB : 5 or 6 GB only
                                       //  under 8 GB     : locked, warning only
    }

    // -------------------------------------------------------------------------
    //  Display scaling (DPI) and the Windows UI font
    // -------------------------------------------------------------------------
    static class Ui
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        static float scale = 0f;

        // Tell Windows we handle scaling ourselves, so it doesn't stretch (blur) the window.
        public static void EnableDpiAwareness()
        {
            try { SetProcessDPIAware(); } catch { }
        }

        // Display scale factor: 1.0 at 100%, 1.5 at 150%, ...
        public static float Scale
        {
            get
            {
                if (scale == 0f)
                {
                    try { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f; }
                    catch { scale = 1f; }
                    if (scale < 1f) scale = 1f;
                }
                return scale;
            }
        }

        // Converts a layout size designed at 100% scaling to the current scaling.
        public static int S(int v) { return (int)Math.Round(v * Scale); }

        public static void Place(Control c, int x, int y, int w, int h)
        {
            c.SetBounds(S(x), S(y), S(w), S(h));
        }

        // Windows' own UI font (Segoe UI on Windows 10/11), at the size we want.
        public static Font Font(float size, FontStyle style)
        {
            string family;
            try { family = SystemFonts.MessageBoxFont.FontFamily.Name; }
            catch { family = "Segoe UI"; }
            return new Font(family, size, style);
        }
        public static Font Font(float size) { return Font(size, FontStyle.Regular); }
    }

    // -------------------------------------------------------------------------
    //  Theme: follows the Windows dark/light mode, accent colour and High
    //  Contrast settings. Controls get a role through their Tag:
    //    "sub" = secondary text, "warn" = warning text, "note" = accent text,
    //    "primary" = main action button (accent coloured)
    // -------------------------------------------------------------------------
    class Palette
    {
        public bool Dark, HighContrast;
        public Color Back, Text, SubText, Warn, Note, Accent, OnAccent,
                     ControlBack, ControlHover, ControlPressed, Border;
    }

    static class Theme
    {
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public static bool SystemIsDark()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    return v is int && (int)v == 0;
                }
            }
            catch { return false; }
        }

        public static Color SystemAccent()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
                {
                    object v = k == null ? null : k.GetValue("AccentColor");
                    if (v is int)
                    {
                        int abgr = (int)v; // stored as 0xAABBGGRR
                        return Color.FromArgb(abgr & 0xFF, (abgr >> 8) & 0xFF, (abgr >> 16) & 0xFF);
                    }
                }
            }
            catch { }
            return SystemColors.Highlight;
        }

        public static Palette Current()
        {
            Palette p = new Palette();
            p.HighContrast = SystemInformation.HighContrast;
            p.Dark = !p.HighContrast && SystemIsDark();

            if (p.HighContrast)
            {
                // Use the user's High Contrast colours exactly
                p.Back = SystemColors.Control; p.Text = p.SubText = p.Warn = p.Note = SystemColors.ControlText;
                p.Accent = SystemColors.Highlight; p.OnAccent = SystemColors.HighlightText;
                p.ControlBack = SystemColors.Control; p.ControlHover = p.ControlPressed = SystemColors.ControlLight;
                p.Border = SystemColors.ControlText;
                return p;
            }

            if (p.Dark)
            {
                p.Back = Color.FromArgb(32, 32, 32);
                p.Text = Color.FromArgb(242, 242, 242);
                p.SubText = Color.FromArgb(160, 160, 160);
                p.Warn = Color.FromArgb(255, 143, 128);
                p.ControlBack = Color.FromArgb(45, 45, 45);
                p.ControlHover = Color.FromArgb(58, 58, 58);
                p.ControlPressed = Color.FromArgb(38, 38, 38);
                p.Border = Color.FromArgb(70, 70, 70);
            }
            else
            {
                p.Back = SystemColors.Control;
                p.Text = SystemColors.ControlText;
                p.SubText = SystemColors.GrayText;
                p.Warn = Color.Firebrick;
                p.ControlBack = SystemColors.Window;
                p.ControlHover = SystemColors.ControlLight;
                p.ControlPressed = SystemColors.ControlDark;
                p.Border = SystemColors.ControlDark;
            }

            p.Accent = SystemAccent();
            p.Note = Readable(p.Accent, p.Back, 4.5);      // accent text that's easy to read
            p.Warn = Readable(p.Warn, p.Back, 4.5);
            p.OnAccent = Luminance(p.Accent) > 0.45 ? Color.Black : Color.White;
            return p;
        }

        // Applies the current theme to a window and everything in it.
        public static void Apply(Form form)
        {
            Palette p = Current();
            form.BackColor = p.Back;
            form.ForeColor = p.Text;
            ApplyTitleBar(form, p.Dark);
            ApplyChildren(form, p);
            form.Invalidate(true);
        }

        static void ApplyChildren(Control parent, Palette p)
        {
            foreach (Control c in parent.Controls)
            {
                string role = c.Tag as string;

                if (c is Banner) continue; // the banner has its own artwork colours

                if (c is LinkLabel)
                {
                    LinkLabel ll = (LinkLabel)c;
                    Color link = p.HighContrast ? SystemColors.HotTrack : p.Note;
                    ll.LinkColor = link;
                    ll.VisitedLinkColor = link;
                    ll.ActiveLinkColor = p.HighContrast ? SystemColors.HotTrack : p.Text;
                }
                else if (c is Label)
                {
                    if (role == "sub") c.ForeColor = p.SubText;
                    else if (role == "warn") c.ForeColor = p.Warn;
                    else if (role == "note") c.ForeColor = p.Note;
                    else c.ResetForeColor();
                }
                else if (c is Button)
                {
                    Button b = (Button)c;
                    StyleButton(b, p);
                    b.EnabledChanged -= OnButtonEnabledChanged;
                    b.EnabledChanged += OnButtonEnabledChanged;
                }
                else if (c is ComboBox)
                {
                    StyleCombo((ComboBox)c, p);
                }

                if (c.HasChildren) ApplyChildren(c, p);
            }
        }

        static void OnButtonEnabledChanged(object sender, EventArgs e)
        {
            StyleButton((Button)sender, Current());
        }

        static void StyleButton(Button b, Palette p)
        {
            bool primary = (b.Tag as string) == "primary";

            if (p.HighContrast || (!p.Dark && (!primary || !b.Enabled)))
            {
                // Native Windows button
                b.FlatStyle = FlatStyle.Standard;
                b.UseVisualStyleBackColor = true;
                b.ResetBackColor();
                b.ResetForeColor();
                return;
            }

            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 1;

            if (primary && b.Enabled)
            {
                b.BackColor = p.Accent;
                b.ForeColor = p.OnAccent;
                b.FlatAppearance.BorderColor = p.Accent;
                b.FlatAppearance.MouseOverBackColor = Blend(p.Accent, p.OnAccent, 0.15);
                b.FlatAppearance.MouseDownBackColor = Blend(p.Accent, p.OnAccent == Color.White ? Color.Black : Color.White, 0.2);
            }
            else
            {
                b.BackColor = p.ControlBack;
                b.ForeColor = b.Enabled ? p.Text : p.SubText;
                b.FlatAppearance.BorderColor = p.Border;
                b.FlatAppearance.MouseOverBackColor = p.ControlHover;
                b.FlatAppearance.MouseDownBackColor = p.ControlPressed;
            }
        }

        static void StyleCombo(ComboBox cb, Palette p)
        {
            cb.DrawItem -= OnComboDrawItem;
            if (p.Dark)
            {
                cb.FlatStyle = FlatStyle.Flat;
                cb.BackColor = p.ControlBack;
                cb.ForeColor = p.Text;
                cb.DrawMode = DrawMode.OwnerDrawFixed;
                cb.DrawItem += OnComboDrawItem;
            }
            else
            {
                cb.FlatStyle = FlatStyle.Standard;
                cb.DrawMode = DrawMode.Normal;
                cb.ResetBackColor();
                cb.ResetForeColor();
            }
        }

        static void OnComboDrawItem(object sender, DrawItemEventArgs e)
        {
            ComboBox cb = (ComboBox)sender;
            Palette p = Current();
            bool inList = (e.State & DrawItemState.ComboBoxEdit) == 0;
            bool selected = (e.State & DrawItemState.Selected) != 0 && inList;
            Color back = selected ? p.Accent : p.ControlBack;
            Color fore = selected ? p.OnAccent : (cb.Enabled ? p.Text : p.SubText);
            using (SolidBrush br = new SolidBrush(back)) e.Graphics.FillRectangle(br, e.Bounds);
            if (e.Index >= 0)
                TextRenderer.DrawText(e.Graphics, cb.Items[e.Index].ToString(), cb.Font, e.Bounds, fore,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.LeftAndRightPadding);
        }

        // Dark or light title bar (Windows 10 1809+ / Windows 11). Ignored on older Windows.
        public static void ApplyTitleBar(Form f, bool dark)
        {
            if (!f.IsHandleCreated) return;
            try
            {
                int v = dark ? 1 : 0;
                if (DwmSetWindowAttribute(f.Handle, 20, ref v, 4) != 0)   // DWMWA_USE_IMMERSIVE_DARK_MODE
                    DwmSetWindowAttribute(f.Handle, 19, ref v, 4);        // older Windows 10 builds
            }
            catch { }
        }

        // ---- colour helpers ----
        public static double Luminance(Color c)
        {
            return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
        }
        static double Lin(int v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        static double ContrastRatio(Color a, Color b)
        {
            double la = Luminance(a), lb = Luminance(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }
        public static Color Blend(Color a, Color b, double t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }
        // Lightens (on dark backgrounds) or darkens (on light backgrounds) a colour until it is readable.
        static Color Readable(Color fg, Color bg, double minRatio)
        {
            Color target = Luminance(bg) < 0.5 ? Color.White : Color.Black;
            Color c = fg;
            for (int i = 1; i <= 20 && ContrastRatio(c, bg) < minRatio; i++)
                c = Blend(fg, target, i * 0.05);
            return c;
        }
    }

    // Base window: native look, follows the Windows theme, updates live if it changes.
    class ThemedForm : Form
    {
        public ThemedForm()
        {
            AutoScaleMode = AutoScaleMode.None;   // we scale layout ourselves (Ui.S)
            Font = Ui.Font(10f);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.ApplyTitleBar(this, Theme.Current().Dark);
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }

        protected override void OnLoad(EventArgs e)
        {
            Theme.Apply(this);
            base.OnLoad(e);
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            base.OnHandleDestroyed(e);
        }

        void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category != UserPreferenceCategory.General && e.Category != UserPreferenceCategory.Color &&
                e.Category != UserPreferenceCategory.VisualStyle && e.Category != UserPreferenceCategory.Accessibility)
                return;
            try { BeginInvoke((MethodInvoker)delegate { Theme.Apply(this); }); } catch { }
        }
    }

    // -------------------------------------------------------------------------
    //  Make sure ProjectZomboid64.exe is not running
    // -------------------------------------------------------------------------
    class ProcessGuard : ThemedForm
    {
        Label message = new Label();
        Button btnProceed = new Button();
        Button btnCancel = new Button();
        System.Windows.Forms.Timer waitTimer = new System.Windows.Forms.Timer();
        int secondsLeft;

        const string AskText =
            "Project Zomboid (ProjectZomboid64.exe) is currently running.\n\n" +
            "Please close Project Zomboid, then click Proceed.";

        public static bool IsGameRunning()
        {
            Process[] procs = Process.GetProcessesByName(Settings.GameProcessName);
            bool running = procs.Length > 0;
            foreach (Process p in procs) p.Dispose();
            return running;
        }

        // Returns true when the game is closed, false if the user cancelled.
        public static bool EnsureGameClosed()
        {
            if (!IsGameRunning())
                return true;
            using (ProcessGuard f = new ProcessGuard())
                return f.ShowDialog() == DialogResult.OK;
        }

        ProcessGuard()
        {
            Text = "PZ Memory Tool - Project Zomboid is running";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            ClientSize = new Size(Ui.S(440), Ui.S(150));

            Ui.Place(message, 15, 15, 410, 80);
            message.Text = AskText;

            btnProceed.Text = "Proceed";
            btnProceed.Tag = "primary";
            Ui.Place(btnProceed, 225, 105, 95, 32);
            btnProceed.Click += delegate { OnProceed(); };

            btnCancel.Text = "Cancel";
            Ui.Place(btnCancel, 330, 105, 95, 32);
            btnCancel.Click += delegate { waitTimer.Stop(); DialogResult = DialogResult.Cancel; };

            waitTimer.Interval = 1000;
            waitTimer.Tick += delegate { OnWaitTick(); };

            Controls.Add(message);
            Controls.Add(btnProceed);
            Controls.Add(btnCancel);
            AcceptButton = btnProceed;
            CancelButton = btnCancel;
        }

        void OnProceed()
        {
            // First check: right after the user clicks Proceed
            if (!IsGameRunning()) { DialogResult = DialogResult.OK; return; }

            // Still running: wait 5 seconds, then check again
            btnProceed.Enabled = false;
            secondsLeft = Settings.GameCheckWaitSeconds;
            ShowCountdown();
            waitTimer.Start();
        }

        void OnWaitTick()
        {
            secondsLeft--;
            if (secondsLeft > 0) { ShowCountdown(); return; }

            waitTimer.Stop();

            // Second check
            if (!IsGameRunning()) { DialogResult = DialogResult.OK; return; }

            // Still running: ask again (loops until closed or cancelled)
            message.Text = "Project Zomboid is still running.\n\n" + AskText;
            btnProceed.Enabled = true;
        }

        void ShowCountdown()
        {
            message.Text = "Project Zomboid is still running.\n\nChecking again in " + secondsLeft +
                           " second" + (secondsLeft == 1 ? "" : "s") + "...";
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) waitTimer.Dispose();
            base.Dispose(disposing);
        }
    }

    // -------------------------------------------------------------------------
    //  Finding and validating ProjectZomboid64.json
    // -------------------------------------------------------------------------
    static class FileLocator
    {
        public static string FindJson()
        {
            // 1) Default Steam folder
            string def = Path.Combine(Settings.DefaultFolder, Settings.JsonName);
            if (IsValidPzJson(def))
                return def;

            // 2) Ask Steam where Project Zomboid is installed (no user interaction)
            string fromSteam = SteamLocator.FindJson();
            if (fromSteam != null)
                return fromSteam;

            // 3) Last resort: ask the user
            DialogResult r = MessageBox.Show(
                Settings.JsonName + " was not found in the default location or in any Steam library:\n\n" +
                Settings.DefaultFolder + "\n\n" +
                "Do you want to search your attached drives for it?\n\n" +
                "Yes = search all drives\n" +
                "No = pick the file myself\n" +
                "Cancel = exit",
                "PZ Memory Tool - File not found",
                MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

            if (r == DialogResult.Cancel)
                return null;

            if (r == DialogResult.Yes)
            {
                string found = SearchForm.RunSearch();
                if (found != null)
                    return found;

                if (MessageBox.Show("The file was not found on any drive.\n\nDo you want to pick it manually?",
                        "PZ Memory Tool", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    return null;
            }

            return BrowseForFile();
        }

        static string BrowseForFile()
        {
            while (true)
            {
                using (OpenFileDialog dlg = new OpenFileDialog())
                {
                    dlg.Title = "Locate " + Settings.JsonName;
                    dlg.Filter = Settings.JsonName + "|" + Settings.JsonName + "|JSON files (*.json)|*.json";
                    if (dlg.ShowDialog() != DialogResult.OK)
                        return null;
                    if (IsValidPzJson(dlg.FileName))
                        return dlg.FileName;
                    MessageBox.Show("That file does not look like Project Zomboid's " + Settings.JsonName +
                        ".\n(It must contain the Project Zomboid main class and an -Xmx memory argument.)",
                        "PZ Memory Tool", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        // "Correct file" check: right name, Project Zomboid main class, vmArgs and an -Xmx argument.
        public static bool IsValidPzJson(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                if (!string.Equals(Path.GetFileName(path), Settings.JsonName, StringComparison.OrdinalIgnoreCase)) return false;
                if (new FileInfo(path).Length > 1024 * 1024) return false;
                string text = File.ReadAllText(path);
                return text.Contains("zombie/gameStates/MainScreenState")
                    && text.Contains("\"vmArgs\"")
                    && XmxParser.Find(text) != null;
            }
            catch { return false; }
        }

        // Quick check of the usual Steam library locations on every drive.
        public static IEnumerable<string> LikelyLocations()
        {
            List<string> list = new List<string>();
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                if (!IsSearchable(d)) continue;
                string root = d.RootDirectory.FullName;
                list.Add(Path.Combine(root, @"Program Files (x86)\Steam\steamapps\common\ProjectZomboid"));
                list.Add(Path.Combine(root, @"Program Files\Steam\steamapps\common\ProjectZomboid"));
                list.Add(Path.Combine(root, @"SteamLibrary\steamapps\common\ProjectZomboid"));
                list.Add(Path.Combine(root, @"Steam\steamapps\common\ProjectZomboid"));
                list.Add(Path.Combine(root, @"Games\SteamLibrary\steamapps\common\ProjectZomboid"));
                list.Add(Path.Combine(root, @"Games\Steam\steamapps\common\ProjectZomboid"));
            }
            return list;
        }

        public static bool IsSearchable(DriveInfo d)
        {
            try
            {
                return d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable);
            }
            catch { return false; }
        }
    }

    // -------------------------------------------------------------------------
    //  Ask Steam where Project Zomboid is installed (read-only)
    //  Steam install folder (registry) -> libraryfolders.vdf (all Steam
    //  libraries) -> appmanifest_108600.acf (Project Zomboid's folder name)
    // -------------------------------------------------------------------------
    static class SteamLocator
    {
        public static string FindJson()
        {
            List<string> gameDirs = new List<string>();

            foreach (string steamRoot in SteamRoots())
            {
                foreach (string lib in SteamLibraries(steamRoot))
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest = Path.Combine(steamapps, "appmanifest_" + Settings.SteamAppId + ".acf");
                    string installDir = ReadVdfValue(manifest, "installdir");
                    if (installDir != null)
                        gameDirs.Add(Path.Combine(Path.Combine(steamapps, "common"), installDir));
                    gameDirs.Add(Path.Combine(Path.Combine(steamapps, "common"), "ProjectZomboid"));
                }
            }

            // Windows "installed programs" entry that Steam creates for each game
            AddRegistryPath(gameDirs, Registry.LocalMachine,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App " + Settings.SteamAppId, "InstallLocation");
            AddRegistryPath(gameDirs, Registry.LocalMachine,
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Steam App " + Settings.SteamAppId, "InstallLocation");

            foreach (string dir in gameDirs)
            {
                string p = Path.Combine(dir, Settings.JsonName);
                if (FileLocator.IsValidPzJson(p))
                    return p;
            }
            return null;
        }

        static List<string> SteamRoots()
        {
            List<string> roots = new List<string>();
            AddRegistryPath(roots, Registry.CurrentUser,  @"Software\Valve\Steam", "SteamPath");
            AddRegistryPath(roots, Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
            AddRegistryPath(roots, Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
            return roots;
        }

        static List<string> SteamLibraries(string steamRoot)
        {
            List<string> libs = new List<string>();
            libs.Add(steamRoot);
            string[] vdfs = {
                Path.Combine(Path.Combine(steamRoot, "steamapps"), "libraryfolders.vdf"),
                Path.Combine(Path.Combine(steamRoot, "config"), "libraryfolders.vdf")
            };
            foreach (string vdf in vdfs)
            {
                try
                {
                    if (!File.Exists(vdf)) continue;
                    string text = File.ReadAllText(vdf);
                    // New format: "path" "D:\\SteamLibrary"   Old format: "1" "D:\\SteamLibrary"
                    foreach (Match m in Regex.Matches(text, "\"(?:path|\\d+)\"\\s+\"([^\"]+)\""))
                    {
                        string lib = m.Groups[1].Value.Replace("\\\\", "\\");
                        if (Directory.Exists(lib) && !libs.Contains(lib))
                            libs.Add(lib);
                    }
                }
                catch { }
            }
            return libs;
        }

        static string ReadVdfValue(string file, string key)
        {
            try
            {
                if (!File.Exists(file)) return null;
                Match m = Regex.Match(File.ReadAllText(file),
                    "\"" + key + "\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase);
                return m.Success ? m.Groups[1].Value.Replace("\\\\", "\\") : null;
            }
            catch { return null; }
        }

        static void AddRegistryPath(List<string> list, RegistryKey hive, string subKey, string valueName)
        {
            try
            {
                using (RegistryKey k = hive.OpenSubKey(subKey))
                {
                    if (k == null) return;
                    string v = k.GetValue(valueName) as string;
                    if (string.IsNullOrEmpty(v)) return;
                    v = v.Replace('/', '\\');
                    if (Directory.Exists(v) && !list.Contains(v))
                        list.Add(v);
                }
            }
            catch { }
        }
    }

    // -------------------------------------------------------------------------
    //  Drive search window (runs the scan in the background)
    // -------------------------------------------------------------------------
    class SearchForm : ThemedForm
    {
        Label status = new Label();
        Button cancel = new Button();
        volatile bool cancelled;
        string result;

        public static string RunSearch()
        {
            using (SearchForm f = new SearchForm())
            {
                f.ShowDialog();
                return f.result;
            }
        }

        SearchForm()
        {
            Text = "Searching drives...";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(Ui.S(520), Ui.S(120));
            Font = Ui.Font(9f);

            Ui.Place(status, 15, 15, 490, 50);
            status.AutoEllipsis = true;
            status.Text = "Starting search...";

            cancel.Text = "Cancel";
            Ui.Place(cancel, 415, 78, 90, 28);
            cancel.Click += delegate { cancelled = true; status.Text = "Cancelling..."; };

            Controls.Add(status);
            Controls.Add(cancel);

            Shown += delegate
            {
                Thread t = new Thread(Search);
                t.IsBackground = true;
                t.Start();
            };
        }

        void SetStatus(string s)
        {
            try { BeginInvoke((MethodInvoker)delegate { status.Text = s; }); } catch { }
        }

        void Search()
        {
            // 1) Fast pass: common Steam library folders on every drive
            foreach (string dir in FileLocator.LikelyLocations())
            {
                if (cancelled) break;
                SetStatus("Checking " + dir);
                string p = Path.Combine(dir, Settings.JsonName);
                if (FileLocator.IsValidPzJson(p)) { result = p; break; }
            }

            // 2) Full pass: walk every folder on every drive
            if (result == null && !cancelled)
            {
                foreach (DriveInfo d in DriveInfo.GetDrives())
                {
                    if (cancelled || result != null) break;
                    if (!FileLocator.IsSearchable(d)) continue;
                    WalkDrive(d.RootDirectory.FullName);
                }
            }

            try { BeginInvoke((MethodInvoker)Close); } catch { }
        }

        void WalkDrive(string root)
        {
            Stack<string> stack = new Stack<string>();
            stack.Push(root);
            int count = 0;
            while (stack.Count > 0 && !cancelled && result == null)
            {
                string dir = stack.Pop();
                if (++count % 200 == 0) SetStatus("Searching " + dir);

                string candidate = Path.Combine(dir, Settings.JsonName);
                if (File.Exists(candidate) && FileLocator.IsValidPzJson(candidate))
                {
                    result = candidate;
                    return;
                }

                string[] subs;
                try { subs = Directory.GetDirectories(dir); }
                catch { continue; } // access denied, etc.

                foreach (string s in subs)
                {
                    try
                    {
                        // Skip junctions/symlinks to avoid loops; skip Windows folder for speed
                        if ((File.GetAttributes(s) & FileAttributes.ReparsePoint) != 0) continue;
                        string name = Path.GetFileName(s);
                        if (string.Equals(name, "Windows", StringComparison.OrdinalIgnoreCase) && dir == root) continue;
                        if (name.StartsWith("$")) continue;
                        stack.Push(s);
                    }
                    catch { }
                }
            }
        }
    }

    // -------------------------------------------------------------------------
    //  Reading / writing the -Xmx argument
    // -------------------------------------------------------------------------
    static class XmxParser
    {
        // Matches e.g. -Xmx3072m, -Xmx8g, -Xmx8G
        static readonly Regex XmxRegex = new Regex(@"-Xmx(\d+)([kKmMgG]?)");

        // Returns the regex match for the -Xmx argument, or null.
        public static Match Find(string text)
        {
            Match m = XmxRegex.Match(text);
            return m.Success ? m : null;
        }

        // Converts the -Xmx value to megabytes.
        public static long ToMB(Match m)
        {
            long n = long.Parse(m.Groups[1].Value);
            switch (m.Groups[2].Value.ToLowerInvariant())
            {
                case "g": return n * 1024;
                case "m": return n;
                case "k": return n / 1024;
                default:  return n / (1024 * 1024); // plain bytes
            }
        }

        // Replaces only the -Xmx argument, leaving the rest of the file untouched.
        public static string Replace(string text, int newGB)
        {
            return XmxRegex.Replace(text, "-Xmx" + (newGB * 1024) + "m", 1);
        }
    }

    // -------------------------------------------------------------------------
    //  Installed memory (uses the Windows API)
    // -------------------------------------------------------------------------
    static class SystemMemory
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalKB);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        class MEMORYSTATUSEX
        {
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                         ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX buffer);

        // Total installed RAM in MB (the number shown on the RAM sticks / in Task Manager).
        public static long InstalledMB()
        {
            try
            {
                ulong kb;
                if (GetPhysicallyInstalledSystemMemory(out kb) && kb > 0)
                    return (long)(kb / 1024);
            }
            catch { }

            MEMORYSTATUSEX s = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(s))
                return (long)(s.ullTotalPhys / (1024 * 1024));
            return 0;
        }
    }

    // -------------------------------------------------------------------------
    //  Animated "ComfyPlacebo's" banner - keygen / cracktro style
    //  Starfield scrolling right-to-left behind a logo with a moving colour
    //  wave. All colours are limited to the SNES 15-bit palette (5 bits per
    //  channel). 8 text rows high.
    // -------------------------------------------------------------------------
    class BannerRenderer
    {
        // Logo in "ANSI Shadow" style. Stored as plain ASCII so this file stays
        // readable in any editor:  # = solid block, = | r 7 L J = double-line
        // shadow pieces (horizontal, vertical, top-left, top-right, bottom-left,
        // bottom-right corners).
        static readonly string[] Logo = {
            " ######7 ######7 ###7   ###7#######7##7   ##7######7 ##7      #####7  ######7#######7######7  ######7  ##7 #######7",
            "##r====J##r===##7####7 ####|##r====JL##7 ##rJ##r==##7##|     ##r==##7##r====J##r====J##r==##7##r===##7 ##| ##r====J",
            "##|     ##|   ##|##r####r##|#####7   L####rJ ######rJ##|     #######|##|     #####7  ######rJ##|   ##| L=J #######7",
            "##|     ##|   ##|##|L##rJ##|##r==J    L##rJ  ##r===J ##|     ##r==##|##|     ##r==J  ##r==##7##|   ##|     L====##|",
            "L######7L######rJ##| L=J ##|##|        ##|   ##|     #######7##|  ##|L######7#######7######rJL######rJ     #######|",
            " L=====J L=====J L=J     L=JL=J        L=J   L=J     L======JL=J  L=J L=====JL======JL=====J  L=====J      L======J",
        };

        public const int Rows = 8;         // banner height in text rows (max 8)
        const int LogoTop = 1;             // logo starts on row 1 (rows 0 and 7 are open sky)
        const float CellAspect = 2.1f;     // text cell height / width
        const int StarCount = 70;
        static readonly float[] StarSpeed = { 0.18f, 0.45f, 1.1f };  // cells per frame, per layer

        static readonly int LogoWidth = MaxLen(Logo);
        public static readonly int Cols = LogoWidth + 4;

        readonly SolidBrush[] waveBrushes;   // bright colour wave for the blocks
        readonly Pen[] shadowPens;           // darker version for the shadow lines
        readonly SolidBrush[] starBrushes;
        readonly SolidBrush bgBrush;
        readonly Random rnd = new Random();
        readonly float[] sx = new float[StarCount];
        readonly int[] sy = new int[StarCount];
        readonly int[] layer = new int[StarCount];
        int frame;
        Font starFont;
        float starFontRowH;

        public BannerRenderer()
        {
            // Wave palette (think 90s cracktro copper bars): deep blue -> cyan ->
            // white -> pink -> magenta -> purple -> back to blue.
            Color[] keys = {
                Snes(16, 24, 160), Snes(0, 120, 248), Snes(0, 216, 248), Snes(224, 248, 248),
                Snes(248, 136, 216), Snes(232, 40, 184), Snes(136, 32, 216), Snes(40, 16, 168)
            };
            Color[] wave = Gradient(keys, 64);
            waveBrushes = new SolidBrush[wave.Length];
            shadowPens = new Pen[wave.Length];
            for (int i = 0; i < wave.Length; i++)
            {
                waveBrushes[i] = new SolidBrush(wave[i]);
                Color c = wave[i];
                shadowPens[i] = new Pen(Snes(c.R * 45 / 100, c.G * 45 / 100, c.B * 45 / 100 + 24), 1f);
            }

            starBrushes = new SolidBrush[] {
                new SolidBrush(Snes(64, 72, 128)),     // far, dim
                new SolidBrush(Snes(136, 160, 232)),   // middle
                new SolidBrush(Snes(248, 248, 248)),   // near, bright
                new SolidBrush(Snes(248, 232, 88)),    // near, twinkle (yellow)
                new SolidBrush(Snes(56, 64, 136))      // streak behind near stars
            };
            bgBrush = new SolidBrush(Snes(8, 8, 32));

            for (int i = 0; i < StarCount; i++)
            {
                sx[i] = (float)(rnd.NextDouble() * Cols);
                sy[i] = rnd.Next(Rows);
                layer[i] = i % 3;
            }
        }

        public static int HeightForWidth(int width)
        {
            return (int)Math.Round(width / (float)Cols * CellAspect * Rows);
        }

        // Advance the animation one frame (called ~25 times a second).
        public void Step()
        {
            frame++;
            for (int i = 0; i < StarCount; i++)
            {
                sx[i] -= StarSpeed[layer[i]];          // right -> left
                if (sx[i] < -2)
                {
                    sx[i] += Cols + 2;
                    sy[i] = rnd.Next(Rows);
                }
            }
        }

        public void Render(Graphics g, int width, int height)
        {
            float cw = width / (float)Cols;
            float ch = height / (float)Rows;
            int logoLeft = (Cols - LogoWidth) / 2;

            g.SmoothingMode = SmoothingMode.None;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
            g.FillRectangle(bgBrush, 0, 0, width, height);

            // Line thickness grows with display scaling (1 px at 100%)
            float lineW = Math.Max(1f, (float)Math.Round(cw / 4.7f));
            if (shadowPens[0].Width != lineW)
                foreach (Pen p in shadowPens) p.Width = lineW;
            float barH = 2 * lineW;

            // Scrolling colour bars at the very top and bottom edge
            for (int x = 0; x < Cols; x++)
            {
                SolidBrush b = waveBrushes[Mod(x + frame * 2, waveBrushes.Length)];
                float px = (float)Math.Floor(x * cw);
                float pw = (float)Math.Floor((x + 1) * cw) - px;
                g.FillRectangle(b, px, 0, pw, barH);
                g.FillRectangle(b, px, height - barH, pw, barH);
            }

            // Stars (only where the logo is empty)
            if (starFont == null || starFontRowH != ch)
            {
                if (starFont != null) starFont.Dispose();
                starFont = new Font(FontFamily.GenericMonospace, Math.Max(6f, ch * 0.95f), GraphicsUnit.Pixel);
                starFontRowH = ch;
            }
            for (int i = 0; i < StarCount; i++)
            {
                int cx = (int)Math.Round(sx[i]);
                if (cx < 0 || cx >= Cols || !IsSky(cx - logoLeft, sy[i] - LogoTop)) continue;
                string glyph;
                SolidBrush brush;
                switch (layer[i])
                {
                    case 0: glyph = "."; brush = starBrushes[0]; break;
                    case 1: glyph = "+"; brush = starBrushes[1]; break;
                    default:
                        glyph = "*";
                        brush = ((frame / 4 + i) % 5 == 0) ? starBrushes[3] : starBrushes[2];
                        // short streak behind the fastest stars
                        for (int k = 1; k <= 2; k++)
                            if (cx + k < Cols && IsSky(cx + k - logoLeft, sy[i] - LogoTop))
                                g.DrawString("-", starFont, starBrushes[4], (cx + k) * cw - cw * 0.3f, sy[i] * ch - ch * 0.05f);
                        break;
                }
                g.DrawString(glyph, starFont, brush, cx * cw - cw * 0.3f, sy[i] * ch - ch * 0.05f);
            }

            // Logo with a colour wave travelling right -> left
            for (int row = 0; row < Logo.Length; row++)
            {
                string line = Logo[row];
                float y0 = (float)Math.Floor((row + LogoTop) * ch);
                float y1 = (float)Math.Floor((row + LogoTop + 1) * ch);
                for (int col = 0; col < line.Length; col++)
                {
                    char c = line[col];
                    if (c == ' ') continue;
                    int x = col + logoLeft;
                    float x0 = (float)Math.Floor(x * cw);
                    float x1 = (float)Math.Floor((x + 1) * cw);

                    int idx = x + frame + row * 2 + (int)(3 * Math.Sin((x + frame) * 0.12));
                    idx = Mod(idx, waveBrushes.Length);

                    if (c == '#')
                        g.FillRectangle(waveBrushes[idx], x0, y0, x1 - x0, y1 - y0);
                    else
                        DrawShadow(g, shadowPens[idx], c, x0, y0, x1, y1);
                }
            }
        }

        // Draws a double-line box piece (the "shadow" of the ANSI Shadow font).
        static void DrawShadow(Graphics g, Pen p, char c, float l, float t, float r, float b)
        {
            float w = r - l, h = b - t;
            float xa = l + w * 0.25f, xb = l + w * 0.70f;   // two vertical lines
            float ya = t + h * 0.35f, yb = t + h * 0.65f;   // two horizontal lines
            switch (c)
            {
                case '=': g.DrawLine(p, l, ya, r, ya); g.DrawLine(p, l, yb, r, yb); break;
                case '|': g.DrawLine(p, xa, t, xa, b); g.DrawLine(p, xb, t, xb, b); break;
                case 'r': // top-left corner: lines go right and down
                    g.DrawLines(p, new PointF[] { new PointF(xa, b), new PointF(xa, ya), new PointF(r, ya) });
                    g.DrawLines(p, new PointF[] { new PointF(xb, b), new PointF(xb, yb), new PointF(r, yb) });
                    break;
                case '7': // top-right corner: lines go left and down
                    g.DrawLines(p, new PointF[] { new PointF(l, ya), new PointF(xb, ya), new PointF(xb, b) });
                    g.DrawLines(p, new PointF[] { new PointF(l, yb), new PointF(xa, yb), new PointF(xa, b) });
                    break;
                case 'L': // bottom-left corner: lines go up and right
                    g.DrawLines(p, new PointF[] { new PointF(xa, t), new PointF(xa, yb), new PointF(r, yb) });
                    g.DrawLines(p, new PointF[] { new PointF(xb, t), new PointF(xb, ya), new PointF(r, ya) });
                    break;
                case 'J': // bottom-right corner: lines go up and left
                    g.DrawLines(p, new PointF[] { new PointF(xb, t), new PointF(xb, yb), new PointF(l, yb) });
                    g.DrawLines(p, new PointF[] { new PointF(xa, t), new PointF(xa, ya), new PointF(l, ya) });
                    break;
            }
        }

        static bool IsSky(int col, int row)
        {
            if (row < 0 || row >= Logo.Length || col < 0 || col >= Logo[row].Length) return true;
            return Logo[row][col] == ' ';
        }

        // Snap a colour to the SNES 15-bit palette (5 bits per channel).
        static Color Snes(int r, int g, int b)
        {
            r = Math.Max(0, Math.Min(255, r)) >> 3;
            g = Math.Max(0, Math.Min(255, g)) >> 3;
            b = Math.Max(0, Math.Min(255, b)) >> 3;
            return Color.FromArgb((r << 3) | (r >> 2), (g << 3) | (g >> 2), (b << 3) | (b >> 2));
        }

        static Color[] Gradient(Color[] keys, int steps)
        {
            Color[] result = new Color[steps];
            for (int i = 0; i < steps; i++)
            {
                double pos = i * keys.Length / (double)steps;
                int k = (int)pos;
                double f = pos - k;
                Color a = keys[k], b = keys[(k + 1) % keys.Length];
                result[i] = Snes((int)(a.R + (b.R - a.R) * f), (int)(a.G + (b.G - a.G) * f), (int)(a.B + (b.B - a.B) * f));
            }
            return result;
        }

        static int MaxLen(string[] lines)
        {
            int m = 0;
            foreach (string s in lines) m = Math.Max(m, s.Length);
            return m;
        }

        static int Mod(int a, int n) { int r = a % n; return r < 0 ? r + n : r; }
    }

    class Banner : Control
    {
        readonly BannerRenderer renderer = new BannerRenderer();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();

        public Banner()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            timer.Interval = 40; // ~25 frames per second
            timer.Tick += delegate { renderer.Step(); Invalidate(); };
            timer.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            renderer.Render(e.Graphics, Width, Height);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }
    }

    // -------------------------------------------------------------------------
    //  Main window
    // -------------------------------------------------------------------------
    class MainForm : ThemedForm
    {
        readonly string jsonPath;
        long installedMB, currentMB;
        int recommendedGB;
        int tier;          // 32, 16, 12, 8, or 0 (under 8 GB)

        Banner banner = new Banner();
        Label lblPath = new Label();
        Label lblInstalled = new Label();
        Label lblCurrent = new Label();
        Label lblRecommend = new Label();
        Label lblWarning = new Label();
        Label lblNote = new Label();
        ComboBox cboGB = new ComboBox();
        Button btnRecommended = new Button();
        LinkLabel lnkKofi = new LinkLabel();
        Button btnSave = new Button();
        Button btnRestore = new Button();
        Button btnClose = new Button();

        public MainForm(string path)
        {
            jsonPath = path;

            const int W = 560;   // layout is designed at 100% scaling; Ui.S / Ui.Place scale it
            Text = "PZ Memory Tool - Project Zomboid RAM allocation";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;

            int bannerH = BannerRenderer.HeightForWidth(W);
            banner.SetBounds(0, 0, Ui.S(W), BannerRenderer.HeightForWidth(Ui.S(W)));

            int y = bannerH + 4;
            Label title = new Label();
            title.Text = "Project Zomboid Memory Allocation Tool";
            title.Font = Ui.Font(14f, FontStyle.Bold);
            Ui.Place(title, 15, y, 530, 32); y += 38;

            Ui.Place(lblPath, 15, y, 530, 36); lblPath.Tag = "sub";
            lblPath.Font = Ui.Font(8.5f); y += 42;

            Ui.Place(lblInstalled, 15, y, 530, 24); lblInstalled.Font = Ui.Font(11f, FontStyle.Bold); y += 28;
            Ui.Place(lblCurrent, 15, y, 530, 24);   lblCurrent.Font = Ui.Font(11f, FontStyle.Bold); y += 28;
            Ui.Place(lblRecommend, 15, y, 530, 24); y += 34;

            Label lblNew = new Label();
            lblNew.Text = "New allocation (GB):";
            Ui.Place(lblNew, 15, y + 3, 150, 24);
            cboGB.DropDownStyle = ComboBoxStyle.DropDownList;
            Ui.Place(cboGB, 170, y, 80, 28);
            cboGB.SelectedIndexChanged += delegate { UpdateWarning(); };
            btnRecommended.Text = "Use recommended";
            Ui.Place(btnRecommended, 265, y - 1, 150, 30);
            btnRecommended.Click += delegate { SelectGB(recommendedGB); };
            y += 42;

            // Warnings first, then the "try the lower value first" note below them
            Ui.Place(lblWarning, 15, y, 530, 64);
            lblWarning.Tag = "warn";
            y += 66;
            Ui.Place(lblNote, 15, y, 530, 40);
            lblNote.Tag = "note";
            lblNote.Font = Ui.Font(10f, FontStyle.Italic);
            y += 44;

            // Ko-fi link, right above the Save button
            lnkKofi.Text = "Support ComfyPlacebo on Ko-fi: ko-fi.com/comfyplacebo";
            lnkKofi.LinkBehavior = LinkBehavior.HoverUnderline;
            Ui.Place(lnkKofi, 15, y, 530, 24);
            lnkKofi.LinkClicked += delegate { OpenKofi(); };
            y += 30;

            btnSave.Text = "Save";
            btnSave.Tag = "primary";
            Ui.Place(btnSave, 15, y, 120, 34);
            btnSave.Font = Ui.Font(10f, FontStyle.Bold);
            btnSave.Click += delegate { Save(); };

            btnRestore.Text = "Restore backup";
            Ui.Place(btnRestore, 145, y, 140, 34);
            btnRestore.Click += delegate { RestoreBackup(); };

            btnClose.Text = "Close";
            Ui.Place(btnClose, 435, y, 110, 34);
            btnClose.Click += delegate { Close(); };
            y += 34 + 15;

            ClientSize = new Size(Ui.S(W), Ui.S(y));

            Controls.AddRange(new Control[] { banner, title, lblPath, lblInstalled, lblCurrent, lblRecommend,
                lblNew, cboGB, btnRecommended, lblWarning, lblNote, lnkKofi, btnSave, btnRestore, btnClose });

            LoadValues();
        }

        int SelectedGB
        {
            get { return cboGB.SelectedItem == null ? 0 : (int)cboGB.SelectedItem; }
        }

        void SelectGB(int gb)
        {
            if (cboGB.Items.Contains(gb)) cboGB.SelectedItem = gb;
        }

        void LoadValues()
        {
            string text = File.ReadAllText(jsonPath);
            Match m = XmxParser.Find(text);
            currentMB = XmxParser.ToMB(m);
            installedMB = SystemMemory.InstalledMB();

            double installedGB = installedMB / 1024.0;
            int installedWholeGB = (int)Math.Round(installedGB);

            // Never more than installed RAM minus 2 GB, never less than 5 GB
            int maxGB = installedWholeGB - Settings.WindowsReserveGB;

            List<int> options = new List<int>();
            string recommendText;

            if (installedWholeGB >= Settings.Tier32)
            {
                tier = 32; recommendedGB = 8; recommendText = "8 to 12 GB";
                for (int gb = Settings.MinAllocGB; gb <= maxGB; gb++) options.Add(gb);
            }
            else if (installedWholeGB >= Settings.Tier16)
            {
                tier = 16; recommendedGB = 8; recommendText = "8 GB";
                for (int gb = Settings.MinAllocGB; gb <= maxGB; gb++) options.Add(gb);
            }
            else if (installedWholeGB >= Settings.Tier12)
            {
                tier = 12; recommendedGB = 6; recommendText = "6 GB or 8 GB (only these two options)";
                foreach (int gb in new int[] { 6, 8 }) if (gb <= maxGB) options.Add(gb);
            }
            else if (installedWholeGB >= Settings.Tier8)
            {
                tier = 8; recommendedGB = 5; recommendText = "5 GB or 6 GB (only these two options)";
                foreach (int gb in new int[] { 5, 6 }) if (gb <= maxGB) options.Add(gb);
            }
            else
            {
                tier = 0; recommendedGB = 0; recommendText = "None - less than 8 GB installed";
            }

            lblPath.Text = "File: " + jsonPath;
            lblInstalled.Text = "Total installed memory:   " + installedGB.ToString("0.#") + " GB";
            lblCurrent.Text   = "Current allocation (-Xmx):   " + (currentMB / 1024.0).ToString("0.#") +
                                " GB   (" + m.Value + ")";
            lblRecommend.Text = "Recommended allocation for this PC:   " + recommendText;

            cboGB.Items.Clear();
            foreach (int gb in options) cboGB.Items.Add(gb);
            if (options.Count > 0)
            {
                if (options.Contains(recommendedGB)) SelectGB(recommendedGB);
                else cboGB.SelectedIndex = 0;
            }

            // Under 8 GB: everything locked except Close
            bool locked = (tier == 0 || options.Count == 0);
            cboGB.Enabled = !locked;
            btnRecommended.Enabled = !locked;
            btnSave.Enabled = !locked;
            btnRestore.Enabled = !locked && File.Exists(jsonPath + ".bak");
            UpdateWarning();
        }

        void UpdateWarning()
        {
            int sel = SelectedGB;
            string w = "";
            string note = "";

            switch (tier)
            {
                case 32:
                    if (sel > 10)
                        w = "Advanced users only: allocations above 10 GB are intended for advanced users.";
                    if (sel > 12)
                        w += " This is also above the recommended range of 8 to 12 GB.";
                    break;
                case 16:
                    if (sel > 8)
                        w = "Setting more than 8 GB on a 16 GB system is not recommended.";
                    break;
                case 12:
                    w = "WARNING: You should consider upgrading to " + Settings.RecommendedUpgrade +
                        " GB or more of total installed memory.";
                    note = "Try 6 GB first. If that doesn't work, try 8 GB.";
                    break;
                case 8:
                    w = "WARNING: Running Project Zomboid with 8 GB of RAM is unsupported. " +
                        "It is recommended to upgrade to at least " + Settings.RecommendedUpgrade +
                        " GB of total installed memory.";
                    note = "Try 5 GB first. If that doesn't work, try 6 GB.";
                    break;
                default:
                    w = "WARNING: This PC has less than 8 GB of total installed memory. " +
                        "It is not advised to attempt to play on heavily modified servers. " +
                        "The memory allocation cannot be changed with this tool.";
                    break;
            }

            lblWarning.Text = w;
            lblNote.Text = note;
        }

        void Save()
        {
            int gb = SelectedGB;
            if (gb < Settings.MinAllocGB) return;

            // Make sure the game wasn't started while this window was open
            if (!ProcessGuard.EnsureGameClosed()) return;

            try
            {
                string text = File.ReadAllText(jsonPath);
                string backup = jsonPath + ".bak";
                if (!File.Exists(backup))
                    File.Copy(jsonPath, backup);   // keep the very first original as backup

                File.WriteAllText(jsonPath, XmxParser.Replace(text, gb));
                MessageBox.Show("Saved. Project Zomboid will now use " + gb + " GB (-Xmx" + (gb * 1024) + "m).\n\n" +
                    "A backup of the original file is at:\n" + backup,
                    "PZ Memory Tool", MessageBoxButtons.OK, MessageBoxIcon.Information);
                LoadValues();
            }
            catch (UnauthorizedAccessException)
            {
                OfferAdminRelaunch();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not save the file:\n" + ex.Message, "PZ Memory Tool",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Opens the Ko-fi page in the default web browser.
        void OpenKofi()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Settings.KofiUrl);
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch
            {
                MessageBox.Show("Could not open your web browser. You can visit:\n" + Settings.KofiUrl,
                    "PZ Memory Tool", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        void RestoreBackup()
        {
            string backup = jsonPath + ".bak";
            if (!File.Exists(backup)) return;
            if (MessageBox.Show("Restore the original file from the backup?", "PZ Memory Tool",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            if (!ProcessGuard.EnsureGameClosed()) return;
            try
            {
                File.Copy(backup, jsonPath, true);
                LoadValues();
                MessageBox.Show("Original file restored.", "PZ Memory Tool");
            }
            catch (UnauthorizedAccessException) { OfferAdminRelaunch(); }
            catch (Exception ex) { MessageBox.Show("Restore failed:\n" + ex.Message, "PZ Memory Tool"); }
        }

        // Files under Program Files need administrator rights to change.
        void OfferAdminRelaunch()
        {
            if (MessageBox.Show("Windows needs administrator permission to change files in this folder.\n\n" +
                    "Restart this tool as administrator?", "PZ Memory Tool",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
                != DialogResult.Yes) return;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath, "\"" + jsonPath + "\"");
                psi.Verb = "runas";
                psi.UseShellExecute = true;
                Process.Start(psi);
                Close();
            }
            catch { /* user pressed No on the Windows prompt */ }
        }
    }
}
