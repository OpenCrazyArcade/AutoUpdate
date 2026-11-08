using System;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenCrazyArcade.Ipc;

namespace OpenCrazyArcade.NMCO;

public static class Exports
{
    private static NamedPipeClientStream s_pipeClient;
    private static StreamWriter s_pipeWriter;
    private static StreamReader s_pipeReader;
    private static readonly Lock s_pipeLock = new();

    private static nint s_hWnd = nint.Zero;
    private static uint s_msg = 0x403;
    // Placeholders until a login answers. AutoUpdate replaces both from the server's reply —
    // NMCOGetMyIdCode handing out a constant would make every player the same person to anything
    // that keys by id.
    private static long s_myIdCode = 1000001L;
    private static string s_myScreenName = "CrazyPlayer";
    private static string s_myPosition = "Lobby";
    private static int s_myStatus = 1;

    [DllImport("user32.dll", EntryPoint = "PostMessageA")]
    private static extern bool PostMessageA(nint hWnd, uint msg, nint wParam, nint lParam);

    static Exports()
    {
        Log("nmco3.dll (.NET 10 Native AOT) loaded.");
    }

    /// <summary>Length of a C string argument, for diagnostics that must not print secrets.</summary>
    private static string Len(nint p) =>
        p == nint.Zero ? "null" : (Marshal.PtrToStringAnsi(p) ?? "").Length.ToString();

    // One file per process under %ProgramData%\OpenCrazyArcade\logs\nmco3, named like the other logs:
    // "[<host>] <yyyyMMdd_HHmmss>.log". It used to be a single ever-growing nmco3.log next to the
    // game (127 MB), because the game calls NMCOChangeMyInfo dozens of times a second in a match.
    private const long MaxLogBytes = 8 * 1024 * 1024;
    private const int KeepLogFiles = 20;
    private static readonly Lock s_logLock = new();
    private static string s_logPath;
    private static long s_logBytes;
    private static bool s_logFull;
    private static string s_lastMsg;
    private static int s_repeats;
    private static long s_repeatSince;

