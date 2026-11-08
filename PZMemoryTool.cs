// =============================================================================
//  PZ Memory Tool
//  Adjusts the Java heap size (-Xmx) in Project Zomboid's ProjectZomboid64.json
//
//  Open source - single file, no network access (the Ko-fi link only opens
//  your web browser when you click it), no installer, no registry
//  changes (it only READS Steam's registry entries to find the game, and the
//  Windows dark/light mode and accent colour settings to match them). It
//  writes ProjectZomboid64.json (after making a .bak backup copy next to it)
//  and, only if they contain -Xmx / -Xms, Steam's launch options for Project
//  Zomboid in localconfig.vdf (also backed up first). The Console Copier
//  puts a copy of console.txt in your Temp folder so it can go on the clipboard.
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
using System.Reflection;

// File details shown in Windows (right-click the .exe > Properties > Details)
[assembly: AssemblyTitle("PZ Memory Tool")]
[assembly: AssemblyDescription("Sets Project Zomboid's memory allocation (-Xmx / -Xms) and copies console.txt. Open source.")]
[assembly: AssemblyCompany("ComfyPlacebo")]
[assembly: AssemblyProduct("PZ Memory Tool")]
[assembly: AssemblyCopyright("Copyright (c) 2026 ComfyPlacebo")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]
[assembly: AssemblyInformationalVersion("1.1.0")]

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

            // The tool opens even while Project Zomboid is running (to check the allocation or
            // use the Console Copier). Save and Restore ask for the game to be closed first.

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
        public const string Version = "1.1.0";
        public const string DefaultFolder =
            @"C:\Program Files (x86)\Steam\steamapps\common\ProjectZomboid";
        public const string JsonName = "ProjectZomboid64.json";
        public const string GameProcessName = "ProjectZomboid64"; // ProjectZomboid64.exe
        public const string SteamProcessName = "steam";           // steam.exe
        public const string SteamAppId = "108600";                // Project Zomboid on Steam

        public const int MinAllocGB        = 5;  // never allow less than this
        public const int DefaultXmsGB      = 4;  // -Xms (minimum / starting memory) selected by default
        public const int MinXmsGB          = 4;  // lowest -Xms offered; the highest is the selected -Xmx minus 1 GB
                                                 // (8 GB systems never get -Xms: the option is locked off there)
        public const int WindowsReserveGB  = 2;  // always leave this much for Windows
        public const int RecommendedUpgrade = 16;
        public const int GameCheckWaitSeconds = 5;
        public const int SteamCloseWaitSeconds = 30;  // how long "Close Steam for me" waits for Steam to exit
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
    //  Make sure a program (Project Zomboid or Steam) is not running.
    //  Ask the user to close it and click Proceed; after Proceed check again,
    //  wait 5 seconds and check once more, then keep asking until it is closed
    //  or the user cancels / skips.
    // -------------------------------------------------------------------------
    class ProcessGuard : ThemedForm
    {
        Label message = new Label();
        Button btnProceed = new Button();
        Button btnCancel = new Button();
        System.Windows.Forms.Timer waitTimer = new System.Windows.Forms.Timer();
        int secondsLeft;
        readonly string processName, displayName, askText;

        public static bool IsRunning(string processName)
        {
            Process[] procs = Process.GetProcessesByName(processName);
            bool running = procs.Length > 0;
            foreach (Process p in procs) p.Dispose();
            return running;
        }

        public static bool IsGameRunning() { return IsRunning(Settings.GameProcessName); }

        // Returns true when the game is closed, false if the user cancelled.
        public static bool EnsureGameClosed()
        {
            return EnsureClosed(Settings.GameProcessName, "Project Zomboid",
                "Project Zomboid (ProjectZomboid64.exe) is currently running.\n\n" +
                "Please close Project Zomboid, then click Proceed.", "Cancel");
        }

        // Returns true when Steam is closed, false if the user chose to skip.
        // reason: what the tool wants to do to the launch options, e.g. "remove the memory settings from".
        public static bool EnsureSteamClosed(string reason, string skipMeans)
        {
            return EnsureClosed(Settings.SteamProcessName, "Steam",
                "Steam is running. To " + reason + " Project Zomboid's Steam launch options, " +
                "Steam must be fully closed first, otherwise Steam puts the old settings back.\n\n" +
                "Click \"Close Steam for me\", or close Steam yourself (Steam > Exit) and click Proceed. " +
                "Click Skip to " + skipMeans + ".", "Skip", true);
        }

        static bool EnsureClosed(string processName, string displayName, string askText, string cancelText)
        {
            return EnsureClosed(processName, displayName, askText, cancelText, false);
        }

        static bool EnsureClosed(string processName, string displayName, string askText, string cancelText, bool offerCloseSteam)
        {
            if (!IsRunning(processName))
                return true;
            using (ProcessGuard f = new ProcessGuard(processName, displayName, askText, cancelText, offerCloseSteam))
                return f.ShowDialog() == DialogResult.OK;
        }

        Button btnCloseSteam;
        System.Windows.Forms.Timer closeTimer;
        int closeSecondsLeft;

        ProcessGuard(string processName, string displayName, string askText, string cancelText, bool offerCloseSteam)
        {
            this.processName = processName;
            this.displayName = displayName;
            this.askText = askText;

            Text = "PZ Memory Tool - " + displayName + " is running";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            int h = offerCloseSteam ? 200 : 170;
            int by = h - 45;
            ClientSize = new Size(Ui.S(440), Ui.S(h));

            Ui.Place(message, 15, 15, 410, by - 25);
            message.Text = askText;

            if (offerCloseSteam)
            {
                btnCloseSteam = new Button();
                btnCloseSteam.Text = "Close Steam for me";
                btnCloseSteam.Tag = "primary";
                Ui.Place(btnCloseSteam, 15, by, 160, 32);
                btnCloseSteam.Click += delegate { CloseSteamForMe(); };
                Controls.Add(btnCloseSteam);

                closeTimer = new System.Windows.Forms.Timer();
                closeTimer.Interval = 1000;
                closeTimer.Tick += delegate { OnCloseTick(); };
            }

            btnProceed.Text = "Proceed";
            if (!offerCloseSteam) btnProceed.Tag = "primary";
            Ui.Place(btnProceed, 225, by, 95, 32);
            btnProceed.Click += delegate { OnProceed(); };

            btnCancel.Text = cancelText;
            Ui.Place(btnCancel, 330, by, 95, 32);
            btnCancel.Click += delegate { waitTimer.Stop(); if (closeTimer != null) closeTimer.Stop(); DialogResult = DialogResult.Cancel; };

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
            if (!IsRunning(processName)) { DialogResult = DialogResult.OK; return; }

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
            if (!IsRunning(processName)) { DialogResult = DialogResult.OK; return; }

            // Still running: ask again (loops until closed or cancelled)
            message.Text = displayName + " is still running.\n\n" + askText;
            btnProceed.Enabled = true;
        }

        void ShowCountdown()
        {
            message.Text = displayName + " is still running.\n\nChecking again in " + secondsLeft +
                           " second" + (secondsLeft == 1 ? "" : "s") + "...";
        }

        // Asks Steam to shut down normally (same as Steam > Exit), then waits for it to finish.
        void CloseSteamForMe()
        {
            btnCloseSteam.Enabled = false;
            btnProceed.Enabled = false;
            waitTimer.Stop();
            SteamLaunchOptions.RequestSteamShutdown();
            closeSecondsLeft = Settings.SteamCloseWaitSeconds;
            message.Text = "Closing Steam...\n\nWaiting for Steam to finish closing. This can take a few seconds.";
            closeTimer.Start();
        }

        void OnCloseTick()
        {
            if (!IsRunning(processName))
            {
                closeTimer.Stop();
                message.Text = "Steam is closed.";
                Thread.Sleep(1000);   // give Steam a moment to finish writing its settings
                DialogResult = DialogResult.OK;
                return;
            }
            closeSecondsLeft--;
            if (closeSecondsLeft > 0)
            {
                message.Text = "Closing Steam...\n\nWaiting for Steam to finish closing (" + closeSecondsLeft + "s).";
                return;
            }
            closeTimer.Stop();
            message.Text = "Steam is taking a long time to close.\n\n" +
                           "Close it from the Steam icon in the taskbar tray (right-click > Exit), then click Proceed. " +
                           "Or click \"Close Steam for me\" to try again.";
            btnCloseSteam.Enabled = true;
            btnProceed.Enabled = true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { waitTimer.Dispose(); if (closeTimer != null) closeTimer.Dispose(); }
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

        public static List<string> SteamRoots()
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
    //  Reading / writing the -Xmx (maximum) and -Xms (minimum) arguments in
    //  ProjectZomboid64.json
    // -------------------------------------------------------------------------
    static class XmxParser
    {
        // Matches e.g. -Xmx3072m, -Xmx8g, -Xmx8G
        static readonly Regex XmxRegex = new Regex(@"-Xmx(\d+)([kKmMgG]?)");
        static readonly Regex XmsRegex = new Regex(@"-Xms(\d+)([kKmMgG]?)");
        // The quoted "-Xmx..." / "-Xms..." entries in the vmArgs list
        static readonly Regex XmxEntryRegex = new Regex(@"""-Xmx\d+[kKmMgG]?""");
        static readonly Regex XmsEntryRegex = new Regex(@"""-Xms\d+[kKmMgG]?""");

        // Returns the regex match for the -Xmx argument, or null.
        public static Match Find(string text)
        {
            Match m = XmxRegex.Match(text);
            return m.Success ? m : null;
        }

        // Returns the regex match for the -Xms argument, or null.
        public static Match FindXms(string text)
        {
            Match m = XmsRegex.Match(text);
            return m.Success ? m : null;
        }

        // Converts an -Xmx / -Xms value to megabytes.
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

        // The whole "-Xms..." entry line in the vmArgs list (with its comma, if any)
        static readonly Regex XmsLineRegex = new Regex(@"(?m)^[ \t]*""-Xms\d+[kKmMgG]?""[ \t]*(,?)[ \t]*\r?\n");

        // Sets -Xmx (maximum) and -Xms (minimum), leaving the rest of the file untouched.
        // -Xms is always written directly BEFORE -Xmx: on its own line if -Xmx is on its
        // own line, otherwise on the same line (", "). An existing -Xms is moved there.
        // xmsGB = 0 means no -Xms at all: any existing -Xms entry is removed.
        public static string SetMemory(string text, int xmxGB, int xmsGB)
        {
            string xmx = "-Xmx" + (xmxGB * 1024) + "m";
            text = RemoveXms(XmxRegex.Replace(text, xmx, 1));
            if (xmsGB <= 0)
                return text;
            string xms = "-Xms" + (xmsGB * 1024) + "m";

            Match entry = XmxEntryRegex.Match(text);
            if (!entry.Success)
                return text;   // -Xmx not in quotes: can't safely add -Xms (Save reports it)

            // Is "-Xmx..." alone on its line? Then put -Xms on the line above with the same indentation.
            int lineStart = entry.Index == 0 ? 0 : text.LastIndexOf('\n', entry.Index - 1) + 1;
            string indent = text.Substring(lineStart, entry.Index - lineStart);
            bool ownLine = indent.Trim().Length == 0;
            string newline = text.Contains("\r\n") ? "\r\n" : "\n";
            string between = ownLine ? "," + newline + indent : ", ";

            return text.Substring(0, entry.Index) + "\"" + xms + "\"" + between + text.Substring(entry.Index);
        }

        // Removes the -Xms entry from the vmArgs list, keeping the list valid JSON.
        static string RemoveXms(string text)
        {
            Match m = XmsLineRegex.Match(text);
            if (!m.Success)
            {
                // -Xms shares a line with other entries: remove it and one neighbouring comma
                Match e = XmsEntryRegex.Match(text);
                if (!e.Success) return text;
                Match commaAfter = new Regex(@"\G[ \t]*,[ \t]*").Match(text, e.Index + e.Length);
                if (commaAfter.Success)
                    return text.Substring(0, e.Index) + text.Substring(commaAfter.Index + commaAfter.Length);
                Match commaBefore = new Regex(@",[ \t]*$").Match(text.Substring(0, e.Index));
                if (commaBefore.Success)
                    return text.Substring(0, commaBefore.Index) + text.Substring(e.Index + e.Length);
                return text.Substring(0, e.Index) + text.Substring(e.Index + e.Length);
            }
            string before = text.Substring(0, m.Index), after = text.Substring(m.Index + m.Length);
            if (m.Groups[1].Value.Length == 0)
            {
                // -Xms was the last entry: drop the comma after the entry before it
                int c = before.TrimEnd(' ', '\t', '\r', '\n').Length - 1;
                if (c >= 0 && before[c] == ',') before = before.Remove(c, 1);
            }
            return before + after;
        }

        public static string Describe(long mb)
        {
            return (mb / 1024.0).ToString("0.#") + " GB";
        }
    }

    // -------------------------------------------------------------------------
    //  Steam launch options for Project Zomboid
    //  Steam keeps them per Steam account in
    //  <Steam>\userdata\<account>\config\localconfig.vdf under
    //  Software > Valve > Steam > apps > 108600 > "LaunchOptions".
    //  Launch options override ProjectZomboid64.json, so any -Xmx / -Xms there
    //  would undo this tool's setting.
    // -------------------------------------------------------------------------
    class LaunchOptionsEntry
    {
        public string ConfigFile;   // the localconfig.vdf it came from
        public string Value;        // the launch options text, as stored
        public int ValueStart;      // where the text sits in the file
        public int ValueLength;
    }

    static class SteamLaunchOptions
    {
        static readonly Regex MemArgRegex = new Regex(@"(?<!\S)-Xm[xs]\d+[kKmMgG]?(?!\S)");

        // The localconfig.vdf of the Steam account that is signed in (or was signed in last).
        // Only that account is checked and changed. Empty list if it can't be found.
        public static List<string> ConfigFiles()
        {
            List<string> files = new List<string>();
            long account = CurrentAccountId();
            if (account <= 0) return files;
            foreach (string root in SteamLocator.SteamRoots())
            {
                string f = Path.Combine(Path.Combine(Path.Combine(Path.Combine(root, "userdata"),
                               account.ToString()), "config"), "localconfig.vdf");
                if (File.Exists(f) && !files.Contains(f)) { files.Add(f); break; }
            }
            return files;
        }

        // Steam account number (the folder name under userdata).
        // 1) the account Steam reports as signed in right now, 2) the most recent sign-in.
        public static long CurrentAccountId()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess"))
                {
                    object v = k == null ? null : k.GetValue("ActiveUser");
                    if (v is int && (int)v != 0) return (long)(uint)(int)v;
                }
            }
            catch { }

            foreach (string root in SteamLocator.SteamRoots())
            {
                try
                {
                    string f = Path.Combine(Path.Combine(root, "config"), "loginusers.vdf");
                    if (!File.Exists(f)) continue;
                    foreach (Match m in Regex.Matches(File.ReadAllText(f), "\"(\\d{17})\"\\s*\\{([^}]*)\\}"))
                    {
                        if (Regex.IsMatch(m.Groups[2].Value, "\"MostRecent\"\\s+\"1\"", RegexOptions.IgnoreCase))
                            return long.Parse(m.Groups[1].Value) - 76561197960265728L;
                    }
                }
                catch { }
            }
            return 0;
        }

        // Asks Steam to close normally (like Steam > Exit).
        public static void RequestSteamShutdown()
        {
            foreach (string root in SteamLocator.SteamRoots())
            {
                string exe = Path.Combine(root, "steam.exe");
                if (!File.Exists(exe)) continue;
                try { Process.Start(exe, "-shutdown"); return; } catch { }
            }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("steam://exit");
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch { }
        }

        public static string BackupPath(string configFile) { return configFile + ".pzmemorytool.bak"; }

        // The launch options as they were before this tool changed them (null if no backup).
        public static string BackedUpValue(string configFile)
        {
            try
            {
                string b = BackupPath(configFile);
                if (!File.Exists(b)) return null;
                LaunchOptionsEntry e = Locate(File.ReadAllText(b));
                return e == null ? null : e.Value;
            }
            catch { return null; }
        }

        // The launch options right now ("" if Project Zomboid has none set).
        public static string CurrentValue(string configFile)
        {
            try
            {
                LaunchOptionsEntry e = Locate(File.ReadAllText(configFile));
                return e == null ? "" : e.Value;
            }
            catch { return ""; }
        }

        // Puts back only Project Zomboid's launch options from the backup.
        // Every other Steam setting is left as it is now. Returns true if changed.
        public static bool RestoreFromBackup(string configFile)
        {
            string old = BackedUpValue(configFile);
            if (old == null) return false;
            string text = File.ReadAllText(configFile);
            LaunchOptionsEntry e = Locate(text);
            if (e == null || e.Value == old) return false;
            File.WriteAllText(configFile, text.Substring(0, e.ValueStart) + old + text.Substring(e.ValueStart + e.ValueLength));
            return true;
        }

        // Project Zomboid's launch options in every account that has some.
        public static List<LaunchOptionsEntry> FindAll()
        {
            List<LaunchOptionsEntry> list = new List<LaunchOptionsEntry>();
            foreach (string f in ConfigFiles())
            {
                try
                {
                    LaunchOptionsEntry e = Locate(File.ReadAllText(f));
                    if (e != null) { e.ConfigFile = f; list.Add(e); }
                }
                catch { }
            }
            return list;
        }

        // Just the -Xmx / -Xms parts, e.g. "-Xmx6g -Xms2g" ("" if none).
        public static string MemoryArgs(string launchOptions)
        {
            List<string> found = new List<string>();
            foreach (Match m in MemArgRegex.Matches(launchOptions)) found.Add(m.Value);
            return string.Join(" ", found.ToArray());
        }

        // Launch options with -Xmx / -Xms taken out, everything else kept.
        public static string WithoutMemoryArgs(string launchOptions)
        {
            string s = MemArgRegex.Replace(launchOptions, "");
            s = Regex.Replace(s, @"\s+", " ").Trim();
            if (s == "--") s = "";   // only the "end of Java options" marker was left
            return s;
        }

        // Removes -Xmx / -Xms from one account's launch options. Makes a backup of
        // localconfig.vdf first. Returns the backup path.
        public static string RemoveMemoryArgs(string configFile)
        {
            string text = File.ReadAllText(configFile);       // re-read: Steam may have rewritten it
            LaunchOptionsEntry e = Locate(text);
            if (e == null || MemoryArgs(e.Value).Length == 0) return null;

            string backup = BackupPath(configFile);
            if (!File.Exists(backup) || MemoryArgs(BackedUpValue(configFile) ?? "").Length == 0)
                File.Copy(configFile, backup, true);   // keep the copy that still has the user's original options

            string updated = text.Substring(0, e.ValueStart) + WithoutMemoryArgs(e.Value) +
                             text.Substring(e.ValueStart + e.ValueLength);
            File.WriteAllText(configFile, updated);
            return backup;
        }

        // Finds Project Zomboid's "LaunchOptions" value in localconfig.vdf text.
        // A small reader for Steam's KeyValues format: "key" "value" and "key" { ... }.
        static LaunchOptionsEntry Locate(string text)
        {
            List<string> path = new List<string>();
            string pendingKey = null;
            int i = 0, n = text.Length;
            while (i < n)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '/' && i + 1 < n && text[i + 1] == '/')
                {
                    while (i < n && text[i] != '\n') i++;
                    continue;
                }
                if (c == '{') { path.Add(pendingKey ?? ""); pendingKey = null; i++; continue; }
                if (c == '}') { if (path.Count > 0) path.RemoveAt(path.Count - 1); pendingKey = null; i++; continue; }
                if (c == '"')
                {
                    int start = ++i;
                    while (i < n && text[i] != '"') { if (text[i] == '\\') i++; i++; }
                    int len = Math.Min(i, n) - start;
                    i++;
                    if (pendingKey == null) { pendingKey = text.Substring(start, len); continue; }

                    // This string is the value for pendingKey
                    if (string.Equals(pendingKey, "LaunchOptions", StringComparison.OrdinalIgnoreCase) && IsZomboidApp(path))
                    {
                        LaunchOptionsEntry e = new LaunchOptionsEntry();
                        e.ValueStart = start; e.ValueLength = len;
                        e.Value = text.Substring(start, len);
                        return e;
                    }
                    pendingKey = null;
                    continue;
                }
                i++; // anything else (unquoted text) is ignored
            }
            return null;
        }

        static bool IsZomboidApp(List<string> path)
        {
            string[] want = { "Software", "Valve", "Steam", "apps", Settings.SteamAppId };
            if (path.Count < want.Length) return false;
            int off = path.Count - want.Length;
            for (int k = 0; k < want.Length; k++)
                if (!string.Equals(path[off + k], want[k], StringComparison.OrdinalIgnoreCase)) return false;
            return true;
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
        public static readonly int Cols = LogoWidth + 4;   // columns at the design width
        public const int DesignWidth = 560;                 // banner height is based on this width
        int cols = Cols;                                     // actual columns (wider window = more sky)

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

        // Banner height for the current display scaling (same at any window width).
        public static int ScaledHeight()
        {
            return HeightForWidth(Ui.S(DesignWidth));
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
                    sx[i] += cols + 2;
                    sy[i] = rnd.Next(Rows);
                }
            }
        }

        public void Render(Graphics g, int width, int height)
        {
            float ch = height / (float)Rows;
            float cw = ch / CellAspect;                        // keep cell shape: wider banner = more columns
            int newCols = Math.Max(Cols, (int)Math.Ceiling(width / cw));
            if (newCols != cols)
            {
                for (int i = 0; i < StarCount; i++)            // spread stars over the new width
                    sx[i] = (float)(rnd.NextDouble() * newCols);
                cols = newCols;
            }
            int logoLeft = (cols - LogoWidth) / 2;

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
            for (int x = 0; x < cols; x++)
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
                if (cx < 0 || cx >= cols || !IsSky(cx - logoLeft, sy[i] - LogoTop)) continue;
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
                            if (cx + k < cols && IsSky(cx + k - logoLeft, sy[i] - LogoTop))
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
    //  Dropdown entry that shows "8 GB" but holds the number 8
    // -------------------------------------------------------------------------
    class GbItem
    {
        public readonly int GB;
        public GbItem(int gb) { GB = gb; }
        public override string ToString() { return GB + " GB"; }
    }

    // -------------------------------------------------------------------------
    //  Small square button with a circular-arrow "refresh" icon
    // -------------------------------------------------------------------------
    class RefreshButton : Button
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float size = Math.Min(Width, Height) * 0.48f;
            float cx = Width / 2f, cy = Height / 2f;
            RectangleF arc = new RectangleF(cx - size / 2, cy - size / 2, size, size);
            Color c = Enabled ? ForeColor : SystemColors.GrayText;
            float w = Math.Max(1.6f, Width / 15f);
            using (Pen p = new Pen(c, w))
                g.DrawArc(p, arc, -60, 300);          // open circle, gap at the top right
            // arrow head at the end of the arc (pointing clockwise)
            double a = -60 * Math.PI / 180;
            float ex = cx + (float)(Math.Cos(a) * size / 2), ey = cy + (float)(Math.Sin(a) * size / 2);
            float h = size * 0.42f;
            PointF[] head = {
                new PointF(ex + h * 0.55f, ey - h * 0.05f),
                new PointF(ex - h * 0.45f, ey - h * 0.55f),
                new PointF(ex - h * 0.15f, ey + h * 0.55f)
            };
            using (SolidBrush b = new SolidBrush(c)) g.FillPolygon(b, head);
        }
    }

    // -------------------------------------------------------------------------
    //  Bordered box with a title in the border (used for "Console Copier")
    // -------------------------------------------------------------------------
    class TitledBox : Panel
    {
        public string Title = "";
        public Font TitleFont;

        public TitledBox()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            Font f = TitleFont ?? Font;
            Size ts = TextRenderer.MeasureText(g, Title, f, Size.Empty, TextFormatFlags.NoPadding);
            int line = Math.Max(2, Ui.S(2));
            int top = ts.Height / 2;
            int half = line / 2;
            using (Pen p = new Pen(ForeColor, line))
            {
                Rectangle r = new Rectangle(half, top, Width - line, Height - top - line + half);
                g.DrawRectangle(p, r);
            }
            // gap in the top border for the title
            int tx = (Width - ts.Width) / 2;
            using (SolidBrush b = new SolidBrush(BackColor))
                g.FillRectangle(b, tx - Ui.S(6), 0, ts.Width + Ui.S(12), ts.Height);
            TextRenderer.DrawText(g, Title, f, new Point(tx, 0), ForeColor, TextFormatFlags.NoPadding);
        }
    }

    // -------------------------------------------------------------------------
    //  Main window
    // -------------------------------------------------------------------------
    class MainForm : ThemedForm
    {
        readonly string jsonPath;
        long installedMB, currentMB, currentXmsMB;   // currentXmsMB = 0 when -Xms is not set
        int recommendedGB;
        int tier;          // 32, 16, 12, 8, or 0 (under 8 GB)
        List<LaunchOptionsEntry> steamEntries = new List<LaunchOptionsEntry>();  // PZ launch options with -Xmx / -Xms
        bool steamChecked;                                                        // found the signed-in account's settings

        Banner banner = new Banner();
        Label lblPath = new Label();
        Label lblSteam = new Label();
        Label lblSteamNote = new Label();
        Label lblInstalled = new Label();
        Label lblCurrent = new Label();
        Label lblRecommend = new Label();
        CheckBox chkXms = new CheckBox();
        ComboBox cboXms = new ComboBox();
        RefreshButton btnRefresh = new RefreshButton();
        ToolTip tips = new ToolTip();
        Label lblWarning = new Label();
        Label lblNote = new Label();
        ComboBox cboGB = new ComboBox();
        Button btnRecommended = new Button();
        LinkLabel lnkKofi = new LinkLabel();
        Button btnSave = new Button();
        Button btnRestore = new Button();
        Button btnClose = new Button();

        TitledBox boxConsole = new TitledBox();
        Label lblConsoleInfo = new Label();
        Label lblConsoleStatus = new Label();
        Button btnCopyConsole = new Button();

        // -Xms to save: the selected minimum when the box is ticked, otherwise none (0).
        // 8 GB systems: the box is locked off, so always none.
        int XmsGB
        {
            get
            {
                if (tier == 8 || !chkXms.Enabled || !chkXms.Checked) return 0;
                GbItem it = cboXms.SelectedItem as GbItem;
                return it == null ? 0 : it.GB;
            }
        }

        public MainForm(string path)
        {
            jsonPath = path;

            const int W = 640;   // layout is designed at 100% scaling; Ui.S / Ui.Place scale it
            const int LeftW = 410;   // width of the left column (the Console Copier box is on the right)
            int BoxX = 435;          // Console Copier box; moved right if "Use recommended" needs the room
            const int BoxH = 272;
            Text = "PZ Memory Tool v" + Settings.Version + " - Now with Console copier!!";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;

            int bannerH = BannerRenderer.HeightForWidth(BannerRenderer.DesignWidth);
            banner.SetBounds(0, 0, Ui.S(W), BannerRenderer.ScaledHeight());

            int y = bannerH + 4;
            Label title = new Label();
            title.Text = "Project Zomboid Memory Allocation Discombobulation Tool";
            title.Font = Ui.Font(14f, FontStyle.Bold);
            Ui.Place(title, 15, y, W - 30 - 40, 32);

            // Refresh button: end of the title line, top-right corner of the window below the banner
            Ui.Place(btnRefresh, W - 15 - 30, y + 1, 30, 30);
            tips.SetToolTip(btnRefresh, "Refresh: re-check the game file, Steam launch options and memory");
            btnRefresh.Click += delegate { RefreshValues(); };
            y += 38;

            Ui.Place(lblPath, 15, y, W - 30, 36); lblPath.Tag = "sub";
            lblPath.Font = Ui.Font(8.5f); y += 38;

            // Steam launch options report, right under the file that was found
            Ui.Place(lblSteam, 15, y, W - 30, 22);
            lblSteam.Font = Ui.Font(9.5f, FontStyle.Bold); y += 22;
            Ui.Place(lblSteamNote, 15, y, W - 30, 20);
            lblSteamNote.Font = Ui.Font(8.5f); y += 28;

            Ui.Place(lblInstalled, 15, y, LeftW, 24); lblInstalled.Font = Ui.Font(11f, FontStyle.Bold); y += 28;
            int regionTop = y;       // the Console Copier box is centred beside this line ... 
            Ui.Place(lblCurrent, 15, y, LeftW + 10, 24); lblCurrent.Font = Ui.Font(11f, FontStyle.Bold); y += 28;
            Ui.Place(lblRecommend, 15, y, LeftW + 10, 24); y += 26;
            y += 8;

            // Minimum (-Xms): tick box + dropdown (4 GB up to the selected maximum minus 1 GB).
            // The dropdowns and button are placed after measuring the text, so nothing gets cut off.
            chkXms.Text = "Minimum allocation (-Xms):";
            const string MaxText = "Maximum allocation (-Xmx):";
            int ComboX = 15 + Math.Max(Unscaled(TextRenderer.MeasureText(chkXms.Text, Font).Width) + 22,
                                       Unscaled(TextRenderer.MeasureText(MaxText, Font).Width)) + 10;
            int btnW = Unscaled(TextRenderer.MeasureText("Use recommended", Font).Width) + 26;
            Ui.Place(chkXms, 15, y + 1, ComboX - 17, 26);
            chkXms.CheckedChanged += delegate { cboXms.Enabled = chkXms.Enabled && chkXms.Checked; };
            cboXms.DropDownStyle = ComboBoxStyle.DropDownList;
            Ui.Place(cboXms, ComboX, y, 85, 28);
            y += 36;

            // Maximum (-Xmx)
            Label lblNew = new Label();
            lblNew.Text = MaxText;
            Ui.Place(lblNew, 15, y + 3, ComboX - 17, 24);
            cboGB.DropDownStyle = ComboBoxStyle.DropDownList;
            Ui.Place(cboGB, ComboX, y, 85, 28);
            cboGB.SelectedIndexChanged += delegate { UpdateXmsOptions(); UpdateWarning(); };
            btnRecommended.Text = "Use recommended";
            btnRecommended.Click += delegate { SelectGB(recommendedGB); };
            // "Use recommended" sits beside the dropdown if it fits without squeezing the
            // Console Copier box (box no further right than MaxBoxX); otherwise on its own line below.
            const int MaxBoxX = 455;
            bool buttonBelow = ComboX + 93 + btnW + 15 > MaxBoxX;
            int leftRight;
            if (!buttonBelow)
            {
                Ui.Place(btnRecommended, ComboX + 93, y - 1, btnW, 30);
                leftRight = ComboX + 93 + btnW;
                y += 44;
            }
            else
            {
                Ui.Place(btnRecommended, ComboX, y + 36, btnW, 30);
                leftRight = ComboX + 85;
                y += 36 + 42;
            }

            // Warnings first, then the "try the lower value first" note below them
            Ui.Place(lblWarning, 15, y, LeftW, 80);
            lblWarning.Tag = "warn";
            y += 82;
            Ui.Place(lblNote, 15, y, LeftW, 40);
            lblNote.Tag = "note";
            lblNote.Font = Ui.Font(10f, FontStyle.Italic);
            y += 44;

            // Ko-fi link, right above the Save button
            lnkKofi.Text = "Support ComfyPlacebo on Ko-fi: ko-fi.com/comfyplacebo";
            lnkKofi.LinkBehavior = LinkBehavior.HoverUnderline;
            Ui.Place(lnkKofi, 15, y, LeftW, 24);
            lnkKofi.LinkClicked += delegate { OpenKofi(); };
            y += 30;

            // Console Copier box: right side, centred beside "Current allocation" ... Ko-fi link
            int regionBottom = y - 6;
            BoxX = Math.Min(MaxBoxX, Math.Max(BoxX, leftRight + 15));
            int boxW = W - 20 - BoxX, boxH = BoxH;
            int boxTop = regionTop + (regionBottom - regionTop - boxH) / 2;
            Ui.Place(boxConsole, BoxX, boxTop, boxW, boxH);
            boxConsole.Title = "Console Copier";
            boxConsole.TitleFont = MonoFont(10f, FontStyle.Bold);
            Ui.Place(lblConsoleInfo, 14, 30, boxW - 28, 122);
            lblConsoleInfo.Text = "Copies Project Zomboid's console.txt log file to your clipboard.\n\n" +
                                  "Paste it into Discord, a message or a folder with right click or Ctrl+V.";
            lblConsoleInfo.Font = Ui.Font(9f);
            lblConsoleInfo.TextAlign = ContentAlignment.TopCenter;
            Ui.Place(lblConsoleStatus, 10, boxH - 112, boxW - 20, 54);
            lblConsoleStatus.Tag = "note";
            lblConsoleStatus.Font = Ui.Font(8.5f);
            lblConsoleStatus.TextAlign = ContentAlignment.BottomCenter;
            btnCopyConsole.Text = "Copy Console";
            int copyW = Math.Min(130, boxW - 24);
            Ui.Place(btnCopyConsole, (boxW - copyW) / 2, boxH - 52, copyW, 34);
            btnCopyConsole.Click += delegate { CopyConsole(); };
            boxConsole.Controls.AddRange(new Control[] { lblConsoleInfo, lblConsoleStatus, btnCopyConsole });

            btnSave.Text = "Save";
            btnSave.Tag = "primary";
            Ui.Place(btnSave, 15, y, 120, 34);
            btnSave.Font = Ui.Font(10f, FontStyle.Bold);
            btnSave.Click += delegate { Save(); };

            btnRestore.Text = "Restore backup";
            Ui.Place(btnRestore, 145, y, 140, 34);
            btnRestore.Click += delegate { RestoreBackup(); };

            btnClose.Text = "Close";
            Ui.Place(btnClose, W - 125, y, 110, 34);
            btnClose.Click += delegate { Close(); };
            y += 34 + 15;

            ClientSize = new Size(Ui.S(W), Ui.S(y));

            Controls.AddRange(new Control[] { banner, title, lblPath, lblSteam, lblSteamNote, lblInstalled, lblCurrent,
                lblRecommend, chkXms, cboXms, lblNew, cboGB, btnRecommended, lblWarning, lblNote, lnkKofi,
                boxConsole, btnSave, btnRestore, btnClose, btnRefresh });

            LoadValues();
        }

        // Measured pixel widths -> layout units at 100% scaling
        static int Unscaled(int px) { return (int)Math.Ceiling(px / Ui.Scale); }

        static Font MonoFont(float size, FontStyle style)
        {
            try
            {
                Font f = new Font("Consolas", size, style);
                if (f.Name == "Consolas") return f;
                f.Dispose();
            }
            catch { }
            return new Font(FontFamily.GenericMonospace, size, style);
        }

        int SelectedGB
        {
            get { GbItem it = cboGB.SelectedItem as GbItem; return it == null ? 0 : it.GB; }
        }

        void SelectGB(int gb) { SelectIn(cboGB, gb); }

        static bool SelectIn(ComboBox cb, int gb)
        {
            foreach (object o in cb.Items)
                if (((GbItem)o).GB == gb) { cb.SelectedItem = o; return true; }
            return false;
        }

        // Minimum dropdown: 4 GB up to (selected maximum - 1 GB). Keeps the choice if it still fits.
        void UpdateXmsOptions()
        {
            GbItem cur = cboXms.SelectedItem as GbItem;
            int keep = cur == null ? Settings.DefaultXmsGB : cur.GB;
            int top = SelectedGB - 1;
            cboXms.Items.Clear();
            for (int gb = Settings.MinXmsGB; gb <= top; gb++) cboXms.Items.Add(new GbItem(gb));
            if (cboXms.Items.Count == 0) return;
            if (!SelectIn(cboXms, Math.Min(keep, top))) cboXms.SelectedIndex = 0;
        }

        // Refresh button: read everything again, keeping the user's choices where they still apply.
        void RefreshValues()
        {
            int keepMax = SelectedGB;
            GbItem xmsItem = cboXms.SelectedItem as GbItem;
            int keepMin = xmsItem == null ? 0 : xmsItem.GB;
            bool keepTick = chkXms.Checked;
            LoadValues();
            if (keepMax > 0) SelectGB(keepMax);
            if (chkXms.Enabled) chkXms.Checked = keepTick;
            if (keepMin > 0) SelectIn(cboXms, keepMin);
        }

        void LoadValues()
        {
            string text = File.ReadAllText(jsonPath);
            Match m = XmxParser.Find(text);
            currentMB = XmxParser.ToMB(m);
            Match xms = XmxParser.FindXms(text);
            currentXmsMB = xms == null ? 0 : XmxParser.ToMB(xms);
            installedMB = SystemMemory.InstalledMB();
            CheckSteamLaunchOptions();

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
                tier = 12; recommendedGB = 6; recommendText = "6 GB or 8 GB only";
                foreach (int gb in new int[] { 6, 8 }) if (gb <= maxGB) options.Add(gb);
            }
            else if (installedWholeGB >= Settings.Tier8)
            {
                tier = 8; recommendedGB = 5; recommendText = "5 GB or 6 GB only";
                foreach (int gb in new int[] { 5, 6 }) if (gb <= maxGB) options.Add(gb);
            }
            else
            {
                tier = 0; recommendedGB = 0; recommendText = "none (under 8 GB)";
            }

            lblPath.Text = "File Found: " + jsonPath;
            lblInstalled.Text = "Total installed memory:   " + installedGB.ToString("0.#") + " GB";
            lblCurrent.Text   = "Current allocation (-Xms / -Xmx):   " +
                                (currentXmsMB > 0 ? XmxParser.Describe(currentXmsMB) : "not set") + " / " +
                                XmxParser.Describe(currentMB);
            lblRecommend.Text = "Recommended allocation for this PC (-Xmx):   " + recommendText;

            cboGB.Items.Clear();
            cboXms.Items.Clear();
            foreach (int gb in options) cboGB.Items.Add(new GbItem(gb));
            if (options.Count > 0)
            {
                if (!SelectIn(cboGB, recommendedGB)) cboGB.SelectedIndex = 0;
            }
            UpdateXmsOptions();
            SelectIn(cboXms, Settings.DefaultXmsGB);

            // Minimum (-Xms): ticked by default on 12 GB and up; locked off on 8 GB; hidden under 8 GB
            chkXms.Visible = cboXms.Visible = tier != 0;
            chkXms.Enabled = tier >= Settings.Tier12;
            chkXms.Checked = tier >= Settings.Tier12;
            cboXms.Enabled = chkXms.Enabled && chkXms.Checked;
            if (tier == 8) cboXms.SelectedIndex = -1;   // locked off: show no value

            // Under 8 GB: everything locked except Close (and the Console Copier)
            bool locked = (tier == 0 || options.Count == 0);
            cboGB.Enabled = !locked;
            btnRecommended.Enabled = !locked;
            btnSave.Enabled = !locked;
            btnRestore.Enabled = !locked && (File.Exists(jsonPath + ".bak") || SteamRestoreAvailable());
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
                    w = "WARNING: 8 GB of RAM on heavily modified servers is not recommended. " +
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

        // Reads the signed-in Steam account's launch options for Project Zomboid and shows the result.
        void CheckSteamLaunchOptions()
        {
            steamEntries.Clear();
            steamChecked = SteamLaunchOptions.ConfigFiles().Count > 0;
            List<string> args = new List<string>();
            foreach (LaunchOptionsEntry e in SteamLaunchOptions.FindAll())
            {
                string mem = SteamLaunchOptions.MemoryArgs(e.Value);
                if (mem.Length == 0) continue;
                steamEntries.Add(e);
                if (!args.Contains(mem)) args.Add(mem);
            }

            if (steamEntries.Count > 0)
            {
                lblSteam.Text = "Steam Memory Allocation Launch Option:   " + string.Join(", ", args.ToArray());
                lblSteam.Tag = "warn";
                lblSteamNote.Text = "This overrides the file above. It will be removed from Steam's launch options when you save.";
                lblSteamNote.Tag = "warn";
            }
            else
            {
                lblSteam.Text = steamChecked
                    ? "Steam Memory Allocation Launch Option:   none"
                    : "Steam Memory Allocation Launch Option:   could not check (Steam settings not found)";
                lblSteam.Tag = "sub";
                lblSteamNote.Text = "";
                lblSteamNote.Tag = "sub";
            }
            Theme.Apply(this);   // refresh colours for the warn / normal state
        }

        // True if this tool changed the Steam launch options and the original can be put back.
        bool SteamRestoreAvailable()
        {
            foreach (string f in SteamLaunchOptions.ConfigFiles())
            {
                string old = SteamLaunchOptions.BackedUpValue(f);
                if (old != null && old != SteamLaunchOptions.CurrentValue(f)) return true;
            }
            return false;
        }

        void Save()
        {
            int gb = SelectedGB;
            if (gb < Settings.MinAllocGB) return;
            int xmsGB = XmsGB;

            // Make sure the game wasn't started while this window was open
            if (!ProcessGuard.EnsureGameClosed()) return;

            // Steam launch options with -Xmx / -Xms need Steam closed, or Steam writes them back
            CheckSteamLaunchOptions();
            bool fixSteam = steamEntries.Count > 0 &&
                ProcessGuard.EnsureSteamClosed("remove the memory settings from",
                    "save without changing the Steam launch options");

            try
            {
                string text = File.ReadAllText(jsonPath);
                string backup = jsonPath + ".bak";
                if (!File.Exists(backup))
                    File.Copy(jsonPath, backup);   // keep the very first original as backup

                File.WriteAllText(jsonPath, XmxParser.SetMemory(text, gb, xmsGB));

                // Read the file back and check both values really are in it
                string check = File.ReadAllText(jsonPath);
                Match cx = XmxParser.Find(check), cs = XmxParser.FindXms(check);
                bool xmxOk = cx != null && XmxParser.ToMB(cx) == gb * 1024L;
                bool xmsOk = xmsGB > 0 ? (cs != null && XmxParser.ToMB(cs) == xmsGB * 1024L && cs.Index < cx.Index)
                                       : cs == null;
                string verify = (xmxOk && xmsOk) ? "" :
                    "\n\nWARNING: the file could not be fully updated (" +
                    (xmxOk ? "" : "-Xmx ") + (xmsOk ? "" : "-Xms ") + "not as expected). " +
                    "Please send your ProjectZomboid64.json to ComfyPlacebo so this can be fixed.";

                string msg = "Saved. Project Zomboid will now use:\n\n" +
                             "     Minimum memory (-Xms):   " +
                             (xmsGB > 0 ? xmsGB + " GB   (-Xms" + (xmsGB * 1024) + "m)"
                                        : (tier == 8 ? "not set (8 GB system)" : "not set (turned off)")) + "\n" +
                             "     Maximum memory (-Xmx):   " + gb + " GB   (-Xmx" + (gb * 1024) + "m)\n\n" +
                             SteamReport(fixSteam) + "\n\n" +
                             "A backup of the original file is at:\n" + backup + verify;
                MessageBox.Show(msg, "PZ Memory Tool", MessageBoxButtons.OK,
                    verify.Length == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
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

        // Removes -Xmx / -Xms from the Steam launch options (if allowed) and describes what happened.
        string SteamReport(bool fixSteam)
        {
            if (steamEntries.Count == 0)
                return steamChecked
                    ? "Steam launch options: no memory settings found, nothing to change."
                    : "Steam launch options: could not be checked (Steam settings not found).";

            List<string> removed = new List<string>(), failed = new List<string>(), backups = new List<string>();
            foreach (LaunchOptionsEntry e in steamEntries)
            {
                string mem = SteamLaunchOptions.MemoryArgs(e.Value);
                if (!fixSteam) { failed.Add(mem); continue; }
                try
                {
                    string b = SteamLaunchOptions.RemoveMemoryArgs(e.ConfigFile);
                    if (b != null) { removed.Add(mem); backups.Add(b); }
                }
                catch { failed.Add(mem); }
            }

            string r = "";
            if (removed.Count > 0)
                r = "Steam launch options: successfully removed " + string.Join(", ", removed.ToArray()) +
                    ".\nSteam backup: " + string.Join("\n", backups.ToArray());
            if (failed.Count > 0)
                r += (r.Length > 0 ? "\n\n" : "") +
                     "Steam launch options were NOT changed and still contain " + string.Join(", ", failed.ToArray()) +
                     ". These override the setting above. Remove them in Steam: right-click Project Zomboid > " +
                     "Properties > General > Launch Options.";
            return r;
        }

        // Copies console.txt to the clipboard as a file (paste into Discord or a folder with Ctrl+V).
        // A snapshot is copied to a temp folder first, so the game can keep writing its log.
        void CopyConsole()
        {
            string console = Path.Combine(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Zomboid"), "console.txt");
            if (!File.Exists(console))
            {
                lblConsoleStatus.Text = "console.txt not found.\nStart Project Zomboid once to create it.";
                return;
            }
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "PZMemoryTool");
                Directory.CreateDirectory(dir);
                string copy = Path.Combine(dir, "console.txt");
                using (FileStream src = new FileStream(console, FileMode.Open, FileAccess.Read,
                                                       FileShare.ReadWrite | FileShare.Delete))
                using (FileStream dst = new FileStream(copy, FileMode.Create, FileAccess.Write))
                    src.CopyTo(dst);

                System.Collections.Specialized.StringCollection files = new System.Collections.Specialized.StringCollection();
                files.Add(copy);
                Clipboard.SetFileDropList(files);

                long kb = new FileInfo(copy).Length / 1024;
                lblConsoleStatus.Text = "Copied console.txt\n(" + (kb >= 1024 ? (kb / 1024.0).ToString("0.0") + " MB" : kb + " KB") +
                                        ", " + File.GetLastWriteTime(console).ToString("t") + ")";
            }
            catch (Exception ex)
            {
                lblConsoleStatus.Text = "Could not copy console.txt:\n" + ex.Message;
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

        // Puts back the original game file and the original Steam launch options.
        void RestoreBackup()
        {
            string backup = jsonPath + ".bak";
            bool fileBackup = File.Exists(backup);
            bool steamBackup = SteamRestoreAvailable();
            if (!fileBackup && !steamBackup) return;

            if (MessageBox.Show("Restore the original settings from the backup?\n\n" +
                    (fileBackup ? "  - the game file (ProjectZomboid64.json)\n" : "") +
                    (steamBackup ? "  - Project Zomboid's Steam launch options\n" : ""),
                    "PZ Memory Tool", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            if (!ProcessGuard.EnsureGameClosed()) return;

            bool doSteam = steamBackup && ProcessGuard.EnsureSteamClosed("restore",
                "restore only the game file and leave the Steam launch options as they are");

            List<string> done = new List<string>();
            try
            {
                if (fileBackup)
                {
                    File.Copy(backup, jsonPath, true);
                    done.Add("Game file restored.");
                }
            }
            catch (UnauthorizedAccessException) { OfferAdminRelaunch(); return; }
            catch (Exception ex) { MessageBox.Show("Restore failed:\n" + ex.Message, "PZ Memory Tool"); return; }

            if (steamBackup)
            {
                if (!doSteam)
                    done.Add("Steam launch options were NOT restored (Steam was still running).");
                else
                {
                    try
                    {
                        foreach (string f in SteamLaunchOptions.ConfigFiles())
                            if (SteamLaunchOptions.RestoreFromBackup(f))
                                done.Add("Steam launch options restored to: " + SteamLaunchOptions.CurrentValue(f));
                    }
                    catch (Exception ex) { done.Add("Steam launch options could not be restored: " + ex.Message); }
                }
            }

            LoadValues();
            MessageBox.Show(string.Join("\n\n", done.ToArray()), "PZ Memory Tool",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
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
