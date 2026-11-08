using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenCrazyArcade.AutoUpdate.Models;

namespace OpenCrazyArcade.AutoUpdate.Managers;

/// <summary>
/// The files this project ships INTO the game folder besides the launcher itself, and where each one goes.
///
/// They come from a release of the project (the project page lists a release's files, see attachments.json of the
/// release) and the launcher installs them on request; a game server has no say over them. The table is the only place
/// that says where a name is written, so neither the page nor a server can make the launcher write somewhere else.
/// Installing replaces what is there (that is the point: nmco3.dll, ehsvc.dll... are replacements for the game's own
/// files), by renaming the old file aside first so a failure leaves the working one in place.
/// </summary>
internal sealed record ClientComponent(string Name, string[] Targets, bool MustBeOurs);

internal static class ClientComponents
{
    /// <summary>Name of a release asset -> relative path(s) in the game folder. hsupdate.exe goes twice: CA.exe runs
    /// hackshield\hsupdate.exe and then hackshield\Update\autoup.exe (see docs/components-and-resources.md).</summary>
    public static readonly IReadOnlyList<ClientComponent> All =
    [
        new("nmco3.dll",    ["nmco3.dll"], MustBeOurs: true),
        new("ehsvc.dll",    [@"hackshield\ehsvc.dll"], MustBeOurs: true),
        new("hsupdate.exe", [@"hackshield\hsupdate.exe", @"hackshield\Update\autoup.exe"], MustBeOurs: true),
        new("CAPlay.dll",   ["CAPlay.dll"], MustBeOurs: false),
        new("ddraw.dll",    ["ddraw.dll"], MustBeOurs: false),   // cnc-ddraw (MIT), see THIRD-PARTY-NOTICES.md
    ];

    public static ClientComponent Find(string name) =>
        All.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the version resource says the file is ours (Nexon's/AhnLab's originals say otherwise).</summary>
    public static bool IsOurs(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            return (FileVersionInfo.GetVersionInfo(path).ProductName ?? "").StartsWith("OpenCrazyArcade", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// SHA-256 (lower-case hex) of what is installed, or null when it is missing - or when a component that goes to
    /// several places has copies that differ (then it is not "installed" as a whole).
    /// </summary>
    public static string InstalledHash(string gameDir, ClientComponent c)
    {
        if (string.IsNullOrEmpty(gameDir)) return null;
        string hash = null;
        foreach (string rel in c.Targets)
        {
            string h = HashOf(Path.Combine(gameDir, rel));
            if (h == null || (hash != null && h != hash)) return null;
            hash = h;
        }
        return hash;
    }

    /// <summary>name -> installed hash (null when missing) for every component; what the page compares with a release.</summary>
    public static Dictionary<string, string> Snapshot(string gameDir) =>
        All.ToDictionary(c => c.Name, c => InstalledHash(gameDir, c));

    private static string HashOf(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        }
        catch { return null; }
    }

    /// <summary>
    /// Download a release file into the launcher's temp folder and check it (SHA-256 when given, a PE image, ours when the
    /// component must be). Returns the staged path, or null (onError says why). Nothing reaches the game folder here.
    /// </summary>
    public static async Task<string> StageAsync(ILogger logger, ClientComponent c, string url, string sha256, Action<int> progress, Action<string> onError, CancellationToken ct = default)
    {
        LauncherConfig.EnsureDirectories();
        string staged = Path.Combine(LauncherConfig.UpdatesTempDir, $"{Guid.NewGuid():N}_{c.Name}");
        sha256 = string.IsNullOrWhiteSpace(sha256) ? null : sha256.Trim().ToLowerInvariant();
        try
        {
            string hash = await Downloader.DownloadAsync(url, staged, progress, ct);
            if (sha256 != null && hash != sha256)
            {
                logger.LogError("[COMPONENT] {Name}: SHA-256 khong khop: mong doi {Want}, nhan {Got}", c.Name, sha256, hash);
                onError($"{c.Name} tải về không khớp mã kiểm tra (SHA-256), đã huỷ.");
            }
            else if (!Downloader.LooksLikePe(staged, 10_000))
            {
                onError($"Tệp {c.Name} tải về không phải tệp thực thi hợp lệ, đã huỷ.");
            }
            else if (c.MustBeOurs && !IsOurs(staged))
            {
                onError($"Tệp {c.Name} tải về không phải của OpenCrazyArcade, đã huỷ.");
            }
            else return staged;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[COMPONENT] {Name}: tai that bai", c.Name);
            onError($"Tải {c.Name} không thành công: {ex.Message}");
        }
        try { File.Delete(staged); } catch { }
        return null;
    }

    /// <summary>
    /// Put the staged file at every target of the component. Each current file is renamed aside (allowed even while
    /// loaded) and not overwritten, so a failure leaves the working file where it was. The staged file is deleted.
    /// </summary>
    public static bool Apply(ILogger logger, ClientComponent c, string stagedPath, string gameDir, Action<string> onError)
    {
        string current = null;
        try
        {
            foreach (string rel in c.Targets)
            {
                string target = Path.Combine(gameDir, rel);
                current = target;
                ReplaceFile(stagedPath, target);
                logger.LogInformation("[COMPONENT] da cai {Target}", target);
            }
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[COMPONENT] khong ghi duoc vao {Target}", current);
            onError($"Không ghi được {c.Name} vào thư mục game ({ex.Message}). Hãy thoát game rồi thử lại.");
            return false;
        }
        finally { try { File.Delete(stagedPath); } catch { } }
    }

    private static void ReplaceFile(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string tmp = target + ".new", old = target + ".old";
        try
        {
            File.Copy(source, tmp, overwrite: true);
            if (File.Exists(old)) { try { File.Delete(old); } catch { old = $"{target}.{DateTime.UtcNow.Ticks}.old"; } }
            if (File.Exists(target)) File.Move(target, old);
            try { File.Move(tmp, target); }
            catch { if (File.Exists(old) && !File.Exists(target)) File.Move(old, target); throw; }
            try { File.Delete(old); } catch { /* still loaded by a lingering process: CleanUpLeftovers removes it next start */ }
        }
        catch { try { File.Delete(tmp); } catch { } throw; }
    }

    public static void CleanUpLeftovers(string gameDir)
    {
        foreach (var c in All)
            foreach (string rel in c.Targets)
                foreach (string ext in new[] { ".old", ".new" })
                    try { File.Delete(Path.Combine(gameDir, rel + ext)); } catch { }
    }
}