    private static string LogDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "OpenCrazyArcade", "logs", "nmco3");

    private static void Log(string msg)
    {
        try
        {
            lock (s_logLock)
            {
                if (s_logFull) return;

                // An identical line repeated back to back is counted, not written; the count is
                // written when something else arrives or every 10 s.
                long now = Environment.TickCount64;
                if (msg == s_lastMsg)
                {
                    if (s_repeats == 0) s_repeatSince = now;
                    s_repeats++;
                    if (now - s_repeatSince < 10_000) return;
                }

                if (s_repeats > 0)
                {
                    WriteLine($"  (dòng trên lặp thêm {s_repeats} lần)");
                    s_repeats = 0;
                    if (msg == s_lastMsg) { s_repeatSince = now; return; }
                }

                s_lastMsg = msg;
                WriteLine(msg);
            }
        }
        catch { }
    }

    private static void WriteLine(string msg)
    {
        if (s_logPath == null)
        {
            string dir = LogDir;
            Directory.CreateDirectory(dir);
            try
            {
                foreach (var f in new DirectoryInfo(dir).GetFiles("*.log")
                             .OrderByDescending(f => f.LastWriteTimeUtc).Skip(KeepLogFiles))
                {
                    try { f.Delete(); } catch { }
                }
            }
            catch { }
            s_logPath = Path.Combine(dir, $"[{Environment.MachineName}] {DateTime.Now:yyyyMMdd_HHmmss}.log");
        }

        string line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n";
        File.AppendAllText(s_logPath, line);
        s_logBytes += line.Length;
        if (s_logBytes > MaxLogBytes)
        {
            File.AppendAllText(s_logPath, "[giới hạn dung lượng] ngừng ghi để tránh đầy ổ đĩa\n");
            s_logFull = true;
        }
    }

    private static void EnsurePipeConnected()
    {
        lock (s_pipeLock)
        {
            if (s_pipeClient != null && s_pipeClient.IsConnected) return;

            try
            {
                s_pipeClient?.Dispose();
                s_pipeClient = new NamedPipeClientStream(".", NmcoPipe.Name, PipeDirection.InOut, PipeOptions.Asynchronous);
                s_pipeClient.Connect(1000); // 1s timeout
                s_pipeWriter = new StreamWriter(s_pipeClient, Encoding.UTF8) { AutoFlush = true };
                s_pipeReader = new StreamReader(s_pipeClient, Encoding.UTF8);
                Log("Connected to AutoUpdate NamedPipe.");
            }
            catch (Exception ex)
            {
                Log($"Failed to connect to NamedPipe: {ex.Message}");
            }
        }
    }

    /// <summary>One request line out, one reply line back. Null when the launcher is not there or did not answer.</summary>
    private static string SendLine(string json)
    {
        try
        {
            EnsurePipeConnected();
            lock (s_pipeLock)
            {
                if (s_pipeClient == null || !s_pipeClient.IsConnected) return null;

                s_pipeWriter.WriteLine(json);
                return s_pipeReader.ReadLine();
            }
        }
        catch (Exception ex)
        {
            Log($"SendPipeMessage error: {ex.Message}");
            return null;
        }
    }

    /// <summary>A typed request (see OpenCrazyArcade.Ipc): the serializer, not string pasting, builds the line and does the escaping.</summary>
    private static string Send<TPayload>(string action, TPayload payload,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<NmcoRequest<TPayload>> typeInfo) =>
        SendLine(JsonSerializer.Serialize(new NmcoRequest<TPayload>(action, payload), typeInfo));

    // ─────────────────────────────────────────────────────────────────────────────
    // 30 C-DECL EXPORTS FOR CA.EXE
    // ─────────────────────────────────────────────────────────────────────────────

    [UnmanagedCallersOnly(EntryPoint = "NMCOInit", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int NMCOInit(int a1, uint a2, nint a3, int a4)
    {
        Log($"NMCOInit({a1}, {a2}, {a3}, {a4})");
        Send(NmcoAction.Init, new InitPayload("single"), NmcoJson.Default.NmcoRequestInitPayload);
        return 1;
    }

    [UnmanagedCallersOnly(EntryPoint = "NMCOInitWithMultiServer", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int NMCOInitWithMultiServer(int a1, int a2, int a3, int a4)
    {
        Log($"NMCOInitWithMultiServer({a1}, {a2}, {a3}, {a4})");
        Send(NmcoAction.InitWithMultiServer, new InitWithMultiServerPayload(a1, a2, a3, a4), NmcoJson.Default.NmcoRequestInitWithMultiServerPayload);
        return 1;
    }

    [UnmanagedCallersOnly(EntryPoint = "NMCORegisterCallbackMessage", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int NMCORegisterCallbackMessage(nint hWnd, uint msg)
    {
        Log($"NMCORegisterCallbackMessage(hWnd=0x{hWnd:X}, msg=0x{msg:X})");
        s_hWnd = hWnd;
        s_msg = msg;

        // Post NMCO Connected event (wParam = 1) to notify CA.exe that NMCO server is ready
        Task.Run(async () =>
        {
            await Task.Delay(100);
            if (s_hWnd != nint.Zero)
            {
                Log($"Posting callback event: hWnd=0x{s_hWnd:X}, msg=0x{s_msg:X}, wParam=1");
                PostMessageA(s_hWnd, s_msg, 1, 0);
            }
        });

        return 1;
    }

    [UnmanagedCallersOnly(EntryPoint = "NMCOLogin", CallConvs = new[] { typeof(CallConvCdecl) })]
    /// <summary>
    /// CA.exe calls this as NMCOLogin(id, idLowered, password, 1, 0, 0) — sub_9A9AB9 @0x9A9AB9.
    ///
    /// The password is the THIRD argument, not the second. Both of the first two come from
    /// sub_9A9410, the helper that appends "@vina.bnb", and the second is only that same id run
    /// through sub_990570 (a romanisation-aware lowercaser). Reading the second as the password
    /// sent the server "account@vina.bnb" as the secret for user acc2 — which the server duly
    /// rejected, for every account, with no way to tell from this side that the string was wrong.
    /// It was caught by logging the SHAPE of what arrived: 13 characters when the player typed 4.
    ///
    /// The third argument is the typed password, or — when the player leaves the box empty — one
    /// the client derives from the id itself (sub_9A9285, a keyed scramble into a 93-character
    /// alphabet). Either way it is what the server must check.
    /// </summary>
    public static int NMCOLogin(nint id, nint idLowered, nint pass, int a4, int a5, int a6)
    {
        string username = Marshal.PtrToStringAnsi(id) ?? "";
        string password = Marshal.PtrToStringAnsi(pass) ?? "";

        // Lengths only, never the values: enough to spot an argument mix-up like the one above,
        // and this file is written in plain text next to the game.
        Log($"NMCOLogin(user='{username}') arg1={Len(idLowered)} arg2={Len(pass)}");

        if (!string.IsNullOrEmpty(username))
        {
            s_myScreenName = username.Contains('@') ? username[..username.IndexOf('@')] : username;
        }

        string reply = Send(NmcoAction.Login, new LoginPayload(username, password), NmcoJson.Default.NmcoRequestLoginPayload);

        ApplyLoginReply(reply);
        return 1;
    }

    /// <summary>
    /// Takes the identity out of AutoUpdate's reply so the getters below answer with the real
    /// account rather than the placeholders.
    ///
    /// A missing or unreadable reply leaves the placeholders in place on purpose: the return value
    /// of NMCOLogin is unchanged either way, because the client decides success from the callback
    /// event, not from this.
    /// </summary>
    private static void ApplyLoginReply(string reply)
    {
        if (string.IsNullOrEmpty(reply)) { Log("login reply: none"); return; }

        try
        {
            // Source-generated metadata (OpenCrazyArcade.Ipc): this assembly is Native AOT, where reflection-based
            // serialization would be trimmed away.
            var login = JsonSerializer.Deserialize(reply, NmcoJson.Default.LoginReply);
            if (login == null) { Log($"login reply unreadable: {reply}"); return; }

            if (login.IdCode > 0) s_myIdCode = login.IdCode;
            if (!string.IsNullOrEmpty(login.ScreenName)) s_myScreenName = login.ScreenName;

            Log($"login reply: success={login.Success} idCode={s_myIdCode} screenName='{s_myScreenName}'");
        }
        catch (Exception ex)
        {
            Log($"login reply unreadable ({ex.Message}): {reply}");
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "NMCOLogout", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int NMCOLogout()
    {
        Log("NMCOLogout()");
        Send(NmcoAction.Logout, new EmptyPayload(), NmcoJson.Default.NmcoRequestEmptyPayload);
        return 1;
    }

    [UnmanagedCallersOnly(EntryPoint = "NMCOGetMyIdCode", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static long NMCOGetMyIdCode()
    {
        Log($"NMCOGetMyIdCode() -> {s_myIdCode}");
        return s_myIdCode;
    }

    [UnmanagedCallersOnly(EntryPoint = "NMCOGetMyScreenName", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int NMCOGetMyScreenName(nint dest)
    {
        Log($"NMCOGetMyScreenName() -> '{s_myScreenName}'");
        if (dest != nint.Zero)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(s_myScreenName + "\0");
            Marshal.Copy(bytes, 0, dest, bytes.Length);
        }
        return 1;
    }

    [UnmanagedCallersOnly(EntryPoint = "NMCOGetMyPosition", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int NMCOGetMyPosition(nint dest)
    {
        Log($"NMCOGetMyPosition() -> '{s_myPosition}'");
        if (dest != nint.Zero)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(s_myPosition + "\0");
            Marshal.Copy(bytes, 0, dest, bytes.Length);
        }
        return 1;
    }

    [UnmanagedCallersOnly(EntryPoint = "NMCOGetMyStatus", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int NMCOGetMyStatus()
    {
        Log($"NMCOGetMyStatus() -> {s_myStatus}");
        return s_myStatus;
    }

    [UnmanagedCallersOnly(EntryPoint = "NMCOGetFriendList", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int NMCOGetFriendList(int a1, int a2, int a3)
    {
        Log($"NMCOGetFriendList({a1}, {a2}, {a3})");
        return 1;
    }

    [UnmanagedCallersOnly(EntryPoint = "NMCOGetIdCodeFromIdx", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static long NMCOGetIdCodeFromIdx(int a1)
    {
        Log($"NMCOGetIdCodeFromIdx({a1})");
        return 0L;
    }

    [UnmanagedCallersOnly(EntryPoint = "NMCOGetFriendInfo", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int NMCOGetFriendInfo(int a1, int a2, int a3, int a4)
    {
        Log($"NMCOGetFriendInfo({a1}, {a2}, {a3}, {a4})");
        return 1;
    }

    [UnmanagedCallersOnly(EntryPoint = "NMCOAddNewFriend", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int NMCOAddNewFriend(nint source, nint a2, nint a3)
    {
        Log("NMCOAddNewFriend()");
        return 1;
    }

    [UnmanagedCallersOnly(EntryPoint = "NMCOConfirmAddFriend", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int NMCOConfirmAddFriend(nint source, int a2, nint a3)
    {
        Log("NMCOConfirmAddFriend()");
        return 1;
    }

    [UnmanagedCallersOnly(EntryPoint = "NMCOChangeMyInfo", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int NMCOChangeMyInfo(nint source, nint a2, nint a3, int a4, int a5)
    {
        // The client wraps this in three helpers (sub_9A95CB / sub_9A9611 / sub_9A9192), each
        // setting ONE thing: a string in slot a2, a string in slot a3, or the integer a4. Which
        // slot is which (screen name? position text? status code?) is what this line is for -
        // it is logged to find out what session state the game pushes here. Strings are read only
        // when non-null and cut at 120 chars; identical consecutive lines are collapsed by Log().
        static string Str(nint p)
        {
            if (p == nint.Zero) return "null";
            try
            {
                string s = Marshal.PtrToStringAnsi(p) ?? "";
                return "'" + (s.Length > 120 ? s[..120] + "…" : s) + "'";
            }
            catch { return "?"; }
        }
        Log($"NMCOChangeMyInfo(a1=0x{source:X}, a2={Str(a2)}, a3={Str(a3)}, a4={a4}, a5={a5})");
        return 1;
    }

    [UnmanagedCallersOnly(EntryPoint = "NMCOBlockUser", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int NMCOBlockUser(int a1, int a2, int a3)
    {
        Log("NMCOBlockUser()");
        return 1;
    }

    [UnmanagedCallersOnly(EntryPoint = "NMCODeleteUser", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int NMCODeleteUser(int a1, int a2, int a3)
    {
        Log("NMCODeleteUser()");
        return 1;
    }

    [UnmanagedCallersOnly(EntryPoint = "NMCOCreateChatSession", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int NMCOCreateChatSession(int a1, int a2)
    {
        Log("NMCOCreateChatSession()");
        return 1;
    }

    [UnmanagedCallersOnly(EntryPoint = "NMCOGetServerMessage", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int NMCOGetServerMessage(int a1)
    {
        Log("NMCOGetServerMessage()");
        return 1;
    }

    [UnmanagedCallersOnly(EntryPoint = "NMCOGetAddFriendConfirmMessage", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int NMCOGetAddFriendConfirmMessage(nint source, int a2)
    {
        Log("NMCOGetAddFriendConfirmMessage()");
        return 1;
    }

    [UnmanagedCallersOnly(EntryPoint = "NMCOSendNoteMsg", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int NMCOSendNoteMsg(int a1)
    {
        Log("NMCOSendNoteMsg()");
        return 1;
    }

    [UnmanagedCallersOnly(EntryPoint = "NMCOGetNoteBox", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int NMCOGetNoteBox(int a1, int a2, int a3, int a4, int a5)
    {
        Log("NMCOGetNoteBox()");
        return 1;
    }

    [UnmanagedCallersOnly(EntryPoint = "NMCOProcessNote", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int NMCOProcessNote(int a1, int a2)
    {
        Log("NMCOProcessNote()");
        return 1;
    }

    [UnmanagedCallersOnly(EntryPoint = "NMGSRegisterCallbackMsg", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int NMGSRegisterCallbackMsg(int a1, int a2, int a3)
    {
        Log("NMGSRegisterCallbackMsg()");
        return 1;
    }

    [UnmanagedCallersOnly(EntryPoint = "NMGSResetCallbackAndWantClose", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int NMGSResetCallbackAndWantClose(int a1)
    {
        Log("NMGSResetCallbackAndWantClose()");
        return 1;
    }

    [UnmanagedCallersOnly(EntryPoint = "NMGSInviteUser", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int NMGSInviteUser(int a1, int a2, int a3)
    {
        Log("NMGSInviteUser()");
        return 1;
    }

    [UnmanagedCallersOnly(EntryPoint = "NMGSGetInviteCandidateList", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int NMGSGetInviteCandidateList(int a1, nint a2)
    {
        Log("NMGSGetInviteCandidateList()");
        if (a2 != nint.Zero)
        {
            Marshal.WriteInt32(a2, 0); // 0 candidates
        }
        return 1;
    }

    [UnmanagedCallersOnly(EntryPoint = "NMGSGetMemberList", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int NMGSGetMemberList(int a1, nint a2)
    {
        Log("NMGSGetMemberList()");
        if (a2 != nint.Zero)
        {
            Marshal.WriteInt32(a2, 0); // 0 members
        }
        return 1;
    }

    [UnmanagedCallersOnly(EntryPoint = "NMCSGetChatMessage", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int NMCSGetChatMessage(int a1, int a2)
    {
        Log("NMCSGetChatMessage()");
        return 0;
    }

    [UnmanagedCallersOnly(EntryPoint = "NMCSSendChatMessage", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int NMCSSendChatMessage(int a1, nint a2)
    {
        Log("NMCSSendChatMessage()");
        return 1;
    }
}
