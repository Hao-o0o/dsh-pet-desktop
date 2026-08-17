// DshPetDesktop - Misaka (misaka-mikoto-premium) desktop pet + DSH Controller
// Standalone WPF program: transparent, borderless, always-on-top floating
// window that plays spritesheet animation tracks, is draggable, reacts to
// clicks, randomizes its idle behaviour every 8 seconds, and can walk/run
// left and right across the screen.
//
// DSH Controller integration (dsh-tray.ps1 style):
//   - tray icon shows dscfgon.ico (running) / dscfgoff.ico (stopped)
//   - left/right click on the tray opens a WPF rounded-corner panel menu
//     (theme follows the system) with DSH service control: start / stop /
//     restart / open WebUI / config... - sharing Workshop\config.json
//   - pet settings live in the same menu (size, walk, speed, click-through,
//     topmost, autostart, hide)
//
// Local HTTP server on 127.0.0.1:18787 so the DSH web pet can drive this
// pet's animation (GET /play?track=.., /state, /health, /config).
//
// Build (no NuGet, no network, .NET Framework 4.x from the OS):
//   "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe \
//     /codepage:65001 /optimize+ /out:Pet.exe \
//     /r:System.dll /r:System.Core.dll /r:System.Xaml.dll /r:Microsoft.CSharp.dll \
//     /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:WindowsBase.dll \
//     /r:PresentationCore.dll /r:PresentationFramework.dll Pet.cs
//
// Usage:
//   - drag the pet to move it around (position is remembered)
//   - left click: random reaction (jump / wave / run)
//   - right click / tray icon: DSH Controller + pet settings panel
using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DshPetDesktop
{
    internal sealed class Track
    {
        public readonly int Row;
        public readonly int[] Frames;
        public readonly int[] DurMs;
        public readonly bool Loop;
        public readonly int Fallback; // row of the fallback track, -1 = none
        public Track(int row, int[] frames, int[] durMs, bool loop, int fallback)
        {
            Row = row; Frames = frames; DurMs = durMs; Loop = loop; Fallback = fallback;
        }
    }

    public class PetWindow : Window
    {
        private const int CELL_W = 192;
        private const int CELL_H = 208;
        private const string RUN_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RUN_VALUE = "DshPetDesktop";
        private const int HTTP_PORT = 18787;

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x20;

        // Size options: 25%..200% in steps of 25.
        private static readonly int[] SIZE_PCTS = { 25, 50, 75, 100, 125, 150, 175, 200 };
        // Walk/run speed options, px per second (screen pixels).
        private static readonly int[] SPEED_OPTIONS = { 20, 40, 60, 80, 120, 160, 240, 320 };
        // Idle-perform interval options, seconds (0 = never perform).
        private static readonly int[] IDLE_INTERVAL_OPTIONS = { 0, 15, 30, 60, 120, 300 };

        // Idle-perform pool: rows played occasionally on top of the base idle
        // (0) - walking/running left & right, waving, waiting. The base idle
        // track is the resting loop; a perform track plays ONE round and then
        // hands back to idle.
        private static readonly int[] IDLE_PERFORM_POOL = { 1, 2, 3, 6 };
        private const int BASE_IDLE_ROW = 0;

        // Track names in row order (mirrors the dsh-pet animation contract).
        private static readonly string[] TRACK_NAMES =
        {
            "idle", "running-right", "running-left", "waving", "jumping",
            "failed", "waiting", "running", "review",
        };

        // Track definitions mirror the dsh-pet spritesheet contract
        // (8 columns x 9 rows of 192x208 cells, 1536x1872 total).
        private static readonly Track[] TRACKS =
        {
            new Track(0, new[] {0,1,2,3,4,5}, new[] {400,400,500,400,400,500}, true, -1),              // idle
            new Track(1, new[] {0,1,2,3,4,5,6,7}, new[] {225,225,225,225,225,225,225,225}, true, -1),  // running-right
            new Track(2, new[] {0,1,2,3,4,5,6,7}, new[] {225,225,225,225,225,225,225,225}, true, -1),  // running-left
            new Track(3, new[] {0,1,2,3}, new[] {350,350,350,350}, true, -1),                          // waving
            new Track(4, new[] {0,1,2,3,4}, new[] {300,300,300,350,350}, false, 0),                    // jumping -> idle
            new Track(5, new[] {0,1,2,3,4,5,6,7}, new[] {450,450,450,500,550,600,450,450}, false, 0),  // failed -> idle
            new Track(6, new[] {0,1,2,3,4,5}, new[] {450,450,500,450,450,500}, true, -1),              // waiting
            new Track(7, new[] {0,1,2,3,4,5}, new[] {250,250,250,250,250,250}, true, -1),              // running
            new Track(8, new[] {0,1,2,3,4,5}, new[] {550,550,550,550,550,550}, true, -1),              // review
        };

        // ------------------------------------------------------------------
        // DSH Controller config (shared with Workshop\config.json). Defaults
        // resolve next to the executable so the project runs anywhere; the
        // real paths come from the controller config when present.
        // ------------------------------------------------------------------
        private string _ctlConfigPath = "";
        private string _ctlWebUrl = "http://127.0.0.1:3080";
        private int _ctlPort = 3080;
        private string _ctlStartCommand = "dsh --profile web";
        private string _ctlWorkDir = "";
        private string _ctlLogDir = "";
        private bool _ctlAutoOpen = true;

        private readonly ImageBrush _brush = new ImageBrush();
        private System.Windows.Shapes.Rectangle _view;
        private System.Windows.Shapes.Ellipse _statusDot;
        private TextBlock _statusText;
        private Border _statusBadge;
        private Window _badgeWin;
        private DispatcherTimer _badgeHideTimer;
        private string _dshPhase = "";   // last DSH activity phase pushed by the web pet
        private string _dshLabel = "";   // concrete tool name (phase=tool)
        private bool _dshRunning;
        private DispatcherTimer _timer;
        private Track _track;
        private int _frameIdx;
        private int[] _frames = new int[0];
        private int[] _durs = new int[0];
        private int[] _rowCounts = new int[9];
        private double _idleElapsedMs;
        private bool _performingIdle;
        private int _idleIntervalMs = 30000;
        private double _jumpBaseTop = double.NaN;
        private readonly Random _rng = new Random();
        private readonly string _configPath;
        private readonly string _spritePath;

        private bool _pressed;
        private bool _dragging;
        private System.Drawing.Point _downScreenPos;
        private double _downLeft;
        private double _downTop;
        private int _sizePct = 100;
        private bool _moveEnabled = true;
        private int _moveSpeed = 60;
        private bool _clickThrough;
        private bool _visible = true;
        private IntPtr _hwnd;

        // Tray (WinForms NotifyIcon; runs on this STA thread's message loop).
        private System.Windows.Forms.NotifyIcon _tray;
        private System.Windows.Forms.ContextMenuStrip _fallbackMenu;
        private System.Drawing.Icon _iconOn;
        private System.Drawing.Icon _iconOff;

        // Controller popup (WPF rounded panel, follows system theme).
        private Window _popupWin;
        private DispatcherTimer _popupFade;
        private DateTime _popupShownAt;

        // Local HTTP server (thread); drives animation from the DSH web pet.
        private TcpListener _http;
        private volatile bool _httpStop;

        public PetWindow()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _spritePath = Path.Combine(baseDir, "misaka.png");
            _configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DshPetDesktop", "config.txt");

            Title = "Dsh Pet Desktop";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            Width = CELL_W;
            Height = CELL_H;

            // Load the spritesheet.
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(_spritePath, UriKind.Absolute);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            _brush.ImageSource = bmp;
            _brush.ViewboxUnits = BrushMappingMode.Absolute;
            _brush.Viewbox = new Rect(0, 0, CELL_W, CELL_H);

            _view = new System.Windows.Shapes.Rectangle { Width = CELL_W, Height = CELL_H, Fill = _brush };
            _view.SnapsToDevicePixels = true;

            // Service-status badge: a small pill rendered in its OWN borderless
            // always-on-top window anchored to the pet's RIGHT edge (left-end
            // anchored, growing rightwards) so it never overlaps the sprite's
            // head. Shows the DSH activity phase (thinking / waiting / done /
            // failed / ...); gray "offline" when the DSH service is not
            // running. The badge window follows the pet window's position.
            _statusDot = new System.Windows.Shapes.Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = Brush("#8A8A8A"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 5, 0),
                IsHitTestVisible = false,
            };
            _statusText = new TextBlock
            {
                Text = "离线",
                FontSize = 10,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };
            var badgePanel = new StackPanel { Orientation = Orientation.Horizontal };
            badgePanel.Children.Add(_statusDot);
            badgePanel.Children.Add(_statusText);
            _statusBadge = new Border
            {
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)),
                Padding = new Thickness(7, 2, 7, 2),
                IsHitTestVisible = false,
                Child = badgePanel,
            };
            _badgeWin = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                Topmost = true,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight,
                Content = _statusBadge,
            };
            _badgeWin.SourceInitialized += (s2, e2) => MakeClickThrough(_badgeWin);

            var root = new Grid();
            root.Children.Add(_view);
            Content = root;

            // Keep the badge window glued to the pet's right edge.
            LocationChanged += (s2, e2) => SyncBadgePosition();
            SizeChanged += (s2, e2) => SyncBadgePosition();

            _rowCounts = DetectFrameCounts(_spritePath);

            LoadControllerConfig();
            LoadControllerIcons(baseDir);

            _timer = new DispatcherTimer();
            _timer.Tick += OnTick;

            BuildMenu();
            BuildTray();
            LoadConfig();
            PlayTrack(0);
            _timer.Start();
            StartHttpServer();
            StartDshPoll();
            ApplyControllerState();
        }

        // ------------------------------------------------------------------
        // Win32 helpers (click-through).
        // ------------------------------------------------------------------
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        private const uint HANDLE_FLAG_INHERIT = 0x1;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var src = HwndSource.FromVisual(this) as HwndSource;
            if (src != null) _hwnd = src.Handle;
            ApplyClickThrough();
        }

        private void ToggleClickThrough()
        {
            _clickThrough = !_clickThrough;
            ApplyClickThrough();
            SaveConfig();
        }

        private void ApplyClickThrough()
        {
            if (_hwnd == IntPtr.Zero) return;
            int style = GetWindowLong(_hwnd, GWL_EXSTYLE);
            if (_clickThrough) style |= WS_EX_TRANSPARENT;
            else style &= ~WS_EX_TRANSPARENT;
            SetWindowLong(_hwnd, GWL_EXSTYLE, style);
        }

        /// <summary>Make any window click-through (badge window).</summary>
        private static void MakeClickThrough(Window win)
        {
            try
            {
                var src = HwndSource.FromVisual(win) as HwndSource;
                if (src == null) return;
                int style = GetWindowLong(src.Handle, GWL_EXSTYLE);
                style |= WS_EX_TRANSPARENT;
                SetWindowLong(src.Handle, GWL_EXSTYLE, style);
            }
            catch { }
        }

        /// <summary>Anchor the badge window centered ABOVE the pet (grows
        /// sideways from its center), clamped to the virtual screen.</summary>
        private void SyncBadgePosition()
        {
            try
            {
                if (_badgeWin == null) return;
                if (!_visible || _badgeWin.Visibility != Visibility.Visible) return;
                _badgeWin.UpdateLayout();
                double vx = SystemParameters.VirtualScreenLeft;
                double vw = SystemParameters.VirtualScreenWidth;
                double vy = SystemParameters.VirtualScreenTop;
                double w = _badgeWin.ActualWidth;
                double h = _badgeWin.ActualHeight;
                // Centered above the pet, 4 px gap above the head.
                double left = Left + (Width - w) / 2.0;
                double top = Top - h - 4;
                // Clamp inside the virtual screen.
                if (left < vx) left = vx;
                if (left + w > vx + vw) left = vx + vw - w;
                if (top < vy) top = Top + 4; // no room above: drop below instead
                _badgeWin.Left = left;
                _badgeWin.Top = top;
            }
            catch { }
        }

        // ------------------------------------------------------------------
        // DSH Controller config (Workshop\config.json).
        // ------------------------------------------------------------------
        private void LoadControllerConfig()
        {
            try
            {
                // Resolve the controller config path: explicit location, or
                // a sibling Workshop folder, or a config.json next to the exe.
                if (string.IsNullOrEmpty(_ctlConfigPath))
                {
                    string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    string sibling = Path.Combine(
                        Path.GetDirectoryName(baseDir), "Workshop", "config.json");
                    string local = Path.Combine(baseDir, "config.json");
                    if (File.Exists(local)) _ctlConfigPath = local;
                    else if (File.Exists(sibling)) _ctlConfigPath = sibling;
                    else _ctlConfigPath = local; // will be created by SaveControllerConfig
                }
                if (File.Exists(_ctlConfigPath))
                {
                    string raw = File.ReadAllText(_ctlConfigPath);
                    _ctlWebUrl = ReadJsonString(raw, "webUrl", _ctlWebUrl);
                    _ctlStartCommand = ReadJsonString(raw, "startCommand", _ctlStartCommand);
                    _ctlWorkDir = ReadJsonString(raw, "startWorkingDir", _ctlWorkDir);
                    _ctlLogDir = ReadJsonString(raw, "logDir", _ctlLogDir);
                    _ctlAutoOpen = ReadJsonBool(raw, "autoOpenWebUi", _ctlAutoOpen);
                    int port;
                    if (ReadJsonInt(raw, "port", out port) && port > 0) _ctlPort = port;
                }
                // Fall back to per-user defaults when the config has no paths.
                if (string.IsNullOrEmpty(_ctlWorkDir) || !Directory.Exists(_ctlWorkDir))
                    _ctlWorkDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
                if (string.IsNullOrEmpty(_ctlLogDir))
                    _ctlLogDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "DshPetDesktop", "logs");
            }
            catch { }
        }

        private static string ReadJsonString(string json, string key, string fallback)
        {
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : fallback;
        }

        private static bool ReadJsonBool(string json, string key, bool fallback)
        {
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(true|false)");
            return m.Success ? m.Groups[1].Value == "true" : fallback;
        }

        private static bool ReadJsonInt(string json, string key, out int value)
        {
            value = 0;
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(\\d+)");
            if (m.Success) { value = int.Parse(m.Groups[1].Value); return true; }
            return false;
        }

        private void SaveControllerConfig()
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("{\n");
                sb.Append("  \"webUrl\": \"").Append(_ctlWebUrl).Append("\",\n");
                sb.Append("  \"logDir\": \"").Append(_ctlLogDir).Append("\",\n");
                sb.Append("  \"startCommand\": \"").Append(_ctlStartCommand).Append("\",\n");
                sb.Append("  \"pollIntervalSeconds\": 3,\n");
                sb.Append("  \"startWorkingDir\": \"").Append(_ctlWorkDir).Append("\",\n");
                sb.Append("  \"autoOpenWebUi\": ").Append(_ctlAutoOpen ? "true" : "false").Append(",\n");
                sb.Append("  \"port\": ").Append(_ctlPort).Append("\n");
                sb.Append("}");
                string dir = Path.GetDirectoryName(_ctlConfigPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_ctlConfigPath, sb.ToString(), Encoding.UTF8);
                WriteLog("controller config saved");
            }
            catch (Exception ex)
            {
                WriteLog("controller config save failed: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------
        // DSH service status (port listening -> pids), start/stop/restart.
        // ------------------------------------------------------------------
        private List<int> GetListenerPids()
        {
            var pids = new List<int>();
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("netstat", "-ano")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                };
                using (var proc = System.Diagnostics.Process.Start(psi))
                {
                    string output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit();
                    Regex re = new Regex(":" + _ctlPort + "\\s");
                    foreach (string line in output.Split('\n'))
                    {
                        if (line.IndexOf("LISTENING", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (!re.IsMatch(line)) continue;
                        string[] fields = line.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (fields.Length >= 5)
                        {
                            int pid;
                            if (int.TryParse(fields[fields.Length - 1], out pid) && pid > 0 && !pids.Contains(pid))
                                pids.Add(pid);
                        }
                    }
                }
            }
            catch { }
            return pids;
        }

        private bool StartDshService()
        {
            List<int> pids = GetListenerPids();
            if (pids.Count > 0) { WriteLog("start skipped: already running"); return false; }
            string cmd = _ctlStartCommand;
            if (string.IsNullOrWhiteSpace(cmd)) { WriteLog("start failed: startCommand is empty"); return false; }
            string workDir = _ctlWorkDir;
            if (!Directory.Exists(workDir)) workDir = Path.GetDirectoryName(_ctlConfigPath);
            string logDir = _ctlLogDir;
            try { Directory.CreateDirectory(logDir); } catch { }
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string outFile = Path.Combine(logDir, "web-stdout-" + stamp + ".log");
            string errFile = Path.Combine(logDir, "web-stderr-" + stamp + ".log");
            WriteLog("starting: " + cmd + " (workdir=" + workDir + ")");
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c " + cmd)
                {
                    WorkingDirectory = workDir,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                System.Diagnostics.Process proc = System.Diagnostics.Process.Start(psi);
                // Drain stdout/stderr in background threads into log files.
                var tOut = new Thread(delegate () { DrainStream(proc.StandardOutput, outFile); });
                var tErr = new Thread(delegate () { DrainStream(proc.StandardError, errFile); });
                tOut.IsBackground = true; tErr.IsBackground = true;
                tOut.Start(); tErr.Start();
                WriteLog("started, launcher pid=" + proc.Id);
                return true;
            }
            catch (Exception ex)
            {
                WriteLog("start failed: " + ex.Message);
                return false;
            }
        }

        private static void DrainStream(StreamReader reader, string file)
        {
            try
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    try { File.AppendAllText(file, line + Environment.NewLine, Encoding.UTF8); } catch { }
                }
            }
            catch { }
        }

        private bool StopDshService()
        {
            List<int> pids = GetListenerPids();
            if (pids.Count == 0) { WriteLog("stop skipped: not running"); return false; }
            foreach (int pid in pids)
            {
                WriteLog("stopping pid " + pid + " (tree)");
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo("taskkill", "/PID " + pid + " /T /F")
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                    using (var proc = System.Diagnostics.Process.Start(psi)) { proc.WaitForExit(); }
                }
                catch { }
            }
            Thread.Sleep(800);
            bool still = GetListenerPids().Count > 0;
            if (still) { WriteLog("stop failed: port still listening"); return false; }
            WriteLog("stopped");
            return true;
        }

        private void ApplyControllerState()
        {
            List<int> pids = GetListenerPids();
            bool running = pids.Count > 0;
            try
            {
                System.Drawing.Icon icon = running ? _iconOn : _iconOff;
                if (icon != null)
                {
                    System.Drawing.Icon old = _tray.Icon;
                    _tray.Icon = icon;
                    if (old != null && old != _iconOn && old != _iconOff) old.Dispose();
                }
            }
            catch { }
            string tip = running
                ? "DSH 运行中 (PID " + string.Join(",", pids) + ") - " + _ctlWebUrl
                : "DSH 已停止 - " + _ctlWebUrl;
            _tray.Text = tip.Substring(0, Math.Min(63, tip.Length));

            _dshRunning = running;
            UpdateStatusBadge(pids);
        }

        /// <summary>Color for one DSH activity phase (badge dot).</summary>
        private static string PhaseColor(string phase)
        {
            switch (phase)
            {
                case "thinking": return "#4C9AFF";  // blue - model working
                case "tool": return "#9A6AFF";      // purple - tool call
                case "review": return "#4CC3C3";    // teal - composing reply
                case "waiting": return "#E8B34C";   // amber - awaiting input
                case "done": return "#4CC38A";      // green - success
                case "failed": return "#E84C4C";    // red - failure
                default: return "#8A8A8A";          // idle / unknown - gray
            }
        }

        /// <summary>Short Chinese label for one DSH activity phase.</summary>
        private static string PhaseLabel(string phase)
        {
            switch (phase)
            {
                case "thinking": return "思考中";
                case "tool": return "使用工具";
                case "review": return "整理回复";
                case "waiting": return "等待输入";
                case "done": return "成功";
                case "failed": return "失败";
                default: return "待机";
            }
        }

        /// <summary>Badge text: for the tool phase show the tool name when the
        /// push carried one; otherwise the phase label.</summary>
        private string BadgeText()
        {
            if (_dshPhase == "tool" && _dshLabel.Length > 0)
            {
                // Truncate long tool names so the badge stays compact.
                const int maxLen = 14;
                string name = _dshLabel.Length > maxLen
                    ? _dshLabel.Substring(0, maxLen) + "…"
                    : _dshLabel;
                return "工具 " + name;
            }
            return PhaseLabel(_dshPhase);
        }

        /// <summary>Refresh the on-pet badge from service state + last phase.</summary>
        private void UpdateStatusBadge(List<int> pids)
        {
            try
            {
                if (_statusDot == null || _statusText == null) return;
                if (_dshRunning)
                {
                    string phase = _dshPhase;
                    _statusDot.Fill = Brush(PhaseColor(phase));
                    _statusText.Text = BadgeText();
                    string tip = phase == "tool" && _dshLabel.Length > 0
                        ? "DSH 使用工具 " + _dshLabel
                        : "DSH " + PhaseLabel(phase);
                    _statusBadge.ToolTip = tip + " (PID " + string.Join(",", pids) + ")";
                    ShowBadgeWindow();
                    // Transient outcomes (success / failure) auto-hide after a
                    // few seconds; steady phases (thinking / waiting / ...)
                    // stay visible until the next push.
                    if (phase == "done" || phase == "failed")
                    {
                        ScheduleBadgeHide(4000);
                    }
                    else
                    {
                        CancelBadgeHide();
                    }
                }
                else
                {
                    _statusDot.Fill = Brush("#8A8A8A");
                    _statusText.Text = "离线";
                    _statusBadge.ToolTip = "DSH 已停止";
                    ShowBadgeWindow();
                    CancelBadgeHide();
                }
            }
            catch { }
        }

        /// <summary>Show the badge window next to the pet (if the pet is
        /// visible) and re-anchor it to the current pet position.</summary>
        private void ShowBadgeWindow()
        {
            if (_badgeWin == null) return;
            // The auto-hide path collapsed the badge content; restore it so a
            // later state change can pop the badge up again.
            if (_statusBadge != null && _statusBadge.Visibility != Visibility.Visible)
            {
                _statusBadge.Visibility = Visibility.Visible;
                _statusBadge.UpdateLayout();
            }
            if (_visible && _badgeWin.Visibility != Visibility.Visible)
            {
                _badgeWin.Show();
                // SizeToContent must re-measure after a hidden->visible flip,
                // otherwise the window stays at its collapsed 1x1 size.
                _badgeWin.UpdateLayout();
            }
            // Re-anchor after layout settles so ActualWidth is final.
            _badgeWin.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(SyncBadgePosition));
            SyncBadgePosition();
        }

        /// <summary>Auto-hide the badge after `ms` (used for done/failed).
        /// Hides the WINDOW only — the badge content stays Visible so a later
        /// Show() pops it up at its normal size (no collapsed 1x1 residue).</summary>
        private void ScheduleBadgeHide(int ms)
        {
            if (_badgeHideTimer == null)
            {
                _badgeHideTimer = new DispatcherTimer();
                _badgeHideTimer.Tick += (s2, e2) =>
                {
                    _badgeHideTimer.Stop();
                    if (_badgeWin != null && _badgeWin.Visibility != Visibility.Hidden)
                        _badgeWin.Hide();
                };
            }
            _badgeHideTimer.Stop();
            _badgeHideTimer.Interval = TimeSpan.FromMilliseconds(ms);
            _badgeHideTimer.Start();
        }

        /// <summary>Cancel a pending auto-hide (steady phase arrived).</summary>
        private void CancelBadgeHide()
        {
            if (_badgeHideTimer != null) _badgeHideTimer.Stop();
        }

        private void CompleteStartAsync()
        {
            // Poll readiness in the background (up to 30 s), then refresh the
            // tray state on the UI thread; auto-open the WebUI when ready.
            var t = new Thread(delegate ()
            {
                bool ready = false;
                for (int i = 0; i < 15; i++)
                {
                    Thread.Sleep(2000);
                    if (GetListenerPids().Count > 0) { ready = true; break; }
                }
                Dispatcher.BeginInvoke(new Action(delegate ()
                {
                    ApplyControllerState();
                    if (ready)
                    {
                        WriteLog("start ready");
                        if (_ctlAutoOpen)
                        {
                            try { System.Diagnostics.Process.Start(_ctlWebUrl); WriteLog("auto-opened webui"); }
                            catch (Exception ex) { WriteLog("auto-open failed: " + ex.Message); }
                        }
                    }
                    else
                    {
                        WriteLog("start timeout: port not ready");
                        MessageBox.Show("启动命令已执行，但 30 秒内端口未就绪，请查看 logs/web-stderr-*.log。",
                            "DSH 控制", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }));
            });
            t.IsBackground = true;
            t.Start();
        }

        private void OnControllerStart()
        {
            WriteLog("menu action: start");
            if (!StartDshService())
            {
                MessageBox.Show("启动失败，详见 logs/tray.log。", "DSH 控制",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                ApplyControllerState();
                return;
            }
            CompleteStartAsync();
        }

        private void OnControllerStop()
        {
            WriteLog("menu action: stop");
            if (StopDshService())
            {
                ApplyControllerState();
                WriteLog("stop ok");
            }
            else
            {
                MessageBox.Show("停止失败，端口仍被占用。", "DSH 控制",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                ApplyControllerState();
            }
        }

        private void OnControllerRestart()
        {
            WriteLog("menu action: restart");
            if (!StopDshService())
            {
                MessageBox.Show("停止失败，无法重启。", "DSH 控制",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            WriteLog("restart: stopped, starting again");
            if (!StartDshService())
            {
                MessageBox.Show("重启失败：启动命令执行失败，详见 logs/tray.log。", "DSH 控制",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            CompleteStartAsync();
        }

        private void OnControllerOpen()
        {
            try { System.Diagnostics.Process.Start(_ctlWebUrl); WriteLog("menu action: open webui"); }
            catch (Exception ex) { WriteLog("open webui failed: " + ex.Message); }
        }

        // ------------------------------------------------------------------
        // Frame counting: scan each cell for non-transparent pixels.
        // ------------------------------------------------------------------
        private static int[] DetectFrameCounts(string pngPath)
        {
            var counts = new int[9];
            try
            {
                using (var src = new System.Drawing.Bitmap(pngPath))
                {
                    var rect = new System.Drawing.Rectangle(0, 0, src.Width, src.Height);
                    BitmapData data = src.LockBits(rect, ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    try
                    {
                        int stride = data.Stride;
                        var rowBuf = new byte[stride];
                        for (int r = 0; r < 9; r++)
                        {
                            int count = 0;
                            for (int c = 0; c < 8; c++)
                            {
                                bool has = false;
                                for (int y = 12; y < CELL_H - 12 && !has; y += 8)
                                {
                                    Marshal.Copy(data.Scan0 + (r * CELL_H + y) * stride, rowBuf, 0, stride);
                                    for (int x = 12; x < CELL_W - 12 && !has; x += 8)
                                    {
                                        if (rowBuf[(c * CELL_W + x) * 4 + 3] > 8) has = true;
                                    }
                                }
                                if (has) count++;
                            }
                            counts[r] = count;
                        }
                    }
                    finally
                    {
                        src.UnlockBits(data);
                    }
                }
            }
            catch
            {
                for (int i = 0; i < 9; i++) counts[i] = 8;
            }
            return counts;
        }

        // ------------------------------------------------------------------
        // Animation.
        // ------------------------------------------------------------------
        private void PlayTrack(int row)
        {
            if (row < 0 || row >= TRACKS.Length) row = 0;
            // Leaving the jumping track: land back on the base line recorded
            // when the jump started.
            if (_track != null && _track.Row == 4 && row != 4 && !double.IsNaN(_jumpBaseTop))
            {
                Top = _jumpBaseTop;
                _jumpBaseTop = double.NaN;
            }
            Track t = TRACKS[row];
            int n = Math.Max(1, Math.Min(_rowCounts[row], t.Frames.Length));
            _frames = new int[n];
            _durs = new int[n];
            for (int i = 0; i < n; i++)
            {
                _frames[i] = t.Frames[i];
                _durs[i] = t.DurMs[i];
            }
            _track = t;
            _frameIdx = 0;
            _idleElapsedMs = 0;
            // Starting a jump: remember the current baseline so the hop can
            // return the pet to exactly this height (re-jumps keep the first
            // baseline rather than stacking).
            if (row == 4 && double.IsNaN(_jumpBaseTop)) _jumpBaseTop = Top;
            ShowFrame();
            _timer.Interval = TimeSpan.FromMilliseconds(_durs[0]);
        }

        private void ShowFrame()
        {
            _brush.Viewbox = new Rect(_frames[_frameIdx] * CELL_W, _track.Row * CELL_H, CELL_W, CELL_H);
        }

        private void OnTick(object sender, EventArgs e)
        {
            // Dragging (or a pressed pointer): freeze the animation - no frame
            // advance, no auto-movement, no idle performs - until the pointer
            // is released, then resume from the remaining frames.
            if (_pressed) return;

            _frameIdx++;
            bool wrapped = false;
            if (_frameIdx >= _frames.Length)
            {
                if (_track.Loop) { _frameIdx = 0; wrapped = true; }
                else { PlayTrack(_track.Fallback); return; }
            }
            ShowFrame();
            int d = _durs[_frameIdx];
            _timer.Interval = TimeSpan.FromMilliseconds(d);

            // Walk/run: move the window horizontally while a walk track is
            // playing and movement is enabled (not while being dragged).
            if (_moveEnabled && (_track.Row == 1 || _track.Row == 2))
            {
                double dt = d / 1000.0;
                double dx = (_track.Row == 2 ? -1.0 : 1.0) * _moveSpeed * dt;
                Left += dx;
                double vx = SystemParameters.VirtualScreenLeft;
                double vw = SystemParameters.VirtualScreenWidth;
                if (Left <= vx && _track.Row == 2) { PlayTrack(1); return; }          // hit left wall, run right
                if (Left + Width >= vx + vw && _track.Row == 1) { PlayTrack(2); return; } // hit right wall, run left
            }

            // Jumping: vertical hop following the track's frame curve (the
            // pet rises on the first frames and lands back at the base line).
            if (_track.Row == 4 && !double.IsNaN(_jumpBaseTop))
            {
                double[] hops = { 0.0, -0.55, -1.0, -0.45, 0.0 };
                double h = 60.0 * _sizePct / 100.0;
                int fi = _frameIdx < hops.Length ? _frameIdx : hops.Length - 1;
                Top = _jumpBaseTop + hops[fi] * h;
            }

            // Idle performs: the base idle track loops; once the configured
            // interval elapses, play ONE random round of an idle-perform track
            // and hand back to idle when that round wraps.
            if (_performingIdle && wrapped)
            {
                _performingIdle = false;
                _idleElapsedMs = 0;
                PlayTrack(BASE_IDLE_ROW);
                return;
            }
            if (!_performingIdle && _track.Row == BASE_IDLE_ROW && _idleIntervalMs > 0)
            {
                _idleElapsedMs += d;
                if (_idleElapsedMs >= _idleIntervalMs)
                {
                    _idleElapsedMs = 0;
                    _performingIdle = true;
                    PlayTrack(IDLE_PERFORM_POOL[_rng.Next(IDLE_PERFORM_POOL.Length)]);
                    return;
                }
            }
        }

        private static int RowOfTrackName(string name)
        {
            for (int i = 0; i < TRACK_NAMES.Length; i++)
                if (TRACK_NAMES[i] == name) return i;
            return -1;
        }

        private static string TrackNameOfRow(int row)
        {
            if (row < 0 || row >= TRACK_NAMES.Length) return "?";
            return TRACK_NAMES[row];
        }

        // ------------------------------------------------------------------
        // Mouse: drag to move, click for a random reaction.
        // ------------------------------------------------------------------
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            _pressed = true;
            _dragging = false;
            // Capture the pointer in SCREEN coordinates (physical px): the
            // drag delta must not be derived from GetPosition(this), whose
            // origin moves with the window itself — that feedback makes the
            // pet lag behind a fast mouse (each update eats part of the
            // delta because the reference frame already shifted).
            _downScreenPos = System.Windows.Forms.Control.MousePosition;
            _downLeft = Left;
            _downTop = Top;
            // Stop the animation timeline entirely while pressing: no ticks
            // run, so no frame advance / walk movement / jump offset can
            // fight the pointer — the pet follows the mouse exactly and
            // resumes the remaining frames when released.
            _timer.Stop();
            CaptureMouse();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_pressed) return;
            // Screen-space delta (physical px), scaled back to DIP for the
            // WPF window position. Absolute coordinates keep the drag immune
            // to the window's own motion.
            System.Drawing.Point s = System.Windows.Forms.Control.MousePosition;
            double scale = DpiScale();
            double dx = (s.X - _downScreenPos.X) / scale;
            double dy = (s.Y - _downScreenPos.Y) / scale;
            if (!_dragging && Math.Abs(dx) + Math.Abs(dy) > 6.0) _dragging = true;
            if (_dragging)
            {
                Left = _downLeft + dx;
                Top = _downTop + dy;
            }
        }

        /// <summary>Physical px per DIP for this window (DPI scale).</summary>
        private double DpiScale()
        {
            try
            {
                var source = PresentationSource.FromVisual(this);
                if (source != null && source.CompositionTarget != null)
                    return source.CompositionTarget.TransformToDevice.M11;
            }
            catch { }
            return 1.0;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            _pressed = false;
            ReleaseMouseCapture();
            // Resume the animation timeline from the paused frame.
            _timer.Start();
            if (!_dragging)
            {
                // Random reaction: jumping / waving / running.
                int[] acts = { 4, 3, 7 };
                PlayTrack(acts[_rng.Next(acts.Length)]);
            }
            else
            {
                SaveConfig();
            }
        }

        // ------------------------------------------------------------------
        // Right-click menu (window) - same controller panel as the tray.
        // ------------------------------------------------------------------
        private void BuildMenu()
        {
            // The window's right-click opens a pet-only panel (pet settings +
            // play-animation list); the tray opens the full controller panel.
            ContextMenu = null;
            MouseRightButtonUp += (s, e) => ShowControllerPanel(null, true);
        }

        private void CycleSize()
        {
            int idx = Array.IndexOf(SIZE_PCTS, _sizePct);
            idx = (idx + 1) % SIZE_PCTS.Length;
            _sizePct = SIZE_PCTS[idx];
            ApplySize();
            SaveConfig();
        }

        private void SetSizePct(int pct)
        {
            _sizePct = pct;
            ApplySize();
            SaveConfig();
        }

        private void ApplySize()
        {
            double scale = _sizePct / 100.0;
            double w = CELL_W * scale;
            double h = CELL_H * scale;
            _view.LayoutTransform = new ScaleTransform(scale, scale);
            Width = w;
            Height = h;
        }

        private void ToggleMove()
        {
            _moveEnabled = !_moveEnabled;
            SaveConfig();
        }

        private void SetMoveSpeed(int speed)
        {
            _moveSpeed = speed;
            SaveConfig();
        }

        private void SetIdleIntervalSec(int seconds)
        {
            _idleIntervalMs = seconds <= 0 ? 0 : seconds * 1000;
            SaveConfig();
        }

        private string IdleIntervalLabel()
        {
            return _idleIntervalMs <= 0 ? "关闭" : (_idleIntervalMs / 1000) + " 秒";
        }

        private void SetVisible(bool visible)
        {
            _visible = visible;
            if (visible) Show();
            else Hide();
            // Hide / restore the badge window along with the pet.
            if (_badgeWin != null)
            {
                if (visible)
                {
                    if (_badgeWin.Visibility == Visibility.Hidden) ShowBadgeWindow();
                }
                else
                {
                    if (_badgeWin.Visibility != Visibility.Hidden) _badgeWin.Hide();
                }
            }
        }

        // ------------------------------------------------------------------
        // Tray icon (DSH controller icon, state-driven).
        // ------------------------------------------------------------------
        private void LoadControllerIcons(string baseDir)
        {
            try
            {
                string resDir = Path.Combine(baseDir, "resources");
                string onPath = Path.Combine(resDir, "dscfgon.ico");
                string offPath = Path.Combine(resDir, "dscfgoff.ico");
                if (File.Exists(onPath)) _iconOn = new System.Drawing.Icon(onPath);
                if (File.Exists(offPath)) _iconOff = new System.Drawing.Icon(offPath);
            }
            catch { }
        }

        private void BuildTray()
        {
            _tray = new System.Windows.Forms.NotifyIcon();
            if (_iconOn != null) _tray.Icon = _iconOn;
            else _tray.Icon = System.Drawing.SystemIcons.Application;
            _tray.Text = "DSH Controller + Pet";

            // Left/right single click: open the controller panel. Left
            // double-click: open the WebUI directly.
            long lastLeftUpTicks = 0;
            _tray.MouseUp += (s, e) =>
            {
                if (e.Button == System.Windows.Forms.MouseButtons.Right)
                {
                    ShowControllerPanel(null);
                }
                else if (e.Button == System.Windows.Forms.MouseButtons.Left)
                {
                    long now = DateTime.Now.Ticks;
                    long dblMs = System.Windows.Forms.SystemInformation.DoubleClickTime;
                    if (now - lastLeftUpTicks < dblMs * TimeSpan.TicksPerMillisecond)
                    {
                        // Double-click: open the WebUI, swallow the second
                        // click's single-click action.
                        lastLeftUpTicks = 0;
                        OnControllerOpen();
                    }
                    else
                    {
                        lastLeftUpTicks = now;
                        // Defer the single-click action briefly; if a second
                        // click arrives within the double-click window, the
                        // deferred action is cancelled and WebUI opens instead.
                        _tray.Tag = 1; // marker: single-click pending
                        System.Threading.Timer timer = null;
                        timer = new System.Threading.Timer(delegate
                        {
                            try
                            {
                                if (_tray != null && object.Equals(_tray.Tag, 1))
                                {
                                    _tray.Tag = 0;
                                    System.Windows.Application.Current.Dispatcher.BeginInvoke(
                                        new Action(delegate { ShowControllerPanel(null); }));
                                }
                            }
                            catch { }
                            if (timer != null) timer.Dispose();
                        }, null, dblMs + 30, System.Threading.Timeout.Infinite);
                    }
                }
            };

            _tray.Visible = true;
        }

        // ------------------------------------------------------------------
        // Controller WPF rounded panel (theme follows the system).
        // ------------------------------------------------------------------
        private static string SystemTheme()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (k != null)
                    {
                        object v = k.GetValue("AppsUseLightTheme", 1);
                        if (v != null && Convert.ToInt32(v) == 0) return "dark";
                    }
                }
            }
            catch { }
            return "light";
        }

        private static Dictionary<string, string> ThemeColors(string theme)
        {
            if (theme == "dark")
            {
                return new Dictionary<string, string>
                {
                    { "bg", "#202020" }, { "border", "#333333" }, { "text", "#F2F2F2" },
                    { "sub", "#9A9A9A" }, { "hover", "#373737" }, { "sep", "#2E2E2E" },
                    { "dotRunning", "#4CC38A" }, { "dotStopped", "#8A8A8A" },
                };
            }
            return new Dictionary<string, string>
            {
                { "bg", "#FFFFFF" }, { "border", "#E3E3E3" }, { "text", "#1A1A1A" },
                { "sub", "#6E6E6E" }, { "hover", "#F0F0F0" }, { "sep", "#ECECEC" },
                { "dotRunning", "#0F7B0F" }, { "dotStopped", "#8A8A8A" },
            };
        }

        private static SolidColorBrush Brush(string hex)
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }

        private Border NewMenuRow(string glyph, string text, string subText, bool enabled,
            Action onClick, Dictionary<string, string> colors)
        {
            var row = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(4, 1, 4, 1),
            };
            if (!enabled) { row.Opacity = 0.45; }
            else { row.Cursor = Cursors.Hand; }

            var panel = new StackPanel { Orientation = Orientation.Horizontal };

            if (!string.IsNullOrEmpty(glyph))
            {
                var g = new TextBlock
                {
                    Text = glyph,
                    FontFamily = new FontFamily("Segoe Fluent Icons"),
                    FontSize = 14,
                    Foreground = Brush(colors["sub"]),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 10, 0),
                };
                panel.Children.Add(g);
            }

            var txtCol = new StackPanel { Orientation = Orientation.Vertical };
            var t = new TextBlock
            {
                Text = text,
                FontSize = 13,
                Foreground = Brush(colors["text"]),
            };
            txtCol.Children.Add(t);
            if (!string.IsNullOrEmpty(subText))
            {
                var st = new TextBlock
                {
                    Text = subText,
                    FontSize = 11,
                    Foreground = Brush(colors["sub"]),
                };
                txtCol.Children.Add(st);
            }
            panel.Children.Add(txtCol);
            row.Child = panel;

            if (enabled && onClick != null)
            {
                string hover = colors["hover"];
                row.MouseEnter += (s2, e2) => row.Background = Brush(hover);
                row.MouseLeave += (s2, e2) => row.Background = Brushes.Transparent;
                row.MouseLeftButtonUp += (s2, e2) =>
                {
                    Action act = onClick;
                    ClosePopup();
                    Dispatcher.BeginInvoke(new Action(delegate ()
                    {
                        try { act(); }
                        catch (Exception ex) { WriteLog("menu action error: " + ex.Message); }
                    }));
                };
            }
            return row;
        }

        private void ShowControllerPanel(string sub, bool petOnly = false)
        {
            try
            {
                if (_popupWin != null) ClosePopup();
                string theme = SystemTheme();
                Dictionary<string, string> colors = ThemeColors(theme);
                List<int> pids = GetListenerPids();
                bool running = pids.Count > 0;
                if (!petOnly) ApplyControllerState();

                var win = new Window
                {
                    WindowStyle = WindowStyle.None,
                    AllowsTransparency = true,
                    Background = Brushes.Transparent,
                    ShowInTaskbar = false,
                    Topmost = true,
                    ResizeMode = ResizeMode.NoResize,
                    Opacity = 0,
                };

                var shell = new Border
                {
                    CornerRadius = new CornerRadius(10),
                    Background = Brush(colors["bg"]),
                    BorderBrush = Brush(colors["border"]),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(6),
                    Margin = new Thickness(12),
                    Effect = new DropShadowEffect
                    {
                        BlurRadius = 16,
                        ShadowDepth = 0,
                        Opacity = 0.35,
                        Color = Colors.Black,
                    },
                };

                var menu = new StackPanel { Width = 250 };

                if (sub == "size")
                {
                    AddRow(menu, colors, "\uE72B", "返回", null, true, () => ShowControllerPanel(null, petOnly));
                    AddSeparator(menu, colors);
                    for (int i = 0; i < SIZE_PCTS.Length; i++)
                    {
                        int pct = SIZE_PCTS[i];
                        string cur = pct == _sizePct ? "  当前" : null;
                        AddRow(menu, colors, null, pct + "%", cur, true, () => { SetSizePct(pct); });
                    }
                }
                else if (sub == "speed")
                {
                    AddRow(menu, colors, "\uE72B", "返回", null, true, () => ShowControllerPanel(null, petOnly));
                    AddSeparator(menu, colors);
                    for (int i = 0; i < SPEED_OPTIONS.Length; i++)
                    {
                        int spd = SPEED_OPTIONS[i];
                        string cur = spd == _moveSpeed ? "  当前" : null;
                        AddRow(menu, colors, null, spd + " px/s", cur, true, () => { SetMoveSpeed(spd); });
                    }
                }
                else if (sub == "play")
                {
                    AddRow(menu, colors, "\uE72B", "返回", null, true, () => ShowControllerPanel(null, petOnly));
                    AddSeparator(menu, colors);
                    for (int i = 0; i < TRACKS.Length; i++)
                    {
                        int row = i;
                        string cur = row == _track.Row ? "  播放中" : null;
                        AddRow(menu, colors, null, TRACK_NAMES[i], cur, true, () => { PlayTrack(row); });
                    }
                }
                else if (sub == "idle")
                {
                    AddRow(menu, colors, "\uE72B", "返回", null, true, () => ShowControllerPanel(null, petOnly));
                    AddSeparator(menu, colors);
                    for (int i = 0; i < IDLE_INTERVAL_OPTIONS.Length; i++)
                    {
                        int sec = IDLE_INTERVAL_OPTIONS[i];
                        string label = sec <= 0 ? "关闭" : sec + " 秒";
                        string cur = (_idleIntervalMs == sec * 1000) ? "  当前" : null;
                        AddRow(menu, colors, null, label, cur, true, () => { SetIdleIntervalSec(sec); });
                    }
                }
                else if (petOnly)
                {
                    // --- pet-only panel (right-click on the pet) ---
                    AddRow(menu, colors, "\uE768", "播放动画...", "9 种动画", true, () => ShowControllerPanel("play", true));
                    AddSeparator(menu, colors);

                    AddRow(menu, colors, null, "闲时动画 " + IdleIntervalLabel(), "触发间隔", true, () => ShowControllerPanel("idle", true));
                    AddRow(menu, colors, null, "尺寸 " + _sizePct + "%", "点击选择", true, () => ShowControllerPanel("size", true));
                    AddRow(menu, colors, null, "移动 " + (_moveEnabled ? "开" : "关"), "左右走路/跑步时移动", true, ToggleMove);
                    AddRow(menu, colors, null, "移速 " + _moveSpeed + " px/s", "点击选择", true, () => ShowControllerPanel("speed", true));
                    AddRow(menu, colors, null, "鼠标穿透 " + (_clickThrough ? "开" : "关"), null, true, ToggleClickThrough);
                    AddRow(menu, colors, null, "置顶 " + (Topmost ? "开" : "关"), null, true, () => { Topmost = !Topmost; SaveConfig(); });
                    AddRow(menu, colors, null, "开机启动 " + (GetAutoStart() ? "开" : "关"), null, true, () => { SetAutoStart(!GetAutoStart()); });
                    AddRow(menu, colors, null, _visible ? "隐藏宠物" : "显示宠物", null, true, () => { SetVisible(!_visible); SaveConfig(); });
                }
                else
                {
                    // --- status row ---
                    var statusRow = new Border { Padding = new Thickness(10, 9, 10, 8) };
                    var sp = new StackPanel { Orientation = Orientation.Horizontal };
                    var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8 };
                    dot.Fill = Brush(running ? colors["dotRunning"] : colors["dotStopped"]);
                    dot.VerticalAlignment = VerticalAlignment.Center;
                    dot.Margin = new Thickness(0, 0, 8, 0);
                    sp.Children.Add(dot);
                    var tcol = new StackPanel();
                    var t1 = new TextBlock
                    {
                        Text = running ? "DSH 运行中 (PID " + string.Join(",", pids) + ")" : "DSH 已停止",
                        FontSize = 13,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = Brush(colors["text"]),
                    };
                    tcol.Children.Add(t1);
                    var t2 = new TextBlock
                    {
                        Text = "端口 " + _ctlPort + " · " + _ctlWebUrl,
                        FontSize = 11,
                        Foreground = Brush(colors["sub"]),
                    };
                    tcol.Children.Add(t2);
                    sp.Children.Add(tcol);
                    statusRow.Child = sp;
                    menu.Children.Add(statusRow);
                    AddSeparator(menu, colors);

                    // --- DSH service actions ---
                    if (!running)
                    {
                        AddRow(menu, colors, "\uE768", "启动 DSH", null, true, OnControllerStart);
                    }
                    else
                    {
                        AddRow(menu, colors, "\uE71A", "停止 DSH", null, true, OnControllerStop);
                        AddRow(menu, colors, "\uE72C", "重启 DSH", null, true, OnControllerRestart);
                        AddRow(menu, colors, "\uE774", "打开 WebUI", null, true, OnControllerOpen);
                    }
                    AddRow(menu, colors, "\uE713", "配置...", null, true, ShowConfigDialog);
                    AddSeparator(menu, colors);

                    // --- pet settings (only while the pet is visible) ---
                    if (_visible)
                    {
                        AddSeparator(menu, colors);
                        AddRow(menu, colors, null, "闲时动画 " + IdleIntervalLabel(), "触发间隔", true, () => ShowControllerPanel("idle"));
                        AddRow(menu, colors, null, "尺寸 " + _sizePct + "%", "点击选择", true, () => ShowControllerPanel("size"));
                        AddRow(menu, colors, null, "移动 " + (_moveEnabled ? "开" : "关"), "左右走路/跑步时移动", true, ToggleMove);
                        AddRow(menu, colors, null, "移速 " + _moveSpeed + " px/s", "点击选择", true, () => ShowControllerPanel("speed"));
                        AddRow(menu, colors, null, "鼠标穿透 " + (_clickThrough ? "开" : "关"), null, true, ToggleClickThrough);
                        AddRow(menu, colors, null, "置顶 " + (Topmost ? "开" : "关"), null, true, () => { Topmost = !Topmost; SaveConfig(); });
                        AddRow(menu, colors, null, "开机启动 " + (GetAutoStart() ? "开" : "关"), null, true, () => { SetAutoStart(!GetAutoStart()); });
                        AddRow(menu, colors, null, "隐藏宠物", null, true, () => { SetVisible(false); SaveConfig(); });
                    }
                    else
                    {
                        AddSeparator(menu, colors);
                        AddRow(menu, colors, null, "显示宠物", null, true, () => { SetVisible(true); SaveConfig(); });
                    }
                    AddSeparator(menu, colors);

                    AddRow(menu, colors, "\uE7E8", "退出", null, true, Quit);

                    var foot = new TextBlock
                    {
                        Text = "状态查询于 " + DateTime.Now.ToString("HH:mm:ss"),
                        FontSize = 10,
                        Margin = new Thickness(10, 6, 10, 6),
                        Foreground = Brush(colors["sub"]),
                    };
                    menu.Children.Add(foot);
                }

                shell.Child = menu;
                win.Content = shell;

                win.SizeToContent = SizeToContent.WidthAndHeight;
                PositionPanel(win, shell);

                _popupWin = win;
                _popupShownAt = DateTime.Now;
                win.Deactivated += (s2, e2) =>
                {
                    if (_popupWin != null && (DateTime.Now - _popupShownAt).TotalMilliseconds > 300)
                        ClosePopup();
                };
                win.KeyDown += (s2, e2) =>
                {
                    if (e2.Key == Key.Escape && _popupWin != null) ClosePopup();
                };
                win.Closed += (s2, e2) =>
                {
                    if (_popupFade != null) _popupFade.Stop();
                    _popupWin = null;
                };

                win.Show();
                win.Activate();
                win.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(delegate ()
                {
                    try { if (_popupWin != null) _popupWin.Activate(); } catch { }
                }));
                win.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate ()
                {
                    try { PositionPanel(win, shell); } catch { }
                }));

                // Manual fade-in via DispatcherTimer.
                _popupFade = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
                _popupFade.Tick += (s2, e2) =>
                {
                    if (_popupWin != null)
                    {
                        _popupWin.Opacity = Math.Min(1.0, _popupWin.Opacity + 0.12);
                        if (_popupWin.Opacity >= 1.0) _popupFade.Stop();
                    }
                    else _popupFade.Stop();
                };
                _popupFade.Start();

                win.UpdateLayout();
            }
            catch (Exception ex)
            {
                WriteLog("wpf popup failed: " + ex.Message + "; falling back");
                try { if (_popupWin != null) { _popupWin.Close(); _popupWin = null; } } catch { }
                ShowFallbackMenu(petOnly);
            }
        }

        private static void PositionPanel(Window win, Border shell)
        {
            try
            {
                var pt = System.Windows.Forms.Control.MousePosition;
                var wa = System.Windows.Forms.Screen.FromPoint(pt).WorkingArea;
                double diuW = SystemParameters.PrimaryScreenWidth;
                double scale = diuW > 0 ? wa.Width / diuW : 1.0;
                if (scale <= 0 || scale > 4) scale = 1.0;
                double ptX = pt.X / scale;
                double ptY = pt.Y / scale;
                double waL = wa.Left / scale;
                double waT = wa.Top / scale;
                double waR = wa.Right / scale;
                double waB = wa.Bottom / scale;
                shell.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double estW = Math.Ceiling(shell.DesiredSize.Width + 24);
                double estH = Math.Ceiling(shell.DesiredSize.Height + 24);
                win.Left = Math.Max(waL, Math.Min(ptX - estW + 8, waR - estW));
                win.Top = Math.Max(waT, Math.Min(ptY - estH - 4, waB - estH));
            }
            catch { }
        }

        private void AddRow(StackPanel menu, Dictionary<string, string> colors,
            string glyph, string text, string sub, bool enabled, Action onClick)
        {
            menu.Children.Add(NewMenuRow(glyph, text, sub, enabled, onClick, colors));
        }

        private static void AddSeparator(StackPanel menu, Dictionary<string, string> colors)
        {
            menu.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Height = 1,
                Fill = Brush(colors["sep"]),
                Margin = new Thickness(8, 0, 8, 0),
            });
        }

        private void ClosePopup()
        {
            if (_popupWin != null)
            {
                Window w = _popupWin;
                _popupWin = null;
                try { if (w.IsVisible) w.Close(); } catch { }
            }
        }

        private void ShowFallbackMenu(bool petOnly = false)
        {
            // Classic WinForms menu when the WPF panel cannot be shown.
            try
            {
                List<int> pids = GetListenerPids();
                bool running = pids.Count > 0;
                var menu = new System.Windows.Forms.ContextMenuStrip();
                if (!petOnly)
                {
                    var s = new System.Windows.Forms.ToolStripMenuItem
                    {
                        Text = running ? "DSH 运行中 (PID " + string.Join(",", pids) + ")" : "DSH 已停止",
                        Enabled = false,
                    };
                    menu.Items.Add(s);
                    menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                    if (!running)
                    {
                        var i = new System.Windows.Forms.ToolStripMenuItem("启动 DSH");
                        i.Click += (s2, e2) => OnControllerStart();
                        menu.Items.Add(i);
                    }
                    else
                    {
                        var i1 = new System.Windows.Forms.ToolStripMenuItem("停止 DSH");
                        i1.Click += (s2, e2) => OnControllerStop();
                        menu.Items.Add(i1);
                        var i2 = new System.Windows.Forms.ToolStripMenuItem("重启 DSH");
                        i2.Click += (s2, e2) => OnControllerRestart();
                        menu.Items.Add(i2);
                        var i3 = new System.Windows.Forms.ToolStripMenuItem("打开 WebUI");
                        i3.Click += (s2, e2) => OnControllerOpen();
                        menu.Items.Add(i3);
                    }
                    var i4 = new System.Windows.Forms.ToolStripMenuItem("配置...");
                    i4.Click += (s2, e2) => ShowConfigDialog();
                    menu.Items.Add(i4);
                    menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                }
                else
                {
                    var iPlay = new System.Windows.Forms.ToolStripMenuItem("播放动画...");
                    for (int i = 0; i < TRACKS.Length; i++)
                    {
                        int row = i;
                        var ti = new System.Windows.Forms.ToolStripMenuItem(TRACK_NAMES[i]);
                        ti.Click += (s2, e2) => PlayTrack(row);
                        iPlay.DropDownItems.Add(ti);
                    }
                    menu.Items.Add(iPlay);
                    menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                }
                if (!petOnly && !_visible)
                {
                    // Pet hidden: only offer bringing it back.
                    var iShow = new System.Windows.Forms.ToolStripMenuItem("显示宠物");
                    iShow.Click += (s2, e2) => { SetVisible(true); SaveConfig(); };
                    menu.Items.Add(iShow);
                }
                else
                {
                    var i5 = new System.Windows.Forms.ToolStripMenuItem("尺寸 " + _sizePct + "%");
                    i5.Click += (s2, e2) => CycleSize();
                    menu.Items.Add(i5);
                    var i5b = new System.Windows.Forms.ToolStripMenuItem("闲时动画 " + IdleIntervalLabel());
                    i5b.Click += (s2, e2) =>
                    {
                        int idx = Array.IndexOf(IDLE_INTERVAL_OPTIONS, _idleIntervalMs / 1000);
                        idx = (idx + 1) % IDLE_INTERVAL_OPTIONS.Length;
                        SetIdleIntervalSec(IDLE_INTERVAL_OPTIONS[idx]);
                    };
                    menu.Items.Add(i5b);
                    var i6 = new System.Windows.Forms.ToolStripMenuItem("移动 " + (_moveEnabled ? "开" : "关"));
                    i6.Click += (s2, e2) => ToggleMove();
                    menu.Items.Add(i6);
                    var i7 = new System.Windows.Forms.ToolStripMenuItem("移速 " + _moveSpeed + " px/s");
                    i7.Click += (s2, e2) =>
                    {
                        int idx = Array.IndexOf(SPEED_OPTIONS, _moveSpeed);
                        idx = (idx + 1) % SPEED_OPTIONS.Length;
                        SetMoveSpeed(SPEED_OPTIONS[idx]);
                    };
                    menu.Items.Add(i7);
                    var i8 = new System.Windows.Forms.ToolStripMenuItem("鼠标穿透 " + (_clickThrough ? "开" : "关"));
                    i8.Click += (s2, e2) => ToggleClickThrough();
                    menu.Items.Add(i8);
                    var i9 = new System.Windows.Forms.ToolStripMenuItem("置顶 " + (Topmost ? "开" : "关"));
                    i9.Click += (s2, e2) => { Topmost = !Topmost; SaveConfig(); };
                    menu.Items.Add(i9);
                    var i10 = new System.Windows.Forms.ToolStripMenuItem("开机启动 " + (GetAutoStart() ? "开" : "关"));
                    i10.Click += (s2, e2) => SetAutoStart(!GetAutoStart());
                    menu.Items.Add(i10);
                    var i11 = new System.Windows.Forms.ToolStripMenuItem(_visible ? "隐藏宠物" : "显示宠物");
                    i11.Click += (s2, e2) => { SetVisible(!_visible); SaveConfig(); };
                    menu.Items.Add(i11);
                }
                if (!petOnly)
                {
                    menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                    var i12 = new System.Windows.Forms.ToolStripMenuItem("退出");
                    i12.Click += (s2, e2) => Quit();
                    menu.Items.Add(i12);
                }

                _tray.ContextMenuStrip = menu;
                _fallbackMenu = menu;
                menu.Show(System.Windows.Forms.Control.MousePosition);
                WriteLog("fallback menu shown (wpf popup unavailable)");
            }
            catch (Exception ex)
            {
                WriteLog("fallback menu failed: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------
        // Config dialog (DSH controller settings, WPF, theme-following).
        // ------------------------------------------------------------------
        private Window _configDlg;
        private TextBox _cfgCmd;
        private TextBox _cfgUrl;
        private TextBox _cfgDir;
        private System.Windows.Controls.CheckBox _cfgAuto;

        private void ShowConfigDialog()
        {
            try
            {
                string theme = SystemTheme();
                Dictionary<string, string> colors = ThemeColors(theme);
                var dlg = new Window
                {
                    Title = "DSH 托盘配置",
                    WindowStyle = WindowStyle.SingleBorderWindow,
                    ResizeMode = ResizeMode.NoResize,
                    SizeToContent = SizeToContent.WidthAndHeight,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    Topmost = true,
                    Background = Brush(colors["bg"]),
                    Foreground = Brush(colors["text"]),
                };

                var grid = new Grid { Margin = new Thickness(16) };
                for (int r = 0; r < 5; r++)
                    grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });

                Func<string, string, int, TextBox> NewField = (label, value, row) =>
                {
                    var lbl = new TextBlock
                    {
                        Text = label,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = Brush(colors["text"]),
                    };
                    Grid.SetRow(lbl, row); Grid.SetColumn(lbl, 0);
                    grid.Children.Add(lbl);
                    var box = new TextBox
                    {
                        Text = value,
                        VerticalContentAlignment = VerticalAlignment.Center,
                        Padding = new Thickness(4, 2, 4, 2),
                        Background = Brush(theme == "dark" ? "#2B2B2B" : "#FFFFFF"),
                        Foreground = Brush(colors["text"]),
                        BorderBrush = Brush(colors["border"]),
                    };
                    Grid.SetRow(box, row); Grid.SetColumn(box, 1);
                    grid.Children.Add(box);
                    return box;
                };

                _cfgCmd = NewField("启动命令：", _ctlStartCommand, 0);
                _cfgUrl = NewField("WebUI 地址：", _ctlWebUrl, 1);
                _cfgDir = NewField("工作目录：", _ctlWorkDir, 2);

                _cfgAuto = new System.Windows.Controls.CheckBox
                {
                    Content = "启动后自动打开 WebUI",
                    IsChecked = _ctlAutoOpen,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Brush(colors["text"]),
                };
                Grid.SetRow(_cfgAuto, 3); Grid.SetColumnSpan(_cfgAuto, 2);
                grid.Children.Add(_cfgAuto);

                var btnPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                };
                var btnOk = new Button { Content = "保存", Width = 80, Margin = new Thickness(0, 0, 10, 0) };
                var btnCancel = new Button { Content = "取消", Width = 80 };
                btnPanel.Children.Add(btnOk);
                btnPanel.Children.Add(btnCancel);
                Grid.SetRow(btnPanel, 4); Grid.SetColumnSpan(btnPanel, 2);
                grid.Children.Add(btnPanel);

                dlg.Content = grid;
                _configDlg = dlg;
                dlg.KeyDown += (s2, e2) =>
                {
                    if (e2.Key == Key.Escape && _configDlg != null) _configDlg.Close();
                };
                btnOk.Click += (s2, e2) =>
                {
                    _ctlStartCommand = _cfgCmd.Text.Trim();
                    _ctlWebUrl = _cfgUrl.Text.Trim();
                    _ctlWorkDir = _cfgDir.Text.Trim();
                    _ctlAutoOpen = _cfgAuto.IsChecked == true;
                    SaveControllerConfig();
                    if (_configDlg != null) _configDlg.Close();
                };
                btnCancel.Click += (s2, e2) => { if (_configDlg != null) _configDlg.Close(); };
                dlg.Closed += (s2, e2) => { _configDlg = null; };

                dlg.Show();
            }
            catch (Exception ex)
            {
                WriteLog("config dialog failed: " + ex.Message);
            }
        }

        private void Quit()
        {
            _httpStop = true;
            try { if (_http != null) _http.Stop(); } catch { }
            if (_badgeWin != null)
            {
                try { if (_badgeWin.IsVisible) _badgeWin.Close(); } catch { }
                _badgeWin = null;
            }
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }
            if (_iconOn != null) { try { _iconOn.Dispose(); } catch { } _iconOn = null; }
            if (_iconOff != null) { try { _iconOff.Dispose(); } catch { } _iconOff = null; }
            Close();
        }

        // ------------------------------------------------------------------
        // Local HTTP server (127.0.0.1:18787) - drives animation from DSH.
        // ------------------------------------------------------------------
        private void StartHttpServer()
        {
            try
            {
                _http = new TcpListener(IPAddress.Loopback, HTTP_PORT);
                // Never let child processes inherit this socket handle: the DSH
                // service is launched with a redirected stdio child, which
                // would otherwise inherit the listener and keep port 18787
                // bound forever after this process exits (orphan LISTENING).
                try
                {
                    SetHandleInformation(_http.Server.Handle, HANDLE_FLAG_INHERIT, 0);
                }
                catch { }
                _http.Start();
                var thread = new Thread(HttpLoop);
                thread.IsBackground = true;
                thread.Name = "pet-http";
                thread.Start();
            }
            catch (Exception ex)
            {
                WriteLog("http server failed: " + ex.Message);
            }
        }

        private void HttpLoop()
        {
            while (!_httpStop)
            {
                TcpClient client;
                try { client = _http.AcceptTcpClient(); }
                catch { break; }
                ThreadPool.QueueUserWorkItem(delegate { HandleHttpClient(client); });
            }
        }

        private void HandleHttpClient(TcpClient client)
        {
            try
            {
                using (client)
                using (NetworkStream stream = client.GetStream())
                {
                    // Read the request head (up to the blank line).
                    var head = new StringBuilder();
                    int emptyLines = 0;
                    while (emptyLines < 2)
                    {
                        int b = stream.ReadByte();
                        if (b == -1) break;
                        if (b == '\n')
                        {
                            emptyLines++;
                            head.Append('\n');
                        }
                        else if (b != '\r')
                        {
                            emptyLines = 0;
                            head.Append((char)b);
                        }
                    }
                    string raw = head.ToString();
                    string first = raw;
                    int nl = raw.IndexOf('\n');
                    if (nl >= 0) first = raw.Substring(0, nl);
                    first = first.Trim();

                    string method = "GET";
                    string path = "/";
                    string[] parts = first.Split(' ');
                    if (parts.Length >= 2)
                    {
                        method = parts[0];
                        path = parts[1];
                    }

                    string pathOnly = path;
                    string query = "";
                    int q = path.IndexOf('?');
                    if (q >= 0)
                    {
                        pathOnly = path.Substring(0, q);
                        query = path.Substring(q + 1);
                    }

                    string status = "200 OK";
                    string body;
                    if (method == "OPTIONS")
                    {
                        body = "";
                        status = "204 No Content";
                    }
                    else
                    {
                        body = Route(pathOnly, query, ref status);
                    }

                    byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
                    var resp = new StringBuilder();
                    resp.Append("HTTP/1.1 ").Append(status).Append("\r\n");
                    resp.Append("Content-Type: application/json; charset=utf-8\r\n");
                    resp.Append("Access-Control-Allow-Origin: *\r\n");
                    resp.Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n");
                    resp.Append("Access-Control-Allow-Headers: content-type\r\n");
                    resp.Append("Access-Control-Max-Age: 600\r\n");
                    resp.Append("Content-Length: ").Append(bodyBytes.Length.ToString()).Append("\r\n");
                    resp.Append("Connection: close\r\n\r\n");
                    byte[] headBytes = Encoding.ASCII.GetBytes(resp.ToString());
                    stream.Write(headBytes, 0, headBytes.Length);
                    if (bodyBytes.Length > 0) stream.Write(bodyBytes, 0, bodyBytes.Length);
                    stream.Flush();
                }
            }
            catch
            {
                // Connection-level noise: ignore.
            }
        }

        private static string GetQueryArg(string query, string key)
        {
            if (query.Length == 0) return null;
            string[] pairs = query.Split('&');
            for (int i = 0; i < pairs.Length; i++)
            {
                string[] kv = pairs[i].Split('=');
                if (kv.Length == 2 && kv[0] == key)
                    return Uri.UnescapeDataString(kv[1].Replace("+", "%20"));
            }
            return null;
        }

        /// <summary>
        /// Apply one web-pet state sample (from a /play push or the DSH host
        /// poll): update the badge phase/label, then play the animation with
        /// idle-family throttling so the web pet's idle rotations don't fight
        /// the local idle schedule.
        /// </summary>
        private void ApplyWebState(int row, string phase, string label)
        {
            // Badge phase/label (a missing phase keeps the previous one).
            if (phase != null && phase.Length > 0)
            {
                _dshPhase = phase;
                _dshLabel = label ?? "";
                UpdateStatusBadge(GetListenerPids());
            }
            // Idle-family tracks (idle/waving/waiting) are THROTTLED — the
            // desktop pet keeps its base idle and only performs occasional
            // idle animations on the user's configured interval, so the web
            // pet's idle rotations must not force a fresh animation every
            // slice. Task tracks (running / review / jumping / failed) play
            // immediately. A pushed `idle` while an idle-perform is running
            // settles the pet back to base idle.
            if (row == 0 || row == 3 || row == 6)
            {
                if (_performingIdle && row == BASE_IDLE_ROW)
                {
                    _performingIdle = false;
                    PlayTrack(BASE_IDLE_ROW);
                }
                else if (!_performingIdle && _track.Row != BASE_IDLE_ROW)
                {
                    PlayTrack(BASE_IDLE_ROW);
                }
                // else: already idle or performing - keep the local schedule.
            }
            else
            {
                PlayTrack(row);
            }
        }

        /// <summary>
        /// Event-driven DSH sync: subscribe to the host's /api/pet/stream SSE
        /// endpoint, which pushes every projected session activity the moment
        /// it happens (no polling, independent of browser tabs). Falls back to
        /// a 2 s poll of /api/pet/state while the stream is unavailable, and
        /// reconnects automatically.
        /// </summary>
        private void StartDshPoll()
        {
            var t = new Thread(delegate () { DshSyncLoop(); });
            t.IsBackground = true;
            t.Name = "pet-dsh-sync";
            t.Start();
        }

        private void DshSyncLoop()
        {
            while (!_httpStop)
            {
                // Prefer the event stream; on any failure fall back to one
                // poll, then back off before retrying the stream. Every path
                // sleeps at least ~1 s so a missing endpoint (404) or a
                // flapping server can never turn this into a busy loop.
                bool streamOk = TryDshSse();
                if (streamOk)
                {
                    // Stream ended cleanly (server closed): brief pause, then
                    // reconnect immediately.
                    for (int i = 0; i < 5 && !_httpStop; i++) Thread.Sleep(200); // ~1 s
                    continue;
                }
                // Stream failed (endpoint missing / server down): one poll for
                // continuity, then a longer pause before retrying the stream.
                TryDshPollOnce();
                for (int i = 0; i < 10 && !_httpStop; i++) Thread.Sleep(200); // ~2 s
            }
        }

        /// <summary>One blocking SSE read: returns when the stream ends.</summary>
        private bool TryDshSse()
        {
            try
            {
                string url = _ctlWebUrl.TrimEnd('/') + "/api/pet/stream";
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = 3000;
                req.ReadWriteTimeout = 60000;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    // SSE frames: "data: {json}\n\n". Blocking read until EOF.
                    StringBuilder frame = new StringBuilder();
                    int ch;
                    while (!_httpStop && (ch = reader.Read()) >= 0)
                    {
                        if (ch == '\n')
                        {
                            string line = frame.ToString().TrimEnd('\r');
                            frame.Length = 0;
                            if (line.StartsWith("data:"))
                            {
                                string payload = line.Substring(5).Trim();
                                if (payload.Length > 0 && payload[0] == '{')
                                {
                                    ConsumeDshJson(payload);
                                }
                            }
                            // blank line = end of event; next lines are a new frame
                        }
                        else
                        {
                            frame.Append((char)ch);
                        }
                    }
                }
                return true; // stream ended cleanly (server closed): reconnect
            }
            catch
            {
                return false; // stream failed: fall back to polling
            }
        }

        /// <summary>One snapshot poll (fallback while the stream is down).</summary>
        private bool TryDshPollOnce()
        {
            try
            {
                string url = _ctlWebUrl.TrimEnd('/') + "/api/pet/state";
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = 2000;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    ConsumeDshJson(reader.ReadToEnd());
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Apply one JSON state sample (SSE data or poll body).</summary>
        private void ConsumeDshJson(string json)
        {
            string animation = ReadJsonString(json, "animation", "");
            string phase = ReadJsonString(json, "phase", "");
            string bubble = ReadJsonString(json, "bubble", "");
            int row = animation.Length > 0 ? RowOfTrackName(animation) : -1;
            if (row < 0) return;
            int playRow = row;
            string playPhase = phase;
            string playLabel = bubble;
            Dispatcher.BeginInvoke(new Action(delegate
            {
                ApplyWebState(playRow, playPhase, playLabel);
            }));
        }

        private string Route(string path, string query, ref string status)
        {
            if (path == "/health")
            {
                return "{\"ok\":true,\"track\":\"" + TrackNameOfRow(_track.Row) + "\",\"pid\":" +
                    System.Diagnostics.Process.GetCurrentProcess().Id + "}";
            }
            if (path == "/state")
            {
                return "{\"ok\":true,\"track\":\"" + TrackNameOfRow(_track.Row) +
                    "\",\"row\":" + _track.Row + ",\"frame\":" + _frameIdx +
                    ",\"clickThrough\":" + (_clickThrough ? "true" : "false") +
                    ",\"visible\":" + (_visible ? "true" : "false") +
                    ",\"sizePct\":" + _sizePct +
                    ",\"moveEnabled\":" + (_moveEnabled ? "true" : "false") +
                    ",\"moveSpeed\":" + _moveSpeed +
                    ",\"idleIntervalSec\":" + (_idleIntervalMs <= 0 ? 0 : _idleIntervalMs / 1000) +
                    ",\"scale\":" + (_sizePct / 100.0).ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
            }
            if (path == "/play")
            {
                string track = GetQueryArg(query, "track");
                string rowS = GetQueryArg(query, "row");
                string phase = GetQueryArg(query, "phase");
                string label = GetQueryArg(query, "label");
                int row = -1;
                if (track != null) row = RowOfTrackName(track);
                else if (rowS != null) int.TryParse(rowS, out row);
                if (row < 0 || row >= TRACKS.Length)
                {
                    status = "400 Bad Request";
                    return "{\"ok\":false,\"error\":\"unknown track\"}";
                }
                int playRow = row;
                string playPhase = phase;
                string playLabel = label;
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    ApplyWebState(playRow, playPhase, playLabel);
                }));
                return "{\"ok\":true,\"track\":\"" + TrackNameOfRow(row) + "\"}";
            }
            if (path == "/menu")
            {
                string sub = GetQueryArg(query, "sub") ?? "";
                string pet = GetQueryArg(query, "pet") ?? "";
                bool petOnly = pet == "1" || pet.ToLower() == "true";
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    ShowControllerPanel(sub.Length > 0 ? sub : null, petOnly);
                }));
                return "{\"ok\":true}";
            }
            if (path == "/config")
            {
                string ct = GetQueryArg(query, "clickthrough");
                string tm = GetQueryArg(query, "topmost");
                string vis = GetQueryArg(query, "visible");
                string sc = GetQueryArg(query, "scale");
                string mv = GetQueryArg(query, "move");
                string spd = GetQueryArg(query, "speed");
                string idle = GetQueryArg(query, "idleInterval");
                string pct = GetQueryArg(query, "sizePct");
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (ct != null)
                    {
                        _clickThrough = ct == "1" || ct.ToLower() == "true";
                        ApplyClickThrough();
                    }
                    if (tm != null) Topmost = tm == "1" || tm.ToLower() == "true";
                    if (vis != null) SetVisible(vis == "1" || vis.ToLower() == "true");
                    if (sc != null)
                    {
                        double v;
                        if (double.TryParse(sc, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out v)
                            && v >= 0.25 && v <= 2.0)
                        {
                            int p = (int)Math.Round(v * 100);
                            if (Array.IndexOf(SIZE_PCTS, p) >= 0) SetSizePct(p);
                        }
                    }
                    if (pct != null)
                    {
                        int p;
                        if (int.TryParse(pct, out p) && Array.IndexOf(SIZE_PCTS, p) >= 0) SetSizePct(p);
                    }
                    if (mv != null) { _moveEnabled = mv == "1" || mv.ToLower() == "true"; SaveConfig(); }
                    if (spd != null)
                    {
                        int s;
                        if (int.TryParse(spd, out s) && Array.IndexOf(SPEED_OPTIONS, s) >= 0) SetMoveSpeed(s);
                    }
                    if (idle != null)
                    {
                        int s;
                        if (int.TryParse(idle, out s) && Array.IndexOf(IDLE_INTERVAL_OPTIONS, s) >= 0) SetIdleIntervalSec(s);
                    }
                    SaveConfig();
                }));
                return "{\"ok\":true}";
            }
            status = "404 Not Found";
            return "{\"ok\":false,\"error\":\"not found\"}";
        }

        private void WriteLog(string message)
        {
            try
            {
                string dir = Path.GetDirectoryName(_configPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "pet.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine);
            }
            catch { }
        }

        // ------------------------------------------------------------------
        // Config persistence (%APPDATA%\DshPetDesktop\config.txt).
        // Format: left / top / topmost / sizePct / moveEnabled / moveSpeed /
        //         clickThrough / visible
        // ------------------------------------------------------------------
        private void LoadConfig()
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    string[] lines = File.ReadAllLines(_configPath);
                    if (lines.Length >= 4)
                    {
                        double left = double.Parse(lines[0]);
                        double top = double.Parse(lines[1]);
                        bool topmost = lines[2] == "1";
                        double scale = double.Parse(lines[3]);
                        // v1 config stored scale (1.0/1.5/2.0); v2 stores sizePct.
                        if (lines.Length >= 6 && scale < 20) _sizePct = (int)Math.Round(scale * 100);
                        else if (Array.IndexOf(SIZE_PCTS, (int)Math.Round(scale)) >= 0) _sizePct = (int)Math.Round(scale);
                        else _sizePct = 100;
                        Topmost = topmost;
                        if (left > -30000 && top > -30000) { Left = left; Top = top; }
                        if (lines.Length >= 9)
                        {
                            _moveEnabled = lines[4] == "1";
                            int.TryParse(lines[5], out _moveSpeed);
                            _clickThrough = lines[6] == "1";
                            bool visible = lines[7] == "1";
                            int idleSec;
                            if (int.TryParse(lines[8], out idleSec) && Array.IndexOf(IDLE_INTERVAL_OPTIONS, idleSec) >= 0)
                                _idleIntervalMs = idleSec <= 0 ? 0 : idleSec * 1000;
                            if (!visible) SetVisible(false);
                        }
                        else if (lines.Length >= 8)
                        {
                            _moveEnabled = lines[4] == "1";
                            int.TryParse(lines[5], out _moveSpeed);
                            _clickThrough = lines[6] == "1";
                            bool visible = lines[7] == "1";
                            if (!visible) SetVisible(false);
                        }
                        else if (lines.Length >= 6)
                        {
                            _clickThrough = lines[4] == "1";
                            bool visible = lines[5] == "1";
                            if (!visible) SetVisible(false);
                        }
                    }
                }
            }
            catch { }

            // Clamp to the virtual screen so the pet never gets lost.
            double vx = SystemParameters.VirtualScreenLeft;
            double vy = SystemParameters.VirtualScreenTop;
            double vw = SystemParameters.VirtualScreenWidth;
            double vh = SystemParameters.VirtualScreenHeight;
            double w = CELL_W * _sizePct / 100.0;
            double h = CELL_H * _sizePct / 100.0;
            if (Left < vx || Left + w > vx + vw || Top < vy || Top + h > vy + vh)
            {
                Left = vx + Math.Max(0.0, (vw - w) / 2.0);
                Top = vy + Math.Max(0.0, (vh - h) / 2.0);
            }
            ApplySize();
        }

        private void SaveConfig()
        {
            try
            {
                string dir = Path.GetDirectoryName(_configPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllLines(_configPath, new[]
                {
                    Left.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Top.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Topmost ? "1" : "0",
                    _sizePct.ToString(),
                    _moveEnabled ? "1" : "0",
                    _moveSpeed.ToString(),
                    _clickThrough ? "1" : "0",
                    _visible ? "1" : "0",
                    (_idleIntervalMs <= 0 ? 0 : _idleIntervalMs / 1000).ToString(),
                });
            }
            catch { }
        }

        // ------------------------------------------------------------------
        // Run at startup (HKCU Run key - no admin needed).
        // ------------------------------------------------------------------
        private bool GetAutoStart()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RUN_KEY))
                {
                    return k != null && k.GetValue(RUN_VALUE) != null;
                }
            }
            catch { return false; }
        }

        private void SetAutoStart(bool on)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RUN_KEY))
                {
                    if (on)
                    {
                        k.SetValue(RUN_VALUE, "\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\"");
                    }
                    else
                    {
                        k.DeleteValue(RUN_VALUE, false);
                    }
                }
            }
            catch { }
        }

        protected override void OnClosed(EventArgs e)
        {
            _timer.Stop();
            SaveConfig();
            base.OnClosed(e);
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            try
            {
                var app = new Application();
                var win = new PetWindow();
                win.Show();
                app.Run(win);
            }
            catch (Exception ex)
            {
                try
                {
                    string log = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "DshPetDesktop", "crash.log");
                    string dir = Path.GetDirectoryName(log);
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(log, ex.ToString());
                }
                catch { }
                throw;
            }
        }
    }
}
