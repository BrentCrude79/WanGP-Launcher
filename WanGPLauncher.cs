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
using System.Security.Cryptography;
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
        static readonly Regex TunnelUrl = new Regex(@"https://([a-z0-9-]+\.trycloudflare\.com)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // Errors after which WanGP stays up but can no longer generate, and keeps holding its RAM and pinned memory.
        static readonly Regex FatalLine = new Regex(@"^Exception in thread |WinError 14(50|55)\]|^MemoryError\b|no attribute 'tiny_vae'", RegexOptions.Compiled);
        const long FatalSettleMs = 15000; // let the traceback finish and any pending save complete before stopping
        const long IdleStopMs = 10 * 60000;  // stop WanGP after this long without work or use
        const long SwitchQuietMs = 60000;    // the other mode may take over only after this long without work or use
        const long ColdStartWaitMs = 5 * 60000;
        const int McpPort = 7861, InternalOffset = 10000; // WanGP itself listens on the public port + 10000

        readonly Icon appIcon, iconStarting, iconRunning, iconStopped;
        readonly ToolStripMenuItem startItem, stopItem, restartItem, autostartItem, forceMcpItem, autoUpdateItem, updateNowItem;
        readonly ToolStripMenuItem tunnelItem, copyTunnelUrlItem;
        readonly ToolStripMenuItem changeFolderItem;
        readonly System.Windows.Forms.Timer uiTimer = new System.Windows.Forms.Timer();
        readonly object jobLock = new object();
        readonly object tunnelJobLock = new object();
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
        const long UpdateRetryBaseDelayMs = 15 * 60000L;
        const long UpdateRetryMaxDelayMs = 120 * 60000L;
        readonly string autoUpdateFile;
        readonly string updatePartialFile;
        bool autoUpdate, manualUpdate, updateChecking, updateReady, updating, stoppingForUpdate;
        Mode afterUpdate = Mode.None;
        long lastUpdateCheckAt, updateRetryAt;
        bool updatePartial;
        List<UpdateFile> partialUpdateFiles;
        HashSet<string> partialLocalFiles;
        string partialPinnedSha, partialOldHead, firstInUsePath;
        int updateFailureCount;
        bool updateRetryWarningShown, updateRepairRequired;
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
        IntPtr tunnelJob = IntPtr.Zero;
        Process tunnelProc;
        volatile string tunnelHost, tunnelToken;
        string onlineTunnelUrl;
        volatile int tunnelEpoch;
        long tunnelStartAt;
        bool tunnelStarting, updatingTunnelCheck;
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
            log.SetUpdateLogFile(Path.Combine(launcherDir, "logs", "update.log"));
            mcpCache = new McpCache(Path.Combine(launcherDir, "mcp-cache.json"));
            forceMcpFile = Path.Combine(launcherDir, "force-mcp.flag");
            forceMcp = File.Exists(forceMcpFile);
            autoUpdateFile = Path.Combine(launcherDir, "auto-update.flag");
            updatePartialFile = Path.Combine(launcherDir, "update-partial.json");
            autoUpdate = File.Exists(autoUpdateFile);
            LoadPartialUpdateState();
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
            tunnelItem = new ToolStripMenuItem("Share MCP server online (Cloudflare tunnel)");
            tunnelItem.CheckOnClick = true;
            tunnelItem.CheckedChanged += delegate
            {
                if (updatingTunnelCheck) return;
                if (tunnelItem.Checked) StartTunnel(); else StopTunnel();
            };
            menu.Items.Add(tunnelItem);
            copyTunnelUrlItem = new ToolStripMenuItem("Copy online MCP URL", null, delegate { CopyTunnelUrl(); });
            copyTunnelUrlItem.Enabled = false;
            menu.Items.Add(copyTunnelUrlItem);
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
                CheckTunnelTimeout();
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
            if (updateRepairRequired) return Avail.Failed;
            if (updatePartial && (!updateReady || updateRetryAt > Now)) return Avail.Busy;
            if (updating || stoppingForUpdate || (updateReady && updateRetryAt <= Now && proc == null && !stopping))
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
            if (updating || stoppingForUpdate || updatePartial || updateRepairRequired) return; // updates restart MCP mode when safe
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
            if (on && !updateReady && !updatePartial) CheckForUpdate();
        }

        void UpdateNow()
        {
            if (updatePartial && !updating && !updateReady && updateRetryAt <= Now)
            {
                updateReady = true;
                log.Commit("[update] Retrying completion of the saved partial update; no fetch is needed.");
                MaybeUpdate();
                return;
            }
            if (updating || manualUpdate || updatePartial || (updateReady && updateRetryAt > Now))
            {
                log.Commit("[update] An update is already pending; it will retry when allowed.");
                return;
            }
            manualUpdate = true;
            log.Commit("[update] Checking GitHub for WanGP updates...");
            if (updateReady) log.Commit("[update] An update is already waiting; installing it as soon as WanGP is not generating.");
            else CheckForUpdate();
        }

        // Fetches from GitHub in the background (harmless while WanGP runs) and counts the new commits.
        void CheckForUpdate()
        {
            if (updateChecking || updating || updateReady || updatePartial || updateRepairRequired) return;
            updateChecking = true;
            lastUpdateCheckAt = Now;
            var t = new Thread(delegate ()
            {
                string output;
                int count = -1;
                string error = null;
                if (RunTool("git", "fetch --quiet", false, out output) != 0) error = "git fetch failed: " + output;
                else if (RunTool("git", "rev-list --count HEAD.." + QuoteArgument("@{u}"), false, out output) != 0 || !int.TryParse(output.Trim(), out count))
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
            if (exiting || updateRepairRequired || updating || updateChecking || stoppingForUpdate) return;
            if (autoUpdate && !updateReady && Now - lastUpdateCheckAt > UpdateCheckEveryMs) CheckForUpdate();
            if (!updateReady) return;
            if (updateRetryAt > Now) return;
            if (!autoUpdate && !manualUpdate && !updatePartial && updateRetryAt == 0)
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
            bool resumePartial = updatePartial;
            List<UpdateFile> resumeFiles = resumePartial && partialUpdateFiles != null ? new List<UpdateFile>(partialUpdateFiles) : null;
            HashSet<string> resumeLocalFiles = resumePartial && partialLocalFiles != null
                ? new HashSet<string>(partialLocalFiles, StringComparer.OrdinalIgnoreCase) : null;
            string resumePinnedSha = resumePartial ? partialPinnedSha : null;
            string resumeOldHead = resumePartial ? partialOldHead : null;
            int failureCount = updateFailureCount;
            bool warningShown = updateRetryWarningShown;
            string firstPath = firstInUsePath;
            updating = true;
            updateReady = false;
            updateRetryAt = 0;
            SetState(State.Stopped);
            log.Commit("[update] ===== Updating WanGP " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " =====");
            bool manualAttempt = manualUpdate;
            var t = new Thread(delegate ()
            {
                string output = "", subject = "", summary = null, error = null;
                List<UpdateFile> files = resumeFiles;
                HashSet<string> localFiles = resumeLocalFiles;
                Dictionary<string, FileStamp> before = null;
                bool partial = resumePartial;
                bool partialRetry = false, partialAbandoned = false, partialFailure = false;
                bool recoveryStateSaved = resumePartial;
                string pinnedSha = resumePinnedSha, oldFullHead = resumeOldHead;
                string postponed = null;
                if (resumePartial && (files == null || localFiles == null))
                {
                    error = "The update was only partly applied and could not be completed automatically. See launcher\\logs\\update.log.";
                    partialAbandoned = true;
                    LogPartialRepair(localFiles, null, pinnedSha, "saved update state is incomplete");
                }
                else if (!resumePartial)
                {
                    if (!TryCaptureUpdateFiles(out files, out localFiles, out pinnedSha, out oldFullHead, out error))
                    {
                        // Keep the ordinary update failure path if Git could not describe the changes.
                    }
                    else if (!TryCaptureFileStamps(files, out before, out error))
                    {
                        // Do not merge if the baseline could not be recorded safely.
                    }
                }
                if (error == null)
                {
                    string currentHead, headError;
                    if (resumePartial && (!ReadHead(out currentHead, out headError) ||
                        (currentHead != oldFullHead && currentHead != pinnedSha)))
                    {
                        partialAbandoned = true;
                        error = "The update was only partly applied and could not be completed automatically. See launcher\\logs\\update.log.";
                        LogPartialRepair(localFiles, null, pinnedSha, headError == null ? "HEAD changed since the partial update" : headError);
                    }
                    string inUseError = null;
                    List<string> inUse = partialAbandoned ? new List<string>() : FindInUseFiles(files, out inUseError);
                    if (!partialAbandoned && inUseError != null)
                    {
                        error = inUseError;
                        partialFailure = resumePartial;
                    }
                    else if (!partialAbandoned && inUse.Count > 0)
                    {
                        postponed = PostponedText(inUse, failureCount + 1);
                        log.Commit("[update] " + postponed);
                        ui.BeginInvoke((MethodInvoker)delegate
                        {
                            FinishPostponedUpdate(postponed, resumePartial, files, localFiles, pinnedSha, oldFullHead,
                                firstPath ?? inUse[0], manualAttempt);
                        });
                        return;
                    }

                    if (error == null && !partialAbandoned && resumePartial)
                    {
                        bool resetSucceeded, abandoned;
                        if (!CompletePartialUpdate(files, localFiles, oldFullHead, pinnedSha, failureCount, warningShown, firstPath,
                            out error, out resetSucceeded, out abandoned))
                        {
                            if (abandoned) partialAbandoned = true;
                            else if (IsUpdateAccessDenied(error)) { partialFailure = true; partial = true; }
                            else { partialRetry = true; partial = true; }
                        }
                        else
                        {
                            partial = false;
                            DeletePartialUpdateState();
                            recoveryStateSaved = false;
                        }
                    }
                    else if (error == null && !partialAbandoned)
                    {
                        if (!ReadHead(out currentHead, out headError) || currentHead != oldFullHead)
                            error = headError == null ? "HEAD changed while the update was being prepared." : headError;
                        else
                        {
                            string stateError;
                            if (!SavePartialUpdateState(pinnedSha, oldFullHead, files, localFiles, failureCount, warningShown,
                                firstPath, DateTime.MinValue, out stateError))
                                error = "Could not save update recovery state: " + stateError;
                            else recoveryStateSaved = true;
                        }

                        if (error == null)
                        {
                            int mergeResult = RunTool("git", "merge --ff-only " + QuoteArgument(pinnedSha), true, out output);
                            if (mergeResult == 0)
                            {
                                if (!ReadHead(out currentHead, out headError) || currentHead != pinnedSha)
                                {
                                    error = headError == null ? "Git did not move HEAD to the pinned update revision." : headError;
                                    partialRetry = true;
                                    partial = true;
                                }
                                else
                                {
                                    DeletePartialUpdateState();
                                    recoveryStateSaved = false;
                                }
                            }
                            else if (ReadHead(out currentHead, out headError) &&
                                ((currentHead == oldFullHead && SnapshotChanged(files, before)) || currentHead == pinnedSha))
                            {
                                partial = true;
                                log.Commit("[update] git stopped partway through and left WanGP half-updated; completing the update.");
                                inUse = FindInUseFiles(files, out inUseError);
                                if (inUseError != null)
                                {
                                    error = inUseError;
                                    partialFailure = true;
                                }
                                else if (inUse.Count > 0)
                                {
                                    postponed = PostponedText(inUse, failureCount + 1);
                                    log.Commit("[update] " + postponed);
                                    ui.BeginInvoke((MethodInvoker)delegate
                                    {
                                        FinishPostponedUpdate(postponed, true, files, localFiles, pinnedSha, oldFullHead,
                                            firstPath ?? inUse[0], manualAttempt);
                                    });
                                    return;
                                }
                                else
                                {
                                    bool resetSucceeded, abandoned;
                                    if (!CompletePartialUpdate(files, localFiles, oldFullHead, pinnedSha, failureCount, warningShown, firstPath,
                                        out error, out resetSucceeded, out abandoned))
                                    {
                                        if (abandoned) partialAbandoned = true;
                                        else if (IsUpdateAccessDenied(error)) { partialFailure = true; partial = true; }
                                        else { partialRetry = true; partial = true; }
                                    }
                                    else
                                    {
                                        partial = false;
                                        DeletePartialUpdateState();
                                        recoveryStateSaved = false;
                                    }
                                }
                            }
                            else if (headError != null)
                            {
                                error = "Could not verify the current revision after Git refused the update; automatic completion will retry.";
                                partialRetry = true;
                                partial = true;
                            }
                            else if (currentHead != oldFullHead && currentHead != pinnedSha)
                            {
                                LogPartialRepair(localFiles, null, pinnedSha, "HEAD changed while the update was running");
                                error = "The update was only partly applied and could not be completed automatically. See launcher\\logs\\update.log.";
                                partialAbandoned = true;
                            }
                            else
                            {
                                DeletePartialUpdateState();
                                recoveryStateSaved = false;
                                error = "git could not fast-forward (a local change is in the way, or the branch has diverged), so WanGP was left as it was.";
                            }
                        }
                    }

                    if (error == null)
                    {
                        string oldShort = oldFullHead.Substring(0, 7), newShort = pinnedSha.Substring(0, 7);
                        if (RunTool("git", "log -1 --format=%s", false, out subject) != 0) subject = "";
                        summary = oldShort + " -> " + newShort + ": " + subject.Trim();
                        if (oldFullHead != pinnedSha)
                        {
                            log.Commit("[update] Installing requirements...");
                            if (InstallRequirements(out output) != 0)
                                error = "The code was updated (" + oldShort + " -> " + newShort + "), but installing requirements failed.";
                        }
                    }
                }
                if (partialAbandoned)
                {
                    DeletePartialUpdateState();
                    ui.BeginInvoke((MethodInvoker)delegate { FinishAbandonedPartialUpdate(error); });
                    return;
                }
                if (partialFailure)
                {
                    ui.BeginInvoke((MethodInvoker)delegate
                    {
                        FinishPartialUpdateFailure(error, files, localFiles, pinnedSha, oldFullHead, firstPath);
                    });
                    return;
                }
                if (partialRetry)
                {
                if (!String.IsNullOrEmpty(error)) log.Commit("[update] " + error);
                    string message = "Automatic completion could not finish; retrying in " + RetryDelayMinutes(failureCount + 1) +
                        " minutes. See launcher\\logs\\update.log.";
                    log.Commit("[update] " + message);
                    ui.BeginInvoke((MethodInvoker)delegate
                    {
                        FinishPostponedUpdate(message, true, files, localFiles, pinnedSha, oldFullHead, firstPath, manualAttempt);
                    });
                    return;
                }
                if (recoveryStateSaved && error != null)
                {
                log.Commit("[update] " + error);
                    string message = "Automatic completion could not finish; retrying in " + RetryDelayMinutes(failureCount + 1) +
                        " minutes. See launcher\\logs\\update.log.";
                    log.Commit("[update] " + message);
                    ui.BeginInvoke((MethodInvoker)delegate
                    {
                        FinishPostponedUpdate(message, true, files, localFiles, pinnedSha, oldFullHead, firstPath, manualAttempt);
                    });
                    return;
                }
                ui.BeginInvoke((MethodInvoker)delegate { FinishUpdate(error, summary); });
            });
            t.IsBackground = true;
            t.Start();
        }

        sealed class UpdateFile
        {
            public string Path;
            public char Status;
        }

        sealed class FileStamp
        {
            public bool Exists;
            public long Length;
            public DateTime LastWriteTimeUtc;
        }

        public sealed class SavedUpdateFile
        {
            public string Path { get; set; }
            public string Status { get; set; }
        }

        public sealed class SavedPartialUpdate
        {
            public int Version { get; set; }
            public string PinnedSha { get; set; }
            public string OldHead { get; set; }
            public string[] LocalFiles { get; set; }
            public SavedUpdateFile[] Files { get; set; }
            public int FailureCount { get; set; }
            public bool WarningShown { get; set; }
            public string FirstInUsePath { get; set; }
            public long RetryAtUtcTicks { get; set; }
        }

        bool IsGitSha(string value)
        {
            return !String.IsNullOrEmpty(value) && Regex.IsMatch(value, "^[0-9a-fA-F]{40}$");
        }

        bool IsUpdateAccessDenied(string error)
        {
            return !String.IsNullOrEmpty(error) && error.StartsWith("cannot write ", StringComparison.OrdinalIgnoreCase) &&
                error.EndsWith(": access denied", StringComparison.OrdinalIgnoreCase);
        }

        void LoadPartialUpdateState()
        {
            if (!File.Exists(updatePartialFile)) return;
            try
            {
                var info = new FileInfo(updatePartialFile);
                if (info.Length <= 0 || info.Length > 4 * 1024 * 1024) throw new InvalidDataException("invalid state file size");
                var serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = 4 * 1024 * 1024;
                SavedPartialUpdate saved = serializer.Deserialize<SavedPartialUpdate>(File.ReadAllText(updatePartialFile, Encoding.UTF8));
                if (saved == null || saved.Version != 1 || !IsGitSha(saved.PinnedSha) || !IsGitSha(saved.OldHead) ||
                    saved.Files == null || saved.LocalFiles == null || saved.Files.Length > 100000 || saved.LocalFiles.Length > 100000 ||
                    saved.FailureCount < 0 || saved.FailureCount > 100000 || saved.RetryAtUtcTicks < 0 || saved.RetryAtUtcTicks > DateTime.MaxValue.Ticks)
                    throw new InvalidDataException("invalid state fields");
                var files = new List<UpdateFile>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (SavedUpdateFile item in saved.Files)
                {
                    if (item == null || !IsSafeUpdatePath(item.Path) || item.Status == null || item.Status.Length != 1 ||
                        "AMDT".IndexOf(item.Status[0]) < 0 || !seen.Add(item.Path))
                        throw new InvalidDataException("invalid update file path");
                    files.Add(new UpdateFile { Path = item.Path, Status = item.Status[0] });
                }
                var localFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string path in saved.LocalFiles)
                {
                    if (!IsSafeUpdatePath(path) || !localFiles.Add(path)) throw new InvalidDataException("invalid local file path");
                }
                if (!String.IsNullOrEmpty(saved.FirstInUsePath) && !IsSafeUpdatePath(saved.FirstInUsePath))
                    throw new InvalidDataException("invalid postponed path");

                partialUpdateFiles = files;
                partialLocalFiles = localFiles;
                partialPinnedSha = saved.PinnedSha.ToLowerInvariant();
                partialOldHead = saved.OldHead.ToLowerInvariant();
                updateFailureCount = saved.FailureCount;
                updateRetryWarningShown = saved.WarningShown;
                firstInUsePath = saved.FirstInUsePath;
                updatePartial = true;
                updateReady = true;
                if (saved.RetryAtUtcTicks > 0)
                {
                    long remaining = Math.Max(0, (new DateTime(saved.RetryAtUtcTicks, DateTimeKind.Utc) - DateTime.UtcNow).Ticks / TimeSpan.TicksPerMillisecond);
                    updateRetryAt = Now + remaining;
                }
                log.Commit("[update] Resuming a saved partial update to " + partialPinnedSha + ".");
            }
            catch (Exception e)
            {
                DeletePartialUpdateState();
                updateRepairRequired = true;
                log.Commit("[update] Saved partial update state is invalid and was removed: " + e.Message);
                log.Commit("[update] Manual repair in the WanGP folder: inspect git status and follow launcher\\logs\\update.log before starting WanGP.");
            }
        }

        bool SavePartialUpdateState(string pinnedSha, string oldHead, List<UpdateFile> files, HashSet<string> localFiles,
            int failureCount, bool warningShown, string firstPath, DateTime retryAtUtc, out string error)
        {
            error = null;
            if (!IsGitSha(pinnedSha) || !IsGitSha(oldHead) || files == null || localFiles == null)
            {
                error = "invalid recovery data";
                return false;
            }
            var saved = new SavedPartialUpdate();
            saved.Version = 1;
            saved.PinnedSha = pinnedSha.ToLowerInvariant();
            saved.OldHead = oldHead.ToLowerInvariant();
            saved.FailureCount = Math.Max(0, failureCount);
            saved.WarningShown = warningShown;
            saved.FirstInUsePath = firstPath;
            saved.RetryAtUtcTicks = retryAtUtc == DateTime.MinValue ? 0 : retryAtUtc.ToUniversalTime().Ticks;
            saved.Files = new SavedUpdateFile[files.Count];
            for (int i = 0; i < files.Count; i++)
            {
                UpdateFile file = files[i];
                if (file == null || !IsSafeUpdatePath(file.Path) || "AMDT".IndexOf(file.Status) < 0)
                {
                    error = "invalid update path";
                    return false;
                }
                saved.Files[i] = new SavedUpdateFile { Path = file.Path, Status = file.Status.ToString() };
            }
            var localPaths = new List<string>(localFiles);
            for (int i = 0; i < localPaths.Count; i++)
                if (!IsSafeUpdatePath(localPaths[i])) { error = "invalid local path"; return false; }
            localPaths.Sort(StringComparer.OrdinalIgnoreCase);
            saved.LocalFiles = localPaths.ToArray();
            if (!String.IsNullOrEmpty(firstPath) && !IsSafeUpdatePath(firstPath)) { error = "invalid postponed path"; return false; }

            string tempFile = Path.Combine(Path.GetDirectoryName(updatePartialFile), "update-partial-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(updatePartialFile));
                var serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = 4 * 1024 * 1024;
                File.WriteAllText(tempFile, serializer.Serialize(saved), new UTF8Encoding(false));
                if (File.Exists(updatePartialFile)) File.Replace(tempFile, updatePartialFile, null);
                else File.Move(tempFile, updatePartialFile);
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
            finally
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch (Exception) { }
            }
        }

        void DeletePartialUpdateState()
        {
            try { if (File.Exists(updatePartialFile)) File.Delete(updatePartialFile); }
            catch (Exception e) { log.Commit("[update] Could not remove saved partial update state: " + e.Message); }
        }

        int RetryDelayMinutes(int attempt)
        {
            if (attempt >= 4) return (int)(UpdateRetryMaxDelayMs / 60000L);
            if (attempt <= 1) return (int)(UpdateRetryBaseDelayMs / 60000L);
            return (int)(UpdateRetryBaseDelayMs / 60000L) << (attempt - 1);
        }

        long RetryDelayMilliseconds(int attempt)
        {
            return Math.Min(UpdateRetryMaxDelayMs, RetryDelayMinutes(attempt) * 60000L);
        }

        bool TryCaptureUpdateFiles(out List<UpdateFile> files, out HashSet<string> localFiles, out string pinnedSha,
            out string oldHead, out string error)
        {
            files = null;
            localFiles = null;
            pinnedSha = null;
            oldHead = null;
            error = null;
            string raw, toolError;
            if (RunToolRaw("git", "rev-parse HEAD", out raw, out toolError) != 0 || !IsGitSha(raw.Trim()))
            {
                error = "could not read the current Git revision: " + toolError.Trim();
                return false;
            }
            oldHead = raw.Trim();
            if (RunToolRaw("git", "rev-parse " + QuoteArgument("@{u}"), out raw, out toolError) != 0 || !IsGitSha(raw.Trim()))
            {
                error = "could not pin the GitHub revision: " + toolError.Trim();
                return false;
            }
            pinnedSha = raw.Trim();
            if (RunToolRaw("git", "diff --name-status --no-renames -z " + QuoteArgument(oldHead) + " " + QuoteArgument(pinnedSha), out raw, out toolError) != 0)
            {
                error = "could not list update files: " + toolError.Trim();
                return false;
            }
            if (!TryParseUpdateFiles(raw, out files))
            {
                error = "git returned an invalid update file list; see launcher\\logs\\update.log.";
                return false;
            }
            if (RunToolRaw("git", "diff --name-only -z " + QuoteArgument(oldHead), out raw, out toolError) != 0)
            {
                error = "could not list local changes: " + toolError.Trim();
                return false;
            }
            if (!TryParseNameOnly(raw, out localFiles))
            {
                error = "git returned an invalid local file list; see launcher\\logs\\update.log.";
                return false;
            }
            return true;
        }

        bool TryParseUpdateFiles(string raw, out List<UpdateFile> files)
        {
            files = new List<UpdateFile>();
            if (raw.Length == 0) return true;
            string[] parts = raw.Split(new char[] { '\0' });
            int length = parts.Length;
            if (length > 0 && parts[length - 1].Length == 0) length--;
            if (length % 2 != 0) return false;
            for (int i = 0; i < length; i += 2)
            {
                if (parts[i].Length != 1 || "AMDT".IndexOf(parts[i][0]) < 0 || !IsSafeUpdatePath(parts[i + 1])) return false;
                files.Add(new UpdateFile { Status = parts[i][0], Path = parts[i + 1] });
            }
            return true;
        }

        bool TryParseNameOnly(string raw, out HashSet<string> paths)
        {
            paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (raw.Length == 0) return true;
            string[] parts = raw.Split(new char[] { '\0' });
            int length = parts.Length;
            if (length > 0 && parts[length - 1].Length == 0) length--;
            for (int i = 0; i < length; i++)
            {
                if (!IsSafeUpdatePath(parts[i])) return false;
                paths.Add(parts[i]);
            }
            return true;
        }

        bool IsSafeUpdatePath(string path)
        {
            if (String.IsNullOrEmpty(path) || Path.IsPathRooted(path) || path.IndexOf('\\') >= 0 || path.IndexOf(':') >= 0) return false;
            string[] parts = path.Split('/');
            char[] invalid = Path.GetInvalidFileNameChars();
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                if (part.Length == 0 || part == "." || part == ".." || part.EndsWith(".") || part.EndsWith(" ")) return false;
                if (part.IndexOfAny(invalid) >= 0) return false;
                string device = part.Split('.')[0].TrimEnd(' ');
                if (String.Equals(device, "CON", StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(device, "PRN", StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(device, "AUX", StringComparison.OrdinalIgnoreCase) ||
                    String.Equals(device, "NUL", StringComparison.OrdinalIgnoreCase) ||
                    Regex.IsMatch(device, @"^(COM|LPT)[1-9]$", RegexOptions.IgnoreCase)) return false;
            }
            string full;
            return TryGetUpdateFullPath(path, out full);
        }

        bool TryGetUpdateFullPath(string path, out string full)
        {
            full = null;
            try
            {
                string basePath = Path.GetFullPath(root);
                if (!basePath.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) &&
                    !basePath.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                    basePath += Path.DirectorySeparatorChar;
                full = Path.GetFullPath(Path.Combine(basePath, path.Replace('/', Path.DirectorySeparatorChar)));
                return full.StartsWith(basePath, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return false; }
        }

        bool TryCaptureFileStamps(List<UpdateFile> files, out Dictionary<string, FileStamp> stamps, out string error)
        {
            stamps = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
            error = null;
            try
            {
                foreach (UpdateFile file in files)
                {
                    string full;
                    if (!TryGetUpdateFullPath(file.Path, out full))
                    {
                        error = "could not safely resolve an update path; see launcher\\logs\\update.log.";
                        return false;
                    }
                    stamps[file.Path] = ReadFileStamp(full);
                }
                return true;
            }
            catch (Exception e)
            {
                error = "could not snapshot update files: " + e.Message;
                return false;
            }
        }

        FileStamp ReadFileStamp(string full)
        {
            var stamp = new FileStamp();
            if (File.Exists(full))
            {
                var info = new FileInfo(full);
                stamp.Exists = true;
                stamp.Length = info.Length;
                stamp.LastWriteTimeUtc = info.LastWriteTimeUtc;
            }
            return stamp;
        }

        bool SnapshotChanged(List<UpdateFile> files, Dictionary<string, FileStamp> before)
        {
            if (before == null) return true;
            foreach (UpdateFile file in files)
            {
                string full;
                if (!TryGetUpdateFullPath(file.Path, out full)) return true;
                FileStamp oldStamp;
                if (!before.TryGetValue(file.Path, out oldStamp)) return true;
                try
                {
                    FileStamp newStamp = ReadFileStamp(full);
                    if (oldStamp.Exists != newStamp.Exists || oldStamp.Length != newStamp.Length ||
                        oldStamp.LastWriteTimeUtc != newStamp.LastWriteTimeUtc) return true;
                }
                catch (Exception) { return true; }
            }
            return false;
        }

        List<string> FindInUseFiles(List<UpdateFile> files, out string error)
        {
            error = null;
            var inUse = new List<string>();
            foreach (UpdateFile file in files)
            {
                string full;
                if (!TryGetUpdateFullPath(file.Path, out full)) continue;
                FileAttributes attributes;
                bool exists;
                if (!TryReadUpdateAttributes(file.Path, full, out attributes, out exists, out error))
                {
                    log.Commit("[update] " + error);
                    return inUse;
                }
                if (!exists) continue;
                try
                {
                    bool readOnly = (attributes & FileAttributes.ReadOnly) != 0;
                    using (var stream = new FileStream(full, FileMode.Open, readOnly ? FileAccess.Read : FileAccess.ReadWrite, FileShare.None)) { }
                }
                catch (UnauthorizedAccessException)
                {
                    error = "cannot write " + DisplayUpdatePath(file.Path) + ": access denied";
                    log.Commit("[update] " + error);
                    return inUse;
                }
                catch (IOException e)
                {
                    if (e.HResult == unchecked((int)0x80070020) || e.HResult == unchecked((int)0x80070021)) inUse.Add(file.Path);
                    else if (e.HResult == unchecked((int)0x80070005))
                    {
                        error = "cannot write " + DisplayUpdatePath(file.Path) + ": access denied";
                        log.Commit("[update] " + error);
                        return inUse;
                    }
                    else
                    {
                        error = "could not check " + DisplayUpdatePath(file.Path) + ": " + e.Message;
                        log.Commit("[update] " + error);
                        return inUse;
                    }
                }
            }
            return inUse;
        }

        bool TryReadUpdateAttributes(string path, string full, out FileAttributes attributes, out bool exists, out string error)
        {
            attributes = 0;
            exists = false;
            error = null;
            try
            {
                attributes = File.GetAttributes(full);
                exists = true;
                return true;
            }
            catch (FileNotFoundException) { return true; }
            catch (DirectoryNotFoundException) { return true; }
            catch (UnauthorizedAccessException)
            {
                error = "cannot write " + DisplayUpdatePath(path) + ": access denied";
                return false;
            }
            catch (IOException e)
            {
                error = "could not check " + DisplayUpdatePath(path) + ": " + e.Message;
                return false;
            }
        }

        string PostponedText(List<string> inUse, int attempt)
        {
            return "Postponed: " + inUse.Count + " file" + (inUse.Count == 1 ? "" : "s") +
                " the update replaces are in use by another program (e.g. " + DisplayUpdatePath(inUse[0]) + "). Retrying in " +
                RetryDelayMinutes(attempt) + " minutes.";
        }

        string DisplayUpdatePath(string path)
        {
            var text = new StringBuilder();
            foreach (char c in path)
            {
                if (c < 32 || c == 127) text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else text.Append(c);
            }
            return text.ToString();
        }

        bool ReadHead(out string head, out string error)
        {
            string output;
            int result = RunTool("git", "rev-parse HEAD", false, out output);
            head = output.Trim();
            error = result == 0 && IsGitSha(head) ? null : "could not read the current Git revision: " + output.Trim();
            return error == null;
        }

        bool CompletePartialUpdate(List<UpdateFile> files, HashSet<string> localFiles, string oldHead, string pinnedSha,
            int failureCount, bool warningShown, string firstPath, out string error, out bool resetSucceeded, out bool abandoned)
        {
            error = null;
            resetSucceeded = false;
            abandoned = false;
            string output;
            string currentHead, headError;
            if (!ReadHead(out currentHead, out headError) || (currentHead != oldHead && currentHead != pinnedSha))
            {
                LogPartialRepair(localFiles, null, pinnedSha, headError == null ? "HEAD changed since the partial update" : headError);
                abandoned = true;
                error = "The update was only partly applied and could not be completed automatically. See launcher\\logs\\update.log.";
                return false;
            }

            if (RunTool("git", "merge-base --is-ancestor " + QuoteArgument(oldHead) + " " + QuoteArgument(pinnedSha), false, out output) != 0)
            {
                LogPartialRepair(localFiles, null, pinnedSha, "the pinned revision is not a descendant of the original HEAD");
                abandoned = true;
                error = "The update was only partly applied and could not be completed automatically. See launcher\\logs\\update.log.";
                return false;
            }

            Dictionary<string, string> oldBlobs, pinnedBlobs;
            if (!TryReadTreeBlobs(oldHead, out oldBlobs, out error) || !TryReadTreeBlobs(pinnedSha, out pinnedBlobs, out error))
            {
                if (error == null) error = "could not read the pinned update trees";
                return false;
            }

            if (RunTool("git", "reset --mixed " + QuoteArgument(pinnedSha), true, out output) != 0)
            {
                log.Commit("[update] Could not reset the index to the pinned revision: " + output.Trim());
                error = "The update was only partly applied and could not be completed automatically. See launcher\\logs\\update.log.";
                return false;
            }
            resetSucceeded = true;

            var candidateFiles = new List<string>();
            foreach (UpdateFile file in files)
                if (!localFiles.Contains(file.Path)) candidateFiles.Add(file.Path);
            Dictionary<string, string> workingHashes;
            if (!TryHashWorkingFiles(candidateFiles, out workingHashes, out error)) return false;

            var checkoutFiles = new List<string>();
            var deleteFiles = new List<string>();
            foreach (UpdateFile file in files)
            {
                if (localFiles.Contains(file.Path)) continue;
                string workingHash;
                if (workingHashes.TryGetValue(file.Path, out workingHash))
                {
                    string oldBlob, pinnedBlob;
                    bool matchesOld = oldBlobs.TryGetValue(file.Path, out oldBlob) && workingHash == oldBlob;
                    bool matchesPinned = pinnedBlobs.TryGetValue(file.Path, out pinnedBlob) && workingHash == pinnedBlob;
                    if (!matchesOld && !matchesPinned)
                    {
                        localFiles.Add(file.Path);
                        log.Commit("[update] Skipped user-edited update path: " + DisplayUpdatePath(file.Path));
                        continue;
                    }
                }
                if (file.Status == 'D') deleteFiles.Add(file.Path);
                else checkoutFiles.Add(file.Path);
            }

            string saveError;
            if (!SavePartialUpdateState(pinnedSha, oldHead, files, localFiles, failureCount, warningShown,
                firstPath, DateTime.MinValue, out saveError))
            {
                error = "Could not save update recovery state: " + saveError;
                return false;
            }
            if (checkoutFiles.Count > 0)
            {
                string tempFile = Path.Combine(Path.GetTempPath(), "wangp-update-" + Guid.NewGuid().ToString("N") + ".paths");
                try
                {
                    var pathspecs = new StringBuilder();
                    foreach (string path in checkoutFiles) pathspecs.Append(":(literal)").Append(path).Append('\0');
                    File.WriteAllText(tempFile, pathspecs.ToString(), new UTF8Encoding(false));
                    string args = "checkout " + QuoteArgument(pinnedSha) + " --pathspec-from-file=" + QuoteArgument(tempFile) + " --pathspec-file-nul";
                    if (RunTool("git", args, true, out output) != 0)
                    {
                        error = "Could not check out all update files: " + output.Trim();
                        return false;
                    }
                }
                catch (Exception e)
                {
                    log.Commit("[update] Could not prepare the update path list: " + e.Message);
                    error = "Could not prepare the update path list: " + e.Message;
                    return false;
                }
                finally
                {
                    try { if (File.Exists(tempFile)) File.Delete(tempFile); }
                    catch (Exception e) { log.Commit("[update] Could not remove the temporary path list: " + e.Message); }
                }
            }

            foreach (string path in deleteFiles)
            {
                string full;
                if (!TryGetUpdateFullPath(path, out full))
                {
                    error = "Could not safely resolve deleted update path " + DisplayUpdatePath(path) + ".";
                    return false;
                }
                FileAttributes attributes;
                bool exists;
                if (!TryReadUpdateAttributes(path, full, out attributes, out exists, out error)) return false;
                if (exists && (attributes & FileAttributes.Directory) != 0)
                {
                    error = "Could not remove deleted update path " + DisplayUpdatePath(path) + ": the path is a directory.";
                    return false;
                }
                try { if (exists) File.Delete(full); }
                catch (Exception e)
                {
                    error = "Could not remove deleted update path " + DisplayUpdatePath(path) + ": " + e.Message;
                    return false;
                }
            }

            bool headRead = ReadHead(out currentHead, out headError);
            HashSet<string> leftovers;
            string diffError;
            bool diffRead = ReadLeftoverPaths(localFiles, out leftovers, out diffError);
            bool verified = headRead && currentHead == pinnedSha && diffRead && leftovers.Count == 0;
            if (!verified)
            {
                string reason = !headRead ? headError : currentHead != pinnedSha ? "HEAD does not match the pinned update revision" :
                    !diffRead ? diffError : "update files remain different from HEAD";
                LogPartialRepair(localFiles, diffRead ? leftovers : null, pinnedSha, reason);
                error = "The update was only partly applied and could not be completed automatically. See launcher\\logs\\update.log.";
                return false;
            }
            return true;
        }

        bool TryReadTreeBlobs(string revision, out Dictionary<string, string> blobs, out string error)
        {
            blobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            error = null;
            string output, toolError;
            if (RunToolRaw("git", "ls-tree -r -z --full-tree " + QuoteArgument(revision), out output, out toolError) != 0)
            {
                error = "could not read files at revision " + revision + ": " + toolError.Trim();
                return false;
            }
            if (output.Length == 0) return true;
            string[] entries = output.Split(new char[] { '\0' });
            int length = entries.Length;
            if (length > 0 && entries[length - 1].Length == 0) length--;
            for (int i = 0; i < length; i++)
            {
                int tab = entries[i].IndexOf('\t');
                if (tab < 0) { error = "git returned an invalid tree entry"; return false; }
                string[] metadata = entries[i].Substring(0, tab).Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                string path = entries[i].Substring(tab + 1);
                if (metadata.Length != 3 || !IsGitSha(metadata[2]) || !IsSafeUpdatePath(path))
                {
                    error = "git returned an unsafe tree entry";
                    return false;
                }
                if (metadata[1] == "blob") blobs[path] = metadata[2].ToLowerInvariant();
            }
            return true;
        }

        bool TryHashWorkingFiles(List<string> paths, out Dictionary<string, string> hashes, out string error)
        {
            hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            error = null;
            var existing = new List<string>();
            var input = new StringBuilder();
            foreach (string path in paths)
            {
                if (!IsSafeUpdatePath(path)) { error = "git returned an unsafe update path"; return false; }
                string full;
                if (!TryGetUpdateFullPath(path, out full)) { error = "could not safely resolve update path " + DisplayUpdatePath(path); return false; }
                FileAttributes attributes;
                bool exists;
                if (!TryReadUpdateAttributes(path, full, out attributes, out exists, out error)) return false;
                if (!exists) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    error = "could not hash " + DisplayUpdatePath(path) + ": the path is a directory";
                    return false;
                }
                existing.Add(path);
                input.Append(path).Append('\n');
            }
            if (existing.Count == 0) return true;
            string output, toolError;
            if (RunToolRawWithInput("git", "hash-object --stdin-paths", input.ToString(), out output, out toolError) != 0)
            {
                error = "could not hash working update files: " + toolError.Trim();
                return false;
            }
            string[] hashesByPath = output.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (hashesByPath.Length != existing.Count)
            {
                error = "git returned an invalid working-file hash list";
                return false;
            }
            for (int i = 0; i < existing.Count; i++)
            {
                string hash = hashesByPath[i].Trim();
                if (!IsGitSha(hash)) { error = "git returned an invalid working-file hash"; return false; }
                hashes[existing[i]] = hash.ToLowerInvariant();
            }
            return true;
        }

        bool ReadLeftoverPaths(HashSet<string> localFiles, out HashSet<string> leftovers, out string error)
        {
            leftovers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            error = null;
            string output, toolError;
            if (RunToolRaw("git", "diff --name-only -z HEAD", out output, out toolError) != 0)
            {
                error = "could not verify local changes: " + toolError.Trim();
                return false;
            }
            HashSet<string> changed;
            if (!TryParseNameOnly(output, out changed))
            {
                error = "git returned an invalid verification file list";
                return false;
            }
            foreach (string path in changed)
                if (!localFiles.Contains(path)) leftovers.Add(path);
            return true;
        }

        void LogPartialRepair(HashSet<string> localFiles, HashSet<string> leftovers, string pinnedSha, string reason)
        {
            log.Commit("[update] Automatic completion could not verify the update: " + reason + ".");
            if (leftovers == null)
            {
                string error;
                HashSet<string> found;
                if (localFiles != null && ReadLeftoverPaths(localFiles, out found, out error)) leftovers = found;
            }
            if (leftovers == null) log.Commit("[update] Leftover paths could not be listed.");
            else if (leftovers.Count == 0) log.Commit("[update] Leftover paths: none.");
            else
            {
                int shown = 0;
                foreach (string path in leftovers)
                {
                    if (shown++ == 20) break;
                    log.Commit("[update] Leftover path: " + DisplayUpdatePath(path));
                }
                if (leftovers.Count > 20) log.Commit("[update] " + (leftovers.Count - 20) + " more leftover path(s) not shown.");
            }
            log.Commit("[update] Manual repair in the WanGP folder:");
            string revision = IsGitSha(pinnedSha) ? pinnedSha : "<pinned-sha>";
            log.Commit("[update]   git reset --mixed \"" + revision + "\"");
            if (leftovers != null && leftovers.Count > 0)
            {
                int shown = 0;
                foreach (string path in leftovers)
                {
                    if (shown++ == 20) break;
                    string literalPath = ":(literal)" + path;
                    log.Commit("[update]   git checkout \"" + revision + "\" -- " + PowerShellQuote(literalPath));
                }
            }
            else log.Commit("[update]   For each leftover path, run git checkout \"" + revision + "\" -- '<path>'.");
        }

        string PowerShellQuote(string value)
        {
            return "'" + value.Replace("'", "''") + "'";
        }

        void FinishPostponedUpdate(string message, bool partial, List<UpdateFile> files, HashSet<string> localFiles,
            string pinnedSha, string oldHead, string postponedPath, bool manualAttempt)
        {
            updating = false;
            updateReady = true;
            updateFailureCount = Math.Min(100000, updateFailureCount + 1);
            long delay = RetryDelayMilliseconds(updateFailureCount);
            updateRetryAt = Now + delay;
            if (String.IsNullOrEmpty(firstInUsePath) && !String.IsNullOrEmpty(postponedPath)) firstInUsePath = postponedPath;
            updatePartial = partial;
            partialUpdateFiles = partial ? new List<UpdateFile>(files) : null;
            partialLocalFiles = partial ? new HashSet<string>(localFiles, StringComparer.OrdinalIgnoreCase) : null;
            partialPinnedSha = partial ? pinnedSha : null;
            partialOldHead = partial ? oldHead : null;
            lastUpdateCheckAt = Now;
            Mode m = forceMcp ? Mode.Mcp : afterUpdate;
            if (!partial) afterUpdate = Mode.None;
            SetState(State.Stopped);
            manualUpdate = false;
            if (partial)
            {
                string saveError;
                if (!SavePartialUpdateState(pinnedSha, oldHead, files, localFiles, updateFailureCount,
                    updateRetryWarningShown, firstInUsePath, DateTime.UtcNow.AddMilliseconds(delay), out saveError))
                {
                    updateRepairRequired = true;
                    log.Commit("[update] Could not persist partial update retry state: " + saveError);
                }
            }
            if (manualAttempt && updateFailureCount == 1) ShowBalloon("WanGP update postponed", message, ToolTipIcon.Warning, ShowLog);
            if (updateFailureCount >= 4 && !updateRetryWarningShown && !String.IsNullOrEmpty(firstInUsePath))
            {
                updateRetryWarningShown = true;
                ShowBalloon("WanGP update keeps being postponed:", DisplayUpdatePath(firstInUsePath) + " is in use. Click for the log.",
                    ToolTipIcon.Warning, ShowLog);
                if (partial)
                {
                    string saveError;
                    if (!SavePartialUpdateState(pinnedSha, oldHead, files, localFiles, updateFailureCount,
                        updateRetryWarningShown, firstInUsePath, DateTime.UtcNow.AddMilliseconds(delay), out saveError))
                        log.Commit("[update] Could not save the retry warning state: " + saveError);
                }
            }
            // A half-updated WanGP would only crash or hold the files open again, so wait for the retry instead.
            if (!exiting && !partial && m != Mode.None) Start(m, false);
        }

        void FinishPartialUpdateFailure(string error, List<UpdateFile> files, HashSet<string> localFiles,
            string pinnedSha, string oldHead, string postponedPath)
        {
            updating = false;
            manualUpdate = false;
            updateReady = false;
            updatePartial = true;
            updateRetryAt = 0;
            partialUpdateFiles = files == null ? null : new List<UpdateFile>(files);
            partialLocalFiles = localFiles == null ? null : new HashSet<string>(localFiles, StringComparer.OrdinalIgnoreCase);
            partialPinnedSha = pinnedSha;
            partialOldHead = oldHead;
            if (String.IsNullOrEmpty(firstInUsePath)) firstInUsePath = postponedPath;
            lastUpdateCheckAt = Now;
            string saveError;
            if (!SavePartialUpdateState(partialPinnedSha, partialOldHead, partialUpdateFiles, partialLocalFiles,
                updateFailureCount, updateRetryWarningShown, firstInUsePath, DateTime.MinValue, out saveError))
            {
                updateRepairRequired = true;
                log.Commit("[update] Could not persist partial update state: " + saveError);
            }
            log.Commit("[update] " + error);
            ShowBalloon("WanGP update failed", error + ". WanGP remains stopped because the update is partial. Click for the log.",
                ToolTipIcon.Warning, ShowLog);
            SetState(State.Stopped);
        }

        void FinishAbandonedPartialUpdate(string error)
        {
            updating = false;
            manualUpdate = false;
            updateReady = false;
            updatePartial = false;
            updateRetryAt = 0;
            partialUpdateFiles = null;
            partialLocalFiles = null;
            partialPinnedSha = null;
            partialOldHead = null;
            afterUpdate = Mode.None;
            updateRepairRequired = true;
            DeletePartialUpdateState();
            if (String.IsNullOrEmpty(error)) error = "The partial update needs manual repair. See launcher\\logs\\update.log.";
            log.Commit("[update] " + error);
            ShowBalloon("WanGP update needs manual repair", "WanGP remains stopped. Click for the log.", ToolTipIcon.Error, ShowLog);
            SetState(State.Stopped);
        }

        string QuoteArgument(string value)
        {
            var quoted = new StringBuilder();
            quoted.Append('"');
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"')
                {
                    quoted.Append('\\', slashes * 2 + 1).Append('"');
                    slashes = 0;
                    continue;
                }
                if (slashes > 0) { quoted.Append('\\', slashes); slashes = 0; }
                quoted.Append(c);
            }
            if (slashes > 0) quoted.Append('\\', slashes * 2);
            quoted.Append('"');
            return quoted.ToString();
        }

        int RunToolRaw(string exe, string args, out string output, out string errorOutput)
        {
            output = "";
            errorOutput = "";
            var psi = new ProcessStartInfo(exe, args);
            psi.WorkingDirectory = root;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = new UTF8Encoding(false);
            psi.StandardErrorEncoding = new UTF8Encoding(false);
            psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            try
            {
                using (var p = new Process())
                {
                    p.StartInfo = psi;
                    p.Start();
                    p.StandardInput.Close();
                    string rawOutput = "", rawError = "";
                    var stdout = new Thread(delegate () { rawOutput = p.StandardOutput.ReadToEnd(); });
                    var stderr = new Thread(delegate () { rawError = p.StandardError.ReadToEnd(); });
                    stdout.IsBackground = true;
                    stderr.IsBackground = true;
                    stdout.Start();
                    stderr.Start();
                    p.WaitForExit();
                    stdout.Join();
                    stderr.Join();
                    output = rawOutput;
                    errorOutput = rawError;
                    return p.ExitCode;
                }
            }
            catch (Exception e)
            {
                errorOutput = e.Message;
                return -1;
            }
        }

        int RunToolRawWithInput(string exe, string args, string input, out string output, out string errorOutput)
        {
            output = "";
            errorOutput = "";
            var psi = new ProcessStartInfo(exe, args);
            psi.WorkingDirectory = root;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = new UTF8Encoding(false);
            psi.StandardErrorEncoding = new UTF8Encoding(false);
            psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            try
            {
                using (var p = new Process())
                {
                    p.StartInfo = psi;
                    p.Start();
                    string rawOutput = "", rawError = "";
                    var stdout = new Thread(delegate () { rawOutput = p.StandardOutput.ReadToEnd(); });
                    var stderr = new Thread(delegate () { rawError = p.StandardError.ReadToEnd(); });
                    stdout.IsBackground = true;
                    stderr.IsBackground = true;
                    stdout.Start();
                    stderr.Start();
                    try { p.StandardInput.Write(input); }
                    finally { p.StandardInput.Close(); }
                    p.WaitForExit();
                    stdout.Join();
                    stderr.Join();
                    output = rawOutput;
                    errorOutput = rawError;
                    return p.ExitCode;
                }
            }
            catch (Exception e)
            {
                errorOutput = e.Message;
                return -1;
            }
        }

        void FinishUpdate(string error, string summary)
        {
            updating = false;
            manualUpdate = false;
            updatePartial = false;
            partialUpdateFiles = null;
            partialLocalFiles = null;
            partialPinnedSha = null;
            partialOldHead = null;
            updateRetryAt = 0;
            updateFailureCount = 0;
            updateRetryWarningShown = false;
            firstInUsePath = null;
            DeletePartialUpdateState();
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
            psi.RedirectStandardInput = true;
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
                    p.StandardInput.Close();
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
            StopTunnel();
            CloseTunnelJob();
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

        void StartTunnel()
        {
            int epoch = ++tunnelEpoch;
            string token;
            try { token = NewTunnelToken(); }
            catch (Exception e)
            {
                log.Commit("[tunnel] Could not create a secret token: " + e.Message);
                StopTunnel();
                ShowBalloon("Cloudflare tunnel failed", "Could not create a secret token. Click for the log.", ToolTipIcon.Warning, ShowLog);
                return;
            }
            tunnelHost = null;
            tunnelToken = token;
            onlineTunnelUrl = null;
            copyTunnelUrlItem.Enabled = false;
            tunnelStarting = true;
            tunnelStartAt = Now;
            log.Commit("[tunnel] Looking for cloudflared.");
            if (!EnsureTunnelJob())
            {
                TunnelStartFailed(epoch, token, "Could not create the Windows job object.");
                return;
            }

            var thread = new Thread(delegate ()
            {
                string executable = null, error = null;
                try { executable = FindCloudflared(epoch); }
                catch (Exception e) { error = e.Message; }
                try
                {
                    ui.BeginInvoke((MethodInvoker)delegate
                    {
                        if (error != null) TunnelStartFailed(epoch, token, error);
                        else TunnelExecutableFound(epoch, executable);
                    });
                }
                catch (InvalidOperationException) { }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        static string NewTunnelToken()
        {
            byte[] bytes = new byte[24];
            using (var random = new RNGCryptoServiceProvider()) random.GetBytes(bytes);
            var token = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) token.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return token.ToString();
        }

        string FindCloudflared(int epoch)
        {
            if (epoch != tunnelEpoch) return null;
            if (TunnelVersionWorks(epoch)) return "cloudflared";
            if (epoch != tunnelEpoch) return null;
            string[] candidates = {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"cloudflared\cloudflared.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"cloudflared\cloudflared.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WinGet\Links\cloudflared.exe")
            };
            foreach (string candidate in candidates)
            {
                if (epoch != tunnelEpoch) return null;
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        bool TunnelVersionWorks(int epoch)
        {
            try
            {
                using (var p = new Process())
                {
                    p.StartInfo = new ProcessStartInfo("cloudflared", "--version");
                    p.StartInfo.UseShellExecute = false;
                    p.StartInfo.CreateNoWindow = true;
                    p.StartInfo.RedirectStandardInput = true;
                    p.Start();
                    if (!AssignTunnelProcess(p, epoch))
                    {
                        if (p.HasExited) return p.ExitCode == 0;
                        KillTree(p.Id);
                        p.WaitForExit(1000);
                        return false;
                    }
                    p.StandardInput.Close();
                    for (int i = 0; i < 100; i++)
                    {
                        if (epoch != tunnelEpoch)
                        {
                            KillTree(p.Id);
                            p.WaitForExit(1000);
                            return false;
                        }
                        if (p.WaitForExit(100)) return p.ExitCode == 0;
                    }
                    KillTree(p.Id);
                    p.WaitForExit(1000);
                    return false;
                }
            }
            catch (Exception) { return false; }
        }

        void TunnelExecutableFound(int epoch, string executable)
        {
            if (epoch != tunnelEpoch || !tunnelItem.Checked) return;
            if (executable == null)
            {
                StopTunnel();
                if (MessageBox.Show("cloudflared is needed to share the MCP server online. Install it with `winget install --id Cloudflare.cloudflared`, or open the Cloudflare download instructions?", "WanGP launcher", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    try { Process.Start("https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/downloads/"); } catch (Exception) { }
                return;
            }
            StartTunnelProcess(epoch, executable, tunnelToken);
        }

        bool EnsureTunnelJob()
        {
            lock (tunnelJobLock)
            {
                if (tunnelJob != IntPtr.Zero) return true;
                IntPtr handle = Native.CreateJobObject(IntPtr.Zero, null);
                if (handle == IntPtr.Zero) return false;
                var info = new Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = Native.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                if (!Native.SetInformationJobObject(handle, Native.JobObjectExtendedLimitInformation, ref info,
                    Marshal.SizeOf(typeof(Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION))))
                {
                    Native.CloseHandle(handle);
                    return false;
                }
                tunnelJob = handle;
                return true;
            }
        }

        bool AssignTunnelProcess(Process p, int epoch)
        {
            lock (tunnelJobLock)
                return epoch == tunnelEpoch && tunnelJob != IntPtr.Zero && Native.AssignProcessToJobObject(tunnelJob, p.Handle);
        }

        void TerminateTunnelJob()
        {
            lock (tunnelJobLock)
                if (tunnelJob != IntPtr.Zero) Native.TerminateJobObject(tunnelJob, 1);
        }

        void StartTunnelProcess(int epoch, string executable, string token)
        {
            Process p = new Process();
            int outputFound = 0;
            try
            {
                p.StartInfo = new ProcessStartInfo(executable, "tunnel --no-autoupdate --url http://127.0.0.1:" + McpPort);
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.CreateNoWindow = true;
                p.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                p.StartInfo.RedirectStandardInput = true;
                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.RedirectStandardError = true;
                p.StartInfo.StandardOutputEncoding = new UTF8Encoding(false);
                p.StartInfo.StandardErrorEncoding = new UTF8Encoding(false);
                p.OutputDataReceived += delegate (object sender, DataReceivedEventArgs e)
                {
                    if (e.Data != null) TunnelOutput(epoch, token, p, e.Data, ref outputFound);
                };
                p.ErrorDataReceived += delegate (object sender, DataReceivedEventArgs e)
                {
                    if (e.Data != null) TunnelOutput(epoch, token, p, e.Data, ref outputFound);
                };
                p.Exited += delegate
                {
                    int code = -1;
                    try { p.WaitForExit(3000); code = p.ExitCode; } catch (Exception) { }
                    try { ui.BeginInvoke((MethodInvoker)delegate { TunnelExited(p, epoch, token, code); }); }
                    catch (InvalidOperationException) { }
                };
                p.EnableRaisingEvents = true;
                if (!p.Start()) throw new InvalidOperationException("cloudflared did not start.");
                tunnelProc = p;
                if (!AssignTunnelProcess(p, epoch))
                    throw new InvalidOperationException("Could not add cloudflared to the Windows job object.");
                p.StandardInput.Close();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                TunnelLog(token, "Started cloudflared.");
            }
            catch (Exception e)
            {
                if (tunnelProc == p) tunnelProc = null;
                try { if (p.Id > 0) KillTree(p.Id); } catch (Exception) { }
                try { TerminateTunnelJob(); } catch (Exception) { }
                try { p.Dispose(); } catch (Exception) { }
                TunnelStartFailed(epoch, token, e.Message);
            }
        }

        void TunnelOutput(int epoch, string token, Process p, string line, ref int outputFound)
        {
            TunnelLog(token, line);
            Match match = TunnelUrl.Match(line);
            if (match.Success && Interlocked.CompareExchange(ref outputFound, 1, 0) == 0)
            {
                string host = match.Groups[1].Value.ToLowerInvariant();
                try { ui.BeginInvoke((MethodInvoker)delegate { TunnelOnline(p, epoch, token, host); }); }
                catch (InvalidOperationException) { }
            }
        }

        void TunnelOnline(Process p, int epoch, string token, string host)
        {
            if (p != tunnelProc || epoch != tunnelEpoch || !tunnelItem.Checked || !Object.ReferenceEquals(tunnelToken, token)) return;
            tunnelHost = host;
            tunnelStarting = false;
            onlineTunnelUrl = "https://" + host + "/" + token + "/mcp";
            copyTunnelUrlItem.Enabled = true;
            TunnelLog(token, "Online: https://" + host + "/<token>/mcp");
            try { Clipboard.SetText(onlineTunnelUrl); } catch (Exception) { }
            ShowBalloon("WanGP MCP server is online", onlineTunnelUrl, ToolTipIcon.Info, null);
        }

        void TunnelExited(Process p, int epoch, string token, int code)
        {
            if (p != tunnelProc || epoch != tunnelEpoch) return;
            tunnelProc = null;
            try { KillTree(p.Id); } catch (Exception) { }
            try { TerminateTunnelJob(); } catch (Exception) { }
            try { p.Dispose(); } catch (Exception) { }
            ++tunnelEpoch;
            ClearTunnelState();
            TunnelLog(token, "cloudflared exited with code " + code + ".");
            log.Commit("[tunnel] Stopped.");
            if (!exiting)
                ShowBalloon("Cloudflare tunnel stopped", "The tunnel exited unexpectedly. Click for the log.", ToolTipIcon.Warning, ShowLog);
        }

        void TunnelStartFailed(int epoch, string token, string message)
        {
            if (epoch != tunnelEpoch) return;
            TunnelLog(token, "Could not start cloudflared: " + message);
            StopTunnel();
            ShowBalloon("Cloudflare tunnel failed", "Click for the log.", ToolTipIcon.Warning, ShowLog);
        }

        void TunnelLog(string token, string line)
        {
            if (token != null && line != null) line = line.Replace(token, "<token>");
            log.Commit("[tunnel] " + line);
        }

        void CheckTunnelTimeout()
        {
            if (tunnelStarting && tunnelItem.Checked && tunnelHost == null && Now - tunnelStartAt >= 30000)
            {
                StopTunnel();
                ShowBalloon("Cloudflare tunnel did not start", "Click for the log.", ToolTipIcon.Warning, ShowLog);
            }
        }

        void StopTunnel()
        {
            ++tunnelEpoch;
            Process p = tunnelProc;
            tunnelProc = null;
            ClearTunnelState();
            if (p != null)
            {
                try { KillTree(p.Id); } catch (Exception) { }
                try { p.WaitForExit(3000); } catch (Exception) { }
                try { p.Dispose(); } catch (Exception) { }
            }
            try { TerminateTunnelJob(); } catch (Exception) { }
            log.Commit("[tunnel] Stopped.");
        }

        void ClearTunnelState()
        {
            tunnelStarting = false;
            tunnelHost = null;
            tunnelToken = null;
            onlineTunnelUrl = null;
            copyTunnelUrlItem.Enabled = false;
            updatingTunnelCheck = true;
            tunnelItem.Checked = false;
            updatingTunnelCheck = false;
        }

        void CopyTunnelUrl()
        {
            if (String.IsNullOrEmpty(onlineTunnelUrl)) return;
            try { Clipboard.SetText(onlineTunnelUrl); }
            catch (Exception) { MessageBox.Show("Could not copy the online MCP URL to the clipboard.", "WanGP launcher", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        void CloseTunnelJob()
        {
            lock (tunnelJobLock)
            {
                if (tunnelJob != IntPtr.Zero) Native.CloseHandle(tunnelJob);
                tunnelJob = IntPtr.Zero;
            }
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
            bool tunnelRequest = false;
            string authorizedTunnelToken = null;
            while (true)
            {
                req = reader.Read(true, delegate (HttpRequest candidate)
                {
                    string ignored;
                    return IsTunnelRequest(candidate, out ignored);
                });
                if (req == null) return;
                authorizedTunnelToken = null;
                string tunnelSnapshot;
                tunnelRequest = IsTunnelRequest(req, out tunnelSnapshot);
                if (tunnelRequest)
                {
                    string token = tunnelToken;
                    string rewritten;
                    if (req.InvalidTunnelBody || !AuthorizeTunnelRequest(req, tunnelSnapshot, token, out rewritten))
                    {
                        WriteResponse(stream, "404 Not Found", "text/plain; charset=utf-8", "Not found.", true);
                        return;
                    }
                    authorizedTunnelToken = token;
                    if (!req.RewriteTunnelRequest(rewritten))
                    {
                        WriteResponse(stream, "404 Not Found", "text/plain; charset=utf-8", "Not found.", true);
                        return;
                    }
                }
                else if (!HostAllowed(req.Header("Host")))
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
                {
                    string rpcMethod = req.RpcMethod ?? req.Method;
                    if (authorizedTunnelToken != null) rpcMethod = rpcMethod.Replace(authorizedTunnelToken, "<token>");
                    log.Commit("[launcher] Agent request" + (IPAddress.IsLoopback(peer) ? "" : " from " + peer) + ": " + rpcMethod);
                }
                byte[] first = !tunnelRequest && IsLoopbackHost(req.Header("Host")) ? req.Raw : AsLocalRequest(req);
                byte[] rest = tunnelRequest ? new byte[0] : reader.Leftover();
                byte[] prefix = new byte[first.Length + rest.Length];
                Buffer.BlockCopy(first, 0, prefix, 0, first.Length);
                Buffer.BlockCopy(rest, 0, prefix, first.Length, rest.Length);
                Relay(conn, Mode.Mcp, prefix, tunnelRequest);
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

        bool IsTunnelRequest(HttpRequest req, out string hostSnapshot)
        {
            hostSnapshot = tunnelHost;
            if (req.HasHeader("Cf-Connecting-Ip") || req.HasHeader("Cf-Ray") || req.HasHeader("Cdn-Loop")) return true;
            if (hostSnapshot == null) return false;
            foreach (string host in req.HeaderValues("Host"))
                if (HostName(host) == hostSnapshot) return true;
            return false;
        }

        bool AuthorizeTunnelRequest(HttpRequest req, string hostSnapshot, string token, out string rewritten)
        {
            rewritten = null;
            if (hostSnapshot == null || token == null || !Object.ReferenceEquals(tunnelHost, hostSnapshot) ||
                !Object.ReferenceEquals(tunnelToken, token) || !ValidTunnelRequestLine(req)) return false;

            string target = req.Path;
            if (!SafeTunnelTarget(target)) return false;
            int queryAt = target.IndexOf('?');
            string path = queryAt < 0 ? target : target.Substring(0, queryAt);
            string query = queryAt < 0 ? "" : target.Substring(queryAt);
            int slashAt = path.IndexOf('/', 1);
            string supplied = slashAt < 0 ? path.Substring(1) : path.Substring(1, slashAt - 1);
            if (!ConstantTimeTokenEquals(supplied, token)) return false;

            rewritten = (slashAt < 0 ? "/" : path.Substring(slashAt)) + query;
            if (!SafeTunnelTarget(rewritten) || req.HasHeader("Transfer-Encoding") && req.HeaderValues("Transfer-Encoding").Count != 1)
                return false;
            return Object.ReferenceEquals(tunnelHost, hostSnapshot) && Object.ReferenceEquals(tunnelToken, token);
        }

        static bool ConstantTimeTokenEquals(string supplied, string token)
        {
            if (supplied == null || token == null) return false;
            int difference = supplied.Length ^ token.Length;
            for (int i = 0; i < token.Length; i++)
                difference |= token[i] ^ (i < supplied.Length ? supplied[i] : (char)0);
            return difference == 0;
        }

        static bool ValidTunnelRequestLine(HttpRequest req)
        {
            if (req == null || String.IsNullOrEmpty(req.Head)) return false;
            if (req.Raw == null || req.Raw.Length < req.Head.Length + 4) return false;
            for (int i = 0; i < req.Head.Length; i++)
            {
                if (req.Raw[i] == '\r' && i + 1 < req.Head.Length && req.Raw[i + 1] == '\n') break;
                if (req.Raw[i] < 0x20 || req.Raw[i] > 0x7e) return false;
            }
            string[] lines = req.Head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] parts = lines[0].Split(' ');
            return parts.Length == 3 && parts[0] == req.Method && parts[1] == req.Path &&
                (parts[2] == "HTTP/1.0" || parts[2] == "HTTP/1.1") &&
                Regex.IsMatch(parts[0], @"^[!#$%&'*+\-.^_`|~0-9A-Za-z]+$");
        }

        static bool SafeTunnelTarget(string target)
        {
            if (String.IsNullOrEmpty(target) || target.Length > 8192 || target[0] != '/' || target.StartsWith("//") ||
                target.IndexOf('\\') >= 0 || target.IndexOf('#') >= 0) return false;
            for (int i = 0; i < target.Length; i++)
                if (target[i] < 0x21 || target[i] > 0x7e) return false;
            int queryAt = target.IndexOf('?');
            string path = queryAt < 0 ? target : target.Substring(0, queryAt);
            if (Regex.IsMatch(path, @"%(?:25)*(2f|5c)", RegexOptions.IgnoreCase) || path.IndexOf("//", StringComparison.Ordinal) >= 0) return false;
            foreach (string segment in path.Split('/'))
            {
                string dots = Regex.Replace(segment, @"%(?:25)*2e", ".", RegexOptions.IgnoreCase);
                if (dots == "." || dots == "..") return false;
            }
            return true;
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
            Relay(conn, m, prefix, false);
        }

        void Relay(Conn conn, Mode m, byte[] prefix, bool oneShot)
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
                if (oneShot)
                    Pipe(server, conn.Client, conn, false);
                else
                {
                    var up = new Thread(delegate () { Pipe(conn.Client, server, conn, true); });
                    up.IsBackground = true;
                    up.Start();
                    Pipe(server, conn.Client, conn, false);
                    up.Join();
                }
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
        public bool InvalidTunnelBody;

        public string Header(string name)
        {
            List<string> values = HeaderValues(name);
            return values.Count == 0 ? null : values[0];
        }

        public List<string> HeaderValues(string name)
        {
            var values = new List<string>();
            MatchCollection matches = Regex.Matches(Head, @"^" + Regex.Escape(name) + @":[ \t]*(.*?)\r?$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            foreach (Match match in matches) values.Add(match.Groups[1].Value);
            return values;
        }

        public bool HasHeader(string name)
        {
            return Regex.IsMatch(Head, @"^" + Regex.Escape(name) + @"[ \t]*:", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        }

        public bool RewriteTunnelRequest(string target)
        {
            if (Raw == null || Head == null || Raw.Length < Head.Length + 4) return false;
            Encoding latin1 = Encoding.GetEncoding(28591);
            int bodyAt = Head.Length + 4;
            string[] lines = latin1.GetString(Raw, 0, Head.Length).Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] requestLine = lines[0].Split(' ');
            if (requestLine.Length != 3) return false;
            requestLine[1] = target;
            lines[0] = String.Join(" ", requestLine);
            var kept = new List<string>();
            string originHeader = @"^" + Regex.Escape("Origin") + @"[ \t]*:";
            foreach (string line in lines)
                if (line.Length == 0 || !Regex.IsMatch(line, originHeader, RegexOptions.IgnoreCase)) kept.Add(line);
            string newHead = String.Join("\r\n", kept.ToArray());
            byte[] head = latin1.GetBytes(newHead + "\r\n\r\n");
            var raw = new byte[head.Length + Raw.Length - bodyAt];
            Buffer.BlockCopy(head, 0, raw, 0, head.Length);
            Buffer.BlockCopy(Raw, bodyAt, raw, head.Length, Raw.Length - bodyAt);
            Head = newHead;
            Path = target;
            Raw = raw;
            return true;
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
            return Read(withBody, null);
        }

        public HttpRequest Read(bool withBody, Predicate<HttpRequest> tunnelCheck)
        {
            int headEnd;
            while ((headEnd = Find(buf, len, "\r\n\r\n")) < 0)
                if (len >= MaxHead || !Fill()) return null;
            var req = new HttpRequest();
            req.Head = Encoding.ASCII.GetString(buf, 0, headEnd);
            string[] first = req.Head.Split(new[] { "\r\n" }, StringSplitOptions.None)[0].Split(' ');
            req.Method = first[0];
            req.Path = first.Length > 1 ? first[1] : "";
            int headerTotal = headEnd + 4;
            int total = headerTotal;
            bool tunnel = withBody && tunnelCheck != null && tunnelCheck(req);
            if (withBody)
            {
                List<string> contentLengths = req.HeaderValues("Content-Length");
                List<string> transferEncodings = req.HeaderValues("Transfer-Encoding");
                string cl = contentLengths.Count == 0 ? null : contentLengths[0];
                int bodyLen = 0;
                if (tunnel && (contentLengths.Count > 1 || transferEncodings.Count > 1 || (cl != null && transferEncodings.Count > 0)))
                    req.InvalidTunnelBody = true;
                bool validLength = true;
                if (cl != null)
                {
                    string value = cl.Trim();
                    validLength = tunnel
                        ? Regex.IsMatch(value, @"^[0-9]+$") && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out bodyLen)
                        : int.TryParse(value, out bodyLen);
                }
                if (req.InvalidTunnelBody)
                    total = headerTotal;
                else if (tunnel && transferEncodings.Count > 0)
                {
                    int chunkedTotal;
                    if (!String.Equals(transferEncodings[0].Trim(), "chunked", StringComparison.OrdinalIgnoreCase) ||
                        !ReadChunked(headerTotal, out chunkedTotal))
                        req.InvalidTunnelBody = true;
                    else total = chunkedTotal;
                }
                else if (transferEncodings.Count > 0)
                {
                    bodyLen = -1; // keep the existing handling for non-tunnel chunked requests
                    total = len;
                }
                else if (cl != null && (!validLength || bodyLen < 0 || bodyLen > MaxBody))
                {
                    if (tunnel) req.InvalidTunnelBody = true;
                    else return null;
                }
                else if (bodyLen > 0)
                {
                    while (len < total + bodyLen)
                    {
                        if (!Fill())
                        {
                            if (tunnel) req.InvalidTunnelBody = true;
                            else return null;
                            break;
                        }
                    }
                    if (!req.InvalidTunnelBody)
                    {
                        try
                        {
                            req.Rpc = McpCache.Json.DeserializeObject(Encoding.UTF8.GetString(buf, total, bodyLen)) as Dictionary<string, object>;
                            if (req.Rpc != null && req.Rpc.ContainsKey("method")) req.RpcMethod = req.Rpc["method"] as string;
                        }
                        catch (Exception) { }
                        total += bodyLen;
                    }
                }
                else if (bodyLen < 0) total = len;
                else if (tunnel && cl == null) total = headerTotal;
                if (req.InvalidTunnelBody) total = headerTotal;
            }
            else total = len;
            req.Raw = new byte[total];
            Buffer.BlockCopy(buf, 0, req.Raw, 0, total);
            Buffer.BlockCopy(buf, total, buf, 0, len - total);
            len -= total;
            return req;
        }

        bool ReadChunked(int bodyStart, out int total)
        {
            total = bodyStart;
            int cursor = bodyStart, payload = 0, chunks = 0;
            while (true)
            {
                int lineEnd = Find(buf, len, "\r\n", cursor);
                while (lineEnd < 0)
                {
                    if (len - bodyStart > MaxBody + MaxHead || !Fill()) return false;
                    lineEnd = Find(buf, len, "\r\n", cursor);
                }
                if (lineEnd - cursor > 8192) return false;
                string sizeLine = Encoding.ASCII.GetString(buf, cursor, lineEnd - cursor);
                int semi = sizeLine.IndexOf(';');
                string hex = semi < 0 ? sizeLine : sizeLine.Substring(0, semi);
                int size;
                if (hex.Length == 0 || hex.Length > 8 || !int.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out size)) return false;
                cursor = lineEnd + 2;
                if (size == 0)
                {
                    while (len < cursor + 2)
                        if (len - bodyStart > MaxBody + MaxHead || !Fill()) return false;
                    if (len >= cursor + 2 && buf[cursor] == '\r' && buf[cursor + 1] == '\n')
                        total = cursor + 2;
                    else
                    {
                        int trailerEnd = Find(buf, len, "\r\n\r\n", cursor);
                        while (trailerEnd < 0)
                        {
                            if (len - cursor > MaxHead || !Fill()) return false;
                            trailerEnd = Find(buf, len, "\r\n\r\n", cursor);
                        }
                        total = trailerEnd + 4;
                    }
                    return total - bodyStart <= MaxBody + MaxHead;
                }
                if (++chunks > 65536) return false;
                if (size > MaxBody - payload || cursor + size + 2 < cursor) return false;
                payload += size;
                int end = cursor + size + 2;
                while (len < end)
                    if (len - bodyStart > MaxBody + MaxHead || !Fill()) return false;
                if (buf[cursor + size] != '\r' || buf[cursor + size + 1] != '\n') return false;
                cursor = end;
            }
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
            return Find(b, count, s, 0);
        }

        static int Find(byte[] b, int count, string s, int start)
        {
            for (int i = start; i + s.Length <= count; i++)
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
        const long MaxUpdateLogBytes = 2 * 1024 * 1024L;
        static readonly Regex Ansi = new Regex(@"\x1B(\[[0-9;?]*[ -/]*[@-~]|\][^\x07\x1B]*(\x07|\x1B\\)|[@-Z\\-_])", RegexOptions.Compiled);
        readonly object sync = new object();
        readonly object updateLogLock = new object();
        readonly List<string> lines = new List<string>();
        readonly string[] live = { "", "" };
        string updateLogPath;
        long firstSeq;
        StreamWriter file;
        int version;

        public int Version { get { return Thread.VolatileRead(ref version); } }

        public void SetUpdateLogFile(string path)
        {
            updateLogPath = path;
        }

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
            if (line.StartsWith("[update]", StringComparison.Ordinal)) AppendUpdateLog(line);
            Interlocked.Increment(ref version);
        }

        void AppendUpdateLog(string line)
        {
            string path = updateLogPath;
            if (String.IsNullOrEmpty(path)) return;
            lock (updateLogLock)
            {
                try
                {
                    string dir = Path.GetDirectoryName(path);
                    Directory.CreateDirectory(dir);
                    if (File.Exists(path) && new FileInfo(path).Length > MaxUpdateLogBytes)
                    {
                        string previous = Path.Combine(dir, "update.prev.log");
                        try
                        {
                            if (File.Exists(previous)) File.Delete(previous);
                            File.Move(path, previous);
                        }
                        catch (Exception) { }
                    }
                    string normalized = line.Replace("\r\n", "\n").Replace('\r', '\n');
                    string[] parts = normalized.Split(new char[] { '\n' });
                    var contents = new StringBuilder();
                    foreach (string part in parts)
                        contents.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(' ').Append(part).Append(Environment.NewLine);
                    File.AppendAllText(path, contents.ToString(), new UTF8Encoding(false));
                }
                catch (Exception) { }
            }
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
