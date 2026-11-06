using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace OpenCrazyArcade.AutoUpdate.Managers;

/// <summary>
/// Replaces the running autoupdate.exe by the exe at a URL - a newer release or an older one. The launcher never
/// looks for updates by itself: the project page (the first tab) lists the releases and asks for one.
///
/// A running exe cannot be overwritten but it can be renamed, so: download "autoupdate.exe.new" (SHA-256 checked
/// when the page gave one), rename the running file to "autoupdate.exe.old", rename ".new" to the real name, start
/// it with <c>--after-update</c> and exit. The new instance waits for this one to release the single-instance
/// mutex, then deletes the ".old" file.
/// </summary>
internal sealed class SelfUpdater(ILogger<SelfUpdater> logger)
{
    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);

    /// <summary>"1.0.0" - what the page shows and what the confirmation says.</summary>
    public static string CurrentVersionText => CurrentVersion.ToString(3);

    /// <summary>
    /// Download, verify and swap in the exe at <paramref name="url"/>, then start it. True when the new exe was
    /// started and this process should exit; false (nothing changed) when anything failed - onError says what.
    /// </summary>
    public async Task<bool> InstallAsync(string url, string sha256, Action<int> progress, Action<string> onError, CancellationToken ct = default)
    {
        string exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) { onError("Không biết đường dẫn của launcher."); return false; }
        string newPath = exe + ".new";
        string oldPath = exe + ".old";
        sha256 = string.IsNullOrWhiteSpace(sha256) ? null : sha256.Trim().ToLowerInvariant();

        try
        {
            // 1. Download beside the exe (same volume, so the renames are instant), hashing as it arrives.
            string hash;
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("OpenCrazyArcade-AutoUpdate/" + CurrentVersionText);
                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? 0;

                using var sha = SHA256.Create();
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = new FileStream(newPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                var buf = new byte[81920];
                long done = 0; int last = -1, n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    sha.TransformBlock(buf, 0, n, null, 0);
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    if (total > 0 && (int)(done * 100 / total) != last) progress?.Invoke(last = (int)(done * 100 / total));
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                hash = Convert.ToHexString(sha.Hash).ToLowerInvariant();
            }
            catch (UnauthorizedAccessException)
            {
                onError($"Không ghi được vào thư mục của launcher ({Path.GetDirectoryName(exe)}).");
                SafeDelete(newPath);
                return false;
            }

            // 2. Verify before touching anything that works.
            if (sha256 != null && hash != sha256)
            {
                logger.LogError("[SELFUPDATE] SHA-256 khong khop: mong doi {Want}, nhan {Got}", sha256, hash);
                SafeDelete(newPath);
                onError("Tệp tải về không khớp mã kiểm tra (SHA-256), đã huỷ cập nhật.");
                return false;
            }
            using (var fs = File.OpenRead(newPath))
                if (fs.Length < 100_000 || fs.ReadByte() != 'M' || fs.ReadByte() != 'Z')
                {
                    SafeDelete(newPath);
                    onError("Tệp tải về không phải chương trình hợp lệ.");
                    return false;
                }

            // 3. Swap. The old file may be left over from a failed clean-up: take a fresh name if it is still locked.
            if (File.Exists(oldPath))
            {
                try { File.Delete(oldPath); }
                catch { oldPath = $"{exe}.{DateTime.UtcNow.Ticks}.old"; }
            }
            File.Move(exe, oldPath);
            try
            {
                File.Move(newPath, exe);
            }
            catch
            {
                File.Move(oldPath, exe);   // put the working one back
                throw;
            }

            // 4. Start the new one; it waits for us to let go of the mutex.
            Process.Start(new ProcessStartInfo(exe, "--after-update") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! });
            logger.LogInformation("[SELFUPDATE] da thay {Cur} bang {Url}, khoi dong ban moi", CurrentVersionText, url);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[SELFUPDATE] cap nhat that bai");
            SafeDelete(newPath);
            onError("Cập nhật launcher không thành công: " + ex.Message);
            return false;
        }
    }

    /// <summary>Called by the instance that has just started: the files an update leaves behind can go now.</summary>
    public static void CleanUpLeftovers()
    {
        _ = Task.Run(async () =>
        {
            string exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            string dir = Path.GetDirectoryName(exe)!, name = Path.GetFileName(exe);
            for (int attempt = 0; attempt < 10; attempt++)
            {
                bool left = false;
                try
                {
                    foreach (var f in Directory.EnumerateFiles(dir, name + ".*")
                                 .Where(f => f.EndsWith(".old", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".new", StringComparison.OrdinalIgnoreCase)))
                    {
                        try { File.Delete(f); } catch { left = true; }   // the old process may still be exiting
                    }
                }
                catch { return; }
                if (!left) return;
                await Task.Delay(1000);
            }
        });
    }

    private static void SafeDelete(string path) { try { File.Delete(path); } catch { } }
}
