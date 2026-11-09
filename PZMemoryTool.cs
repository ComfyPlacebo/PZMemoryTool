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
//  The ZombieBuddy installer copies ZombieBuddy.jar and zbNative.dll from your
//  Steam Workshop download into the game folder and adds -agentlib:zbNative to
//  the game file, ProjectZomboid64.bat and/or Steam's launch options.
//  A log of what the tool does is kept in %LOCALAPPDATA%\PZMemoryTool
//  (this run and the one before only, at most 1 MB).
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
using System.IO.Compression;
using System.Text;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using System.Reflection;

// File details shown in Windows (right-click the .exe > Properties > Details)
[assembly: AssemblyTitle("PZ Memory Tool")]
[assembly: AssemblyDescription("Sets Project Zomboid's memory allocation (-Xmx / -Xms), installs ZombieBuddy and copies console.txt. Open source.")]
[assembly: AssemblyCompany("ComfyPlacebo")]
[assembly: AssemblyProduct("PZ Memory Tool")]
[assembly: AssemblyCopyright("Copyright (c) 2026 ComfyPlacebo")]
[assembly: AssemblyVersion("1.2.0.0")]
[assembly: AssemblyFileVersion("1.2.0.0")]
[assembly: AssemblyInformationalVersion("1.2.0-preview")]

