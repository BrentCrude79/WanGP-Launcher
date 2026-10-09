// WanGP tray launcher: lives in the system tray and starts wgp.py on demand, without a console window.
// It listens on WanGP's port (GUI, e.g. 7860) and on port 7861 (MCP for coding agents), both LAN-wide with --listen, starts WanGP
// in the matching mode when someone visits or an agent calls a tool, proxies to it, and stops it again
// after 10 idle minutes to free RAM and VRAM. It also shows the live console output.
// Build with build.ps1 (uses the C# 5 compiler that ships with Windows / .NET Framework 4.x).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WanGPLauncher
{
    static class Program
    {
        const string DefaultArgs = "wgp.py --listen --server-port 7860 --perc-reserved-mem-max 0.25";
        const string ShowEventName = @"Local\WanGPTrayLauncher.Show";
        public static string RestartWith;

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--make-icon")
            {
                Icons.WriteIco(args[1]);
                return 0;
            }
            bool showLog = false;
            var extra = new List<string>();
            foreach (string a in args)
            {
                if (a == "--show-log") showLog = true;
                else extra.Add(a.IndexOf(' ') >= 0 ? "\"" + a + "\"" : a);
            }

            bool created;
            using (var mutex = new Mutex(true, @"Local\WanGPTrayLauncher", out created))
            {
                if (!created)
                {
                    // Already running: ask the first instance to show its console window.
                    try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch (Exception) { }
                    return 1;
                }
                Native.SetProcessDPIAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                using (var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName))
                {
                    string root = WanGPLocation.FindRoot();
                    ApplicationContext context = root == null ? (ApplicationContext)new SetupApp(showEvent) : new TrayApp(root, string.Join(" ", extra.ToArray()), showLog, showEvent);
                    while (context != null)
                    {
                        Application.Run(context);
                        root = RestartWith;
                        RestartWith = null;
                        context = root == null ? null : (ApplicationContext)new TrayApp(root, string.Join(" ", extra.ToArray()), false, showEvent);
                    }
                }
            }
            return 0;
        }

        public static string ReadArgs(string argsFile)
        {
            if (!File.Exists(argsFile))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(argsFile));
                File.WriteAllText(argsFile,
                    "# Arguments the tray launcher passes to python when it starts WanGP.\r\n" +
                    "# Only the first line that is not blank or a comment is used. Restart WanGP from the tray to apply changes.\r\n" +
                    "# --listen opens the GUI (7860) and MCP (7861) ports to the LAN; without it both stay on this PC (restart the tray to apply).\r\n" +
                    DefaultArgs + "\r\n");
            }
            foreach (string raw in File.ReadAllLines(argsFile))
            {
                string line = raw.Trim();
                if (line.Length > 0 && !line.StartsWith("#")) return line;
            }
            return DefaultArgs;
        }
    }

    static class WanGPLocation
    {
        const string KeyPath = @"Software\WanGP Launcher";

        public static string FindRoot()
        {
            string saved = null;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath))
                    saved = key == null ? null : key.GetValue("WanGPFolder") as string;
            }
            catch (Exception) { }
            if (Valid(saved)) return Path.GetFullPath(saved);

            string dir = Path.GetDirectoryName(Application.ExecutablePath);
            for (int i = 0; i < 2 && dir != null; i++)
            {
                if (Valid(dir)) { Save(dir); return Path.GetFullPath(dir); }
                dir = Path.GetDirectoryName(dir);
            }
            string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string[] candidates = { Path.Combine(user, "Wan2GP"), Path.Combine(user, @"Apps\Wan2GP"), Path.Combine(user, @"Apps\WanGP") };
            foreach (string candidate in candidates)
                if (Valid(candidate)) { Save(candidate); return Path.GetFullPath(candidate); }
            return null;
        }

        public static bool Valid(string path)
        {
            try { return !String.IsNullOrEmpty(path) && File.Exists(Path.Combine(Path.GetFullPath(path), "wgp.py")); }
            catch (Exception) { return false; }
        }

        public static void Save(string path)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath))
                    key.SetValue("WanGPFolder", Path.GetFullPath(path), RegistryValueKind.String);
            }
            catch (Exception) { }
        }
    }

    sealed class PyEnv
    {
        public string Type, Dir, Python;

        public static PyEnv Resolve(string root)
        {
            string config = Path.Combine(root, "envs.json");
            if (File.Exists(config))
            {
                try
                {
                    var serializer = new JavaScriptSerializer();
                    var data = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(config));
                    object activeValue, envsValue;
                    if (data != null && data.TryGetValue("active", out activeValue) && data.TryGetValue("envs", out envsValue))
                    {
                        string active = activeValue as string;
                        var envs = envsValue as Dictionary<string, object>;
                        object entryValue;
                        if (!String.IsNullOrEmpty(active) && envs != null && envs.TryGetValue(active, out entryValue))
                        {
                            var entry = entryValue as Dictionary<string, object>;
                            object typeValue, pathValue;
                            string type = entry != null && entry.TryGetValue("type", out typeValue) ? typeValue as string : null;
                            string path = entry != null && entry.TryGetValue("path", out pathValue) ? pathValue as string : null;
                            if (type == "none") return new PyEnv { Type = "none", Dir = null, Python = "python" };
                            if ((type == "venv" || type == "uv" || type == "conda") && !String.IsNullOrWhiteSpace(path))
                            {
                                string dir = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
                                string python = type == "conda" ? Path.Combine(dir, "python.exe") : Path.Combine(dir, @"Scripts\python.exe");
                                return new PyEnv { Type = type, Dir = dir, Python = python };
                            }
                        }
                    }
                }
                catch (Exception) { }
            }
            string fallback = Path.Combine(root, ".venv"), fallbackPython = Path.Combine(fallback, @"Scripts\python.exe");
            bool uv = false;
            try { uv = Regex.IsMatch(File.ReadAllText(Path.Combine(fallback, "pyvenv.cfg")), @"^uv\s*=", RegexOptions.Multiline); }
            catch (Exception) { }
            return new PyEnv { Type = uv ? "uv" : "venv", Dir = fallback, Python = fallbackPython };
        }

        public static string FindUv()
        {
            string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string local = Path.Combine(user, @".local\bin\uv.exe");
            if (File.Exists(local)) return local;
            string app = Path.Combine(appData, @"uv\bin\uv.exe");
            if (File.Exists(app)) return app;
            return "uv";
        }
    }

    enum State { Starting, Running, Stopped }
    enum Mode { None, Gui, Mcp }
    enum Avail { Ready, Starting, Busy, Failed }

    sealed class SetupApp : ApplicationContext
    {
        readonly EventWaitHandle showEvent;
        readonly ManualResetEvent showStop = new ManualResetEvent(false);
        readonly LogBuffer log = new LogBuffer();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly Control ui = new Control();
        readonly Icon icon;
        readonly ToolStripMenuItem installItem, locateItem;
        readonly string logFile;
        readonly object childLock = new object();
        readonly System.Windows.Forms.Timer handoverTimer = new System.Windows.Forms.Timer();
        LogForm logForm;
        Process child;
        Icon installingIcon;
        bool installing;
        volatile bool exiting;

        public SetupApp(EventWaitHandle showEvent)
        {
            this.showEvent = showEvent;
            IntPtr unused = ui.Handle;
            icon = Icons.Make(SystemInformation.SmallIconSize.Width, Color.FromArgb(148, 163, 184));
            logFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"WanGP Launcher\logs\install.log");
            log.OpenFile(logFile);
            var menu = new ContextMenuStrip();
            installItem = new ToolStripMenuItem("Install WanGP...", null, delegate { Install(); });
            installItem.Font = new Font(installItem.Font, FontStyle.Bold);
            menu.Items.Add(installItem);
            locateItem = new ToolStripMenuItem("Locate existing WanGP...", null, delegate { Locate(); });
            menu.Items.Add(locateItem);
            menu.Items.Add(new ToolStripMenuItem("Show install log", null, delegate { ShowLog(); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Exit", null, delegate { ExitApp(); }));
            menu.Opening += delegate { installItem.Enabled = !installing; locateItem.Enabled = !installing; };
            tray.ContextMenuStrip = menu;
            tray.Icon = icon;
            tray.Text = "WanGP launcher - WanGP not installed";
            tray.DoubleClick += delegate { Install(); };
            tray.BalloonTipClicked += delegate { ShowLog(); };
            tray.Visible = true;
            tray.ShowBalloonTip(5000, "WanGP is not installed", "Right-click the tray icon to install it or locate an existing copy.", ToolTipIcon.Info);
            var waiter = new Thread(delegate ()
            {
                try
                {
                    WaitHandle[] handles = { showEvent, showStop };
                    while (WaitHandle.WaitAny(handles) == 0)
                        try { ui.BeginInvoke((MethodInvoker)delegate { ShowLog(); }); }
                        catch (InvalidOperationException) { return; } // the tray is shutting down
                }
                catch (ObjectDisposedException) { }
            });
            waiter.IsBackground = true;
            waiter.Start();
            log.Commit("[setup] WanGP was not found. Choose Install WanGP or Locate existing WanGP.");
        }

        void Locate()
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Select the folder containing wgp.py";
                if (dialog.ShowDialog() != DialogResult.OK) return;
                if (!WanGPLocation.Valid(dialog.SelectedPath))
                {
                    MessageBox.Show("That folder does not contain wgp.py. Choose the WanGP folder and try again.", "WanGP launcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                HandOver(dialog.SelectedPath, "[setup] Located existing WanGP at ");
            }
        }

        void Install()
        {
            if (installing) return;
            string target;
            using (var dialog = new TargetDialog())
                if (dialog.ShowDialog() != DialogResult.OK) return;
                else target = dialog.Target;

            string git = FindGit();
            if (git == null)
            {
                if (MessageBox.Show("Git is needed to install WanGP. Open the Git for Windows download page?", "WanGP launcher", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes) OpenUrl("https://git-scm.com/download/win");
                return;
            }
            string uv = PyEnv.FindUv();
            bool useUv = uv != "uv" || HasCommand("uv", "--version");
            string python = "python";
            if (!useUv && RunQuick(python, "-c \"import sys; sys.exit(0 if sys.version_info >= (3,11) else 1)\"") != 0)
            {
                if (MessageBox.Show("uv (recommended: https://docs.astral.sh/uv/getting-started/installation/) or Python 3.11+ (https://www.python.org/downloads/) is needed to install WanGP. Open the uv installation instructions?", "WanGP launcher", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes) OpenUrl("https://docs.astral.sh/uv/getting-started/installation/");
                return;
            }
            BeginInstall(target, git, useUv ? uv : python, useUv);
        }

        static string FindGit()
        {
            if (HasCommand("git", "--version")) return "git";
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string candidate = Path.Combine(pf, @"Git\cmd\git.exe");
            return File.Exists(candidate) ? candidate : null;
        }

        static bool HasCommand(string exe, string args)
        {
            return RunQuick(exe, args) == 0;
        }

        static int RunQuick(string exe, string args)
        {
            try
            {
                using (var p = new Process())
                {
                    p.StartInfo = new ProcessStartInfo(exe, args);
                    p.StartInfo.UseShellExecute = false;
                    p.StartInfo.CreateNoWindow = true;
                    p.StartInfo.RedirectStandardOutput = true;
                    p.StartInfo.RedirectStandardError = true;
                    p.StartInfo.RedirectStandardInput = true;
                    p.Start();
                    p.StandardInput.Close();
                    if (!p.WaitForExit(10000)) { TrayApp.KillTree(p.Id); return -1; }
                    return p.ExitCode;
                }
            }
            catch (Exception) { return -1; }
        }

        void BeginInstall(string target, string git, string tool, bool useUv)
        {
            installing = true;
            installItem.Enabled = false;
            locateItem.Enabled = false;
            tray.Text = "WanGP launcher - Installing WanGP";
            installingIcon = Icons.Make(SystemInformation.SmallIconSize.Width, Color.FromArgb(251, 191, 36));
            tray.Icon = installingIcon;
            ShowLog();
            log.Commit("[install] Installing WanGP to " + target);
            var thread = new Thread(delegate ()
            {
                string error = null;
                string parent = Path.GetDirectoryName(target);
                try
                {
                    Directory.CreateDirectory(parent);
                    int clone = RunInstallTool(git, "clone https://github.com/deepbeepmeep/Wan2GP.git " + Quote(target), parent);
                    if (clone != 0) error = "git clone failed with exit code " + clone + ".";
                    else
                    {
                        string setupArgs = useUv ? "run --no-project --python 3.11 setup.py install --env uv --auto" : "setup.py install --env venv --auto";
                        int install = RunInstallTool(tool, setupArgs, target);
                        if (install != 0) error = "WanGP setup failed with exit code " + install + ".";
                        else
                        {
                            PyEnv env = PyEnv.Resolve(target);
                            if ((env.Type == "none" && RunQuick(env.Python, "--version") != 0) || (env.Type != "none" && !File.Exists(env.Python))) error = "Setup completed, but envs.json does not resolve to an existing Python.";
                        }
                    }
                }
                catch (Exception e) { error = e.Message; }
                string result = error;
                try { ui.BeginInvoke((MethodInvoker)delegate { InstallFinished(target, result); }); }
                catch (InvalidOperationException) { }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        int RunInstallTool(string exe, string args, string cwd)
        {
            var psi = new ProcessStartInfo(exe, args);
            psi.WorkingDirectory = cwd;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = new UTF8Encoding(false);
            psi.StandardErrorEncoding = new UTF8Encoding(false);
            psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
            try
            {
                using (var p = new Process())
                {
                    p.StartInfo = psi;
                    p.OutputDataReceived += delegate (object sender, DataReceivedEventArgs e) { if (e.Data != null) log.Commit("[install] " + e.Data); };
                    p.ErrorDataReceived += delegate (object sender, DataReceivedEventArgs e) { if (e.Data != null) log.Commit("[install] " + e.Data); };
                    lock (childLock)
                    {
                        if (exiting) return -1;
                        p.Start();
                        child = p;
                    }
                    p.StandardInput.Close();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    p.WaitForExit();
                    lock (childLock) child = null;
                    return p.ExitCode;
                }
            }
            catch (Exception e) { lock (childLock) child = null; log.Commit("[install] " + e.Message); return -1; }
        }

        static string Quote(string value) { return "\"" + value + "\""; }

        void InstallFinished(string target, string error)
        {
            installing = false;
            installItem.Enabled = true;
            locateItem.Enabled = true;
            if (exiting) return;
            if (error != null)
            {
                log.Commit("[install] Failed: " + error);
                log.Commit("[install] Partial installation left at " + target);
                tray.ShowBalloonTip(5000, "WanGP install failed", "Click for the log.", ToolTipIcon.Error);
                tray.Text = "WanGP launcher - WanGP not installed";
                tray.Icon = icon;
                if (installingIcon != null) { installingIcon.Dispose(); installingIcon = null; }
                return;
            }
            log.Commit("[install] WanGP installed at " + target);
            HandOver(target, "[install] Installed WanGP at ");
        }

        void HandOver(string root, string prefix)
        {
            WanGPLocation.Save(root);
            log.Commit(prefix + root);
            Program.RestartWith = Path.GetFullPath(root);
            showStop.Set();
            if (prefix.StartsWith("[install]"))
            {
                tray.ShowBalloonTip(3000, "WanGP installed", "Starting the WanGP tray launcher.", ToolTipIcon.Info);
                handoverTimer.Interval = 1200;
                handoverTimer.Tick += delegate { handoverTimer.Stop(); handoverTimer.Dispose(); CompleteHandOver(); };
                handoverTimer.Start();
                return;
            }
            CompleteHandOver();
        }

        void CompleteHandOver()
        {
            tray.Visible = false;
            tray.Dispose();
            icon.Dispose();
            if (installingIcon != null) installingIcon.Dispose();
            if (logForm != null) logForm.Dispose();
            log.CloseFile();
            ui.Dispose();
            ExitThread();
        }

        void ShowLog()
        {
            if (logForm == null || logForm.IsDisposed) logForm = new LogForm(log, icon, delegate { }, OpenLogFile);
            logForm.Show();
            if (logForm.WindowState == FormWindowState.Minimized) logForm.WindowState = FormWindowState.Normal;
            logForm.Activate();
        }

        void OpenLogFile() { if (File.Exists(logFile)) Process.Start("notepad.exe", Quote(logFile)); }

        void ExitApp()
        {
            if (installing && MessageBox.Show("An install is running. Exit and stop it?", "WanGP launcher", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            Process running;
            lock (childLock) { exiting = true; running = child; }
            showStop.Set();
            if (running != null) TrayApp.KillTree(running.Id);
            tray.Visible = false;
            tray.Dispose();
            handoverTimer.Stop();
            handoverTimer.Dispose();
            icon.Dispose();
            if (installingIcon != null) installingIcon.Dispose();
            if (logForm != null) logForm.Dispose();
            log.CloseFile();
            ui.Dispose();
            ExitThread();
        }

        static void OpenUrl(string url) { try { Process.Start(url); } catch (Exception) { } }
    }

    sealed class TargetDialog : Form
    {
        readonly TextBox targetBox = new TextBox();
        readonly Label spaceLabel = new Label();
        readonly Label warningLabel = new Label();
        public string Target { get { return targetBox.Text.Trim(); } }

        public TargetDialog()
        {
            Text = "Install WanGP";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            ClientSize = new Size(520, 190);
            var label = new Label { Text = "Install folder", Left = 12, Top = 14, AutoSize = true };
            targetBox.SetBounds(12, 38, 400, 24);
            targetBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Wan2GP");
            var browse = new Button { Text = "Browse…", Left = 420, Top = 36, Width = 86 };
            browse.Click += delegate { Browse(); };
            spaceLabel.SetBounds(12, 72, 490, 22);
            warningLabel.SetBounds(12, 96, 490, 36);
            warningLabel.Text = "WanGP needs roughly 50 GB for itself plus tens to hundreds of GB for models.";
            warningLabel.ForeColor = Color.Firebrick;
            var install = new Button { Text = "Install", Left = 330, Top = 148, Width = 84 };
            var cancel = new Button { Text = "Cancel", Left = 422, Top = 148, Width = 84 };
            install.Click += delegate { if (ValidTarget()) { DialogResult = DialogResult.OK; Close(); } };
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.AddRange(new Control[] { label, targetBox, browse, spaceLabel, warningLabel, install, cancel });
            AcceptButton = install; CancelButton = cancel;
            targetBox.TextChanged += delegate { UpdateSpace(); };
            UpdateSpace();
        }

        void Browse()
        {
            using (var dialog = new FolderBrowserDialog())
            {
                if (Directory.Exists(Target)) dialog.SelectedPath = Target;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                string chosen = dialog.SelectedPath;
                if (Directory.Exists(chosen) && Directory.GetFileSystemEntries(chosen).Length > 0) chosen = Path.Combine(chosen, "Wan2GP");
                targetBox.Text = chosen;
            }
        }

        void UpdateSpace()
        {
            try
            {
                string full = Path.GetFullPath(Target);
                string drive = Path.GetPathRoot(full);
                var info = new DriveInfo(drive);
                long free = info.AvailableFreeSpace;
                spaceLabel.Text = "Free space: " + (free / (1024.0 * 1024 * 1024)).ToString("0.0") + " GB on " + drive;
                warningLabel.Visible = true;
                warningLabel.ForeColor = free < 50L * 1024 * 1024 * 1024 ? Color.Firebrick : SystemColors.ControlText;
            }
            catch (Exception) { spaceLabel.Text = "Free space could not be determined."; warningLabel.Visible = true; }
        }

        bool ValidTarget()
        {
            try
            {
                string full = Path.GetFullPath(Target);
                if (String.Equals(full.TrimEnd('\\', '/') + "\\", Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(this, "Choose a folder, not the root of a drive.", "WanGP launcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
                full = full.TrimEnd('\\', '/'); // a trailing backslash would escape the closing quote on the command line
                if (Directory.Exists(full) && Directory.GetFileSystemEntries(full).Length > 0)
                {
                    MessageBox.Show(this, "Choose a folder that does not exist or is empty.", "WanGP launcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
                if (File.Exists(full))
                {
                    MessageBox.Show(this, "The install path is a file. Choose a folder.", "WanGP launcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
                targetBox.Text = full;
                return true;
            }
            catch (Exception e) { MessageBox.Show(this, e.Message, "WanGP launcher", MessageBoxButtons.OK, MessageBoxIcon.Error); return false; }
        }
    }

    sealed class TrayApp : ApplicationContext
    {
        readonly string root, argsFile, logFile, extraArgs;
        readonly LogBuffer log = new LogBuffer();
        readonly Control ui = new Control();
        readonly ManualResetEvent showStop = new ManualResetEvent(false);
        readonly NotifyIcon tray = new NotifyIcon();
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "WanGPTray";
        const long BusyHoldMs = 3 * 60000; // WanGP counts as working until this long after its last work line
        static readonly Stopwatch Clock = Stopwatch.StartNew();
        static readonly Regex WorkLine = new Regex(@"^(Loading |Hooked to model|Lora ')|%\||prompt enhancement|Decode=|denoising", RegexOptions.Compiled);
        static readonly Regex DecodeRate = new Regex(@"Decode=(\d+(?:\.\d+)?)\s*tok/s", RegexOptions.Compiled);
        static readonly Regex Percent = new Regex(@"(\d{1,3})%\|", RegexOptions.Compiled);
        // Errors after which WanGP stays up but can no longer generate, and keeps holding its RAM and pinned memory.
        static readonly Regex FatalLine = new Regex(@"^Exception in thread |WinError 14(50|55)\]|^MemoryError\b|no attribute 'tiny_vae'", RegexOptions.Compiled);
        const long FatalSettleMs = 15000; // let the traceback finish and any pending save complete before stopping
        const long IdleStopMs = 10 * 60000;  // stop WanGP after this long without work or use
        const long SwitchQuietMs = 60000;    // the other mode may take over only after this long without work or use
        const long ColdStartWaitMs = 5 * 60000;
        const int McpPort = 7861, InternalOffset = 10000; // WanGP itself listens on the public port + 10000

        readonly Icon appIcon, iconStarting, iconRunning, iconStopped;
        readonly ToolStripMenuItem startItem, stopItem, restartItem, autostartItem, forceMcpItem, autoUpdateItem, updateNowItem;
        readonly ToolStripMenuItem changeFolderItem;
        readonly System.Windows.Forms.Timer uiTimer = new System.Windows.Forms.Timer();
        readonly object jobLock = new object();
        // Set while WanGP works, so the Kobold tray unloads koboldcpp and stays unloaded: WanGP takes precedence.
        readonly EventWaitHandle busyEvent = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\WanGP.Busy.Tray");
        readonly object watchLock = new object();
        readonly int iconSize;
        long lastWorkAt = -1000000, decodeAt = -1000000, percentAt = -1000000;
        double decodeRate;
        int percent, busy;
        long fatalAt = -1, lastLineAt;
        string fatalLine;
        Mode mode = Mode.None, nextMode = Mode.None;
        bool manualStart;
        bool forceMcp; // ticked in the tray menu: keep the MCP server running, no idle stop, no GUI
        readonly string forceMcpFile;
        // Updates: "Keep WanGP updated" checks GitHub every few hours and installs new commits whenever WanGP
        // is not in use; "Update WanGP now" does the same at once, but never stops a generation.
        const long UpdateCheckEveryMs = 6 * 3600000L;
        readonly string autoUpdateFile;
        bool autoUpdate, manualUpdate, updateChecking, updateReady, updating, stoppingForUpdate;
        Mode afterUpdate = Mode.None;
        long lastUpdateCheckAt;
        long startedAt, lastGuiUseAt, lastMcpUseAt, lastFailAt = -1000000;
        bool onLan; // --listen: the GUI and MCP ports accept LAN connections
        TcpListener guiListener, mcpListener;
        readonly List<Conn> conns = new List<Conn>();
        readonly object connLock = new object();
        readonly McpCache mcpCache;
        Icon numberIcon;
        string numberKey, numberTip;
        LogForm logForm;
        Process proc;
        IntPtr job = IntPtr.Zero;
        bool stopping, exiting;
        int port = 7860; // public GUI port
        State state = State.Stopped;
        Action balloonAction;

        public TrayApp(string root, string extraArgs, bool showLog, EventWaitHandle showEvent)
        {
            this.root = root;
            this.extraArgs = extraArgs;
            string launcherDir = Path.Combine(root, "launcher");
            argsFile = Path.Combine(launcherDir, "launcher-args.txt");
            logFile = Path.Combine(launcherDir, "logs", "console.log");
            mcpCache = new McpCache(Path.Combine(launcherDir, "mcp-cache.json"));
            forceMcpFile = Path.Combine(launcherDir, "force-mcp.flag");
            forceMcp = File.Exists(forceMcpFile);
            autoUpdateFile = Path.Combine(launcherDir, "auto-update.flag");
            autoUpdate = File.Exists(autoUpdateFile);
            lastUpdateCheckAt = Now - UpdateCheckEveryMs + 120000; // first check two minutes after the tray starts
            string startArgs = UserArgs();
            port = ParsePort(startArgs);
            onLan = Regex.IsMatch(startArgs, @"(^|\s)--listen(\s|$)");
            IntPtr unused = ui.Handle; // create the handle so worker threads can BeginInvoke onto the UI thread

            int small = SystemInformation.SmallIconSize.Width;
            iconSize = small;
            appIcon = Icons.Make(32, null);
            iconStarting = Icons.Make(small, Color.FromArgb(251, 191, 36));
            iconRunning = Icons.Make(small, Color.FromArgb(34, 197, 94));
            iconStopped = Icons.Make(small, Color.FromArgb(148, 163, 184));

            var menu = new ContextMenuStrip();
            var openItem = new ToolStripMenuItem("Open WanGP in browser", null, delegate { OpenBrowser(); });
            openItem.Font = new Font(openItem.Font, FontStyle.Bold);
            menu.Items.Add(openItem);
            menu.Items.Add(new ToolStripMenuItem("Show console log", null, delegate { ShowLog(); }));
            menu.Items.Add(new ToolStripSeparator());
            startItem = new ToolStripMenuItem("Start WanGP", null, delegate { Start(Mode.Gui, true); });
            stopItem = new ToolStripMenuItem("Stop WanGP", null, delegate { Stop(false); });
            restartItem = new ToolStripMenuItem("Restart WanGP", null, delegate { Stop(true); });
            menu.Items.Add(startItem);
            menu.Items.Add(stopItem);
            menu.Items.Add(restartItem);
            forceMcpItem = new ToolStripMenuItem("Keep MCP server running (port " + McpPort + ")", null, delegate { SetForceMcp(!forceMcp); });
            forceMcpItem.Checked = forceMcp;
            menu.Items.Add(forceMcpItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Edit launch arguments", null, delegate { Program.ReadArgs(argsFile); Process.Start("notepad.exe", "\"" + argsFile + "\""); }));
            menu.Items.Add(new ToolStripMenuItem("Open WanGP folder", null, delegate { Process.Start("explorer.exe", "\"" + root + "\""); }));
            changeFolderItem = new ToolStripMenuItem("Change WanGP folder...", null, delegate { ChangeFolder(); });
            menu.Items.Add(changeFolderItem);
            autostartItem = new ToolStripMenuItem("Start with Windows", null, delegate { ToggleAutostart(); });
            menu.Items.Add(autostartItem);
            autoUpdateItem = new ToolStripMenuItem("Keep WanGP updated", null, delegate { SetAutoUpdate(!autoUpdate); });
            menu.Items.Add(autoUpdateItem);
            updateNowItem = new ToolStripMenuItem("Update WanGP now", null, delegate { UpdateNow(); });
            menu.Items.Add(updateNowItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Exit (stops WanGP)", null, delegate { ExitApp(); }));
            menu.Opening += delegate
            {
                autostartItem.Checked = AutostartEnabled();
                autoUpdateItem.Checked = autoUpdate;
                updateNowItem.Enabled = !updating && !updateChecking && !manualUpdate;
                changeFolderItem.Enabled = true;
            };

            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { ShowLog(); };
            tray.BalloonTipClicked += delegate
            {
                Action a = balloonAction;
                balloonAction = null;
                if (a != null) a();
            };
            tray.Visible = true;

            uiTimer.Interval = 500;
            uiTimer.Tick += delegate
            {
                if (proc != null && state == State.Starting && !stopping && PortInUse(InternalPort(mode)))
                {
                    SetState(State.Running);
                    if (mode == Mode.Mcp)
                    {
                        log.Commit("[launcher] WanGP MCP server is ready at " + McpUrl);
                        mcpCache.Refresh(InternalPort(Mode.Mcp), log.Commit);
                    }
                    else
                    {
                        log.Commit("[launcher] WanGP is ready at " + Url);
                        if (manualStart) ShowBalloon("WanGP is ready", "Click to open " + Url, ToolTipIcon.Info, OpenBrowser);
                    }
                }
                CheckFatal();
                CheckIdle();
                KeepMcp();
                MaybeUpdate();
                SampleCpu();
                // Model loads and some post-processing print nothing for minutes: stay busy while WanGP still uses the CPU or GPU.
                if (busy == 1 && proc != null && Now - Interlocked.Read(ref lastWorkAt) > BusyHoldMs && StillWorking())
                    Interlocked.Exchange(ref lastWorkAt, Now);
                if (busy == 1 && (proc == null || Now - Interlocked.Read(ref lastWorkAt) > BusyHoldMs)) SetIdle();
                RefreshIcon();
            };
            uiTimer.Start();

            var waiter = new Thread(delegate ()
            {
                try
                {
                    WaitHandle[] handles = { showEvent, showStop };
                    while (WaitHandle.WaitAny(handles) == 0)
                        try { ui.BeginInvoke((MethodInvoker)delegate { ShowLog(); }); }
                        catch (InvalidOperationException) { return; } // the tray is shutting down
                }
                catch (ObjectDisposedException) { }
            });
            waiter.IsBackground = true;
            waiter.Start();

            SetState(State.Stopped);
            if (showLog) ShowLog();
            if (forceMcp) log.Commit("[launcher] Keep MCP server running is ticked: starting WanGP in MCP mode.");
            IPAddress bind = onLan ? IPAddress.Any : IPAddress.Loopback;
            guiListener = Listen(bind, port, HandleGui);
            mcpListener = Listen(bind, McpPort, HandleMcp);
            if (guiListener != null)
                log.Commit("[launcher] Waiting for visitors on " + bind + ":" + port + " (GUI) and agents on " + bind + ":" + McpPort +
                    " (MCP" + (onLan ? ", http://" + Environment.MachineName.ToLowerInvariant() + ":" + McpPort + "/mcp from the LAN" : "") +
                    "). WanGP starts on demand and stops after " + IdleStopMs / 60000 + " idle minutes.");
        }

        string Url { get { return "http://localhost:" + port; } }
        static string McpUrl { get { return "http://127.0.0.1:" + McpPort + "/mcp"; } }
        int InternalPort(Mode m) { return (m == Mode.Mcp ? McpPort : port) + InternalOffset; }

        string UserArgs()
        {
            string args = Program.ReadArgs(argsFile);
            return extraArgs.Length > 0 ? args + " " + extraArgs : args;
        }

        // WanGP itself only listens on loopback at an internal port; the launcher owns the public ports.
        string BuildArgs(Mode m)
        {
            string args = Regex.Replace(UserArgs(), @"(^|\s)--(listen|share)(?=\s|$)", "");
            args = Regex.Replace(args, @"(^|\s)--server[-_](port|name)[ =]+\S+", "");
            if (m == Mode.Mcp)
                return args + " --mcp --mcp-transport streamable-http --mcp-host 127.0.0.1 --mcp-port " + InternalPort(m) + " --mcp-async --mcp-console-output";
            return args + " --server-port " + InternalPort(m) + " --server-name 127.0.0.1";
        }

        void Start(Mode m, bool manual)
        {
            if (proc != null || updating) return;
            PyEnv env = PyEnv.Resolve(root);
            string python = env.Python;
            if (env.Type != "none" && !File.Exists(python))
            {
                Fail("Python was not found at " + python);
                return;
            }
            string args = BuildArgs(m);

            if (!CloseOtherCopies()) return;
            if (PortInUse(InternalPort(m)))
            {
                Fail("Port " + InternalPort(m) + " is already in use by another program. Close it, then try again.");
                return;
            }

            log.OpenFile(logFile);
            log.Commit("===== Starting WanGP in " + (m == Mode.Mcp ? "MCP" : "GUI") + " mode " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " =====");
            log.Commit("> " + python + " -u " + args);

            var psi = new ProcessStartInfo(python, "-u " + args);
            psi.WorkingDirectory = root;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = new UTF8Encoding(false);
            psi.StandardErrorEncoding = new UTF8Encoding(false);
            psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";

            var p = new Process();
            p.StartInfo = psi;
            p.EnableRaisingEvents = true;
            p.Exited += delegate { ui.BeginInvoke((MethodInvoker)delegate { OnExited(p); }); };
            try
            {
                p.Start();
            }
            catch (Exception e)
            {
                p.Dispose();
                Fail("Could not start WanGP: " + e.Message);
                return;
            }
            proc = p;
            mode = m;
            manualStart = manual;
            startedAt = Now;
            Interlocked.Exchange(ref lastGuiUseAt, startedAt);
            Interlocked.Exchange(ref lastMcpUseAt, startedAt);
            stopping = false;
            CreateJob();
            TrackChildren(p.Id);
            Pump(p.StandardOutput, 0);
            Pump(p.StandardError, 1);
            SetState(State.Starting);
            if (manual) ShowBalloon("WanGP is starting", "Running in the background. Double-click the tray icon to see the console log.", ToolTipIcon.Info, ShowLog);
        }

        void Stop(bool restart)
        {
            if (!restart && forceMcp) SetForceMcp(false); // a manual stop would otherwise be undone right away
            if (proc == null)
            {
                if (restart) Start(Mode.Gui, true);
                return;
            }
            StopThen(restart ? mode : Mode.None);
        }

        // Stops WanGP, then starts it in mode next unless that is Mode.None.
        void StopThen(Mode next)
        {
            nextMode = next;
            if (stopping) return;
            stopping = true;
            log.Commit("[launcher] Stopping WanGP...");
            KillTree(proc.Id);
            TerminateJob();
        }

        // Called on the UI thread for a visit (Mode.Gui) or an agent tool call (Mode.Mcp) that needs WanGP.
        Avail RequestMode(Mode m)
        {
            if (exiting) return Avail.Failed;
            if (updating || stoppingForUpdate || (updateReady && proc == null && !stopping))
            {
                if (forceMcp && m != Mode.Mcp) return Avail.Busy;
                afterUpdate = m; // start in this mode once the update is installed
                if (!updating && !stoppingForUpdate) BeginUpdate();
                return Avail.Starting;
            }
            if (forceMcp && m != Mode.Mcp)
            {
                KeepMcp();
                return Avail.Busy;
            }
            if (proc != null && stopping)
            {
                if (nextMode == m) return Avail.Starting;
                if (nextMode != Mode.None) return Avail.Busy;
                nextMode = m; // an idle or fatal stop is under way: start again right after it
                return Avail.Starting;
            }
            if (proc != null && mode == m) return state == State.Running ? Avail.Ready : Avail.Starting;
            if (proc != null)
            {
                if (!Quiet()) return Avail.Busy;
                log.Commit("[launcher] " + (m == Mode.Mcp ? "An agent" : "A visitor") + " needs WanGP in " + ModeName(m) +
                    " mode; switching, since the " + ModeName(mode) + " session is idle.");
                StopThen(m);
                return Avail.Starting;
            }
            if (Now - lastFailAt < 30000) return Avail.Failed; // it just failed: don't retry in a loop
            Start(m, false);
            return proc != null ? Avail.Starting : Avail.Failed;
        }

        static string ModeName(Mode m) { return m == Mode.Mcp ? "MCP" : "GUI"; }

        // Last page load (GUI) or request (MCP) for the running mode, or its start.
        long LastUse()
        {
            long used = mode == Mode.Mcp ? Interlocked.Read(ref lastMcpUseAt) : Interlocked.Read(ref lastGuiUseAt);
            return Math.Max(Math.Max(used, startedAt), Interlocked.Read(ref lastWorkAt));
        }

        // True when the running mode may be taken over: nothing generating and nobody used it for a minute.
        bool Quiet()
        {
            if (busy != 0 || (mode == Mode.Mcp && AnyPending())) return false;
            return Now - LastUse() > SwitchQuietMs;
        }

        // Frees RAM and VRAM once WanGP sat unused. An open GUI tab polls WanGP all the time, so for the GUI
        // only page loads and generation count as use; for MCP every request does.
        void CheckIdle()
        {
            if (forceMcp) return;
            if (proc == null || stopping || state != State.Running || busy != 0) return;
            if (mode == Mode.Mcp && AnyPending()) return;
            if (Now - LastUse() < IdleStopMs) return;
            log.Commit("[launcher] WanGP was idle for " + IdleStopMs / 60000 + " minutes; stopping it to free memory. The next visit or agent tool call starts it again.");
            StopThen(Mode.None);
        }

        void SetForceMcp(bool on)
        {
            forceMcp = on;
            forceMcpItem.Checked = on;
            try
            {
                if (on) File.WriteAllText(forceMcpFile, "Keep MCP server running is ticked in the WanGP tray menu.\r\n");
                else if (File.Exists(forceMcpFile)) File.Delete(forceMcpFile);
            }
            catch (Exception) { }
            if (on)
            {
                log.Commit("[launcher] Keep MCP server running: on. WanGP stays in MCP mode on " + McpUrl + " until this is unticked; the web UI is unavailable.");
                if (proc != null && mode == Mode.Gui && busy != 0)
                    log.Commit("[launcher] The GUI is generating; switching to MCP once it finishes.");
                KeepMcp();
            }
            else
            {
                log.Commit("[launcher] Keep MCP server running: off. WanGP is back to on-demand mode and stops after " + IdleStopMs / 60000 + " idle minutes.");
                if (proc != null && mode == Mode.Mcp)
                {
                    long now = Now; // count from now, so the idle stop doesn't fire the moment the box is unticked
                    Interlocked.Exchange(ref lastMcpUseAt, now);
                }
            }
            UpdateTray();
        }

        // While Keep MCP server running is ticked: start MCP mode if WanGP sleeps, and take over the GUI
        // once it isn't generating. After a crash it waits two minutes before trying again.
        void KeepMcp()
        {
            if (!forceMcp || exiting) return;
            if (updating || stoppingForUpdate) return; // the update restarts MCP mode when it is done
            if (stopping)
            {
                if (proc != null) nextMode = Mode.Mcp; // whatever stop is under way, come back as MCP
                return;
            }
            if (proc == null)
            {
                if (Now - lastFailAt < 120000) return;
                Start(Mode.Mcp, false);
                if (proc == null) lastFailAt = Now;
            }
            else if (mode == Mode.Gui && busy == 0)
            {
                log.Commit("[launcher] Switching the GUI session to MCP mode (Keep MCP server running is ticked).");
                StopThen(Mode.Mcp);
            }
        }

        // ---------------------------------------------------------------- updates

        void SetAutoUpdate(bool on)
        {
            autoUpdate = on;
            autoUpdateItem.Checked = on;
            try
            {
                if (on) File.WriteAllText(autoUpdateFile, "Keep WanGP updated is ticked in the WanGP tray menu.\r\n");
                else if (File.Exists(autoUpdateFile)) File.Delete(autoUpdateFile);
            }
            catch (Exception) { }
            log.Commit(on ? "[update] Keep WanGP updated: on. The tray checks GitHub every " + UpdateCheckEveryMs / 3600000 +
                " hours and installs updates while WanGP is not in use; it never stops a generation."
                : "[update] Keep WanGP updated: off.");
            if (on) CheckForUpdate();
        }

        void UpdateNow()
        {
            if (updating || manualUpdate) return;
            manualUpdate = true;
            log.Commit("[update] Checking GitHub for WanGP updates...");
            if (updateReady) log.Commit("[update] An update is already waiting; installing it as soon as WanGP is not generating.");
            else CheckForUpdate();
        }

        // Fetches from GitHub in the background (harmless while WanGP runs) and counts the new commits.
        void CheckForUpdate()
        {
            if (updateChecking || updating) return;
            updateChecking = true;
            lastUpdateCheckAt = Now;
            var t = new Thread(delegate ()
            {
                string output;
                int count = -1;
                string error = null;
                if (RunTool("git", "fetch --quiet", false, out output) != 0) error = "git fetch failed: " + output;
                else if (RunTool("git", "rev-list --count HEAD..@{u}", false, out output) != 0 || !int.TryParse(output.Trim(), out count))
                    error = "could not compare with GitHub: " + output;
                ui.BeginInvoke((MethodInvoker)delegate
                {
                    updateChecking = false;
                    lastUpdateCheckAt = Now;
                    if (error != null)
                    {
                        log.Commit("[update] " + error.Trim());
                        if (manualUpdate)
                        {
                            manualUpdate = false;
                            ShowBalloon("WanGP update failed", "Could not check GitHub for updates. Click for the log.", ToolTipIcon.Warning, ShowLog);
                        }
                        return;
                    }
                    if (count == 0)
                    {
                        if (manualUpdate)
                        {
                            manualUpdate = false;
                            log.Commit("[update] WanGP is up to date.");
                            ShowBalloon("WanGP is up to date", "There are no new updates on GitHub.", ToolTipIcon.Info, null);
                        }
                        return;
                    }
                    updateReady = true;
                    log.Commit("[update] " + count + " new WanGP commit" + (count == 1 ? "" : "s") + " on GitHub. " +
                        (proc == null ? "Installing now." : busy != 0 ? "Installing once the current generation finishes."
                        : manualUpdate ? "Stopping WanGP to install them." : "Installing once WanGP is idle."));
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        // Called by the UI timer: checks on schedule, and installs a found update at a safe moment.
        void MaybeUpdate()
        {
            if (exiting || updating || updateChecking || stoppingForUpdate) return;
            if (autoUpdate && !updateReady && Now - lastUpdateCheckAt > UpdateCheckEveryMs) CheckForUpdate();
            if (!updateReady) return;
            if (!autoUpdate && !manualUpdate)
            {
                updateReady = false; // unticked before it was installed
                return;
            }
            if (proc == null)
            {
                if (!stopping) BeginUpdate();
                return;
            }
            // Never interrupt a generation or an agent's request. An open GUI is left alone until its idle stop,
            // unless you asked for the update; a held MCP server never idles out, so take it once it sat unused.
            if (stopping || state != State.Running || busy != 0 || (mode == Mode.Mcp && AnyPending())) return;
            if (!manualUpdate && !(forceMcp && Now - LastUse() > IdleStopMs)) return;
            log.Commit("[update] Stopping WanGP to install the update; it starts again in " + ModeName(mode) + " mode afterwards.");
            afterUpdate = mode;
            stoppingForUpdate = true;
            StopThen(Mode.None);
        }

        void BeginUpdate()
        {
            if (updating || proc != null) return;
            updating = true;
            updateReady = false;
            SetState(State.Stopped);
            log.Commit("[update] ===== Updating WanGP " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " =====");
            var t = new Thread(delegate ()
            {
                string output, oldHead, newHead, subject, summary = null, error = null;
                RunTool("git", "rev-parse --short HEAD", false, out oldHead);
                oldHead = oldHead.Trim();
                // Fast-forward only: git refuses, and changes nothing, if a local edit or untracked file is in the way.
                if (RunTool("git", "merge --ff-only @{u}", true, out output) != 0)
                    error = "git could not fast-forward (a local change is in the way, or the branch has diverged), so WanGP was left as it was.";
                else
                {
                    RunTool("git", "rev-parse --short HEAD", false, out newHead);
                    RunTool("git", "log -1 --format=%s", false, out subject);
                    newHead = newHead.Trim();
                    summary = oldHead + " -> " + newHead + ": " + subject.Trim();
                    if (oldHead != newHead)
                    {
                        log.Commit("[update] Installing requirements...");
                        if (InstallRequirements(out output) != 0)
                            error = "The code was updated (" + oldHead + " -> " + newHead + "), but installing requirements failed.";
                    }
                }
                ui.BeginInvoke((MethodInvoker)delegate { FinishUpdate(error, summary); });
            });
            t.IsBackground = true;
            t.Start();
        }

        void FinishUpdate(string error, string summary)
        {
            updating = false;
            manualUpdate = false;
            lastUpdateCheckAt = Now;
            if (error != null)
            {
                log.Commit("[update] " + error);
                ShowBalloon("WanGP update failed", error + " Click for the log.", ToolTipIcon.Warning, ShowLog);
            }
            else
            {
                log.Commit("[update] WanGP updated (" + summary + ").");
                ShowBalloon("WanGP updated", summary, ToolTipIcon.Info, ShowLog);
            }
            Mode m = forceMcp ? Mode.Mcp : afterUpdate;
            afterUpdate = Mode.None;
            SetState(State.Stopped);
            if (exiting) return;
            if (m != Mode.None) Start(m, false);
        }

        // A venv made by uv has no pip of its own, so install the way WanGP's setup.py does for uv environments.
        int InstallRequirements(out string output)
        {
            PyEnv env = PyEnv.Resolve(root);
            if (env.Type == "uv")
                return RunTool(PyEnv.FindUv(), "pip install --index-strategy unsafe-best-match --python \"" + env.Dir + "\" -r requirements.txt", true, out output);
            if (env.Type == "conda")
                return RunTool("conda", "run -p \"" + env.Dir + "\" pip install -r requirements.txt", true, out output);
            if (env.Type == "none")
                return RunTool("pip", "install -r requirements.txt", true, out output);
            return RunTool(env.Python, "-m pip install -r requirements.txt --progress-bar off --disable-pip-version-check", true, out output);
        }

        // Runs a command in the WanGP folder and waits for it. With echo, every output line goes to the console log.
        int RunTool(string exe, string args, bool echo, out string output)
        {
            var text = new StringBuilder();
            var psi = new ProcessStartInfo(exe, args);
            psi.WorkingDirectory = root;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = new UTF8Encoding(false);
            psi.StandardErrorEncoding = new UTF8Encoding(false);
            psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            DataReceivedEventHandler onLine = delegate (object sender, DataReceivedEventArgs e)
            {
                if (e.Data == null) return;
                lock (text) text.AppendLine(e.Data);
                if (echo && e.Data.Trim().Length > 0) log.Commit("[update] " + e.Data);
            };
            try
            {
                using (var p = new Process())
                {
                    p.StartInfo = psi;
                    p.OutputDataReceived += onLine;
                    p.ErrorDataReceived += onLine;
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    p.WaitForExit();
                    lock (text) output = text.ToString();
                    return p.ExitCode;
                }
            }
            catch (Exception e)
            {
                output = e.Message;
                return -1;
            }
        }

        void OnExited(Process p)
        {
            if (p != proc) return;
            int code = -1;
            try { code = p.ExitCode; } catch (InvalidOperationException) { }
            KillTree(p.Id); // orphaned children still report the dead parent's id
            p.Dispose();
            proc = null;
            mode = Mode.None;
            CloseJob();
            log.Commit("[launcher] WanGP exited with code " + code + ".");
            SetIdle();
            bool wasStopping = stopping;
            Mode next = nextMode;
            stopping = false;
            nextMode = Mode.None;
            if (exiting) return;
            SetState(State.Stopped);
            if (!wasStopping)
            {
                lastFailAt = Now;
                ShowBalloon("WanGP stopped", "WanGP exited unexpectedly (code " + code + "). Click to view the console log.", ToolTipIcon.Warning, ShowLog);
            }
            else if (stoppingForUpdate)
            {
                stoppingForUpdate = false;
                BeginUpdate();
            }
            else if (next != Mode.None)
            {
                Start(next, false);
            }
        }

        void ExitApp()
        {
            exiting = true;
            showStop.Set();
            foreach (TcpListener l in new[] { guiListener, mcpListener })
                if (l != null) try { l.Stop(); } catch (Exception) { }
            if (proc != null)
            {
                stopping = true;
                KillTree(proc.Id);
                TerminateJob();
                try { proc.WaitForExit(5000); } catch (Exception) { }
            }
            CloseJob();
            try { busyEvent.Reset(); } catch (Exception) { }
            log.CloseFile();
            uiTimer.Stop();
            tray.Visible = false;
            tray.Dispose();
            if (logForm != null) logForm.Dispose();
            ExitThread();
        }

        void ChangeFolder()
        {
            if (updating || stoppingForUpdate)
            {
                MessageBox.Show("WanGP is installing an update; try again when it finishes.", "WanGP launcher", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (busy != 0 || (mode == Mode.Mcp && AnyPending()))
            {
                MessageBox.Show("WanGP is generating; try again when it finishes.", "WanGP launcher", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Select the folder containing wgp.py";
                if (dialog.ShowDialog() != DialogResult.OK) return;
                if (!WanGPLocation.Valid(dialog.SelectedPath))
                {
                    MessageBox.Show("That folder does not contain wgp.py. Choose the WanGP folder and try again.", "WanGP launcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                string chosen = Path.GetFullPath(dialog.SelectedPath);
                if (String.Equals(chosen.TrimEnd('\\'), Path.GetFullPath(root).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
                if (proc != null && MessageBox.Show("WanGP is running. Stop it and switch folders?", "WanGP launcher", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                // A generation may have started while the dialogs were open: never stop one.
                if (busy != 0 || (mode == Mode.Mcp && AnyPending()) || updating || stoppingForUpdate)
                {
                    MessageBox.Show("WanGP started working in the meantime; try again when it finishes.", "WanGP launcher", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                WanGPLocation.Save(chosen);
                Program.RestartWith = chosen;
                ExitApp();
            }
        }

        void SetState(State s)
        {
            state = s;
            string text = StatusText();
            RefreshIcon();
            UpdateTray();
            startItem.Enabled = proc == null && !updating;
            stopItem.Enabled = proc != null;
            restartItem.Enabled = proc != null;
            if (logForm != null && !logForm.IsDisposed) logForm.Text = "WanGP console - " + text;
        }

        string StatusText()
        {
            if (updating) return "updating WanGP...";
            return state == State.Running ? ModeName(mode) + " running on port " + (mode == Mode.Mcp ? McpPort : port)
                : state == State.Starting ? "starting " + ModeName(mode) + "..." : "sleeping (starts on demand)";
        }

        string StatusLine()
        {
            return StatusText() + (forceMcp ? " (held)" : "");
        }

        static long Now { get { return Clock.ElapsedMilliseconds; } }

        // Called on the output threads with every finished line and the live (progress bar) line.
        void Watch(string line)
        {
            if (line.Length == 0) return;
            long now = Now;
            Interlocked.Exchange(ref lastLineAt, now);
            if (FatalLine.IsMatch(line))
                lock (watchLock)
                {
                    if (fatalAt < 0) fatalLine = line.Trim();
                    fatalAt = now;
                }
            if (!WorkLine.IsMatch(line)) return;
            Interlocked.Exchange(ref lastWorkAt, now);
            if (Interlocked.Exchange(ref busy, 1) == 0)
            {
                try { busyEvent.Set(); } catch (Exception) { }
                log.Commit("[launcher] WanGP is working; Kobold, if loaded, unloads to free VRAM and RAM.");
            }
            Match m = DecodeRate.Match(line);
            double rate;
            if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out rate))
                lock (watchLock)
                {
                    decodeRate = rate;
                    decodeAt = now;
                }
            m = Percent.Match(line);
            if (m.Success)
                lock (watchLock)
                {
                    percent = Math.Min(100, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
                    percentAt = now;
                }
        }

        // A fatal error leaves WanGP's web UI up but its generation thread dead while it still holds its memory,
        // which starves Kobold and the next job. Stop it once the output settles; the next request starts it fresh.
        void CheckFatal()
        {
            string line;
            lock (watchLock)
            {
                if (fatalAt < 0) return;
                if (proc == null || stopping)
                {
                    fatalAt = -1;
                    return;
                }
                long now = Now;
                if (now - fatalAt < FatalSettleMs) return;
                if (Interlocked.Read(ref lastWorkAt) > fatalAt + 2000)
                {
                    fatalAt = -1; // generation carried on after the error, so WanGP recovered by itself
                    return;
                }
                if (now - Interlocked.Read(ref lastLineAt) < FatalSettleMs && now - fatalAt < 60000) return;
                line = fatalLine;
                fatalAt = -1;
            }
            log.Commit("[launcher] WanGP is stuck after a fatal error (" + line + "); stopping it to free RAM and VRAM. The next visit or agent tool call starts it fresh.");
            ShowBalloon("WanGP stopped", "It ran out of system memory and got stuck, so it was stopped to free RAM. Click for the log.", ToolTipIcon.Warning, ShowLog);
            StopThen(Mode.None);
        }

        long cpuSampleAt, cpuSampleTicks = -1;
        double cpuCores; // average CPU cores WanGP's process tree used over the last ~5 s

        void SampleCpu()
        {
            long now = Now;
            if (now - cpuSampleAt < 5000) return;
            long ticks = -1;
            lock (jobLock)
            {
                Native.JOBOBJECT_BASIC_ACCOUNTING_INFORMATION info;
                if (job != IntPtr.Zero && Native.QueryInformationJobObject(job, Native.JobObjectBasicAccountingInformation, out info, Marshal.SizeOf(typeof(Native.JOBOBJECT_BASIC_ACCOUNTING_INFORMATION)), IntPtr.Zero))
                    ticks = info.TotalUserTime + info.TotalKernelTime;
            }
            cpuCores = ticks >= 0 && cpuSampleTicks >= 0 && ticks >= cpuSampleTicks ? (ticks - cpuSampleTicks) / 10000.0 / (now - cpuSampleAt) : 0;
            cpuSampleTicks = ticks;
            cpuSampleAt = now;
        }

        bool StillWorking()
        {
            if (cpuCores >= 0.5) return true;
            try
            {
                var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=utilization.gpu --format=csv,noheader,nounits");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                using (Process p = Process.Start(psi))
                {
                    if (!p.WaitForExit(5000)) return false;
                    int util;
                    string first = p.StandardOutput.ReadLine();
                    return first != null && int.TryParse(first.Trim(), out util) && util >= 15;
                }
            }
            catch (Exception) { return false; }
        }

        void SetIdle()
        {
            if (Interlocked.Exchange(ref busy, 0) == 0) return;
            try { busyEvent.Reset(); } catch (Exception) { }
            if (!exiting) log.Commit("[launcher] WanGP is idle; Kobold may load again.");
        }

        // Swaps the status-dot icon for live decode tokens/s (prompt enhancer) or the current progress percent.
        void RefreshIcon()
        {
            string key = null, tip = null;
            float progress = -1;
            bool rateMode = false;
            long now = Now;
            if (proc != null)
                lock (watchLock)
                {
                    if (now - decodeAt < 5000 && decodeRate > 0)
                    {
                        key = Icons.FormatRate(decodeRate);
                        tip = key + " tok/s";
                        rateMode = true;
                    }
                    else if ((percent < 100 && now - percentAt < 180000) || (percent == 100 && now - percentAt < 3000))
                    {
                        key = percent.ToString(CultureInfo.InvariantCulture);
                        tip = key + "%";
                        progress = percent / 100f;
                    }
                }
            if (key != null && !rateMode) key += "|p";
            if (key == numberKey) return;
            Icon old = numberIcon;
            numberIcon = key == null ? null : rateMode
                ? Icons.MakeNumber(iconSize, key, Color.FromArgb(74, 222, 128), Color.FromArgb(168, 85, 247), -1)
                : Icons.MakeNumber(iconSize, key.Substring(0, key.Length - 2), Color.White, Color.FromArgb(168, 85, 247), progress);
            numberKey = key;
            numberTip = tip;
            UpdateTray();
            if (old != null) old.Dispose();
        }

        void UpdateTray()
        {
            Icon icon = numberIcon ?? (state == State.Running ? iconRunning : state == State.Starting ? iconStarting : iconStopped);
            if (tray.Icon != icon) tray.Icon = icon;
            string tip = "WanGP - " + StatusLine() + (numberTip != null ? " - " + numberTip : "");
            tip = tip.Length > 63 ? tip.Substring(0, 63) : tip;
            if (tray.Text != tip) tray.Text = tip;
        }

        bool AutostartEnabled()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue(RunValue) != null;
        }

        void ToggleAutostart()
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (k.GetValue(RunValue) != null) k.DeleteValue(RunValue);
                else k.SetValue(RunValue, "\"" + Application.ExecutablePath + "\"");
            }
        }

        void ShowLog()
        {
            if (logForm == null || logForm.IsDisposed)
            {
                logForm = new LogForm(log, appIcon, OpenBrowser, OpenLogFile);
                SetState(state);
            }
            logForm.Show();
            if (logForm.WindowState == FormWindowState.Minimized) logForm.WindowState = FormWindowState.Normal;
            logForm.Activate();
        }

        void OpenBrowser()
        {
            try { Process.Start(Url); } catch (Exception) { }
        }

        void OpenLogFile()
        {
            if (File.Exists(logFile)) Process.Start("notepad.exe", "\"" + logFile + "\"");
        }

        void ShowBalloon(string title, string text, ToolTipIcon icon, Action onClick)
        {
            balloonAction = onClick;
            tray.ShowBalloonTip(5000, title, text, icon);
        }

        void Fail(string message)
        {
            log.Commit("[launcher] " + message);
            SetState(State.Stopped);
            ShowBalloon("WanGP did not start", message, ToolTipIcon.Error, ShowLog);
        }

        // Another WanGP from this folder (e.g. started with the .bat) would fight this one for VRAM.
        bool CloseOtherCopies()
        {
            var others = new List<int>();
            PyEnv env = PyEnv.Resolve(root);
            string venv = env.Dir == null ? null : Path.GetFullPath(env.Dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
            string rootPrefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT ProcessId, ExecutablePath, CommandLine FROM Win32_Process WHERE Name = 'python.exe'"))
                {
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        string exe = (mo["ExecutablePath"] as string ?? "").ToLowerInvariant();
                        string cmd = mo["CommandLine"] as string ?? "";
                        bool match = venv != null ? exe.StartsWith(venv + "\\") :
                            cmd.IndexOf("wgp.py", StringComparison.OrdinalIgnoreCase) >= 0 && cmd.IndexOf(rootPrefix, StringComparison.OrdinalIgnoreCase) >= 0;
                        if (match && cmd.IndexOf("wgp.py", StringComparison.OrdinalIgnoreCase) >= 0) others.Add(Convert.ToInt32(mo["ProcessId"]));
                    }
                }
            }
            catch (Exception) { return true; }
            if (others.Count == 0) return true;

            var answer = MessageBox.Show(
                "Another copy of WanGP from this folder is already running (for example, one started with Launch WanGP.bat).\n\n" +
                "Close it and start WanGP from the tray launcher instead?",
                "WanGP launcher", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes)
            {
                log.Commit("[launcher] Not started: another copy of WanGP is already running.");
                SetState(State.Stopped);
                return false;
            }
            foreach (int pid in others) KillTree(pid);
            for (int i = 0; i < 20 && (PortInUse(InternalPort(Mode.Gui)) || PortInUse(InternalPort(Mode.Mcp))); i++) Thread.Sleep(250);
            log.Commit("[launcher] Closed the other copy of WanGP.");
            return true;
        }

        void Pump(StreamReader reader, int stream)
        {
            var t = new Thread(delegate ()
            {
                var cur = new StringBuilder();
                bool cr = false;
                var buf = new char[4096];
                try
                {
                    int n;
                    while ((n = reader.Read(buf, 0, buf.Length)) > 0)
                    {
                        for (int i = 0; i < n; i++)
                        {
                            char c = buf[i];
                            if (c == '\n')
                            {
                                Watch(cur.ToString());
                                log.Commit(cur.ToString());
                                cur.Length = 0;
                                cr = false;
                            }
                            else if (c == '\r')
                            {
                                cr = true; // progress bars redraw the line after a bare \r
                            }
                            else
                            {
                                if (cr)
                                {
                                    cur.Length = 0;
                                    cr = false;
                                }
                                cur.Append(c);
                            }
                        }
                        string live = cur.ToString();
                        Watch(live);
                        log.SetLive(stream, live);
                    }
                }
                catch (Exception) { }
                if (cur.Length > 0) log.Commit(cur.ToString());
                log.SetLive(stream, "");
            });
            t.IsBackground = true;
            t.Start();
        }

        // ---------------------------------------------------------------- on-demand proxy

        TcpListener Listen(IPAddress address, int listenPort, Action<TcpClient> handler)
        {
            TcpListener listener;
            try
            {
                listener = new TcpListener(address, listenPort);
                listener.Start(64);
            }
            catch (SocketException e)
            {
                Fail("Could not listen on port " + listenPort + " (" + e.Message + "). Is another WanGP running? Close it, then restart the tray launcher.");
                return null;
            }
            var t = new Thread(delegate ()
            {
                while (true)
                {
                    TcpClient c;
                    try { c = listener.AcceptTcpClient(); }
                    catch (Exception) { return; } // listener stopped
                    var h = new Thread(delegate () { Serve(c, handler); });
                    h.IsBackground = true;
                    h.Start();
                }
            });
            t.IsBackground = true;
            t.Start();
            return listener;
        }

        void Serve(TcpClient client, Action<TcpClient> handler)
        {
            try
            {
                client.NoDelay = true;
                handler(client);
            }
            catch (Exception) { }
            finally
            {
                try { client.Close(); } catch (Exception) { }
            }
        }

        // Browsers: a page load starts WanGP's GUI (or shows a "starting" page that reloads itself); anything else,
        // like the polling of a tab left open, is only passed through while the GUI runs, so it never wakes WanGP.
        void HandleGui(TcpClient client)
        {
            NetworkStream stream = client.GetStream();
            var reader = new HttpReader(stream);
            HttpRequest req = reader.Read(false);
            if (req == null) return;
            if (req.IsPageLoad)
            {
                Interlocked.Exchange(ref lastGuiUseAt, Now);
                Avail a = Ask(Mode.Gui);
                if (a != Avail.Ready)
                {
                    WriteGuiPage(stream, a);
                    return;
                }
            }
            else if (!(proc != null && mode == Mode.Gui && !stopping))
            {
                WriteResponse(stream, "503 Service Unavailable", "text/plain; charset=utf-8", "WanGP is not running. Reload the page to start it.", true);
                return;
            }
            var conn = new Conn { Client = client };
            if (!WaitReady(conn, Mode.Gui))
            {
                WriteResponse(stream, "503 Service Unavailable", "text/plain; charset=utf-8", "WanGP did not start. Check the tray console log.", true);
                return;
            }
            Relay(conn, Mode.Gui, req.Raw);
        }

        void WriteGuiPage(Stream stream, Avail a)
        {
            string title, text;
            int retry;
            if (a == Avail.Starting && (updating || stoppingForUpdate))
            {
                title = "Updating WanGP...";
                text = "WanGP is installing an update. This page reloads by itself and opens WanGP when it is done.";
                retry = 5;
            }
            else if (a == Avail.Starting)
            {
                title = "Starting WanGP...";
                text = "WanGP is loading. This page reloads by itself; it usually takes under a minute.";
                retry = 3;
            }
            else if (a == Avail.Busy)
            {
                title = "WanGP is busy";
                text = forceMcp
                    ? "WanGP is held in MCP mode from its tray menu. Untick Keep MCP server running to use the web UI. This page tries again every 30 seconds."
                    : "An agent is using WanGP through MCP right now. This page tries again every 30 seconds.";
                retry = 30;
            }
            else
            {
                title = "WanGP did not start";
                text = "Check the console log from the WanGP tray icon. This page tries again every 30 seconds.";
                retry = 30;
            }
            string html = "<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\">" +
                "<meta http-equiv=\"refresh\" content=\"" + retry + "\"><title>" + title + "</title>" +
                "<style>body{font-family:system-ui,sans-serif;background:#111827;color:#e5e7eb;display:flex;align-items:center;justify-content:center;height:90vh;margin:0 16px}" +
                "div{max-width:30em;text-align:center}h1{font-weight:600}</style></head><body><div><h1>" + title + "</h1><p>" + text + "</p></div></body></html>";
            WriteResponse(stream, "503 Service Unavailable", "text/html; charset=utf-8", html, true, "Retry-After: " + retry + "\r\nCache-Control: no-store\r\n");
        }

        // Agents: the MCP handshake and the tool, prompt and resource lists are answered from a cache, because
        // Claude Code and Codex connect to every MCP server when a session opens. Only a tool call (or anything
        // not cached) starts WanGP in MCP mode; the request then waits for it and goes through.
        void HandleMcp(TcpClient client)
        {
            NetworkStream stream = client.GetStream();
            var reader = new HttpReader(stream);
            HttpRequest req;
            while (true)
            {
                req = reader.Read(true);
                if (req == null) return;
                if (!HostAllowed(req.Header("Host")))
                {
                    WriteResponse(stream, "421 Misdirected Request", "text/plain; charset=utf-8", "Unknown Host header. Use http://" +
                        Environment.MachineName.ToLowerInvariant() + ":" + McpPort + "/mcp or this PC's IP address.", true);
                    return;
                }
                if (proc != null && mode == Mode.Mcp && !stopping) break;
                if (AnswerMcpLocally(stream, req)) continue;
                Interlocked.Exchange(ref lastMcpUseAt, Now);
                Avail a = Ask(Mode.Mcp);
                if (a == Avail.Ready || a == Avail.Starting) break;
                WriteMcpRefusal(stream, req, a == Avail.Busy
                    ? "WanGP is busy: a person is using its web UI right now. Try again in a few minutes."
                    : "WanGP failed to start. Check the WanGP tray console log.");
            }
            Interlocked.Exchange(ref lastMcpUseAt, Now);
            var conn = new Conn { Client = client, Mcp = true, LastUp = Now };
            lock (connLock) conns.Add(conn); // a waiting tool call keeps WanGP from being stopped or switched
            try
            {
                if (!WaitReady(conn, Mode.Mcp))
                {
                    WriteMcpRefusal(stream, req, "WanGP did not start in time. Check the WanGP tray console log.");
                    return;
                }
                IPAddress peer = ((IPEndPoint)client.Client.RemoteEndPoint).Address;
                if (req.Method == "POST" && (req.Path ?? "").Contains("/mcp"))
                    log.Commit("[launcher] Agent request" + (IPAddress.IsLoopback(peer) ? "" : " from " + peer) + ": " + (req.RpcMethod ?? req.Method));
                byte[] first = IsLoopbackHost(req.Header("Host")) ? req.Raw : AsLocalRequest(req);
                byte[] rest = reader.Leftover();
                byte[] prefix = new byte[first.Length + rest.Length];
                Buffer.BlockCopy(first, 0, prefix, 0, first.Length);
                Buffer.BlockCopy(rest, 0, prefix, first.Length, rest.Length);
                Relay(conn, Mode.Mcp, prefix);
            }
            finally
            {
                lock (connLock) conns.Remove(conn);
            }
        }

        // WanGP's MCP server only accepts loopback Host names (its DNS rebinding guard), so the launcher applies that guard
        // itself with this PC's LAN names and addresses added, then hands a LAN request to WanGP as a local one. It also asks
        // WanGP to close the connection after the reply, so the agent's next request comes on a new connection and is
        // checked and rewritten in turn.
        static byte[] AsLocalRequest(HttpRequest req)
        {
            Encoding latin1 = Encoding.GetEncoding(28591); // keeps every header byte as it was
            var lines = new List<string>();
            foreach (string line in latin1.GetString(req.Raw, 0, req.Head.Length).Split(new[] { "\r\n" }, StringSplitOptions.None))
                if (!Regex.IsMatch(line, @"^(Host|Connection|Keep-Alive)[ \t]*:", RegexOptions.IgnoreCase)) lines.Add(line);
            lines.Add("Host: 127.0.0.1:" + McpPort);
            lines.Add("Connection: close");
            byte[] head = latin1.GetBytes(string.Join("\r\n", lines.ToArray()) + "\r\n\r\n");
            int bodyAt = req.Head.Length + 4;
            var raw = new byte[head.Length + req.Raw.Length - bodyAt];
            Buffer.BlockCopy(head, 0, raw, 0, head.Length);
            Buffer.BlockCopy(req.Raw, bodyAt, raw, head.Length, req.Raw.Length - bodyAt);
            return raw;
        }

        // The name part of a Host header, lower case: "odin:7861" -> "odin", "[::1]:7861" -> "::1".
        static string HostName(string host)
        {
            if (string.IsNullOrEmpty(host)) return null;
            host = host.Trim().ToLowerInvariant();
            if (host.StartsWith("["))
            {
                int end = host.IndexOf(']');
                return end > 1 ? host.Substring(1, end - 1) : null;
            }
            int colon = host.IndexOf(':');
            return (colon >= 0 ? host.Substring(0, colon) : host).TrimEnd('.');
        }

        static bool IsLoopbackHost(string host)
        {
            string name = HostName(host);
            IPAddress ip;
            return name == "localhost" || (name != null && IPAddress.TryParse(name, out ip) && IPAddress.IsLoopback(ip));
        }

        // Names and addresses that lead to this PC. A web page that rebinds its own domain to this PC sends that domain,
        // which is refused, so it cannot drive WanGP from a browser on the LAN.
        static bool HostAllowed(string host)
        {
            string name = HostName(host);
            if (name == null) return false;
            if (IsLoopbackHost(host)) return true;
            string machine = Environment.MachineName.ToLowerInvariant();
            if (name == machine) return true;
            var suffixes = new List<string> { "local", "lan", "home", "home.arpa", "localdomain" };
            IPAddress ip;
            bool isIp = IPAddress.TryParse(name, out ip);
            try
            {
                suffixes.Add(IPGlobalProperties.GetIPGlobalProperties().DomainName);
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    IPInterfaceProperties p = ni.GetIPProperties();
                    suffixes.Add(p.DnsSuffix);
                    if (isIp)
                        foreach (UnicastIPAddressInformation a in p.UnicastAddresses)
                            if (a.Address.Equals(ip)) return true;
                }
            }
            catch (NetworkInformationException) { }
            if (isIp) return false;
            foreach (string s in suffixes)
                if (!string.IsNullOrEmpty(s) && name == machine + "." + s.ToLowerInvariant().Trim('.')) return true;
            return false;
        }

        bool AnswerMcpLocally(Stream stream, HttpRequest req)
        {
            if (req.Method == "GET" || req.Method == "DELETE")
            {
                // No server-to-client stream and no session to end while WanGP sleeps.
                WriteResponse(stream, "405 Method Not Allowed", "text/plain", "", false, "Allow: POST\r\n");
                return true;
            }
            if (req.Method != "POST" || req.Rpc == null) return false;
            if (!req.Rpc.ContainsKey("id"))
            {
                WriteResponse(stream, "202 Accepted", "text/plain", "", false); // notification
                return true;
            }
            Dictionary<string, object> reply;
            if (req.RpcMethod == "ping") reply = new Dictionary<string, object> { { "result", new Dictionary<string, object>() } };
            else
            {
                var p = req.Rpc.ContainsKey("params") ? req.Rpc["params"] as Dictionary<string, object> : null;
                if (p != null && p.ContainsKey("cursor")) return false;
                reply = mcpCache.Get(req.RpcMethod);
                if (reply == null) return false;
                var result = reply.ContainsKey("result") ? reply["result"] as Dictionary<string, object> : null;
                if (req.RpcMethod == "initialize" && result != null && p != null && p.ContainsKey("protocolVersion"))
                    result["protocolVersion"] = p["protocolVersion"];
            }
            reply["jsonrpc"] = "2.0";
            reply["id"] = req.Rpc["id"];
            WriteResponse(stream, "200 OK", "application/json", McpCache.Json.Serialize(reply), false);
            return true;
        }

        void WriteMcpRefusal(Stream stream, HttpRequest req, string message)
        {
            if (req.Rpc == null || !req.Rpc.ContainsKey("id"))
            {
                WriteResponse(stream, "503 Service Unavailable", "text/plain", message, true);
                return;
            }
            var reply = new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", req.Rpc["id"] } };
            if (req.RpcMethod == "tools/call")
                reply["result"] = new Dictionary<string, object> {
                    { "content", new object[] { new Dictionary<string, object> { { "type", "text" }, { "text", message } } } },
                    { "isError", true } };
            else
                reply["error"] = new Dictionary<string, object> { { "code", -32000 }, { "message", message } };
            WriteResponse(stream, "200 OK", "application/json", McpCache.Json.Serialize(reply), false);
        }

        static void WriteResponse(Stream stream, string status, string type, string body, bool close, string extraHeaders = "")
        {
            byte[] b = Encoding.UTF8.GetBytes(body);
            string head = "HTTP/1.1 " + status + "\r\nContent-Type: " + type + "\r\nContent-Length: " + b.Length + "\r\n" + extraHeaders +
                (close ? "Connection: close\r\n" : "") + "\r\n";
            byte[] h = Encoding.ASCII.GetBytes(head);
            stream.Write(h, 0, h.Length);
            stream.Write(b, 0, b.Length);
            stream.Flush();
        }

        Avail Ask(Mode m)
        {
            Avail a = Avail.Failed;
            try { ui.Invoke((MethodInvoker)delegate { a = RequestMode(m); }); } catch (Exception) { }
            return a;
        }

        // Called on a connection thread: waits (the request stays buffered) until WanGP answers in mode m.
        bool WaitReady(Conn conn, Mode m)
        {
            long deadline = Now + ColdStartWaitMs;
            while (Now < deadline && !exiting)
            {
                if (updating || stoppingForUpdate) deadline = Now + ColdStartWaitMs; // an update can take several minutes
                if (proc != null && mode == m && state == State.Running && !stopping) return true;
                if (proc == null && nextMode != m && Now - lastFailAt < 30000) return false;
                Thread.Sleep(200);
                if (!ClientAlive(conn.Client)) return false;
            }
            return false;
        }

        void Relay(Conn conn, Mode m, byte[] prefix)
        {
            var server = new TcpClient();
            server.NoDelay = true;
            try
            {
                server.Connect(IPAddress.Loopback, InternalPort(m));
                conn.Server = server;
                if (prefix.Length > 0)
                {
                    server.GetStream().Write(prefix, 0, prefix.Length);
                    conn.LastUp = Now;
                }
                var up = new Thread(delegate () { Pipe(conn.Client, server, conn, true); });
                up.IsBackground = true;
                up.Start();
                Pipe(server, conn.Client, conn, false);
                up.Join();
            }
            finally
            {
                try { server.Close(); } catch (Exception) { }
            }
        }

        void Pipe(TcpClient from, TcpClient to, Conn conn, bool up)
        {
            var buf = new byte[65536];
            try
            {
                NetworkStream src = from.GetStream(), dst = to.GetStream();
                int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    dst.Write(buf, 0, n);
                    long now = Now;
                    if (up)
                    {
                        conn.LastUp = now;
                        if (conn.Mcp) Interlocked.Exchange(ref lastMcpUseAt, now);
                    }
                    else conn.LastDown = now;
                }
                to.Client.Shutdown(SocketShutdown.Send); // pass the half-close along, the other direction may still be busy
            }
            catch (Exception)
            {
                try { from.Close(); } catch (Exception) { }
                try { to.Close(); } catch (Exception) { }
            }
        }

        static bool ClientAlive(TcpClient c)
        {
            try
            {
                Socket s = c.Client;
                return !(s.Poll(0, SelectMode.SelectRead) && s.Available == 0); // readable with no data means the peer closed
            }
            catch (Exception) { return false; }
        }

        // True while an agent waits on an unanswered request, e.g. a synchronous tool call.
        bool AnyPending()
        {
            long now = Now;
            lock (connLock)
            {
                foreach (Conn c in conns)
                    if (c.LastUp > c.LastDown && now - c.LastUp < 20 * 60 * 1000) return true;
            }
            return false;
        }

        static int ParsePort(string args)
        {
            var m = Regex.Match(args, @"--server[-_]port[ =]+(\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value) : 7860;
        }

        static bool PortInUse(int port)
        {
            foreach (var ep in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
                if (ep.Port == port) return true;
            return false;
        }

        // Job object with kill-on-close, so WanGP dies with the launcher even if the launcher is killed.
        void CreateJob()
        {
            lock (jobLock)
            {
                job = Native.CreateJobObject(IntPtr.Zero, null);
                var info = new Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = Native.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                Native.SetInformationJobObject(job, Native.JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf(typeof(Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION)));
            }
        }

        void TerminateJob()
        {
            lock (jobLock)
            {
                if (job != IntPtr.Zero) Native.TerminateJobObject(job, 1);
            }
        }

        void CloseJob()
        {
            lock (jobLock)
            {
                if (job != IntPtr.Zero) Native.CloseHandle(job);
                job = IntPtr.Zero;
            }
        }

        // The venv python.exe is a redirector that spawns the real interpreter right away, possibly before
        // it could inherit the job, so keep adding the process tree to the job for the first few seconds.
        void TrackChildren(int rootPid)
        {
            var t = new Thread(delegate ()
            {
                for (int i = 0; i < 50; i++)
                {
                    lock (jobLock)
                    {
                        if (job == IntPtr.Zero) return;
                        foreach (int pid in Tree(rootPid))
                        {
                            IntPtr h = Native.OpenProcess(Native.PROCESS_TERMINATE | Native.PROCESS_SET_QUOTA, false, pid);
                            if (h == IntPtr.Zero) continue;
                            Native.AssignProcessToJobObject(job, h);
                            Native.CloseHandle(h);
                        }
                    }
                    Thread.Sleep(100);
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        public static void KillTree(int rootPid)
        {
            List<int> tree = Tree(rootPid);
            for (int i = tree.Count - 1; i >= 0; i--)
            {
                try
                {
                    using (var p = Process.GetProcessById(tree[i])) p.Kill();
                }
                catch (Exception) { }
            }
        }

        // rootPid followed by its descendants, parents before children.
        static List<int> Tree(int rootPid)
        {
            var children = new Dictionary<int, List<int>>();
            foreach (var pe in Native.Snapshot())
            {
                List<int> list;
                if (!children.TryGetValue(pe.Value, out list)) children[pe.Value] = list = new List<int>();
                list.Add(pe.Key);
            }
            var order = new List<int>();
            var queue = new Queue<int>();
            queue.Enqueue(rootPid);
            while (queue.Count > 0)
            {
                int pid = queue.Dequeue();
                if (order.Contains(pid)) continue;
                order.Add(pid);
                List<int> list;
                if (children.TryGetValue(pid, out list))
                    foreach (int c in list)
                        if (c != pid && c != 0) queue.Enqueue(c);
            }
            return order;
        }
    }

    sealed class Conn
    {
        public TcpClient Client, Server;
        public bool Mcp;
        public long LastUp, LastDown;
    }

    sealed class HttpRequest
    {
        public string Method, Path, Head;
        public byte[] Raw;                       // the bytes to forward for this request
        public Dictionary<string, object> Rpc;   // the JSON-RPC message, when the body is a single one
        public string RpcMethod;

        public string Header(string name)
        {
            var m = Regex.Match(Head, @"^" + Regex.Escape(name) + @":[ \t]*(.*?)\r?$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }

        public bool IsPageLoad
        {
            get
            {
                if (Method != "GET") return false;
                string mode = Header("Sec-Fetch-Mode");
                if (mode != null) return mode == "navigate";
                string accept = Header("Accept");
                return accept != null && accept.Contains("text/html");
            }
        }
    }

    // Reads HTTP/1.1 requests off a connection, keeping any bytes that belong to the next one.
    sealed class HttpReader
    {
        const int MaxHead = 64 * 1024, MaxBody = 16 * 1024 * 1024;
        readonly Stream stream;
        byte[] buf = new byte[8192];
        int len;

        public HttpReader(Stream stream) { this.stream = stream; }

        public byte[] Leftover()
        {
            var b = new byte[len];
            Buffer.BlockCopy(buf, 0, b, 0, len);
            len = 0;
            return b;
        }

        // withBody: read the Content-Length body and parse JSON-RPC; otherwise everything read so far is the request.
        public HttpRequest Read(bool withBody)
        {
            int headEnd;
            while ((headEnd = Find(buf, len, "\r\n\r\n")) < 0)
                if (len >= MaxHead || !Fill()) return null;
            var req = new HttpRequest();
            req.Head = Encoding.ASCII.GetString(buf, 0, headEnd);
            string[] first = req.Head.Split(new[] { "\r\n" }, StringSplitOptions.None)[0].Split(' ');
            req.Method = first[0];
            req.Path = first.Length > 1 ? first[1] : "";
            int total = headEnd + 4;
            if (withBody)
            {
                string cl = req.Header("Content-Length");
                int bodyLen = 0;
                if (cl != null && (!int.TryParse(cl.Trim(), out bodyLen) || bodyLen < 0 || bodyLen > MaxBody)) return null;
                if (req.Header("Transfer-Encoding") != null) bodyLen = -1; // chunked: just forward it
                if (bodyLen > 0)
                {
                    while (len < total + bodyLen)
                        if (!Fill()) return null;
                    try
                    {
                        req.Rpc = McpCache.Json.DeserializeObject(Encoding.UTF8.GetString(buf, total, bodyLen)) as Dictionary<string, object>;
                        if (req.Rpc != null && req.Rpc.ContainsKey("method")) req.RpcMethod = req.Rpc["method"] as string;
                    }
                    catch (Exception) { }
                    total += bodyLen;
                }
                else if (bodyLen < 0) total = len;
            }
            else total = len;
            req.Raw = new byte[total];
            Buffer.BlockCopy(buf, 0, req.Raw, 0, total);
            Buffer.BlockCopy(buf, total, buf, 0, len - total);
            len -= total;
            return req;
        }

        bool Fill()
        {
            if (len == buf.Length) Array.Resize(ref buf, buf.Length * 2);
            int n;
            try { n = stream.Read(buf, len, buf.Length - len); }
            catch (Exception) { return false; }
            if (n <= 0) return false;
            len += n;
            return true;
        }

        static int Find(byte[] b, int count, string s)
        {
            for (int i = 0; i + s.Length <= count; i++)
            {
                int j = 0;
                while (j < s.Length && b[i + j] == s[j]) j++;
                if (j == s.Length) return i;
            }
            return -1;
        }
    }

    // WanGP's answers to the MCP handshake and list requests, saved whenever it runs in MCP mode,
    // so agents can connect and see its tools without WanGP being loaded.
    sealed class McpCache
    {
        public static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 1000 };
        static readonly string[] Methods = { "initialize", "tools/list", "prompts/list", "resources/list", "resources/templates/list" };
        readonly string file;
        readonly object gate = new object();
        Dictionary<string, object> entries = new Dictionary<string, object>();

        public McpCache(string file)
        {
            this.file = file;
            try
            {
                if (File.Exists(file)) entries = Json.DeserializeObject(File.ReadAllText(file, Encoding.UTF8)) as Dictionary<string, object> ?? entries;
            }
            catch (Exception) { }
        }

        // A fresh copy of the cached reply (result or error) for method, or null.
        public Dictionary<string, object> Get(string method)
        {
            if (method == null) return null;
            lock (gate)
            {
                object v;
                if (!entries.TryGetValue(method, out v)) return null;
                return Json.DeserializeObject(Json.Serialize(v)) as Dictionary<string, object>;
            }
        }

        public void Refresh(int port, Action<string> log)
        {
            var t = new Thread(delegate ()
            {
                var fresh = new Dictionary<string, object>();
                foreach (string method in Methods)
                {
                    object p = method == "initialize"
                        ? (object)new Dictionary<string, object> {
                            { "protocolVersion", "2025-06-18" }, { "capabilities", new Dictionary<string, object>() },
                            { "clientInfo", new Dictionary<string, object> { { "name", "wangp-tray" }, { "version", "1" } } } }
                        : new Dictionary<string, object>();
                    var msg = new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", 1 }, { "method", method }, { "params", p } };
                    var reply = Post(port, Json.Serialize(msg));
                    if (reply == null) continue;
                    reply.Remove("id");
                    reply.Remove("jsonrpc");
                    if (reply.ContainsKey("result") || reply.ContainsKey("error")) fresh[method] = reply;
                }
                if (!fresh.ContainsKey("initialize") || !fresh.ContainsKey("tools/list"))
                {
                    log("[launcher] Could not read WanGP's MCP tool list; agents keep the cached one.");
                    return;
                }
                lock (gate) entries = fresh;
                try { File.WriteAllText(file, Json.Serialize(fresh), new UTF8Encoding(false)); } catch (Exception) { }
                log("[launcher] Saved WanGP's MCP tool list, so agents can connect while WanGP sleeps.");
            });
            t.IsBackground = true;
            t.Start();
        }

        static Dictionary<string, object> Post(int port, string body)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/mcp");
                req.Method = "POST";
                req.Proxy = null;
                req.Timeout = 60000;
                req.ContentType = "application/json";
                req.Accept = "application/json, text/event-stream";
                byte[] b = Encoding.UTF8.GetBytes(body);
                using (Stream s = req.GetRequestStream()) s.Write(b, 0, b.Length);
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string text = r.ReadToEnd().Trim();
                    if (!text.StartsWith("{")) // an event stream: take its data line
                    {
                        Match m = Regex.Match(text, @"^data:\s?(.*)$", RegexOptions.Multiline);
                        text = m.Success ? m.Groups[1].Value : "";
                    }
                    return Json.DeserializeObject(text) as Dictionary<string, object>;
                }
            }
            catch (Exception) { return null; }
        }
    }

    sealed class LogBuffer
    {
        const int MaxLines = 5000;
        static readonly Regex Ansi = new Regex(@"\x1B(\[[0-9;?]*[ -/]*[@-~]|\][^\x07\x1B]*(\x07|\x1B\\)|[@-Z\\-_])", RegexOptions.Compiled);
        readonly object sync = new object();
        readonly List<string> lines = new List<string>();
        readonly string[] live = { "", "" };
        long firstSeq;
        StreamWriter file;
        int version;

        public int Version { get { return Thread.VolatileRead(ref version); } }

        public long End { get { lock (sync) return firstSeq + lines.Count; } }

        // Starts a fresh console.log for this run and keeps the previous run as console.prev.log.
        public void OpenFile(string path)
        {
            lock (sync)
            {
                CloseFileLocked();
                try
                {
                    string dir = Path.GetDirectoryName(path);
                    Directory.CreateDirectory(dir);
                    if (File.Exists(path)) File.Copy(path, Path.Combine(dir, Path.GetFileNameWithoutExtension(path) + ".prev.log"), true);
                    file = new StreamWriter(path, false, new UTF8Encoding(false));
                    file.AutoFlush = true;
                }
                catch (Exception)
                {
                    file = null;
                }
            }
        }

        public void CloseFile()
        {
            lock (sync) CloseFileLocked();
        }

        void CloseFileLocked()
        {
            if (file != null) file.Dispose();
            file = null;
        }

        public void Commit(string line)
        {
            line = Clean(line);
            lock (sync)
            {
                lines.Add(line);
                if (lines.Count > MaxLines)
                {
                    int drop = lines.Count - MaxLines + 500;
                    lines.RemoveRange(0, drop);
                    firstSeq += drop;
                }
                if (file != null)
                {
                    try { file.WriteLine(line); } catch (Exception) { }
                }
            }
            Interlocked.Increment(ref version);
        }

        public void SetLive(int stream, string text)
        {
            text = Clean(text);
            lock (sync)
            {
                if (live[stream] == text) return;
                live[stream] = text;
            }
            Interlocked.Increment(ref version);
        }

        // Appends the lines numbered nextSeq onward to 'into' and returns the number after the last one.
        // 'rebuilt' means lines before nextSeq were dropped, so 'into' starts at the oldest line kept.
        public long Read(long nextSeq, List<string> into, out bool rebuilt, out string liveText)
        {
            lock (sync)
            {
                rebuilt = nextSeq < firstSeq;
                for (int i = rebuilt ? 0 : (int)(nextSeq - firstSeq); i < lines.Count; i++) into.Add(lines[i]);
                liveText = live[0].Length > 0 && live[1].Length > 0 ? live[0] + "   |   " + live[1] : live[0] + live[1];
                return firstSeq + lines.Count;
            }
        }

        static string Clean(string s)
        {
            return s.IndexOf('\x1B') >= 0 ? Ansi.Replace(s, "") : s;
        }
    }

    sealed class LogForm : Form
    {
        readonly LogBuffer log;
        readonly TextBox box = new TextBox();
        readonly Label liveLabel = new Label();
        readonly CheckBox pause = new CheckBox();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly List<string> pending = new List<string>();
        long nextSeq;
        int shownVersion = -1;
        int boxLines;

        public LogForm(LogBuffer log, Icon icon, Action openBrowser, Action openLogFile)
        {
            this.log = log;
            Text = "WanGP console";
            Icon = icon;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(1100, 640);
            MinimumSize = new Size(520, 300);
            StartPosition = FormStartPosition.CenterScreen;

            var bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Top;
            bar.AutoSize = true;
            bar.WrapContents = false;
            bar.Padding = new Padding(6, 6, 6, 2);
            bar.Controls.Add(MakeButton("Open WanGP", delegate { openBrowser(); }));
            bar.Controls.Add(MakeButton("Copy all", delegate { if (box.TextLength > 0) Clipboard.SetText(box.Text); }));
            bar.Controls.Add(MakeButton("Clear", delegate { box.Clear(); boxLines = 0; nextSeq = log.End; }));
            bar.Controls.Add(MakeButton("Open log file", delegate { openLogFile(); }));
            pause.Text = "Pause scrolling";
            pause.AutoSize = true;
            pause.Margin = new Padding(12, 8, 3, 3);
            pause.CheckedChanged += delegate { shownVersion = -1; };
            bar.Controls.Add(pause);

            box.Multiline = true;
            box.ReadOnly = true;
            box.WordWrap = false;
            box.ScrollBars = ScrollBars.Both;
            box.MaxLength = 0;
            box.Dock = DockStyle.Fill;
            box.BorderStyle = BorderStyle.None;
            box.Font = new Font("Consolas", 9.5f);
            box.BackColor = Color.FromArgb(24, 24, 27);
            box.ForeColor = Color.FromArgb(228, 228, 231);

            liveLabel.Dock = DockStyle.Bottom;
            liveLabel.AutoEllipsis = true;
            liveLabel.UseMnemonic = false;
            liveLabel.Font = box.Font;
            liveLabel.Height = TextRenderer.MeasureText("Ag", box.Font).Height + 12;
            liveLabel.Padding = new Padding(6, 5, 6, 3);
            liveLabel.BackColor = Color.FromArgb(39, 39, 42);
            liveLabel.ForeColor = Color.FromArgb(134, 239, 172);

            // Docking runs from the last control added to the first, so the fill control goes in first.
            Controls.Add(box);
            Controls.Add(liveLabel);
            Controls.Add(bar);

            timer.Interval = 200;
            timer.Tick += delegate { Pull(); };
            timer.Start();
        }

        static Button MakeButton(string text, EventHandler onClick)
        {
            var b = new Button();
            b.Text = text;
            b.AutoSize = true;
            b.Margin = new Padding(3);
            b.Click += onClick;
            return b;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) Pull();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }

        void Pull()
        {
            if (!Visible || pause.Checked) return;
            int v = log.Version;
            if (v == shownVersion) return;
            shownVersion = v;

            pending.Clear();
            if (boxLines > 12000) nextSeq = -1; // keep the text box from growing without bound
            bool rebuilt;
            string live;
            nextSeq = log.Read(nextSeq, pending, out rebuilt, out live);
            if (rebuilt)
            {
                box.Clear();
                boxLines = 0;
            }
            if (pending.Count > 0)
            {
                box.AppendText(string.Join("\r\n", pending.ToArray()) + "\r\n");
                boxLines += pending.Count;
                box.SelectionStart = box.TextLength;
                box.ScrollToCaret();
            }
            liveLabel.Text = live;
        }
    }

    static class Icons
    {
        public static Icon Make(int size, Color? dot)
        {
            using (Bitmap bmp = Draw(size, dot))
            {
                IntPtr h = bmp.GetHicon();
                Icon owned = (Icon)Icon.FromHandle(h).Clone();
                Native.DestroyIcon(h);
                return owned;
            }
        }

        public static string FormatRate(double rate)
        {
            return rate < 9.95 ? rate.ToString("0.0", CultureInfo.InvariantCulture) : Math.Round(rate).ToString(CultureInfo.InvariantCulture);
        }

        // Whole-icon number (tokens/s or percent) on a dark tile, with an accent line on top and, when
        // progress >= 0, a progress bar along the bottom.
        public static Icon MakeNumber(int size, string text, Color textColor, Color accent, float progress)
        {
            using (var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    using (GraphicsPath path = RoundRect(new RectangleF(0, 0, size - 1, size - 1), size * 0.18f))
                    using (var bg = new SolidBrush(Color.FromArgb(17, 24, 39)))
                        g.FillPath(bg, path);
                    float line = Math.Max(1f, size / 12f);
                    bool bar = progress >= 0;
                    float barH = bar ? Math.Max(2f, size / 7f) : 0;
                    float top = line, bottom = size - barH;
                    if (!bar)
                        using (var b = new SolidBrush(accent)) g.FillRectangle(b, size * 0.2f, 0, size * 0.6f, line);
                    using (var sf = (StringFormat)StringFormat.GenericTypographic.Clone())
                    {
                        sf.Alignment = StringAlignment.Center;
                        sf.LineAlignment = StringAlignment.Center;
                        sf.FormatFlags |= StringFormatFlags.NoWrap;
                        float em = size * 0.95f;
                        Font font = null;
                        try
                        {
                            while (true)
                            {
                                font = NumberFont(em);
                                SizeF m = g.MeasureString(text, font, 1000, sf);
                                if (m.Width <= size + 0.5f || em <= 5) break;
                                font.Dispose();
                                font = null;
                                em -= 0.5f;
                            }
                            using (var brush = new SolidBrush(textColor))
                                g.DrawString(text, font, brush, new RectangleF(-4, top, size + 8, bottom - top), sf);
                        }
                        finally
                        {
                            if (font != null) font.Dispose();
                        }
                    }
                    if (bar)
                    {
                        g.SmoothingMode = SmoothingMode.None;
                        using (var track = new SolidBrush(Color.FromArgb(55, 65, 81))) g.FillRectangle(track, 1, size - barH, size - 2, barH - 1);
                        float w = (size - 2) * Math.Min(1f, progress);
                        if (w > 0) using (var fill = new SolidBrush(accent)) g.FillRectangle(fill, 1, size - barH, w, barH - 1);
                    }
                }
                IntPtr h = bmp.GetHicon();
                Icon owned = (Icon)Icon.FromHandle(h).Clone();
                Native.DestroyIcon(h);
                return owned;
            }
        }

        static bool? condensed;

        // Narrow digits fit three to a 16 px icon; Bahnschrift ships with Windows 10 and 11.
        static Font NumberFont(float em)
        {
            if (condensed != false)
            {
                var f = new Font("Bahnschrift SemiBold Condensed", em, FontStyle.Regular, GraphicsUnit.Pixel);
                if (f.Name == "Bahnschrift SemiBold Condensed")
                {
                    condensed = true;
                    return f;
                }
                f.Dispose();
                condensed = false;
            }
            return new Font("Segoe UI", em, FontStyle.Bold, GraphicsUnit.Pixel);
        }

        static Bitmap Draw(int size, Color? dot)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                var rect = new RectangleF(0, 0, size - 1, size - 1);
                using (GraphicsPath path = RoundRect(rect, size * 0.22f))
                using (var brush = new LinearGradientBrush(new RectangleF(0, 0, size, size), Color.FromArgb(79, 70, 229), Color.FromArgb(147, 51, 234), 45f))
                    g.FillPath(brush, path);
                using (var font = new Font("Segoe UI", size * 0.64f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString("W", font, Brushes.White, new RectangleF(0, size * 0.03f, size, size), sf);
                }
                if (dot.HasValue)
                {
                    float d = size * 0.44f;
                    var dr = new RectangleF(size - d - 0.5f, size - d - 0.5f, d, d);
                    using (var fill = new SolidBrush(dot.Value)) g.FillEllipse(fill, dr);
                    using (var pen = new Pen(Color.White, Math.Max(1f, size / 16f))) g.DrawEllipse(pen, dr);
                }
            }
            return bmp;
        }

        static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            float d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // Multi-size .ico for the exe: 32-bit BMP entries for the small sizes, PNG for 256.
        public static void WriteIco(string path)
        {
            int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };
            var images = new List<byte[]>();
            foreach (int s in sizes)
            {
                using (Bitmap bmp = Draw(s, null))
                    images.Add(s == 256 ? Png(bmp) : Dib(bmp));
            }
            using (var w = new BinaryWriter(File.Create(path)))
            {
                w.Write((ushort)0);
                w.Write((ushort)1);
                w.Write((ushort)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0);
                    w.Write((byte)0);
                    w.Write((ushort)1);
                    w.Write((ushort)32);
                    w.Write(images[i].Length);
                    w.Write(offset);
                    offset += images[i].Length;
                }
                foreach (byte[] img in images) w.Write(img);
            }
        }

        static byte[] Png(Bitmap bmp)
        {
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }

        static byte[] Dib(Bitmap bmp)
        {
            int s = bmp.Width;
            int maskStride = (s + 31) / 32 * 4;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(40);
                w.Write(s);
                w.Write(s * 2); // height covers the color image plus the AND mask
                w.Write((ushort)1);
                w.Write((ushort)32);
                w.Write(0);
                w.Write(s * s * 4 + maskStride * s);
                w.Write(0);
                w.Write(0);
                w.Write(0);
                w.Write(0);
                BitmapData data = bmp.LockBits(new Rectangle(0, 0, s, s), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var row = new byte[s * 4];
                    for (int y = s - 1; y >= 0; y--) // bottom-up rows
                    {
                        Marshal.Copy(new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), row, 0, row.Length);
                        w.Write(row);
                    }
                }
                finally
                {
                    bmp.UnlockBits(data);
                }
                w.Write(new byte[maskStride * s]);
                return ms.ToArray();
            }
        }
    }

    static class Native
    {
        public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
        public const int JobObjectExtendedLimitInformation = 9;
        public const uint PROCESS_TERMINATE = 0x0001;
        public const uint PROCESS_SET_QUOTA = 0x0100;
        const uint TH32CS_SNAPPROCESS = 0x2;

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        public const int JobObjectBasicAccountingInformation = 1;

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
        {
            public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime; // 100 ns units
            public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
        }

        [DllImport("kernel32.dll")]
        public static extern bool QueryInformationJobObject(IntPtr job, int infoClass, out JOBOBJECT_BASIC_ACCOUNTING_INFORMATION info, int size, IntPtr returnLength);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateJobObject(IntPtr attributes, string name);

        [DllImport("kernel32.dll")]
        public static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int size);

        [DllImport("kernel32.dll")]
        public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll")]
        public static extern bool TerminateJobObject(IntPtr job, uint exitCode);

        [DllImport("kernel32.dll")]
        public static extern IntPtr OpenProcess(uint access, bool inheritHandle, int pid);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr handle);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32 entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32 entry);

        // (pid, parent pid) for every running process.
        public static List<KeyValuePair<int, int>> Snapshot()
        {
            var result = new List<KeyValuePair<int, int>>();
            IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return result;
            try
            {
                var e = new PROCESSENTRY32();
                e.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));
                for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
                    result.Add(new KeyValuePair<int, int>((int)e.th32ProcessID, (int)e.th32ParentProcessID));
            }
            finally
            {
                CloseHandle(snap);
            }
            return result;
        }
    }
}
