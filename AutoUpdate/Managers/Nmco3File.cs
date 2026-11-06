using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenCrazyArcade.AutoUpdate.Models;

namespace OpenCrazyArcade.AutoUpdate.Managers;

/// <summary>
/// nmco3.dll, the messenger library CA.exe loads from the game folder. The game installs Nexon's original; ours
/// (the NMCO3 project, released with the launcher) replaces it. The launcher owns this file:
///   - a release of the project page can install any version (<see cref="StageAsync"/> + <see cref="Apply"/>);
///   - the server manifest may only provide it when it is missing or is not ours, never replace a good one
///     (see <see cref="IsOurs"/>), or it would undo the version the page installed.
/// </summary>
internal static class Nmco3File
{
    public const string FileName = "nmco3.dll";

    /// <summary>True for our nmco3.dll: the version resource says so (Nexon's original says "ClientDL ... / Nexon").</summary>
    public static bool IsOurs(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var vi = FileVersionInfo.GetVersionInfo(path);
            return (vi.ProductName ?? "").StartsWith("OpenCrazyArcade", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>"1.0.0" of our nmco3.dll in <paramref name="gameDir"/>, or null when there is none (or it is Nexon's).</summary>
    public static string InstalledVersion(string gameDir)
    {
        if (string.IsNullOrEmpty(gameDir)) return null;
        string path = Path.Combine(gameDir, FileName);
        if (!IsOurs(path)) return null;
        var v = FileVersionInfo.GetVersionInfo(path);
        return new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart).ToString();
    }

    /// <summary>
    /// Download and verify a release's nmco3.dll into the launcher's temp folder. Returns its path, or null
    /// (onError says why). Refuses a file that is not ours: a wrong asset must never reach the game folder.
    /// </summary>
    public static async Task<string> StageAsync(ILogger logger, string url, string sha256, Action<int> progress, Action<string> onError, CancellationToken ct = default)
    {
        LauncherConfig.EnsureDirectories();
        string staged = Path.Combine(LauncherConfig.UpdatesTempDir, $"{Guid.NewGuid():N}_{FileName}");
        sha256 = string.IsNullOrWhiteSpace(sha256) ? null : sha256.Trim().ToLowerInvariant();
        try
        {
            string hash = await Downloader.DownloadAsync(url, staged, progress, ct);
            if (sha256 != null && hash != sha256)
            {
                logger.LogError("[NMCO3] SHA-256 khong khop: mong doi {Want}, nhan {Got}", sha256, hash);
                onError("nmco3.dll tải về không khớp mã kiểm tra (SHA-256), đã huỷ.");
            }
            else if (!Downloader.LooksLikePe(staged, 10_000) || !IsOurs(staged))
            {
                onError("Tệp nmco3.dll tải về không phải của OpenCrazyArcade, đã huỷ.");
            }
            else return staged;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[NMCO3] tai that bai");
            onError("Tải nmco3.dll không thành công: " + ex.Message);
        }
        try { File.Delete(staged); } catch { }
        return null;
    }

    /// <summary>
    /// Put the staged file in the game folder. The current one is renamed (allowed even while loaded) rather than
    /// overwritten, so a failure leaves the working file where it was.
    /// </summary>
    public static bool Apply(ILogger logger, string stagedPath, string gameDir, Action<string> onError)
    {
        string target = Path.Combine(gameDir, FileName), tmp = target + ".new", old = target + ".old";
        try
        {
            File.Copy(stagedPath, tmp, overwrite: true);
            if (File.Exists(old)) { try { File.Delete(old); } catch { old = $"{target}.{DateTime.UtcNow.Ticks}.old"; } }
            if (File.Exists(target)) File.Move(target, old);
            try { File.Move(tmp, target); }
            catch { if (File.Exists(old) && !File.Exists(target)) File.Move(old, target); throw; }
            try { File.Delete(old); } catch { /* still loaded by a lingering process: CleanUpLeftovers removes it next start */ }
            logger.LogInformation("[NMCO3] da cai {Target}", target);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[NMCO3] khong ghi duoc vao {Target}", target);
            try { File.Delete(tmp); } catch { }
            onError($"Không ghi được nmco3.dll vào thư mục game ({ex.Message}). Hãy thoát game rồi thử lại.");
            return false;
        }
        finally { try { File.Delete(stagedPath); } catch { } }
    }

    public static void CleanUpLeftovers(string gameDir)
    {
        foreach (string ext in new[] { ".old", ".new" })
            try { File.Delete(Path.Combine(gameDir, FileName + ext)); } catch { }
    }
}