namespace PZMemoryTool
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Log.Init();
            Log.W("PZ Memory Tool " + Settings.Version);
            Log.W("  Windows " + Environment.OSVersion.Version + (SystemMemory.IsWindows11() ? " (Windows 11)" : " (Windows 10 or older)") +
                  ", " + (Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit") + ", culture " +
                  System.Globalization.CultureInfo.CurrentCulture.Name);
            Log.W("  Program: " + Application.ExecutablePath + (args.Length > 0 ? " | arguments: " + string.Join(" ", args) : ""));
            Log.W("  Project Zomboid running: " + (ProcessGuard.IsRunning(Settings.GameProcessName) ? "yes" : "no") +
                  " | Steam running: " + (ProcessGuard.IsRunning(Settings.SteamProcessName) ? "yes" : "no"));
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
            {
                Log.W("No game file chosen: closing");
                return; // user cancelled
            }
            Log.W("Game file: " + path);

            Application.Run(new MainForm(path));
            Log.W("Closing the tool");
        }
    }

    // -------------------------------------------------------------------------
    //  Settings - change these to adjust the tool's behaviour
    // -------------------------------------------------------------------------
    static class Settings
    {
        public const string Version = "1.2.0-preview";
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
        public const int WindowsReserveGB  = 2;  // 8 GB / 12 GB tiers: their fixed choices must fit in installed RAM minus this
        public const int Win10ReserveGB    = 6;  // 16 GB and up: highest maximum = installed RAM minus 6 GB on Windows 10
        public const int Win11ReserveGB    = 7;  // 16 GB and up: highest maximum = installed RAM minus 7 GB on Windows 11
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
            if (AppIcon != null) Icon = AppIcon;
        }

        // The "PZ" icon built into the exe (PZMemoryTool.ico, same as the Linux window icon),
        // used for the title bar and taskbar. Falls back to the default icon if it can't be read.
        static Icon appIcon;
        static bool appIconTried;
        public static Icon AppIcon
        {
            get
            {
                if (!appIconTried)
                {
                    appIconTried = true;
                    try { appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { appIcon = null; }
                }
                return appIcon;
            }
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
            HookLogging(this);
            base.OnLoad(e);
        }

        // Writes every button press, tick box, dropdown choice and link click to the log.
        void HookLogging(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                string where = Text;
                if (c is Button) c.Click += delegate (object s, EventArgs a) { Log.W("Button: " + ((Control)s).Text + "  [" + where + "]"); };
                else if (c is CheckBox) ((CheckBox)c).CheckedChanged += delegate (object s, EventArgs a)
                    { if (((Control)s).Focused) Log.W("Tick box: " + ((CheckBox)s).Text + " -> " + (((CheckBox)s).Checked ? "on" : "off")); };
                else if (c is RadioButton) ((RadioButton)c).CheckedChanged += delegate (object s, EventArgs a)
                    { if (((RadioButton)s).Checked && ((Control)s).Focused) Log.W("Choice: " + ((Control)s).Text); };
                else if (c is ComboBox) ((ComboBox)c).SelectionChangeCommitted += delegate (object s, EventArgs a)
                    { Log.W("Dropdown: " + ((ComboBox)s).Text + "  [" + where + "]"); };
                else if (c is LinkLabel) ((LinkLabel)c).LinkClicked += delegate (object s, LinkLabelLinkClickedEventArgs a)
                    { Log.W("Link: " + ((Control)s).Text); };
                if (c.HasChildren) HookLogging(c);
            }
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
            DialogResult r = Msg.Show(
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

                if (Msg.Show("The file was not found on any drive.\n\nDo you want to pick it manually?",
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
                    Msg.Show("That file does not look like Project Zomboid's " + Settings.JsonName +
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

            Log.W("Steam folders: " + string.Join(", ", SteamRoots().ToArray()));
            foreach (string dir in gameDirs)
            {
                string p = Path.Combine(dir, Settings.JsonName);
                bool ok = FileLocator.IsValidPzJson(p);
                Log.W("  checking " + p + ": " + (ok ? "FOUND" : File.Exists(p) ? "exists but not valid" : "not there"));
                if (ok)
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

        // Every Steam library folder of every Steam install found
        public static List<string> AllLibraries()
        {
            List<string> all = new List<string>();
            foreach (string root in SteamRoots())
                foreach (string lib in SteamLibraries(root))
                    if (!all.Contains(lib)) all.Add(lib);
            return all;
        }

        public static List<string> SteamLibraries(string steamRoot)
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
        // 1) the account Steam reports as signed in right now, 2) loginusers.vdf: the account marked
        // MostRecent, or else the latest sign-in, 3) the account folder whose settings changed last.
        static string lastAccountLog = "";
        static readonly StringBuilder accountLog = new StringBuilder();
        static void AccountLog(string line) { accountLog.AppendLine(line); }

        public static long CurrentAccountId()
        {
            accountLog.Length = 0;
            long id = FindAccountId();
            string text = accountLog.ToString().TrimEnd();
            if (text != lastAccountLog) { lastAccountLog = text; Log.W(text); }   // called often: log only changes
            return id;
        }

        static long FindAccountId()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess"))
                {
                    object v = k == null ? null : k.GetValue("ActiveUser");
                    AccountLog("Steam ActiveUser (registry): " + (v == null ? "(none)" : v.ToString()));
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
                    string best = null; long bestTs = -1;
                    foreach (Match m in Regex.Matches(File.ReadAllText(f), "\"(\\d{17})\"\\s*\\{([^}]*)\\}"))
                    {
                        string body = m.Groups[2].Value;
                        bool recent = Regex.IsMatch(body, "\"MostRecent\"\\s+\"1\"", RegexOptions.IgnoreCase);
                        Match t = Regex.Match(body, "\"Timestamp\"\\s+\"(\\d+)\"", RegexOptions.IgnoreCase);
                        long ts = t.Success ? long.Parse(t.Groups[1].Value) : 0;
                        AccountLog("  loginusers.vdf account " + m.Groups[1].Value + ": MostRecent " + (recent ? 1 : 0) + ", Timestamp " + ts);
                        if (recent) return long.Parse(m.Groups[1].Value) - 76561197960265728L;
                        if (ts > bestTs) { bestTs = ts; best = m.Groups[1].Value; }
                    }
                    if (best != null) { AccountLog("Steam account: latest sign-in " + best); return long.Parse(best) - 76561197960265728L; }
                }
                catch { }
            }

            long newest = 0; DateTime newestTime = DateTime.MinValue;
            foreach (string root in SteamLocator.SteamRoots())
            {
                try
                {
                    string ud = Path.Combine(root, "userdata");
                    if (!Directory.Exists(ud)) continue;
                    foreach (string d in Directory.GetDirectories(ud))
                    {
                        long id;
                        if (!long.TryParse(Path.GetFileName(d), out id) || id <= 0) continue;
                        string lc = Path.Combine(Path.Combine(d, "config"), "localconfig.vdf");
                        if (!File.Exists(lc)) continue;
                        DateTime t = File.GetLastWriteTimeUtc(lc);
                        if (t > newestTime) { newestTime = t; newest = id; }
                    }
                }
                catch { }
            }
            AccountLog(newest > 0 ? "Steam account: newest userdata folder " + newest : "No Steam account found");
            return newest;
        }

        // Starts Steam again (after the tool closed it).
        public static bool StartSteam()
        {
            foreach (string root in SteamLocator.SteamRoots())
            {
                string exe = Path.Combine(root, "steam.exe");
                if (!File.Exists(exe)) continue;
                try { Process.Start(exe); Log.W("Started Steam: " + exe); return true; } catch { }
            }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("steam://open/main");
                psi.UseShellExecute = true;
                Process.Start(psi);
                Log.W("Started Steam: steam://open/main");
                return true;
            }
            catch { return false; }
        }

        // Changes Project Zomboid's launch options with edit(current) -> new value. Adds the
        // LaunchOptions entry if there is none yet. Backs up localconfig.vdf (once) to file + backupSuffix.
        // Returns 1 changed, 0 nothing to change, -1 could not change.
        public static int Edit(string configFile, Func<string, string> edit, string backupSuffix)
        {
            string text = File.ReadAllText(configFile);
            LaunchOptionsEntry e = Locate(text);
            string cur = e == null ? "" : e.Value;
            string nv = edit(cur);
            Log.W("Steam launch options: \"" + cur + "\" -> \"" + nv + "\"" + (e == null ? " (no LaunchOptions entry yet)" : ""));
            if (nv == cur) return 0;
            string updated;
            if (e != null) updated = text.Substring(0, e.ValueStart) + nv + text.Substring(e.ValueStart + e.ValueLength);
            else
            {
                int at = FindAppSection(text);
                if (at < 0) { Log.W("Project Zomboid's section not found in " + configFile); return -1; }
                int ls = at - 1;
                while (ls > 0 && text[ls - 1] != '\n') ls--;
                string indent = "";
                for (int j = ls; j < at - 1 && (text[j] == '\t' || text[j] == ' '); j++) indent += text[j];
                string nl = text.Contains("\r\n") ? "\r\n" : "\n";
                updated = text.Insert(at, nl + indent + "\t\"LaunchOptions\"\t\t\"" + nv + "\"");
            }
            string backup = configFile + backupSuffix;
            if (!File.Exists(backup)) File.Copy(configFile, backup);
            File.WriteAllText(configFile, updated);
            Log.W("Wrote " + configFile + " (backup: " + backup + ")");
            return 1;
        }

        // Position just after the "{" of Software/Valve/Steam/apps/108600, or -1.
        static int FindAppSection(string text)
        {
            List<string> path = new List<string>();
            string pendingKey = null;
            int i = 0, n = text.Length;
            while (i < n)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '/' && i + 1 < n && text[i + 1] == '/') { while (i < n && text[i] != '\n') i++; continue; }
                if (c == '{')
                {
                    path.Add(pendingKey ?? ""); pendingKey = null; i++;
                    if (IsZomboidApp(path)) return i;
                    continue;
                }
                if (c == '}') { if (path.Count > 0) path.RemoveAt(path.Count - 1); pendingKey = null; i++; continue; }
                if (c == '"')
                {
                    int start = ++i;
                    while (i < n && text[i] != '"') { if (text[i] == '\\') i++; i++; }
                    string tok = text.Substring(start, Math.Min(i, n) - start);
                    i++;
                    pendingKey = pendingKey == null ? tok : null;
                    continue;
                }
                i++;
            }
            return -1;
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

            return UsableMB();
        }

        // RAM Windows can actually use (installed minus what the hardware reserves).
        public static long UsableMB()
        {
            MEMORYSTATUSEX s = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(s))
                return (long)(s.ullTotalPhys / (1024 * 1024));
            return 0;
        }

        // Windows 11 is build 22000 or later (both report version 10.0).
        public static bool IsWindows11()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    string b = k == null ? null : k.GetValue("CurrentBuildNumber") as string;
                    int build;
                    if (b != null && int.TryParse(b, out build)) return build >= 22000;
                }
            }
            catch { }
            return Environment.OSVersion.Version.Build >= 22000;
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
    //  Log file - everything the tool detects and does, for finding problems.
    //  %LOCALAPPDATA%\PZMemoryTool\log.txt (this run) and log-previous.txt (the
    //  run before). Each start deletes the older file, so only two runs are ever
    //  kept, and one run never writes more than 1 MB. Nothing is sent anywhere.
    //  F12 in the main window copies the log to the clipboard.
    // -------------------------------------------------------------------------
    static class Log
    {
        public const long MaxBytes = 1024 * 1024;
        static StreamWriter writer;
        static long written;
        static bool capped;
        static string current;

        public static string Folder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PZMemoryTool"); }
        }
        public static string FilePath { get { return Path.Combine(Folder, "log.txt"); } }

        public static void Init()
        {
            try { Directory.CreateDirectory(Folder); } catch { }
            try
            {
                string prev = Path.Combine(Folder, "log-previous.txt");
                if (File.Exists(prev)) File.Delete(prev);
                if (File.Exists(FilePath)) File.Move(FilePath, prev);
            }
            catch { }   // e.g. the tool is open twice: keep going with a fresh log
            string[] names = { FilePath, Path.Combine(Folder, "log-2.txt") };
            foreach (string f in names)
            {
                try
                {
                    writer = new StreamWriter(new FileStream(f, FileMode.Create, FileAccess.Write, FileShare.ReadWrite),
                                              new UTF8Encoding(false));
                    writer.AutoFlush = true;
                    current = f;
                    return;
                }
                catch { writer = null; }
            }
        }

        // One log line per text line, so multi-line messages stay readable.
        public static void W(string msg)
        {
            if (writer == null || capped) return;
            try
            {
                string ts = DateTime.Now.ToString("HH:mm:ss.fff");
                StringBuilder sb = new StringBuilder();
                bool first = true;
                foreach (string line in (msg ?? "").Replace("\r", "").Split('\n'))
                {
                    sb.Append(ts).Append(' ').Append(first ? "" : "    | ").Append(line).Append("\r\n");
                    first = false;
                }
                if (written + sb.Length > MaxBytes)
                {
                    writer.Write(ts + " (the log reached 1 MB: nothing more is written during this run)\r\n");
                    capped = true;
                    return;
                }
                writer.Write(sb.ToString());
                written += sb.Length;
            }
            catch { }
        }

        public static void FileContents(string label, string path, int max)
        {
            try
            {
                string t = File.ReadAllText(path);
                W(label + ": " + path + " (" + t.Length + " characters)" + (t.Length > max ? ", first " + max : "") + ":\n" +
                  (t.Length > max ? t.Substring(0, max) : t));
            }
            catch (Exception ex) { W(label + ": " + path + " (could not read: " + ex.Message + ")"); }
        }

        public static string ReadAll()
        {
            try
            {
                using (FileStream fs = new FileStream(current ?? FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader r = new StreamReader(fs))
                    return r.ReadToEnd();
            }
            catch { return null; }
        }
    }

    // Message boxes, logged (what was shown and what was answered).
    static class Msg
    {
        public static DialogResult Show(string text) { return Show(text, "", MessageBoxButtons.OK, MessageBoxIcon.None); }
        public static DialogResult Show(string text, string caption) { return Show(text, caption, MessageBoxButtons.OK, MessageBoxIcon.None); }
        public static DialogResult Show(string text, string caption, MessageBoxButtons buttons)
        {
            return Show(text, caption, buttons, MessageBoxIcon.None);
        }
        public static DialogResult Show(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            Log.W("Message shown: " + caption + "\n" + text);
            DialogResult r = MessageBox.Show(text, caption, buttons, icon);
            Log.W("Message answered: " + r);
            return r;
        }
        public static DialogResult Show(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon,
                                        MessageBoxDefaultButton def)
        {
            Log.W("Message shown: " + caption + "\n" + text);
            DialogResult r = MessageBox.Show(text, caption, buttons, icon, def);
            Log.W("Message answered: " + r);
            return r;
        }
    }

    // -------------------------------------------------------------------------
    //  ZombieBuddy (by Zed, https://github.com/zed-0xff/ZombieBuddy, MIT licence)
    //  lets Project Zomboid load Java mods. This does what its Windows installer
    //  does: copies zbNative.dll and ZombieBuddy.jar from the Steam Workshop
    //  download (...\workshop\content\108600\3619862853\mods\ZombieBuddy\libs) into
    //  the game folder and adds "-agentlib:zbNative" to the game file (normal
    //  launch), ProjectZomboid64.bat (Alternate Launch) and/or Steam's launch
    //  options. ZombieBuddy then keeps itself up to date from the Workshop copy.
    // -------------------------------------------------------------------------
    class ZbStatus
    {
        public bool[] WsFound = new bool[2];            // [0] stable, [1] beta Workshop copy downloaded
        public string[] WsLibs = new string[2];         // the Workshop "libs" folder
        public string[] WsVer = { "", "" };
        public string GameDir = "";
        public bool JarInstalled, DllInstalled;
        public string InstVer = "";
        public bool InJson, BatExists, InBat, InSteam, ApprovalsExist;
        public string JsonArg = "", SteamOpts = "";
        public bool AnyInstalled { get { return JarInstalled || DllInstalled || InJson || InBat || InSteam; } }
        public bool AnyWorkshop { get { return WsFound[0] || WsFound[1]; } }
    }

    static class Zb
    {
        public const string ModId = "3619862853";
        public const string BetaModId = "3812624292";
        public const string JarName = "ZombieBuddy.jar";
        public const string DllName = "zbNative.dll";
        public const string AgentArg = "-agentlib:zbNative";
        public const string BackupSuffix = ".pzmemorytool-zb.bak";
        static string lastScan = "";

        public static string ApprovalsFolder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".zombie_buddy"); }
        }

        public static bool IsZbArg(string s)
        {
            return s == AgentArg || s.StartsWith(AgentArg + "=") ||
                   (s.StartsWith("-javaagent:") && s.IndexOf("ZombieBuddy", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // "-agentlib:zbNative" plus the Advanced options (policy 0 = ask, 1 = deny-new; verbosity 0..2)
        public static string MakeArg(int policy, int verbosity)
        {
            List<string> extra = new List<string>();
            if (policy == 1) extra.Add("policy=deny-new");
            if (verbosity > 0) extra.Add("verbosity=" + verbosity);
            return AgentArg + (extra.Count > 0 ? "=" + string.Join(",", extra.ToArray()) : "");
        }

        public static string BatPath(string gameDir) { return Path.Combine(gameDir, "ProjectZomboid64.bat"); }

        public static ZbStatus Scan(string jsonPath)
        {
            ZbStatus st = new ZbStatus();
            st.GameDir = Path.GetDirectoryName(jsonPath);

            // Workshop copies: the game's own library first, then every Steam library
            List<string> libs = new List<string>();
            int sa = jsonPath.IndexOf(@"\steamapps\", StringComparison.OrdinalIgnoreCase);
            if (sa > 0) libs.Add(jsonPath.Substring(0, sa));
            foreach (string l in SteamLocator.AllLibraries()) if (!libs.Contains(l)) libs.Add(l);
            string[] ids = { ModId, BetaModId };
            for (int c = 0; c < 2; c++)
            {
                foreach (string lib in libs)
                {
                    string dir = Path.Combine(lib, @"steamapps\workshop\content\" + Settings.SteamAppId + @"\" + ids[c] +
                                                   @"\mods\ZombieBuddy\libs");
                    if (!File.Exists(Path.Combine(dir, JarName))) continue;
                    st.WsFound[c] = true;
                    st.WsLibs[c] = dir;
                    st.WsVer[c] = JarVersion(Path.Combine(dir, JarName));
                    break;
                }
            }

            string jar = Path.Combine(st.GameDir, JarName);
            st.JarInstalled = File.Exists(jar);
            if (st.JarInstalled) st.InstVer = JarVersion(jar);
            st.DllInstalled = File.Exists(Path.Combine(st.GameDir, DllName));
            try
            {
                int end; string arg;
                st.InJson = FindJsonEntry(File.ReadAllText(jsonPath), out end, out arg) >= 0;
                if (st.InJson) st.JsonArg = arg;
            }
            catch { }
            string bat = BatPath(st.GameDir);
            st.BatExists = File.Exists(bat);
            if (st.BatExists)
            {
                try
                {
                    string v = BatJavaOptions(File.ReadAllText(bat));
                    st.InBat = v != null && ContainsZbArg(v);
                }
                catch { }
            }
            foreach (LaunchOptionsEntry e in SteamLaunchOptions.FindAll())
            {
                st.SteamOpts = e.Value;
                if (ContainsZbArg(e.Value)) st.InSteam = true;
            }
            st.ApprovalsExist = Directory.Exists(ApprovalsFolder);

            string sum = "ZombieBuddy scan: Workshop stable " + (st.WsFound[0] ? "found " + st.WsVer[0] : "not found") +
                         " | beta " + (st.WsFound[1] ? "found " + st.WsVer[1] : "not found") +
                         " | game folder " + st.GameDir + " | jar " + (st.JarInstalled ? "installed " + st.InstVer : "not installed") +
                         " | dll " + (st.DllInstalled ? "yes" : "no") + " | game file " + (st.InJson ? "has " + st.JsonArg : "no entry") +
                         " | bat " + (!st.BatExists ? "missing" : st.InBat ? "has ZombieBuddy" : "no ZombieBuddy") +
                         " | Steam " + (st.InSteam ? "has ZombieBuddy" : "no ZombieBuddy");
            if (sum != lastScan)   // runs every 2 seconds while the window is open: log only changes
            {
                lastScan = sum;
                Log.W(sum);
                for (int c = 0; c < 2; c++) if (st.WsFound[c]) Log.W("  " + (c == 0 ? "stable" : "beta") + " Workshop folder: " + st.WsLibs[c]);
            }
            return st;
        }

        public static string StatusText(ZbStatus st)
        {
            string ws = st.WsFound[0] ? st.WsVer[0] : st.WsFound[1] ? st.WsVer[1] : "";
            bool on = st.JarInstalled && (st.InJson || st.InBat || st.InSteam);
            if (!st.AnyInstalled) return "Not installed";
            if (!on) return "Copied, but not switched on";
            if (ws.Length > 0 && st.InstVer.Length > 0 && VersionNewer(ws, st.InstVer))
                return "Installed v" + st.InstVer + " (v" + ws + " on Workshop)";
            return st.InstVer.Length > 0 ? "Installed v" + st.InstVer : "Installed";
        }

        // ---- Version inside the .jar (META-INF/MANIFEST.MF "Implementation-Version") ----
        public static string JarVersion(string jar)
        {
            try
            {
                byte[] z = File.ReadAllBytes(jar);
                int eocd = -1;
                for (int i = z.Length - 22; i >= 0 && i >= z.Length - 65557; i--)
                    if (BitConverter.ToUInt32(z, i) == 0x06054b50) { eocd = i; break; }
                if (eocd < 0) return "";
                int count = BitConverter.ToUInt16(z, eocd + 10);
                int p = (int)BitConverter.ToUInt32(z, eocd + 16);
                for (int k = 0; k < count && p + 46 <= z.Length && BitConverter.ToUInt32(z, p) == 0x02014b50; k++)
                {
                    int method = BitConverter.ToUInt16(z, p + 10);
                    int csize = (int)BitConverter.ToUInt32(z, p + 20);
                    int fnl = BitConverter.ToUInt16(z, p + 28), exl = BitConverter.ToUInt16(z, p + 30), cml = BitConverter.ToUInt16(z, p + 32);
                    int loh = (int)BitConverter.ToUInt32(z, p + 42);
                    string name = Encoding.ASCII.GetString(z, p + 46, fnl);
                    if (name == "META-INF/MANIFEST.MF")
                    {
                        int data = loh + 30 + BitConverter.ToUInt16(z, loh + 26) + BitConverter.ToUInt16(z, loh + 28);
                        string mf;
                        if (method == 0) mf = Encoding.UTF8.GetString(z, data, csize);
                        else
                        {
                            using (DeflateStream ds = new DeflateStream(new MemoryStream(z, data, csize), CompressionMode.Decompress))
                            using (StreamReader r = new StreamReader(ds))
                                mf = r.ReadToEnd();
                        }
                        Match m = Regex.Match(mf, @"Implementation-Version:\s*(\S+)");
                        return m.Success ? m.Groups[1].Value : "";
                    }
                    p += 46 + fnl + exl + cml;
                }
            }
            catch { }
            return "";
        }

        // "3.10.2" is newer than "3.9.0"
        public static bool VersionNewer(string a, string b)
        {
            string[] x = Regex.Split(a, @"[^0-9A-Za-z]+"), y = Regex.Split(b, @"[^0-9A-Za-z]+");
            for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
            {
                string s = i < x.Length ? x[i] : "", t = i < y.Length ? y[i] : "";
                int u, v;
                bool nu = int.TryParse(s, out u), nv = int.TryParse(t, out v);
                if (nu && nv) { if (u != v) return u > v; continue; }
                if (s.Length == 0 || t.Length == 0) return s.Length > 0 && nu;   // "3.5" > "3.5-beta"? no: extra numeric part is newer
                int c = string.CompareOrdinal(s, t);
                if (c != 0) return c > 0;
            }
            return false;
        }

        // ---- Files ----
        // Copies ZombieBuddy.jar and zbNative.dll from the Workshop download. Throws on failure.
        public static void CopyFiles(ZbStatus st, int channel)
        {
            string dir = st.WsLibs[channel];
            string jar = Path.Combine(st.GameDir, JarName);
            File.Copy(Path.Combine(dir, JarName), jar, true);
            Log.W("Copied " + Path.Combine(dir, JarName) + " -> " + jar);
            if (File.Exists(Path.Combine(dir, DllName)))
            {
                File.Copy(Path.Combine(dir, DllName), Path.Combine(st.GameDir, DllName), true);
                Log.W("Copied " + Path.Combine(dir, DllName) + " -> " + Path.Combine(st.GameDir, DllName));
            }
            else Log.W("No zbNative.dll in the Workshop folder");
            // an update waiting from an older install would replace this one: remove it
            if (File.Exists(jar + ".new")) { File.Delete(jar + ".new"); Log.W("Removed old " + jar + ".new"); }
        }

        public static List<string> RemoveFiles(ZbStatus st)
        {
            List<string> removed = new List<string>();
            foreach (string n in new string[] { JarName, JarName + ".new", JarName + ".bak", DllName })
            {
                string p = Path.Combine(st.GameDir, n);
                if (!File.Exists(p)) continue;
                File.Delete(p);
                removed.Add(n);
                Log.W("Removed " + p);
            }
            return removed;
        }

        public static bool DeleteApprovals()
        {
            try
            {
                if (Directory.Exists(ApprovalsFolder)) Directory.Delete(ApprovalsFolder, true);
                Log.W("Deleted " + ApprovalsFolder);
                return true;
            }
            catch (Exception ex) { Log.W("Could not delete " + ApprovalsFolder + ": " + ex.Message); return false; }
        }

        // ---- Game file (vmArgs) ----
        // Finds a ZombieBuddy entry ("...") inside "vmArgs". Returns the opening quote's position or -1.
        public static int FindJsonEntry(string t, out int end, out string arg)
        {
            end = 0; arg = null;
            int v = t.IndexOf("\"vmArgs\"", StringComparison.Ordinal);
            if (v < 0) return -1;
            int b = t.IndexOf('[', v);
            if (b < 0) return -1;
            for (int p = b + 1; p < t.Length && t[p] != ']'; p++)
            {
                if (t[p] != '"') continue;
                int s = p + 1, e = s;
                while (e < t.Length && t[e] != '"') { if (t[e] == '\\') e++; e++; }
                if (e >= t.Length) return -1;
                string val = t.Substring(s, e - s);
                if (IsZbArg(val)) { end = e + 1; arg = val; return p; }
                p = e;
            }
            return -1;
        }

        // Removes the list entry from q (opening quote) to e (after the closing quote), keeping valid JSON.
        public static string RemoveJsonEntry(string t, int q, int e)
        {
            int ls = q;
            while (ls > 0 && t[ls - 1] != '\n') ls--;
            bool wsBefore = true;
            for (int i = ls; i < q; i++) if (t[i] != ' ' && t[i] != '\t') wsBefore = false;
            int a = e;
            while (a < t.Length && (t[a] == ' ' || t[a] == '\t')) a++;
            bool commaAfter = a < t.Length && t[a] == ',';
            if (commaAfter) a++;
            while (a < t.Length && (t[a] == ' ' || t[a] == '\t')) a++;
            int lineEnd = a;
            if (lineEnd < t.Length && t[lineEnd] == '\r') lineEnd++;
            bool atEol = lineEnd < t.Length && t[lineEnd] == '\n';

            if (wsBefore && atEol)
            {
                string o = t.Remove(ls, lineEnd + 1 - ls);
                if (!commaAfter)
                {
                    int c = ls - 1;
                    while (c >= 0 && " \t\r\n".IndexOf(o[c]) >= 0) c--;
                    if (c >= 0 && o[c] == ',') o = o.Remove(c, 1);
                }
                return o;
            }
            if (commaAfter) return t.Remove(q, a - q);
            int cc = q - 1;
            while (cc >= 0 && (t[cc] == ' ' || t[cc] == '\t')) cc--;
            if (cc >= 0 && t[cc] == ',') return t.Remove(cc, e - cc);
            return t.Remove(q, e - q);
        }

        // Puts arg first in vmArgs (replacing an older ZombieBuddy entry), or removes it (arg == null).
        public static string SetJsonEntry(string t, string arg)
        {
            int end; string old;
            int q = FindJsonEntry(t, out end, out old);
            if (arg != null && q >= 0 && old == arg) return t;
            if (q >= 0) t = RemoveJsonEntry(t, q, end);
            if (arg == null) return t;
            int v = t.IndexOf("\"vmArgs\"", StringComparison.Ordinal);
            int b = v < 0 ? -1 : t.IndexOf('[', v);
            if (b < 0) throw new InvalidDataException("The game file has no vmArgs list.");
            int f = b + 1;
            while (f < t.Length && " \t\r\n".IndexOf(t[f]) >= 0) f++;
            if (f < t.Length && t[f] == ']') return t.Insert(f, "\"" + arg + "\"");
            bool ownLine = t.IndexOf('\n', b + 1, f - b - 1) >= 0;
            if (!ownLine) return t.Insert(f, "\"" + arg + "\", ");
            int ls = f;
            while (ls > 0 && t[ls - 1] != '\n') ls--;
            string nl = t.Contains("\r\n") ? "\r\n" : "\n";
            return t.Insert(f, "\"" + arg + "\"," + nl + t.Substring(ls, f - ls));
        }

        // ---- ProjectZomboid64.bat (Alternate Launch): SET _JAVA_OPTIONS=... ----
        static bool ContainsZbArg(string opts)
        {
            foreach (string f in opts.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                if (IsZbArg(f)) return true;
            return false;
        }

        // The value of "SET _JAVA_OPTIONS=" in the .bat, or null if there is no such line.
        public static string BatJavaOptions(string text)
        {
            foreach (string line in text.Replace("\r\n", "\n").Split('\n'))
            {
                string l = line.TrimStart(' ', '\t');
                if (l.StartsWith("SET _JAVA_OPTIONS=", StringComparison.OrdinalIgnoreCase))
                    return l.Substring(18).Trim();
            }
            return null;
        }

        // Same rules as ZombieBuddy's installer. arg == null removes it. Returns the new text.
        public static string SetBat(string text, string arg)
        {
            string nl = text.Contains("\r\n") ? "\r\n" : "\n";
            List<string> lines = new List<string>(text.Replace("\r\n", "\n").Split('\n'));
            bool found = false;
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];
                string l = line.TrimStart(' ', '\t');
                if (!l.StartsWith("SET _JAVA_OPTIONS=", StringComparison.OrdinalIgnoreCase)) continue;
                found = true;
                List<string> keep = new List<string>();
                foreach (string f in l.Substring(18).Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                    if (!IsZbArg(f)) keep.Add(f);
                if (arg != null) keep.Insert(0, arg);
                lines[i] = line.Substring(0, line.Length - l.Length) + "SET _JAVA_OPTIONS=" + string.Join(" ", keep.ToArray());
                break;
            }
            if (!found && arg != null) lines.Insert(0, "SET _JAVA_OPTIONS=" + arg);
            return string.Join(nl, lines.ToArray());
        }

        // ---- Steam launch options (same rules as ZombieBuddy's installer) ----
        public static string SetSteamOptions(string cur, string arg)
        {
            // take out any ZombieBuddy agent, and the "--" right after it when nothing follows that
            List<string> tok = new List<string>(cur.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
            List<string> rest = new List<string>();
            for (int i = 0; i < tok.Count; i++)
            {
                if (IsZbArg(tok[i]))
                {
                    if (i + 1 == tok.Count - 1 && tok[i + 1] == "--") i++;
                    continue;
                }
                rest.Add(tok[i]);
            }
            if (rest.Count == 1 && rest[0] == "--") rest.Clear();
            if (arg == null) return string.Join(" ", rest.ToArray());
            int cmd = rest.IndexOf("%command%");
            if (rest.Count == 0) return arg + " --";
            if (cmd >= 0) { rest.Insert(cmd + 1, arg); rest.Insert(cmd + 2, "--"); return string.Join(" ", rest.ToArray()); }
            if (rest.Contains("--")) return arg + " " + string.Join(" ", rest.ToArray());   // stay in front of the existing "--"
            return arg + " -- " + string.Join(" ", rest.ToArray());
        }
    }

    // -------------------------------------------------------------------------
    //  ZombieBuddy window: Workshop step, where to switch it on, Advanced
    //  options, Install / Uninstall / Close.
    // -------------------------------------------------------------------------
    class ZombieBuddyForm : ThemedForm
    {
        readonly string jsonPath;
        readonly MainForm owner;
        ZbStatus st;
        int channel;
        bool advanced, busy;

        Label lblTitle = new Label(), lblIntro = new Label(), lblStep1 = new Label(), lblWs = new Label();
        Button btnSubscribe = new Button();
        RadioButton rbStable = new RadioButton(), rbBeta = new RadioButton();
        Label lblStep2 = new Label();
        CheckBox chkJson = new CheckBox(), chkBat = new CheckBox(), chkSteam = new CheckBox();
        Label lblJson = new Label(), lblBat = new Label(), lblSteamDesc = new Label();
        Label lblNow = new Label();
        LinkLabel lnkAdv = new LinkLabel();
        Label lblPolicy = new Label(), lblVerb = new Label(), lblArgNote = new Label();
        ComboBox cboPolicy = new ComboBox(), cboVerb = new ComboBox();
        CheckBox chkApprovals = new CheckBox();
        Button btnInstall = new Button(), btnUninstall = new Button(), btnClose = new Button();
        System.Windows.Forms.Timer watch = new System.Windows.Forms.Timer();

        const int W = 600;
        int advTop;

        public ZombieBuddyForm(string jsonPath, MainForm owner)
        {
            this.jsonPath = jsonPath;
            this.owner = owner;
            Text = "PZ Memory Tool - ZombieBuddy";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;

            int x = 20, w = W - 40, y = 14;
            lblTitle.Text = "ZombieBuddy";
            lblTitle.Font = Ui.Font(12f, FontStyle.Bold);
            Ui.Place(lblTitle, x, y, w, 26); y += 30;
            lblIntro.Text = "ZombieBuddy lets Project Zomboid load Java mods. It's made by Zed (github.com/zed-0xff/ZombieBuddy), " +
                            "not by ComfyPlacebo. Java mods have full access to your computer, so ZombieBuddy asks before it " +
                            "loads each new one: only allow mods you trust.";
            lblIntro.Tag = "sub"; lblIntro.Font = Ui.Font(9f);
            Ui.Place(lblIntro, x, y, w, 64); y += 68;

            // Step 1: Workshop
            lblStep1.Text = "1.  Subscribe to ZombieBuddy on the Steam Workshop";
            lblStep1.Font = Ui.Font(10f, FontStyle.Bold);
            Ui.Place(lblStep1, x, y, w, 22); y += 26;
            Ui.Place(lblWs, x, y, w - 200, 40);
            btnSubscribe.Text = "Subscribe on Workshop";
            btnSubscribe.Tag = "primary";
            Ui.Place(btnSubscribe, x + w - 190, y, 190, 32);
            btnSubscribe.Click += delegate { OpenWorkshop(); };
            rbStable.Text = "Stable"; rbBeta.Text = "Beta";
            Ui.Place(rbStable, x + 330, y - 2, 110, 24);
            Ui.Place(rbBeta, x + 445, y - 2, 120, 24);
            rbStable.CheckedChanged += delegate { if (rbStable.Checked) channel = 0; UpdateUi(); };
            rbBeta.CheckedChanged += delegate { if (rbBeta.Checked) channel = 1; UpdateUi(); };
            y += 46;

            // Step 2: where to switch it on
            lblStep2.Text = "2.  Choose where to switch it on";
            lblStep2.Font = Ui.Font(10f, FontStyle.Bold);
            Ui.Place(lblStep2, x, y, w, 22); y += 26;
            chkJson.Text = "Game file (ProjectZomboid64.json)";
            Ui.Place(chkJson, x, y, w, 24); y += 24;
            lblJson.Text = "Normal launch: Project Zomboid reads this every time it starts.";
            lblJson.Tag = "sub"; lblJson.Font = Ui.Font(8.5f);
            Ui.Place(lblJson, x + 20, y, w - 20, 18); y += 24;
            chkBat.Text = "Alternate Launch (ProjectZomboid64.bat)";
            Ui.Place(chkBat, x, y, w, 24); y += 24;
            lblBat.Text = "Used when you pick \"Alternate Launch\" when you press Play in Steam.";
            lblBat.Tag = "sub"; lblBat.Font = Ui.Font(8.5f);
            Ui.Place(lblBat, x + 20, y, w - 20, 18); y += 24;
            chkSteam.Text = "Steam launch options";
            Ui.Place(chkSteam, x, y, w, 24); y += 24;
            lblSteamDesc.Text = "Steam is closed while this is changed, then opened again.";
            lblSteamDesc.Tag = "sub"; lblSteamDesc.Font = Ui.Font(8.5f);
            Ui.Place(lblSteamDesc, x + 20, y, w - 20, 18); y += 28;
            foreach (CheckBox c in new CheckBox[] { chkJson, chkBat, chkSteam }) c.CheckedChanged += delegate { UpdateButtons(); };

            Ui.Place(lblNow, x, y, w, 22); y += 28;

            // Advanced (the window grows to show it)
            lnkAdv.LinkBehavior = LinkBehavior.HoverUnderline;
            Ui.Place(lnkAdv, x, y, 160, 22);
            lnkAdv.LinkClicked += delegate { advanced = !advanced; Log.W("Advanced " + (advanced ? "opened" : "closed")); Arrange(); };
            y += 28;
            advTop = y;
            lblPolicy.Text = "Approval policy:";
            cboPolicy.DropDownStyle = ComboBoxStyle.DropDownList;
            cboPolicy.Items.AddRange(new object[] { "Ask me for each new mod (recommended)", "Only load mods I already approved" });
            lblVerb.Text = "Log detail:";
            cboVerb.DropDownStyle = ComboBoxStyle.DropDownList;
            cboVerb.Items.AddRange(new object[] { "Normal", "Debug (patch details)", "Trace (everything)" });
            lblArgNote.Tag = "sub"; lblArgNote.Font = Ui.Font(8.5f);
            lblArgNote.Text = "These go after the ZombieBuddy option, e.g. -agentlib:zbNative=policy=deny-new";
            cboPolicy.SelectedIndexChanged += delegate { UpdateButtons(); };
            cboVerb.SelectedIndexChanged += delegate { UpdateButtons(); };

            chkApprovals.Text = "When uninstalling, also delete saved mod approvals";
            btnInstall.Tag = "primary";
            btnInstall.Font = Ui.Font(10f, FontStyle.Bold);
            btnInstall.Click += delegate { Install(); };
            btnUninstall.Text = "Uninstall";
            btnUninstall.Click += delegate { Uninstall(); };
            btnClose.Text = "Close";
            btnClose.Click += delegate { Close(); };
            CancelButton = btnClose;

            Controls.AddRange(new Control[] { lblTitle, lblIntro, lblStep1, lblWs, btnSubscribe, rbStable, rbBeta, lblStep2,
                chkJson, lblJson, chkBat, lblBat, chkSteam, lblSteamDesc, lblNow, lnkAdv, lblPolicy, cboPolicy, lblVerb, cboVerb,
                lblArgNote, chkApprovals, btnInstall, btnUninstall, btnClose });

            // Start from what is installed now
            st = Zb.Scan(jsonPath);
            channel = st.WsFound[0] || !st.WsFound[1] ? 0 : 1;
            rbStable.Checked = channel == 0; rbBeta.Checked = channel == 1;
            bool steamOk = SteamLaunchOptions.ConfigFiles().Count > 0;
            chkJson.Checked = true;
            chkBat.Checked = st.BatExists;
            chkSteam.Checked = steamOk;
            chkSteam.Enabled = steamOk;
            if (!steamOk) lblSteamDesc.Text = "Not available: your Steam account's settings weren't found.";
            string cur = st.JsonArg + " " + st.SteamOpts;
            cboPolicy.SelectedIndex = cur.Contains("policy=deny-new") ? 1 : 0;
            Match vm = Regex.Match(cur, @"verbosity=(\d)");
            cboVerb.SelectedIndex = vm.Success ? Math.Min(2, int.Parse(vm.Groups[1].Value)) : 0;
            advanced = cboPolicy.SelectedIndex != 0 || cboVerb.SelectedIndex != 0;
            Arrange();
            UpdateUi();

            // Notice by itself when Steam has downloaded ZombieBuddy (or anything changes)
            watch.Interval = 2000;
            watch.Tick += delegate { if (busy) return; bool had = st.AnyWorkshop; st = Zb.Scan(jsonPath);
                                     if (!had && st.AnyWorkshop) { Log.W("Workshop download appeared"); channel = st.WsFound[0] ? 0 : 1; }
                                     UpdateUi(); };
            watch.Start();
        }

        // Positions the Advanced options and the bottom row; resizes the window.
        void Arrange()
        {
            int x = 20, w = W - 40, y = advTop;
            lnkAdv.Text = (advanced ? "▼" : "►") + "  Advanced";
            foreach (Control c in new Control[] { lblPolicy, cboPolicy, lblVerb, cboVerb, lblArgNote }) c.Visible = advanced;
            if (advanced)
            {
                Ui.Place(lblPolicy, x + 16, y + 4, 130, 22);
                Ui.Place(cboPolicy, x + 150, y, 300, 28); y += 36;
                Ui.Place(lblVerb, x + 16, y + 4, 130, 22);
                Ui.Place(cboVerb, x + 150, y, 300, 28); y += 36;
                Ui.Place(lblArgNote, x + 16, y, w - 16, 20); y += 26;
            }
            Ui.Place(chkApprovals, x, y, w, 24);
            y += 34;
            Ui.Place(btnInstall, x, y, 150, 34);
            Ui.Place(btnUninstall, x + 160, y, 130, 34);
            Ui.Place(btnClose, W - 20 - 110, y, 110, 34);
            y += 34 + 16;
            ClientSize = new Size(Ui.S(W), Ui.S(y));
            Theme.Apply(this);
        }

        void UpdateUi()
        {
            if (st.AnyWorkshop)
            {
                string v = st.WsVer[channel];
                lblWs.Text = "✓  Subscribed and downloaded" + (v.Length > 0 ? " (v" + v + ")" : "");
                lblWs.Tag = "note";
                btnSubscribe.Visible = false;
                rbStable.Visible = rbBeta.Visible = st.WsFound[0] && st.WsFound[1];
                rbStable.Text = "Stable v" + st.WsVer[0];
                rbBeta.Text = "Beta v" + st.WsVer[1];
            }
            else
            {
                lblWs.Text = "Not subscribed yet. Subscribe in Steam: this window notices by itself when Steam has downloaded it.";
                lblWs.Tag = "warn";
                btnSubscribe.Visible = true;
                rbStable.Visible = rbBeta.Visible = false;
            }
            chkBat.Enabled = st.BatExists;
            if (!st.BatExists) { chkBat.Checked = false; lblBat.Text = "ProjectZomboid64.bat wasn't found in the game folder."; }
            string where = (st.InJson ? "  |  in game file" : "") + (st.InBat ? "  |  in Alternate Launch" : "") +
                           (st.InSteam ? "  |  in Steam launch options" : "");
            lblNow.Text = "Now: " + Zb.StatusText(st) + where;
            chkApprovals.Visible = st.ApprovalsExist;
            Theme.Apply(this);
            UpdateButtons();
        }

        void UpdateButtons()
        {
            btnInstall.Text = st.AnyInstalled ? "Install / Update" : "Install";
            btnInstall.Enabled = st.AnyWorkshop && (chkJson.Checked || chkBat.Checked || chkSteam.Checked);
            btnUninstall.Enabled = st.AnyInstalled;
        }

        void OpenWorkshop()
        {
            string url = "steam://url/CommunityFilePage/" + Zb.ModId;
            Log.W("Opening " + url);
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(url);
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo("https://steamcommunity.com/sharedfiles/filedetails/?id=" + Zb.ModId);
                    psi.UseShellExecute = true;
                    Process.Start(psi);
                }
                catch { }
            }
        }

        string AgentArg() { return Zb.MakeArg(cboPolicy.SelectedIndex, cboVerb.SelectedIndex); }

        // Adds (arg) or removes (null) ZombieBuddy in Steam's launch options: closes Steam first, opens it again after.
        string SteamStep(string arg)
        {
            List<string> files = SteamLaunchOptions.ConfigFiles();
            if (files.Count == 0) return "Steam launch options: not changed (Steam's settings weren't found).";
            bool wasRunning = ProcessGuard.IsRunning(Settings.SteamProcessName);
            if (!ProcessGuard.EnsureSteamClosed(arg != null ? "add ZombieBuddy to" : "remove ZombieBuddy from",
                                                "skip the Steam launch options"))
                return "Steam launch options: skipped (Steam was still running).";
            List<string> r = new List<string>();
            foreach (string f in files)
            {
                try
                {
                    int rc = SteamLaunchOptions.Edit(f, delegate (string cur) { return Zb.SetSteamOptions(cur, arg); }, Zb.BackupSuffix);
                    r.Add(rc == 1 ? (arg != null ? "Steam launch options: ZombieBuddy added." : "Steam launch options: ZombieBuddy removed.")
                                  : rc == 0 ? (arg != null ? "Steam launch options: already set." : "Steam launch options: nothing to remove.")
                                            : "Steam launch options: could NOT be changed.");
                }
                catch (Exception ex) { r.Add("Steam launch options: could NOT be changed (" + ex.Message + ")."); }
            }
            if (wasRunning && SteamLaunchOptions.StartSteam()) r.Add("Steam was started again.");
            return string.Join("\n", r.ToArray());
        }

        void Install()
        {
            string arg = AgentArg();
            string v = st.WsVer[channel];
            string changes = "ZombieBuddy will make these changes:\n\n" +
                "  - Copy ZombieBuddy.jar" + (v.Length > 0 ? " v" + v : "") + " and zbNative.dll (Workshop" +
                (channel == 1 ? ", beta" : "") + ") into the game folder:\n    " + st.GameDir + "\n" +
                (chkJson.Checked ? "  - Game file: add " + arg + "\n" : "") +
                (chkBat.Checked ? "  - Alternate Launch (ProjectZomboid64.bat): add " + arg + "\n" : "") +
                (chkSteam.Checked ? "  - Steam launch options: add \"" + arg + " --\"" +
                    (ProcessGuard.IsRunning(Settings.SteamProcessName) ? " (Steam is closed first and opened again after)" : "") + "\n" : "") +
                (!chkJson.Checked && st.InJson ? "  - Game file: remove ZombieBuddy (not ticked)\n" : "") +
                (!chkBat.Checked && st.InBat ? "  - Alternate Launch: remove ZombieBuddy (not ticked)\n" : "") +
                (!chkSteam.Checked && st.InSteam ? "  - Steam launch options: remove ZombieBuddy (not ticked)\n" : "");
            if (Msg.Show(changes, "PZ Memory Tool - Install ZombieBuddy", MessageBoxButtons.OKCancel, MessageBoxIcon.Question)
                != DialogResult.OK) return;
            if (!ProcessGuard.EnsureGameClosed()) return;

            busy = true;
            List<string> report = new List<string>();
            try
            {
                st = Zb.Scan(jsonPath);
                Zb.CopyFiles(st, channel);
                report.Add("Copied ZombieBuddy.jar" + (v.Length > 0 ? " v" + v : "") + " and zbNative.dll into the game folder.");

                string jt = File.ReadAllText(jsonPath);
                string jn = Zb.SetJsonEntry(jt, chkJson.Checked ? arg : null);
                if (jn != jt)
                {
                    File.WriteAllText(jsonPath, jn);
                    report.Add(chkJson.Checked ? "Game file: ZombieBuddy added." : "Game file: ZombieBuddy removed.");
                }
                else if (chkJson.Checked) report.Add("Game file: already set.");
                Log.FileContents("Game file after ZombieBuddy install", jsonPath, 6000);

                string bat = Zb.BatPath(st.GameDir);
                if (File.Exists(bat))
                {
                    string bt = File.ReadAllText(bat);
                    string bn = Zb.SetBat(bt, chkBat.Checked ? arg : null);
                    if (bn != bt)
                    {
                        File.WriteAllText(bat, bn);
                        report.Add(chkBat.Checked ? "Alternate Launch: ZombieBuddy added." : "Alternate Launch: ZombieBuddy removed.");
                        Log.W("Alternate Launch .bat: _JAVA_OPTIONS now \"" + Zb.BatJavaOptions(bn) + "\"");
                    }
                    else if (chkBat.Checked) report.Add("Alternate Launch: already set.");
                }
            }
            catch (UnauthorizedAccessException)
            {
                busy = false;
                Log.W("Install: no permission to change the game folder");
                Close();
                owner.OfferAdminRelaunch();
                return;
            }
            catch (Exception ex)
            {
                Log.W("Install failed: " + ex);
                report.Add("Install failed: " + ex.Message);
            }

            if (chkSteam.Checked || st.InSteam)
                report.Add(SteamStep(chkSteam.Checked ? arg : null));
            Finish(report);
        }

        void Uninstall()
        {
            bool approvals = chkApprovals.Visible && chkApprovals.Checked;
            string list = "This removes ZombieBuddy:\n\n  - ZombieBuddy.jar and zbNative.dll from the game folder\n" +
                (st.InJson ? "  - Game file: remove " + st.JsonArg + "\n" : "") +
                (st.InBat ? "  - Alternate Launch: remove ZombieBuddy\n" : "") +
                (st.InSteam ? "  - Steam launch options: remove ZombieBuddy" +
                    (ProcessGuard.IsRunning(Settings.SteamProcessName) ? " (Steam is closed first and opened again after)" : "") + "\n" : "") +
                (approvals ? "  - Saved mod approvals (" + Zb.ApprovalsFolder + ")\n" : "") +
                "\nMods that need ZombieBuddy will stop working. Your other mods and settings stay as they are.";
            if (Msg.Show(list, "PZ Memory Tool - Uninstall ZombieBuddy", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning,
                         MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
            if (!ProcessGuard.EnsureGameClosed()) return;

            busy = true;
            List<string> report = new List<string>();
            try
            {
                st = Zb.Scan(jsonPath);
                List<string> removed = Zb.RemoveFiles(st);
                report.Add(removed.Count > 0 ? "Removed " + string.Join(", ", removed.ToArray()) + " from the game folder."
                                             : "No ZombieBuddy files were in the game folder.");
                string jt = File.ReadAllText(jsonPath);
                string jn = Zb.SetJsonEntry(jt, null);
                if (jn != jt) { File.WriteAllText(jsonPath, jn); report.Add("Game file: ZombieBuddy removed."); }
                string bat = Zb.BatPath(st.GameDir);
                if (File.Exists(bat))
                {
                    string bt = File.ReadAllText(bat), bn = Zb.SetBat(bt, null);
                    if (bn != bt) { File.WriteAllText(bat, bn); report.Add("Alternate Launch: ZombieBuddy removed."); }
                }
                if (approvals) report.Add(Zb.DeleteApprovals() ? "Saved mod approvals: deleted." : "Saved mod approvals: could NOT be deleted.");
            }
            catch (UnauthorizedAccessException)
            {
                busy = false;
                Close();
                owner.OfferAdminRelaunch();
                return;
            }
            catch (Exception ex)
            {
                Log.W("Uninstall failed: " + ex);
                report.Add("Uninstall failed: " + ex.Message);
            }
            if (st.InSteam) report.Add(SteamStep(null));
            Finish(report);
        }

        void Finish(List<string> report)
        {
            st = Zb.Scan(jsonPath);
            report.Add("\nZombieBuddy now: " + Zb.StatusText(st) + ".");
            Msg.Show(string.Join("\n", report.ToArray()), "PZ Memory Tool - ZombieBuddy", MessageBoxButtons.OK, MessageBoxIcon.Information);
            busy = false;
            UpdateUi();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            watch.Stop();
            Log.W("ZombieBuddy window closed");
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) watch.Dispose();
            base.Dispose(disposing);
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

        TitledBox boxZb = new TitledBox();
        Label lblZbStatus = new Label();
        Button btnZb = new Button();

        TitledBox boxConsole = new TitledBox();
        Label lblConsoleInfo = new Label();
        Label lblConsoleStatus = new Label();
        Button btnCopyConsole = new Button();
        Button btnCopyText = new Button();

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
            int BoxX = 435;          // ZombieBuddy + Console Copier boxes; moved right if "Use recommended" needs the room
            Text = "PZ Memory Tool v" + Settings.Version + " - Now with ZombieBuddy installer!!";
            KeyPreview = true;   // F12 copies the log
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

            int installedY = y;      // the ZombieBuddy box starts level with this line
            Ui.Place(lblInstalled, 15, y, LeftW, 24); lblInstalled.Font = Ui.Font(11f, FontStyle.Bold); y += 28;
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

            // Right side: the ZombieBuddy box, with the Console Copier box under it
            int regionBottom = y - 6;
            BoxX = Math.Min(MaxBoxX, Math.Max(BoxX, leftRight + 15));
            int boxW = W - 20 - BoxX;
            const int ZbH = 94;
            Ui.Place(boxZb, BoxX, installedY, boxW, ZbH);
            boxZb.Title = "ZombieBuddy";
            boxZb.TitleFont = MonoFont(10f, FontStyle.Bold);
            Ui.Place(lblZbStatus, 8, 24, boxW - 16, 18);
            lblZbStatus.Tag = "sub";
            lblZbStatus.Font = Ui.Font(8.5f);
            lblZbStatus.TextAlign = ContentAlignment.TopCenter;
            Ui.Place(btnZb, 12, 46, boxW - 24, 34);
            btnZb.Click += delegate { OpenZombieBuddy(); };
            boxZb.Controls.AddRange(new Control[] { lblZbStatus, btnZb });

            int boxTop = installedY + ZbH + 6, boxH = regionBottom - boxTop;
            Ui.Place(boxConsole, BoxX, boxTop, boxW, boxH);
            boxConsole.Title = "Console Copier";
            boxConsole.TitleFont = MonoFont(10f, FontStyle.Bold);
            Ui.Place(lblConsoleInfo, 12, 28, boxW - 24, 74);
            lblConsoleInfo.Text = "Copies console.txt to your clipboard as a file or as text. Paste it with right click or Ctrl+V.";
            lblConsoleInfo.Font = Ui.Font(9f);
            lblConsoleInfo.TextAlign = ContentAlignment.TopCenter;
            Ui.Place(lblConsoleStatus, 8, boxH - 146, boxW - 16, 50);
            lblConsoleStatus.Tag = "note";
            lblConsoleStatus.Font = Ui.Font(8.5f);
            lblConsoleStatus.TextAlign = ContentAlignment.BottomCenter;
            btnCopyConsole.Text = "Copy Console";
            int copyW = Math.Min(130, boxW - 24);
            Ui.Place(btnCopyConsole, (boxW - copyW) / 2, boxH - 92, copyW, 34);
            btnCopyConsole.Click += delegate { CopyConsole(false); };
            btnCopyText.Text = "Copy as text";
            Ui.Place(btnCopyText, (boxW - copyW) / 2, boxH - 50, copyW, 34);
            btnCopyText.Click += delegate { CopyConsole(true); };
            boxConsole.Controls.AddRange(new Control[] { lblConsoleInfo, lblConsoleStatus, btnCopyConsole, btnCopyText });

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
                boxZb, boxConsole, btnSave, btnRestore, btnClose, btnRefresh });

            LoadValues();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F12) { CopyLog(); e.Handled = true; }
            base.OnKeyDown(e);
        }

        // F12: the log as text on the clipboard, to paste into a message.
        void CopyLog()
        {
            string t = Log.ReadAll();
            try
            {
                if (string.IsNullOrEmpty(t)) throw new IOException("empty");
                Clipboard.SetText(t);
                lblConsoleStatus.Text = "Copied the log as text\n(" + Math.Max(1, t.Length / 1024) + " KB)";
                Log.W("Log copied to the clipboard");
            }
            catch { lblConsoleStatus.Text = "Could not copy the log.\nIt is in " + Log.Folder; }
        }

        void OpenZombieBuddy()
        {
            Log.W("Opening the ZombieBuddy window");
            using (ZombieBuddyForm f = new ZombieBuddyForm(jsonPath, this))
                f.ShowDialog(this);
            if (IsDisposed) return;   // closed for an administrator restart
            LoadValues();
        }

        void UpdateZbBox()
        {
            ZbStatus z = Zb.Scan(jsonPath);
            lblZbStatus.Text = Zb.StatusText(z);
            btnZb.Text = z.AnyInstalled ? "ZombieBuddy options..." : "Install ZombieBuddy...";
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
            long usableMB = SystemMemory.UsableMB();
            CheckSteamLaunchOptions();

            double installedGB = installedMB / 1024.0;
            int installedWholeGB = (int)Math.Round(installedGB);

            // Never more than installed RAM minus 2 GB, never less than 5 GB
            int maxGB = installedWholeGB - Settings.WindowsReserveGB;   // 8 GB / 12 GB tiers (fixed choices)
            // 16 GB and up: leave about 2 GB of headroom on top of Windows and Java's own needs
            int capGB = installedWholeGB - (SystemMemory.IsWindows11() ? Settings.Win11ReserveGB : Settings.Win10ReserveGB);

            List<int> options = new List<int>();
            string recommendText;

            if (installedWholeGB >= Settings.Tier32)
            {
                tier = 32; recommendedGB = 8; recommendText = "8 to 12 GB";
                for (int gb = Settings.MinAllocGB; gb <= capGB; gb++) options.Add(gb);
            }
            else if (installedWholeGB >= Settings.Tier16)
            {
                tier = 16; recommendedGB = 8; recommendText = "8 GB";
                for (int gb = Settings.MinAllocGB; gb <= capGB; gb++) options.Add(gb);
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
            lblInstalled.Text = "Total installed memory:   " + installedWholeGB + " GB" +
                                (usableMB > 0 ? "   (" + (usableMB / 1024.0).ToString("0.0") + " GB usable)" : "");
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
            UpdateZbBox();
            Log.W("Memory: installed " + installedMB + " MB, usable " + usableMB + " MB | tier " + tier + ", choices [" +
                  string.Join(",", options.ConvertAll(delegate (int g) { return g.ToString(); }).ToArray()) + "] GB, recommended " +
                  recommendText + " | current -Xmx " + currentMB + " MB, -Xms " + currentXmsMB + " MB");
            Log.W("Steam settings: " + (steamChecked ? string.Join(", ", SteamLaunchOptions.ConfigFiles().ToArray()) : "(not found)") +
                  " | memory options in Steam: " + (steamEntries.Count > 0 ? lblSteam.Text : "none"));
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
            bool steamWasRunning = ProcessGuard.IsRunning(Settings.SteamProcessName);
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
                             SteamReport(fixSteam) + ReopenSteam(fixSteam && steamWasRunning) + "\n\n" +
                             "A backup of the original file is at:\n" + backup + verify;
                Log.FileContents("Game file after save", jsonPath, 6000);
                Msg.Show(msg, "PZ Memory Tool", MessageBoxButtons.OK,
                    verify.Length == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                LoadValues();
            }
            catch (UnauthorizedAccessException)
            {
                OfferAdminRelaunch();
            }
            catch (Exception ex)
            {
                Msg.Show("Could not save the file:\n" + ex.Message, "PZ Memory Tool",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // After the tool closed Steam: start it again.
        static string ReopenSteam(bool wanted)
        {
            if (!wanted || ProcessGuard.IsRunning(Settings.SteamProcessName)) return "";
            return SteamLaunchOptions.StartSteam() ? "\nSteam was started again." : "";
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

        // Copies console.txt to the clipboard as a file (asText = false) or as its text (asText = true).
        // A snapshot is copied to a temp folder first, so the game can keep writing its log.
        void CopyConsole(bool asText)
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

                if (asText)
                    Clipboard.SetText(File.ReadAllText(copy));
                else
                {
                    System.Collections.Specialized.StringCollection files = new System.Collections.Specialized.StringCollection();
                    files.Add(copy);
                    Clipboard.SetFileDropList(files);
                }

                long kb = new FileInfo(copy).Length / 1024;
                lblConsoleStatus.Text = (asText ? "Copied console.txt as text\n(" : "Copied console.txt\n(") + (kb >= 1024 ? (kb / 1024.0).ToString("0.0") + " MB" : kb + " KB") +
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
                Msg.Show("Could not open your web browser. You can visit:\n" + Settings.KofiUrl,
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

            if (Msg.Show("Restore the original settings from the backup?\n\n" +
                    (fileBackup ? "  - the game file (ProjectZomboid64.json)\n" : "") +
                    (steamBackup ? "  - Project Zomboid's Steam launch options\n" : ""),
                    "PZ Memory Tool", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            if (!ProcessGuard.EnsureGameClosed()) return;

            bool steamWasRunning = ProcessGuard.IsRunning(Settings.SteamProcessName);
            bool doSteam = steamBackup && ProcessGuard.EnsureSteamClosed("restore",
                "restore only the game file and leave the Steam launch options as they are");

            List<string> done = new List<string>();
            ZbStatus zbBefore = Zb.Scan(jsonPath);   // the backups are older than ZombieBuddy: keep it switched on
            try
            {
                if (fileBackup)
                {
                    File.Copy(backup, jsonPath, true);
                    done.Add("Game file restored.");
                    if (zbBefore.InJson)
                    {
                        string t = File.ReadAllText(jsonPath), n = Zb.SetJsonEntry(t, zbBefore.JsonArg);
                        if (n != t) { File.WriteAllText(jsonPath, n); done.Add("ZombieBuddy was kept switched on in the game file."); }
                    }
                }
            }
            catch (UnauthorizedAccessException) { OfferAdminRelaunch(); return; }
            catch (Exception ex) { Msg.Show("Restore failed:\n" + ex.Message, "PZ Memory Tool"); return; }

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
                            {
                                string zbArg = null;
                                foreach (string tok in zbBefore.SteamOpts.Split(' ')) if (zbBefore.InSteam && Zb.IsZbArg(tok)) zbArg = tok;
                                if (zbArg != null &&
                                    SteamLaunchOptions.Edit(f, delegate (string cur) { return Zb.SetSteamOptions(cur, zbArg); }, Zb.BackupSuffix) == 1)
                                    done.Add("ZombieBuddy was kept in Steam's launch options.");
                                done.Add("Steam launch options restored to: " + SteamLaunchOptions.CurrentValue(f));
                            }
                    }
                    catch (Exception ex) { done.Add("Steam launch options could not be restored: " + ex.Message); }
                    string again = ReopenSteam(steamWasRunning);
                    if (again.Length > 0) done.Add(again.Trim());
                }
            }

            LoadValues();
            Msg.Show(string.Join("\n\n", done.ToArray()), "PZ Memory Tool",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // Files under Program Files need administrator rights to change.
        public void OfferAdminRelaunch()
        {
            if (Msg.Show("Windows needs administrator permission to change files in this folder.\n\n" +
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
