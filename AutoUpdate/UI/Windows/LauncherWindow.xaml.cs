using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using OpenCrazyArcade.AutoUpdate.Managers;
using OpenCrazyArcade.AutoUpdate.Models;

namespace OpenCrazyArcade.AutoUpdate.UI.Windows;

internal partial class LauncherWindow : Window
{
    private readonly ILogger<LauncherWindow> _logger;
    private readonly LauncherConfig _config;
    private readonly NMCoreManager _nmCoreManager;
    private readonly RemoteCommands _remote;
    private readonly SelfUpdater _selfUpdater;

    private Process _gameProcess;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct STARTUPINFO
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern bool CreateProcessA(
        string lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, uint nSize, out nuint lpNumberOfBytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpThreadAttributes, uint dwStackSize, IntPtr lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, out uint lpThreadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr GetModuleHandleA(string lpModuleName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);
    public LauncherWindow(ILogger<LauncherWindow> logger, LauncherConfig config, NMCoreManager nmCoreManager,
        RemoteCommands remote, SelfUpdater selfUpdater)
    {
        _logger = logger;
        _config = config;
        _nmCoreManager = nmCoreManager;
        _remote = remote;
        _selfUpdater = selfUpdater;

        InitializeComponent();

        _nmCoreManager.PresenceChanged += OnPresenceChanged;
        Loaded += LauncherWindow_Loaded;
    }

    /// <summary>
    /// The server says who is playing on this machine. Shown in the status line while the game runs;
    /// the line goes back to the plain "running" text when the presence ends.
    /// </summary>
    private void OnPresenceChanged(NMCoreManager.PresenceInfo p)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_gameProcess == null) return;   // the status line belongs to the update flow when no game runs
            TxtStatus.Text = p.State switch
            {
                "LoggingIn" => $"Trò chơi đang chạy — đang đăng nhập: {p.Username}",
                "Playing" => $"Đang chơi: {p.Username} (ID {p.AccountId})",
                "ChannelList" => $"{p.Username} — đang ở danh sách kênh",
                _ => "Trò chơi đang chạy...",
            };
        });
    }

    private async void LauncherWindow_Loaded(object sender, RoutedEventArgs e)
    {
        TxtServerUrl.Text = _config.ServerUrl;
        ChkAutoUpdateStartup.IsChecked = _config.AutoCheckUpdateAtStartup;
        ChkWindowedMode.IsChecked = _config.WindowedMode;
        ChkConfigWindowedMode.IsChecked = _config.WindowedMode;
        ChkConfigClientLog.IsChecked = _config.EnableClientLog;
        TxtLogDir.Text = LauncherConfig.LogDir;

        await InitializeWebViewAsync();

        // From here on links and "show yourself" requests from other instances can be served.
        _remote.Attach(line => Dispatcher.BeginInvoke(() => OnRemoteLine(line)));

        if (_config.AutoCheckUpdateAtStartup)
        {
            await CheckUpdatesAsync();
        }

        StartUpdateWatching();
    }

    // ── Launcher tu cap nhat + yeu cau tu instance khac (link opencrazyarcade://) ─────────────
    private bool _modalBusy;   // a launcher dialog (link / install) is open: one at a time

    private void BringToFront()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true; Topmost = false;   // Windows lets a topmost flip win the foreground when Activate alone is refused
    }

    private async void OnRemoteLine(string line)
    {
        try
        {
            BringToFront();
            if (line.StartsWith("URI\t", StringComparison.Ordinal))
                await HandleLinkAsync(line[4..]);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[REMOTE] loi khi xu ly yeu cau: {Message}", ex.Message); }
    }

    /// <summary>
    /// opencrazyarcade://set-server/?src=... : the player always decides. The dialog shows the address that will
    /// be replaced and the one that will replace it, because a link can come from any page.
    /// </summary>
    private async Task HandleLinkAsync(string link)
    {
        _logger.LogInformation("[REMOTE] nhan lien ket: {Link}", link);
        if (_modalBusy) return;   // one dialog at a time
        _modalBusy = true;
        try
        {
            if (!ProtocolHandler.TryParseSetServer(link, out string url, out string error))
            {
                MessageBox.Show(this, error, "Cấu hình nhanh", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (IsGameRunning())
            {
                MessageBox.Show(this, "Trò chơi đang chạy. Hãy thoát game trước khi đổi máy chủ.", "Cấu hình nhanh", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string current = _config.ServerUrl?.Trim().TrimEnd('/');
            if (string.Equals(current, url, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, $"Launcher đã dùng máy chủ này rồi:\n{url}", "Cấu hình nhanh", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string plain = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                ? "\n\nLưu ý: địa chỉ này không dùng HTTPS." : "";
            string text = string.IsNullOrEmpty(current)
                ? $"Đặt máy chủ của launcher thành:\n{url}{plain}\n\nChỉ đồng ý nếu bạn tin tưởng trang vừa gửi liên kết này."
                : $"Đổi máy chủ của launcher?\n\nHiện tại: {current}\nMới: {url}{plain}\n\nChỉ đồng ý nếu bạn tin tưởng trang vừa gửi liên kết này.";
            if (MessageBox.Show(this, text, "Cấu hình nhanh", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;

            _config.ServerUrl = url;
            _config.Save();
            TxtServerUrl.Text = url;
            _logger.LogInformation("[REMOTE] doi may chu: {Old} -> {New}", current, url);

            await _nmCoreManager.ResetServerConnectionAsync();
            _knownUpdateVersion = null;
            NavigateToServerHome();
            TxtStatus.Text = "Đã đổi máy chủ!";
            try { await _nmCoreManager.EnsureConnectToOCAServer(); } catch { }
            await CheckUpdatesAsync();
        }
        finally { _modalBusy = false; }
    }

    // ── Trang dự án <-> launcher (WebView2 postMessage) ─────────────────────────────────────────
    // The project page (first tab) lists the launcher releases and may ask this launcher to install one - newer or
    // older. Only that page is listened to (its origin is checked on every message); it names the exe to download.
    //   page -> launcher  { action: "hello" }                                   -> { type: "hello", version: "1.0.0" }
    //   page -> launcher  { action: "install", version, url, sha256? }          -> confirm, download, restart
    //   launcher -> page  { type: "install", state: "confirm" | "declined" | "downloading" | "error", ... }
    /// <summary>The project page the first tab shows. Fixed on purpose: this page may ask the launcher to install an exe,
    /// so nothing on the machine (an environment variable, a config file) may swap it.</summary>
    private const string ProjectHomeUrl = "https://opencrazyarcade.github.io/on-launcher";

    private bool IsProjectPage(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var from) || !Uri.TryCreate(ProjectHomeUrl, UriKind.Absolute, out var project))
            return false;
        return from.Scheme == project.Scheme && from.Host.Equals(project.Host, StringComparison.OrdinalIgnoreCase) && from.Port == project.Port;
    }

    private void ReplyToPage(object message)
    {
        try { NewsWebView.CoreWebView2.PostWebMessageAsJson(System.Text.Json.JsonSerializer.Serialize(message)); }
        catch (Exception ex) { _logger.LogDebug("Khong gui duoc tin cho trang: {Message}", ex.Message); }
    }

    private async void OnPageMessage(object sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            if (!IsProjectPage(e.Source))
            {
                _logger.LogWarning("[PAGE] bo qua tin nhan tu nguon la: {Source}", e.Source);
                return;
            }

            using var doc = System.Text.Json.JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            string action = root.TryGetProperty("action", out var a) ? a.GetString() : null;
            string Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;

            if (action == "hello")
            {
                ReplyToPage(new { type = "hello", version = SelfUpdater.CurrentVersionText });
            }
            else if (action == "install")
            {
                await InstallFromPageAsync(Str("version"), Str("url"), Str("sha256"));
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[PAGE] loi khi xu ly tin nhan: {Message}", ex.Message); }
    }

    private async Task InstallFromPageAsync(string version, string url, string sha256)
    {
        void Fail(string message) => ReplyToPage(new { type = "install", state = "error", message });

        if (_modalBusy) { Fail("Launcher đang chờ bạn trả lời một hộp thoại khác."); return; }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            Fail("Địa chỉ tải phải là https://.");
            return;
        }
        // The launcher also carries the game's NMCO pipe: restarting it under a running game would cut the game off.
        if (IsGameRunning()) { Fail("Trò chơi đang chạy. Hãy thoát game trước khi đổi phiên bản launcher."); return; }

        _modalBusy = true;
        try
        {
            string current = SelfUpdater.CurrentVersionText;
            string direction = Version.TryParse(version, out var target)
                ? (target > SelfUpdater.CurrentVersion ? " (nâng cấp)" : target < SelfUpdater.CurrentVersion ? " (hạ cấp)" : " (cài lại)")
                : "";
            BringToFront();
            var ask = MessageBox.Show(this,
                $"Bạn có muốn cài đặt phiên bản\n{uri}\n\nthay thế cho v{current}?{direction}\n\nLauncher sẽ tự khởi động lại.",
                "Cài đặt phiên bản launcher", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (ask != MessageBoxResult.Yes)
            {
                ReplyToPage(new { type = "install", state = "declined" });
                return;
            }

            _logger.LogInformation("[PAGE] cai dat {Version} tu {Url} (dang dung {Current})", version, uri, current);
            ReplyToPage(new { type = "install", state = "downloading", percent = 0 });
            TxtStatus.Text = "Đang tải launcher...";
            bool started = await _selfUpdater.InstallAsync(uri.ToString(), sha256,
                pct => Dispatcher.BeginInvoke(() => { TxtStatus.Text = $"Đang tải launcher... {pct}%"; ReplyToPage(new { type = "install", state = "downloading", percent = pct }); }),
                msg => Dispatcher.Invoke(() => { Fail(msg); MessageBox.Show(this, msg, "Cài đặt phiên bản launcher", MessageBoxButton.OK, MessageBoxImage.Warning); }));
            if (!started) { TxtStatus.Text = "Không cài được phiên bản launcher."; return; }

            Application.Current.Shutdown();
        }
        finally { _modalBusy = false; }
    }

    // ── Cap nhat khi launcher dang mo ───────────────────────────────────────────────────────
    // Chinh: server day su kien UpdatesChanged qua hub SignalR (va sau moi lan reconnect).
    // Du phong: hoi GET /api/updates/version moi UpdatePollInterval phong khi hub khong noi duoc.
    private static readonly TimeSpan UpdatePollInterval = TimeSpan.FromMinutes(5);
    private string _knownUpdateVersion;
    private bool _updatePending;
    private bool _updateRunning;
    private bool _lastUpdateFailed;

    private async void StartUpdateWatching()
    {
        _nmCoreManager.UpdatesChanged += version => Dispatcher.InvokeAsync(() => OnUpdateVersion(version, "hub"));
        try { await _nmCoreManager.EnsureConnectToOCAServer(); } catch { }

        _knownUpdateVersion = await FetchUpdateVersionAsync();
        _logger.LogInformation("[UPDATE] theo doi cap nhat: phien ban hien tai {Version}", _knownUpdateVersion ?? "(khong biet)");

        var timer = new System.Windows.Threading.DispatcherTimer { Interval = UpdatePollInterval };
        timer.Tick += async (_, _) => OnUpdateVersion(await FetchUpdateVersionAsync(), "poll");
        timer.Start();
    }

    private async Task<System.Collections.Generic.List<string>> FetchRemovedAsync(System.Net.Http.HttpClient http, string baseUrl)
    {
        try
        {
            return await System.Net.Http.Json.HttpClientJsonExtensions.GetFromJsonAsync<System.Collections.Generic.List<string>>(
                http, $"{baseUrl}/api/updates/removed") ?? new();
        }
        catch (Exception ex)
        {
            // An older server has no such endpoint: nothing to delete, not an error.
            _logger.LogDebug("[UPDATE] khong lay duoc danh sach xoa: {Message}", ex.Message);
            return new();
        }
    }

    private async Task<string> FetchUpdateVersionAsync()
    {
        if (!_config.HasServer) return null;
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            string v = await http.GetStringAsync($"{_config.GetNormalizedServerUrl()}/api/updates/version");
            return v.Trim().Trim('"');
        }
        catch (Exception ex)
        {
            _logger.LogDebug("[UPDATE] khong hoi duoc phien ban: {Message}", ex.Message);
            return null;
        }
    }

    private async void OnUpdateVersion(string version, string source)
    {
        if (string.IsNullOrEmpty(version) || version == _knownUpdateVersion) return;
        _logger.LogInformation("[UPDATE] ({Source}) server co bo cap nhat moi: {Old} -> {New}", source, _knownUpdateVersion ?? "?", version);
        _knownUpdateVersion = version;

        if (IsGameRunning())
        {
            // The files are locked by the running game; apply as soon as it exits.
            _updatePending = true;
            TxtStatus.Text = "Có bản cập nhật mới — sẽ áp dụng khi thoát game.";
            return;
        }
        await RunUpdateIfIdleAsync();
    }

    private async Task RunUpdateIfIdleAsync()
    {
        if (_updateRunning) { _updatePending = true; return; }
        _updateRunning = true;
        _updatePending = false;
        try { await CheckUpdatesAsync(); }
        finally { _updateRunning = false; }
        if (_updatePending && !IsGameRunning()) await RunUpdateIfIdleAsync();
    }

    private void ChkWindowedMode_Click(object sender, RoutedEventArgs e)
    {
        _config.WindowedMode = ChkWindowedMode.IsChecked == true;
        ChkConfigWindowedMode.IsChecked = _config.WindowedMode;
        _config.Save();

        string caExe = FindGameExecutable();
        if (!string.IsNullOrEmpty(caExe))
        {
            ApplyWindowedModeSetting(Path.GetDirectoryName(caExe)!, _config.WindowedMode);
        }
    }

    // KHÔNG xoá channel2_<idx>.ssd trước khi chạy game. Đã thử ngày 2026-10-14 và nó làm
    // lưới kênh TRỐNG MỌI LẦN, chứ không phải thỉnh thoảng.
    //
    // Lý do: tệp đó KHÔNG phải cache vứt đi được — nó là NGUỒN của giá trị mà cổng opcode 16
    // so sánh. LoadChannelSSD @0x993EF4 đọc nó trước khi opcode 58 rời máy; tệp thiếu thì
    // nhánh else đặt *(this+12) = 0 và *(this+16) = 0, nên mọi opcode 16 có count khác 0 đều
    // bị bỏ qua không một lời nào.
    //
    // Máy chủ ghi đè tệp này bằng opcode 14 trong mỗi phiên đăng nhập, nên bảng cũ tự khỏi sau
    // một lần. Để yên là đúng.
    //
    // Nếu sau này cần lo cho người chơi MỚI (chưa có tệp, lần đăng nhập đầu sẽ trống lưới):
    // launcher có thể TỰ GHI tệp đó từ danh sách kênh trong /api/config. Định dạng đã giải
    // được: "ssi1" + u32 adler32 + 0x18 + u32 (size-13) + u8 method(0 = không nén) rồi payload
    // tại offset 14 — u16 count, mỗi kênh { u32 nameLen, tên CP1258, 27 byte config }.


    private void ApplyWindowedModeSetting(string gameDir, bool isWindowed)
    {
        if (string.IsNullOrEmpty(gameDir)) return;
        string iniPath = Path.Combine(gameDir, "ddraw.ini");

        // A fresh install has ddraw.dll (the server publishes it) but no ddraw.ini, because the
        // ini is the PLAYER's file: cnc-ddraw rewrites it on exit and this method rewrites it on
        // every tick of the checkbox. Publishing it would mean the server reverting the player's
        // own choice on the next launch, so it is created here instead.
        //
        // Returning quietly when it was missing is what made the Windowed checkbox do nothing at
        // all on a machine that had never had the file.
        if (!File.Exists(iniPath))
        {
            try
            {
                File.WriteAllText(iniPath, DefaultDdrawIni);
                _logger.LogInformation("[WINDOW] tao moi ddraw.ini tai '{Path}'", iniPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[WINDOW] KHONG tao duoc ddraw.ini tai '{Path}'", iniPath);
                return;
            }
        }

        try
        {
            string text = File.ReadAllText(iniPath);
            if (isWindowed)
            {
                text = Set(text, "fullscreen", "false");
                text = Set(text, "windowed", "true");

                // cnc-ddraw calls it "developer mode", but all it does is leave the cursor
                // unlocked. A window that traps the mouse is worse than fullscreen - you cannot
                // reach anything else on the screen, and the only way out is a hotkey nobody
                // knows. So the two settings travel together.
                text = Set(text, "devmode", "true");
            }
            else
            {
                text = Set(text, "fullscreen", "true");
                text = Set(text, "windowed", "false");

                // Fullscreen owns the screen anyway, so the cursor belongs to the game.
                text = Set(text, "devmode", "false");
            }
            File.WriteAllText(iniPath, text);
            _logger.LogInformation("[WINDOW] dat che do cua so = {Mode} trong '{Path}'", isWindowed, iniPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WINDOW] KHONG ghi duoc ddraw.ini tai '{Path}'", iniPath);
        }
    }

    /// <summary>
    /// Replaces every <c>key=value</c> line for <paramref name="key"/>, whatever it said.
    ///
    /// Every section of ddraw.ini may carry its own copy — the shipped file has hundreds of
    /// per-game sections — and cnc-ddraw reads the one that matches. Rewriting all of them is
    /// what makes the checkbox take effect regardless of which section wins.
    ///
    /// The value is matched with <c>[^\r\n]*</c> rather than <c>.*$</c> on purpose:
    /// in .NET <c>.</c> matches <c>\r</c>, so <c>.*$</c> swallows the carriage return and the
    /// rewritten lines come back LF-only in a CRLF file.
    /// </summary>
    private static string Set(string ini, string key, string value) =>
        System.Text.RegularExpressions.Regex.Replace(
            ini, $@"(?m)^{key}[ \t]*=[^\r\n]*", $"{key}={value}");

    /// <summary>
    /// The smallest ddraw.ini that makes the Windowed checkbox mean something.
    ///
    /// Only the keys this launcher manages, plus the few cnc-ddraw needs to behave: the shipped
    /// file in the dev install is 30 KB of per-game sections that no player needs. cnc-ddraw
    /// fills in the rest itself and rewrites the file on exit, which is exactly why this is
    /// created once rather than published from the server.
    /// </summary>
    private const string DefaultDdrawIni = """
; cnc-ddraw - https://github.com/FunkyFr3sh/cnc-ddraw
; Tao tu dong boi OpenCrazyArcade launcher. Sua thoai mai - may chu KHONG ghi de tep nay.

[ddraw]
; 0 = dung kich thuoc game yeu cau
width=800
height=600

; windowed=true + fullscreen=false  => cua so thuong
; windowed=true + fullscreen=true   => cua so khong vien (borderless)
fullscreen=false
windowed=true

maintas=false
boxing=false
maxfps=-1
vsync=false
renderer=auto

; cnc-ddraw goi la "developer mode", nhung viec no lam la KHONG khoa chuot.
; Cua so ma giam chuot thi te hon fullscreen, nen mac dinh la khong khoa.
devmode=true
border=true
savesettings=1

""";

    private string _currentInternalUrl;

    private async Task InitializeWebViewAsync()
    {
        try
        {
            string userDataDir = LauncherConfig.WebView2DataDir;
            var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, userDataDir);
            await NewsWebView.EnsureCoreWebView2Async(env);

            // Bat su kien mo cua so moi (target="_blank" hoac window.open) -> mo trinh duyet mac dinh
            NewsWebView.CoreWebView2.NewWindowRequested += (s, args) =>
            {
                args.Handled = true;
                OpenInDefaultBrowser(args.Uri);
            };

            // Bat su kien dieu huong -> neu nguoi dung bam link ngoai tren trang on-launcher, mo trinh duyet mac dinh
            NewsWebView.CoreWebView2.NavigationStarting += (s, args) =>
            {
                if (IsExternalLink(args.Uri))
                {
                    args.Cancel = true;
                    OpenInDefaultBrowser(args.Uri);
                }
            };

            NewsWebView.CoreWebView2.WebMessageReceived += OnPageMessage;

            NavigateToProjectHome();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize WebView2 profile at {Dir}: {Message}", LauncherConfig.WebView2DataDir, ex.Message);
        }
    }

    private void Hyperlink_GotoProjectHome_Click(object sender, RoutedEventArgs e)
    {
        NavigateToProjectHome();
    }

    private void Hyperlink_GotoServerHome_Click(object sender, RoutedEventArgs e)
    {
        NavigateToServerHome();
    }

    private void NavigateToProjectHome()
    {
        NavigateToUrl(ProjectHomeUrl);
    }

    private void NavigateToServerHome()
    {
        string serverUrl = _config.GetNormalizedServerUrl();
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            ShowNoServerPage();
            return;
        }
        NavigateToUrl($"{serverUrl}/on-launcher");
    }

    /// <summary>The server tab before any server is set: say what to do instead of showing an error page.</summary>
    private void ShowNoServerPage()
    {
        _currentInternalUrl = null;
        const string html = """
            <!DOCTYPE html><html lang="vi"><head><meta charset="UTF-8"><style>
            body{margin:0;padding:28px 22px;font:13px/20px Tahoma,Arial,sans-serif;color:#444;background:#caebfc linear-gradient(180deg,#2fa9f6 0,#8ad6fc 90px,#caebfc 220px) no-repeat}
            .card{max-width:520px;margin:0 auto;background:#fff;border:1px solid #5fc6ff;border-radius:14px;box-shadow:0 0 0 4px rgba(255,255,255,.65);padding:16px 22px 18px}
            h3{margin:0 0 10px;padding-bottom:7px;font-size:14px;color:#0070b2;border-bottom:1px dotted #5fc6ff}
            li{margin-bottom:6px} b{color:#0070b2}
            </style></head><body><div class="card"><h3>CHƯA CẤU HÌNH MÁY CHỦ</h3>
            <p>Launcher chưa biết bạn chơi ở máy chủ nào. Hãy lấy địa chỉ từ chủ máy chủ cộng đồng rồi chọn một trong hai cách:</p>
            <ol><li>Bấm nút <b>⚙</b> ở góc dưới bên phải, dán địa chỉ máy chủ vào ô <b>Địa chỉ máy chủ</b> và lưu.</li>
            <li>Mở trang của máy chủ cộng đồng trên trình duyệt và bấm <b>Cấu hình nhanh</b>, launcher sẽ hỏi bạn xác nhận.</li></ol>
            </div></body></html>
            """;
        try { NewsWebView?.CoreWebView2?.NavigateToString(html); }
        catch (Exception ex) { _logger.LogError(ex, "Khong hien duoc trang chua co may chu: {Message}", ex.Message); }
    }

    private void NavigateToUrl(string url)
    {
        try
        {
            _currentInternalUrl = url.TrimEnd('/');
            if (NewsWebView != null && NewsWebView.CoreWebView2 != null)
            {
                NewsWebView.CoreWebView2.Navigate(url);
            }
            else if (NewsWebView != null)
            {
                NewsWebView.Source = new Uri(url);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to navigate WebView2 to {Url}: {Message}", url, ex.Message);
        }
    }

    private bool IsExternalLink(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri) || uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return false;   // NavigateToString pages

        string cleanUri = uri.TrimEnd('/');

        // Neu la chinh URL on-launcher cua Project hoac Server dang load thi cho phep hien thi trong WebView
        if (_currentInternalUrl != null && cleanUri.Equals(_currentInternalUrl, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string projectOnLauncher = ProjectHomeUrl.TrimEnd('/');
        if (cleanUri.Equals(projectOnLauncher, StringComparison.OrdinalIgnoreCase) ||
            cleanUri.Equals(projectOnLauncher + "/index.html", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string serverUrl = _config.GetNormalizedServerUrl();
        if (!string.IsNullOrWhiteSpace(serverUrl))
        {
            string serverOnLauncher = $"{serverUrl.TrimEnd('/')}/on-launcher";
            if (cleanUri.Equals(serverOnLauncher, StringComparison.OrdinalIgnoreCase) ||
                cleanUri.Equals(serverOnLauncher + "/index.html", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        // Moi link khac click tren on-launcher (link ngoai, bai viet, GitHub, Discord, v.v.) deu mo tren Default Browser
        return true;
    }

    private void OpenInDefaultBrowser(string url)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            _logger.LogInformation("Opening external URL in default browser: {Url}", url);
            var psi = new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open URL in default browser {Url}: {Message}", url, ex.Message);
        }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch { }
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        if (IsGameRunning())
        {
            // When game is running, clicking close only minimizes the launcher
            _logger.LogInformation("Game is currently running; minimizing launcher instead of exiting.");
            WindowState = WindowState.Minimized;
        }
        else
        {
            _logger.LogInformation("Exiting OpenCrazyArcade Launcher.");
            Application.Current.Shutdown();
        }
    }

    private bool IsGameRunning()
    {
        if (_gameProcess != null && !_gameProcess.HasExited) return true;
        return Process.GetProcessesByName("CA").Length > 0;
    }

    private void BtnConfig_Click(object sender, RoutedEventArgs e)
    {
        TxtServerUrl.Text = _config.ServerUrl;
        ChkAutoUpdateStartup.IsChecked = _config.AutoCheckUpdateAtStartup;
        ChkConfigWindowedMode.IsChecked = _config.WindowedMode;
        ChkConfigClientLog.IsChecked = _config.EnableClientLog;
        TxtLogDir.Text = LauncherConfig.LogDir;

        // Hide WebView2 to resolve the HWND airspace issue so ConfigOverlay displays on top
        NewsWebView.Visibility = Visibility.Hidden;
        ConfigOverlay.Visibility = Visibility.Visible;
    }

    private void BtnOpenLogs_Click(object sender, RoutedEventArgs e) => OpenFolder(LauncherConfig.LogDir);

    private void BtnOpenLauncherLogs_Click(object sender, RoutedEventArgs e) => OpenFolder(LauncherConfig.LauncherLogDir);

    private void BtnOpenNmco3Logs_Click(object sender, RoutedEventArgs e) => OpenFolder(LauncherConfig.Nmco3LogDir);

    private void OpenFolder(string dir)
    {
        try
        {
            LauncherConfig.EnsureDirectories();
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Khong mo duoc thu muc nhat ky '{Dir}': {Message}", dir, ex.Message);
        }
    }

    // CAPlay reads these from its environment, which the game inherits from the launcher:
    // CA_LOGDIR redirects its log into %ProgramData%, CA_NOLOG=1 turns it off.
    private void ApplyClientLogEnvironment()
    {
        // The checkbox only decides whether CAPlay writes its log file; CAPlay itself is always injected.
        if (_config.EnableClientLog)
        {
            Environment.SetEnvironmentVariable("CA_NOLOG", null);
            Environment.SetEnvironmentVariable("CA_LOGDIR", LauncherConfig.LogDir);
        }
        else
        {
            Environment.SetEnvironmentVariable("CA_LOGDIR", null);
            Environment.SetEnvironmentVariable("CA_NOLOG", "1");
        }
    }

    private string CaPlayPath()
    {
        string caExe = FindGameExecutable();
        return string.IsNullOrEmpty(caExe) ? null : Path.Combine(Path.GetDirectoryName(caExe)!, "CAPlay.dll");
    }

    /// <summary>
    /// Dev flags from the hidden Dev Config window, validated against the list in the CAPlay.dll about to be injected.
    /// Every flag CAPlay declares is set or cleared, so a stale variable of an earlier run never leaks into this one.
    /// </summary>
    private void ApplyDevFlagsEnvironment()
    {
        string dll = CaPlayPath();
        var flags = dll == null ? Array.Empty<CaPlayFlag>() : CaPlayFlags.Load(dll, out _);
        foreach (string gone in CaPlayFlags.Reconcile(_config.DevFlags, flags))
            _logger.LogWarning("[DEVFLAGS] co '{Env}' khong con trong CAPlay.dll - da bo khoi cau hinh", gone);
        _config.Save();

        var active = new System.Collections.Generic.List<string>();
        foreach (var flag in flags)
        {
            if (_config.DevFlags.TryGetValue(flag.Env, out var value))
            {
                Environment.SetEnvironmentVariable(flag.Env, value);
                active.Add($"{flag.Env}={value}");
            }
            else
            {
                Environment.SetEnvironmentVariable(flag.Env, null);
            }
        }
        _logger.LogInformation("[DEVFLAGS] co dev dang bat: {Flags}", active.Count == 0 ? "(khong)" : string.Join(", ", active));
    }

    private void BtnConfig_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers != (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift)) return;
        e.Handled = true;

        // Read the flag list fresh from the DLL on every open: nothing cached can disagree with the file on disk.
        string dll = CaPlayPath();
        string err = dll == null ? "Chưa xác định được thư mục game." : null;
        var flags = dll == null ? Array.Empty<CaPlayFlag>() : CaPlayFlags.Load(dll, out err);
        CaPlayFlags.Reconcile(_config.DevFlags, flags);

        var dlg = new DevConfigWindow(flags, err, _config.DevFlags) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _config.DevFlags = new System.Collections.Generic.Dictionary<string, string>(dlg.Result);
            _config.Save();
            TxtStatus.Text = "Đã lưu cờ dev (áp dụng ở lần mở game kế tiếp).";
        }
    }

    private void BtnCloseConfig_Click(object sender, RoutedEventArgs e)
    {
        ConfigOverlay.Visibility = Visibility.Collapsed;
        NewsWebView.Visibility = Visibility.Visible;
    }

    private async void BtnSaveConfig_Click(object sender, RoutedEventArgs e)
    {
        string url = TxtServerUrl.Text.Trim();

        if (string.IsNullOrEmpty(url))
        {
            MessageBox.Show("Địa chỉ Máy Chủ không được để trống!", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "http://" + url;
        }

        _config.ServerUrl = url.TrimEnd('/');
        _config.AutoCheckUpdateAtStartup = ChkAutoUpdateStartup.IsChecked == true;
        _config.WindowedMode = ChkConfigWindowedMode.IsChecked == true;
        _config.EnableClientLog = ChkConfigClientLog.IsChecked == true;
        ChkWindowedMode.IsChecked = _config.WindowedMode;
        _config.Save();

        string caExe = FindGameExecutable();
        if (!string.IsNullOrEmpty(caExe))
        {
            ApplyWindowedModeSetting(Path.GetDirectoryName(caExe)!, _config.WindowedMode);
        }

        ConfigOverlay.Visibility = Visibility.Collapsed;
        NewsWebView.Visibility = Visibility.Visible;
        TxtStatus.Text = "Đã lưu cấu hình máy chủ!";

        // Chạy lại kiểm tra cập nhật ngay sau khi lưu cấu hình
        await CheckUpdatesAsync();
    }

    private record UpdateManifestItem(
        [property: System.Text.Json.Serialization.JsonPropertyName("path")] string Path,
        [property: System.Text.Json.Serialization.JsonPropertyName("size")] long Size,
        [property: System.Text.Json.Serialization.JsonPropertyName("hash")] string Hash,
        [property: System.Text.Json.Serialization.JsonPropertyName("downloadUrl")] string DownloadUrl
    );

    private async Task CheckUpdatesAsync()
    {
        if (!_config.HasServer)
        {
            // Nothing to update from and nowhere to play: say why the button is off.
            BtnStart.IsEnabled = false;
            BtnStart.Content = "Chưa cấu hình";
            TxtStatus.Text = "Chưa cấu hình máy chủ. Bấm ⚙ để nhập địa chỉ máy chủ.";
            return;
        }

        // Updates are written next to ca.exe: without it there is no game folder, so do not download anywhere.
        if (FindGameExecutable() == null)
        {
            TxtStatus.Text = MissingGameText;
            return;
        }

        // Vô hiệu hóa nút Bắt đầu trong suốt quá trình cập nhật
        _lastUpdateFailed = false;
        BtnStart.IsEnabled = false;
        BtnStart.Content = "⏳ Đang Cập Nhật...";
        TxtStatus.Text = "Đang kiểm tra bản cập nhật máy chủ...";

        try
        {
            string caExe = FindGameExecutable();
            string gameDir = Path.GetDirectoryName(caExe);

            string baseUrl = _config.GetNormalizedServerUrl();
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };

            string manifestUrl = $"{baseUrl}/api/updates/manifest";
            _logger.LogInformation("[UPDATE] thu muc game = '{Dir}' (ca.exe = '{Exe}')",
                gameDir ?? "(khong tim thay)", caExe ?? "(khong tim thay)");
            _logger.LogInformation("[UPDATE] hoi manifest: {Url}", manifestUrl);

            var items = await System.Net.Http.Json.HttpClientJsonExtensions.GetFromJsonAsync<List<UpdateManifestItem>>(http, manifestUrl);
            _logger.LogInformation("[UPDATE] manifest tra ve {Count} tep", items?.Count ?? -1);

            if (string.IsNullOrEmpty(gameDir) || !Directory.Exists(gameDir))
            {
                // Without a game directory there is nothing to update INTO, and reporting "up to
                // date" would be a lie that looks exactly like success.
                _lastUpdateFailed = true;
                _logger.LogError("[UPDATE] KHONG xac dinh duoc thu muc game - bo qua cap nhat");
                TxtStatus.Text = "Khong tim thay thu muc game - chua cap nhat duoc!";
                return;
            }

            if (items != null && items.Count > 0 && !string.IsNullOrEmpty(gameDir) && Directory.Exists(gameDir))
            {
                int updatedCount = 0;
                int failedCount = 0;
                using var sha256 = System.Security.Cryptography.SHA256.Create();

                foreach (var item in items)
                {
                    if (string.IsNullOrWhiteSpace(item.Path)) continue;

                    string localRel = item.Path.Replace('/', Path.DirectorySeparatorChar);
                    string targetFile = Path.Combine(gameDir, localRel);

                    bool needDownload;
                    string localHash = "(khong co tep)";
                    if (!File.Exists(targetFile))
                    {
                        needDownload = true;
                    }
                    else
                    {
                        try
                        {
                            using var fs = File.OpenRead(targetFile);
                            localHash = Convert.ToHexString(sha256.ComputeHash(fs)).ToLowerInvariant();
                            needDownload = !string.Equals(localHash, item.Hash, StringComparison.OrdinalIgnoreCase);
                        }
                        catch (Exception ex)
                        {
                            // Usually the file is locked because the game is still running.
                            localHash = $"(doc that bai: {ex.Message})";
                            needDownload = true;
                        }
                    }

                    _logger.LogInformation("[UPDATE] {Path}: may={Local} / server={Remote} -> {Verdict}",
                        item.Path,
                        localHash.Length == 64 ? localHash.Substring(0, 16) : localHash,
                        item.Hash.Length >= 16 ? item.Hash.Substring(0, 16) : item.Hash,
                        needDownload ? "TAI LAI" : "da dung");

                    if (needDownload)
                    {
                      try
                      {
                        string fileName = Path.GetFileName(targetFile);
                        TxtStatus.Text = $"Đang cập nhật {fileName}...";
                        _logger.LogInformation("Updating game file: {Path} -> {Target}", item.Path, targetFile);

                        string dlUrl = item.DownloadUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                            ? item.DownloadUrl
                            : $"{baseUrl}{item.DownloadUrl}";

                        LauncherConfig.EnsureDirectories();
                        string tempFile = Path.Combine(LauncherConfig.UpdatesTempDir, $"{Guid.NewGuid():N}_{fileName}");

                        using (var response = await http.GetAsync(dlUrl, System.Net.Http.HttpCompletionOption.ResponseHeadersRead))
                        {
                            response.EnsureSuccessStatusCode();
                            using var s = await response.Content.ReadAsStreamAsync();
                            using var fs = File.Create(tempFile);
                            await s.CopyToAsync(fs);
                        }

                        string targetDir = Path.GetDirectoryName(targetFile)!;
                        Directory.CreateDirectory(targetDir);

                        File.Copy(tempFile, targetFile, overwrite: true);
                        try { File.Delete(tempFile); } catch { }

                        _logger.LogInformation("[UPDATE] {Path}: da ghi {Size}b vao '{Target}'",
                            item.Path, new FileInfo(targetFile).Length, targetFile);
                        updatedCount++;
                      }
                      catch (Exception ex)
                      {
                        // One file failing must not take the rest with it - and must not be
                        // swallowed either. A locked ehsvc.dll quietly skipped is exactly how a
                        // player ends up with half an update and an error nobody can see.
                        failedCount++;
                        _logger.LogError(ex, "[UPDATE] {Path}: KHONG cap nhat duoc vao '{Target}'",
                            item.Path, targetFile);
                      }
                    }
                }

                // Files the server says to delete (published once, dropped since). The manifest only adds and
                // replaces, so without this a retired file (e.g. ddraw.dll) stays on the machine for ever.
                int removedCount = 0;
                foreach (string rel in await FetchRemovedAsync(http, baseUrl))
                {
                    try
                    {
                        string root = Path.GetFullPath(gameDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                        string full = Path.GetFullPath(Path.Combine(gameDir, rel.Replace('/', Path.DirectorySeparatorChar)));
                        string name = Path.GetFileName(full);
                        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                            || name.Equals("ca.exe", StringComparison.OrdinalIgnoreCase)
                            || name.Equals("CAPlay.dll", StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogWarning("[UPDATE] bo qua yeu cau xoa '{Path}' (ngoai thu muc game hoac tep duoc bao ve)", rel);
                            continue;
                        }
                        if (!File.Exists(full)) continue;

                        File.Delete(full);
                        removedCount++;
                        _logger.LogInformation("[UPDATE] {Path}: da xoa (server bao tep nay da bi go)", rel);
                    }
                    catch (Exception ex)
                    {
                        failedCount++;
                        _logger.LogError(ex, "[UPDATE] {Path}: KHONG xoa duoc", rel);
                    }
                }

                if (failedCount > 0)
                {
                    // Reporting "up to date" here is how a half-applied update turns into a bug
                    // report about the game instead of about the launcher.
                    _lastUpdateFailed = true;
                    _logger.LogError("[UPDATE] xong: {Ok} tep da cap nhat, {Fail} tep HONG", updatedCount, failedCount);
                    TxtStatus.Text = $"Cap nhat HONG {failedCount} tep - nhat ky o C:\\ProgramData\\OpenCrazyArcade\\logs\\launcher!";
                }
                else if (updatedCount + removedCount > 0)
                {
                    _logger.LogInformation("[UPDATE] xong: da cap nhat {Count} tep, xoa {Removed} tep", updatedCount, removedCount);
                    TxtStatus.Text = removedCount == 0
                        ? $"Đã cập nhật thành công {updatedCount} tệp game!"
                        : $"Đã cập nhật {updatedCount} tệp, gỡ {removedCount} tệp cũ!";
                    TxtUpdateTime.Text = $"Vừa update xong lúc {DateTime.Now:HH:mm:ss dd/MM/yyyy} ({updatedCount + removedCount} tệp)";
                    TxtUpdateTime.Visibility = Visibility.Visible;
                }
                else
                {
                    _logger.LogInformation("[UPDATE] xong: khong tep nao can cap nhat");
                    TxtStatus.Text = "Hệ thống đã cập nhật phiên bản mới nhất!";
                }
            }
            else
            {
                TxtStatus.Text = "Hệ thống đã cập nhật phiên bản mới nhất!";
            }
        }
        catch (Exception ex)
        {
            // It used to say "ready to play" here - a failed update reported to the player as
            // success, which is how an un-updated client becomes a mystery bug in the game.
            _lastUpdateFailed = true;
            _logger.LogError(ex, "[UPDATE] kiem tra cap nhat HONG");
            TxtStatus.Text = "Khong kiem tra duoc cap nhat - nhat ky o C:\\ProgramData\\OpenCrazyArcade\\logs\\launcher";
        }
        finally
        {
            if (!IsGameRunning())
            {
                BtnStart.IsEnabled = true;
                BtnStart.Content = "▶ Bắt Đầu";
                BtnStart.Style = (Style)Resources["NostalgicOrangeButton"];
            }
        }
    }

    private const string GuideUrl = "https://opencrazyarcade.github.io/huong-dan/";

    /// <summary>
    /// ca.exe next to this launcher, or null. The only supported setup is: install the game from the original
    /// installer, then copy autoupdate.exe over it, so no other place is searched (a game found elsewhere could be
    /// someone else's install, and updates would be written into it).
    /// </summary>
    private static string FindGameExecutable()
    {
        string dir = Path.GetDirectoryName(Environment.ProcessPath);
        if (string.IsNullOrEmpty(dir)) return null;
        foreach (string name in new[] { "ca.exe", "CA.exe" })
        {
            string candidate = Path.Combine(dir, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private const string MissingGameText = "Không thấy ca.exe cạnh launcher. Hãy chép AutoUpdate.exe vào thư mục game (nơi có ca.exe) rồi chạy lại.";

    /// <summary>Tells the player where to put the launcher and offers the guide page.</summary>
    private void ShowMissingGameNotice()
    {
        var answer = MessageBox.Show(
            MissingGameText + "\n\nMở trang hướng dẫn trên opencrazyarcade.github.io?",
            "Chưa thấy game", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (answer == MessageBoxResult.Yes) OpenInDefaultBrowser(GuideUrl);
    }

    private async void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        if (IsGameRunning())
        {
            // Kill game process
            var res = MessageBox.Show("Bạn có chắc chắn muốn buộc dừng game Crazy Arcade?", "Xác nhận", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res == MessageBoxResult.Yes)
            {
                try
                {
                    if (_gameProcess != null && !_gameProcess.HasExited)
                    {
                        _gameProcess.Kill();
                    }
                    foreach (var p in Process.GetProcessesByName("CA"))
                    {
                        try { p.Kill(); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError("Error terminating game process: {Message}", ex.Message);
                }
            }
            return;
        }

        if (!_config.HasServer)
        {
            MessageBox.Show("Chưa cấu hình máy chủ. Bấm nút ⚙ để nhập địa chỉ máy chủ.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string caExe = FindGameExecutable();
        if (string.IsNullOrEmpty(caExe))
        {
            TxtStatus.Text = MissingGameText;
            ShowMissingGameNotice();
            return;
        }

        // The server may have published since the last check (push not delivered yet, or hub down): ask for its
        // version right now and update first if it differs or the last update was incomplete.
        string serverVersion = await FetchUpdateVersionAsync();
        if (!_config.AutoCheckUpdateAtStartup || _lastUpdateFailed || serverVersion == null || serverVersion != _knownUpdateVersion)
        {
            _logger.LogInformation("[UPDATE] truoc khi choi: server={Server} da-biet={Known} loi-lan-truoc={Failed} -> kiem tra",
                serverVersion ?? "(khong hoi duoc)", _knownUpdateVersion ?? "?", _lastUpdateFailed);
            if (serverVersion != null) _knownUpdateVersion = serverVersion;
            await RunUpdateIfIdleAsync();
        }

        string gameDir = Path.GetDirectoryName(caExe)!;

        // Lấy cấu hình máy chủ từ Server (SignalR / HTTP / fallback)
        TxtStatus.Text = "Đang kết nối máy chủ...";
        var endpoints = await _nmCoreManager.GetGameServerEndpointsAsync();

        // Chuẩn bị tham số dòng lệnh cho ca.exe và CAPlay (đọc qua GetCommandLineW):
        // - "loginip:xxx loginport:yyy": CA.exe's WinMain đọc trực tiếp vào server list (không cần CmdLine.txt)
        // - "-connect=xxx": CAPlay's ParseCommandLine đọc vào g_config.serverIp
        string gameArgs = $"loginip:{endpoints.LoginIp} loginport:{endpoints.LoginPort} -connect={endpoints.LoginIp}";

        // Tùy chọn: ghi CmdLine.txt dự phòng nếu thư mục cho phép ghi (silent fallback)
        try
        {
            string cmdLinePath = Path.Combine(gameDir, "CmdLine.txt");
            string cmdContent = $"loginip:{endpoints.LoginIp} loginport:{endpoints.LoginPort}";
            File.WriteAllText(cmdLinePath, cmdContent, System.Text.Encoding.ASCII);
            _logger.LogInformation("Saved fallback CmdLine.txt: {Content} at {Path}", cmdContent, cmdLinePath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("CmdLine.txt fallback skipped ({Message}) - args passed directly to process.", ex.Message);
        }

        // Cấu hình chế độ cửa sổ / toàn màn hình vào ddraw.ini
        ApplyWindowedModeSetting(gameDir, _config.WindowedMode);
        ApplyClientLogEnvironment();
        ApplyDevFlagsEnvironment();

        // 3. Khởi chạy ca.exe
        try
        {
            TxtStatus.Text = "Đang khởi động trò chơi...";

            // CAPlay is mandatory: it carries the custom resource pack (oca.idx/idd) hook and the client log.
            // A missing file is first fetched through the normal update, never run without.
            string caplayDllPath = Path.Combine(gameDir, "CAPlay.dll");
            if (!File.Exists(caplayDllPath))
            {
                _logger.LogWarning("Thieu CAPlay.dll tai '{Path}' - chay cap nhat de tai lai", caplayDllPath);
                await CheckUpdatesAsync();
            }
            if (!File.Exists(caplayDllPath))
            {
                _logger.LogError("CAPlay.dll van thieu sau cap nhat - khong khoi dong game");
                TxtStatus.Text = "Thiếu CAPlay.dll — không tải được từ máy chủ, game không thể chạy!";
                MessageBox.Show("Không tìm thấy CAPlay.dll và không tải lại được từ máy chủ.\nKiểm tra kết nối rồi bấm Bắt đầu lại.",
                    "Thiếu CAPlay.dll", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            {
                // Provenance of the dll that is about to be injected, for logs sent back by others.
                try
                {
                    var fi = new FileInfo(caplayDllPath);
                    var vi = FileVersionInfo.GetVersionInfo(caplayDllPath);
                    _logger.LogInformation("CAPlay.dll: {Path} | ban ghi {Time:yyyy-MM-dd HH:mm:ss} | {Size} bytes | version {Ver}",
                        caplayDllPath, fi.LastWriteTime, fi.Length, string.IsNullOrEmpty(vi.FileVersion) ? "?" : vi.FileVersion);
                }
                catch (Exception ex) { _logger.LogDebug("Khong doc duoc thong tin CAPlay.dll: {Message}", ex.Message); }
            }

            {
                _logger.LogInformation("Phat hien CAPlay.dll tai '{Path}'. Khoi dong CA.exe suspended va inject...", caplayDllPath);

                var si = new STARTUPINFO();
                si.cb = Marshal.SizeOf(si);
                var pi = new PROCESS_INFORMATION();

                string cmdLine = $"\"{caExe}\" {gameArgs}";
                const uint CREATE_SUSPENDED = 0x00000004;
                const uint MEM_COMMIT = 0x00001000;
                const uint MEM_RESERVE = 0x00002000;
                const uint PAGE_READWRITE = 0x04;

                bool created = CreateProcessA(
                    null,
                    cmdLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    CREATE_SUSPENDED,
                    IntPtr.Zero,
                    gameDir,
                    ref si,
                    out pi);

                bool injected = false;
                if (created && pi.hProcess != IntPtr.Zero)
                {
                    try
                    {
                        IntPtr hKernel32 = GetModuleHandleA("kernel32.dll");
                        IntPtr pLoadLibraryA = GetProcAddress(hKernel32, "LoadLibraryA");

                        byte[] dllBytes = Encoding.ASCII.GetBytes("CAPlay.dll\0");
                        IntPtr allocAddr = VirtualAllocEx(pi.hProcess, IntPtr.Zero, (uint)dllBytes.Length, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                        if (allocAddr != IntPtr.Zero)
                        {
                            WriteProcessMemory(pi.hProcess, allocAddr, dllBytes, (uint)dllBytes.Length, out _);
                            IntPtr hThread = CreateRemoteThread(pi.hProcess, IntPtr.Zero, 0, pLoadLibraryA, allocAddr, 0, out _);
                            if (hThread != IntPtr.Zero)
                            {
                                WaitForSingleObject(hThread, 5000);
                                CloseHandle(hThread);
                                injected = true;
                                _logger.LogInformation("CAPlay.dll da inject thanh cong vao CA.exe (PID: {Pid})", pi.dwProcessId);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError("Loi khi inject CAPlay.dll: {Message}", ex.Message);
                    }
                    finally
                    {
                        // Never let an un-injected ca.exe run: it would be a client without the custom pack and without logs.
                        if (injected)
                        {
                            ResumeThread(pi.hThread);
                        }
                        else
                        {
                            try { Process.GetProcessById(pi.dwProcessId).Kill(); } catch { }
                        }
                        CloseHandle(pi.hThread);
                        CloseHandle(pi.hProcess);
                    }

                    if (!injected)
                    {
                        _logger.LogError("Inject CAPlay.dll that bai - da dung ca.exe (PID {Pid})", pi.dwProcessId);
                        TxtStatus.Text = "Không nạp được CAPlay.dll vào game — xem nhật ký launcher!";
                        return;
                    }
                    _gameProcess = Process.GetProcessById(pi.dwProcessId);
                }
                else
                {
                    int err = Marshal.GetLastWin32Error();
                    _logger.LogError("CreateProcessA suspended that bai (Error: {Error}) - khong khoi dong game", err);
                    TxtStatus.Text = $"Không tạo được tiến trình game (lỗi {err})!";
                    return;
                }
            }

            if (_gameProcess != null)
            {
                _gameProcess.EnableRaisingEvents = true;
                _gameProcess.Exited += GameProcess_Exited;

                BtnStart.Content = "⏹ Buộc Thoát Game";
                BtnStart.Style = (Style)Resources["NostalgicRedButton"];
                TxtStatus.Text = "Trò chơi đang chạy...";

                // Minimize launcher when game starts
                WindowState = WindowState.Minimized;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to launch game: {Message}", ex.Message);
            MessageBox.Show($"Không thể khởi chạy game: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
            TxtStatus.Text = "Khởi chạy game thất bại!";
        }
    }

    private void GameProcess_Exited(object sender, EventArgs e)
    {
        // The surest "not playing" signal: the server ends this launcher's presence on it.
        _ = _nmCoreManager.NotifyClientExitedAsync();

        Dispatcher.Invoke(() =>
        {
            _gameProcess = null;
            BtnStart.Content = "▶ Bắt Đầu";
            BtnStart.Style = (Style)Resources["NostalgicOrangeButton"];
            TxtStatus.Text = "Sẵn sàng tham gia OpenCrazyArcade!";
            if (_updatePending) _ = RunUpdateIfIdleAsync();

            // Restore launcher window when game exits
            WindowState = WindowState.Normal;
            Activate();
            Focus();
        });
    }
}
