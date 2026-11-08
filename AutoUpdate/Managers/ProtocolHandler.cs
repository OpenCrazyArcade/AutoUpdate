using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace OpenCrazyArcade.AutoUpdate.Managers;

/// <summary>
/// The <c>opencrazyarcade://</c> link scheme. A community server's page offers
/// <c>opencrazyarcade://set-server/?src=https://server-address.example/</c>; the launcher registers itself for it under
/// HKCU (no admin rights) every time it starts, so the link follows the exe if the player moves it.
/// </summary>
internal static class ProtocolHandler
{
    public const string Scheme = "opencrazyarcade";
    private const string Key = @"Software\Classes\" + Scheme;

    public static void Register()
    {
        try
        {
            string exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            string command = $"\"{exe}\" \"%1\"";

            using (var cmdKey = Registry.CurrentUser.OpenSubKey(Key + @"\shell\open\command"))
                if (cmdKey?.GetValue(null) as string == command) return;   // already right

            using var root = Registry.CurrentUser.CreateSubKey(Key);
            root.SetValue(null, "URL:OpenCrazyArcade");
            root.SetValue("URL Protocol", "");
            using var icon = root.CreateSubKey("DefaultIcon");
            icon.SetValue(null, $"\"{exe}\",0");
            using var cmd = root.CreateSubKey(@"shell\open\command");
            cmd.SetValue(null, command);
        }
        catch (Exception ex)
        {
            // Not fatal: the player can still type the address in the config box.
            Debug.WriteLine("Dang ky giao thuc that bai: " + ex.Message);
        }
    }

    public static bool IsLink(string arg) =>
        arg != null && arg.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Understand <c>opencrazyarcade://set-server/?src=&lt;http(s) address&gt;</c> (the address may or may not be
    /// percent-encoded). Gives the server address in the form the config stores: no trailing slash, no query.
    /// Credentials in the address, other schemes and other actions are refused.
    /// </summary>
    public static bool TryParseSetServer(string link, out string serverUrl, out string error)
    {
        serverUrl = null; error = null;
        if (!Uri.TryCreate(link?.Trim(), UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            error = "Liên kết không đúng dạng opencrazyarcade://.";
            return false;
        }
        if (!uri.Host.Equals("set-server", StringComparison.OrdinalIgnoreCase))
        {
            error = $"Launcher chưa hỗ trợ thao tác \"{uri.Host}\".";
            return false;
        }

        string src = null;
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq > 0 && part[..eq].Equals("src", StringComparison.OrdinalIgnoreCase))
            {
                src = Uri.UnescapeDataString(part[(eq + 1)..]);
                break;
            }
        }
        if (string.IsNullOrWhiteSpace(src)) { error = "Liên kết thiếu địa chỉ máy chủ (src)."; return false; }

        if (!Uri.TryCreate(src.Trim(), UriKind.Absolute, out var server) ||
            (server.Scheme != Uri.UriSchemeHttp && server.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(server.Host))
        {
            error = "Địa chỉ máy chủ phải là http:// hoặc https://.";
            return false;
        }
        if (!string.IsNullOrEmpty(server.UserInfo))
        {
            error = "Địa chỉ máy chủ không được chứa tên đăng nhập hay mật khẩu.";
            return false;
        }

        serverUrl = (server.GetLeftPart(UriPartial.Authority) + server.AbsolutePath).TrimEnd('/');
        return true;
    }
}
