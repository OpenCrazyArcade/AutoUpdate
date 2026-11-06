using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenCrazyArcade.AutoUpdate.Models;

namespace OpenCrazyArcade.AutoUpdate.Managers;

/// <summary>
/// What a game server may put on the player's machine: extra asset packs (name.idx + name.idd), nothing else.
///
/// GET {server}/api/updates/resources describes them. They are kept in a folder of their own beside ca.exe
/// (<see cref="FolderName"/>) which this class owns: files are written only there, only with the two data extensions,
/// only with a plain file name, and the folder is made to match the manifest (files the server dropped are deleted).
/// When the player switches to another server the folder is emptied first, so one server's packs never leak into the
/// next. The server never sees a path outside that folder and cannot ship code.
///
/// After a successful sync the launcher writes <c>from-oca\load-order.txt</c>, one line per pack, "name seed", in the
/// order the server listed them (a later pack wins over an earlier one). CAPlay reads that file and attaches the packs;
/// it never parses anything that came from the network. See docs/server-resources.md.
/// </summary>
internal static class ServerResources
{
    public const string FolderName = "from-oca";
    public const string LoadOrderFile = "load-order.txt";
    private const string ServerMarker = ".server";
    public const int SupportedSchema = 1;
    private const long MaxFileBytes = 2L * 1024 * 1024 * 1024;

    private static readonly Regex PackName = new("^[A-Za-z0-9_-]{1,32}$", RegexOptions.Compiled);
    private static readonly Regex Sha256Hex = new("^[0-9a-fA-F]{64}$", RegexOptions.Compiled);

    internal sealed record ResourceManifest(
        [property: JsonPropertyName("schema")] int Schema,
        [property: JsonPropertyName("minLauncher")] string MinLauncher,
        [property: JsonPropertyName("packs")] List<ResourcePack> Packs);

    internal sealed record ResourcePack(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("seed")] int Seed,
        [property: JsonPropertyName("files")] List<ResourceFile> Files);

    internal sealed record ResourceFile(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("sha256")] string Sha256,
        [property: JsonPropertyName("url")] string Url);

    public enum Outcome { NotOffered, Unsupported, Synced, Failed }

    public sealed record Result(Outcome Outcome, int Downloaded, int Removed, string Message);

    public static string FolderPath(string gameDir) => Path.Combine(gameDir, FolderName);

    /// <summary>Checks a manifest. Returns the problem, or null when every pack and file is acceptable.</summary>
    public static string Validate(ResourceManifest m)
    {
        if (m?.Packs == null) return "manifest has no packs";
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in m.Packs)
        {
            if (p?.Name == null || !PackName.IsMatch(p.Name)) return $"bad pack name '{p?.Name}'";
            if (!names.Add(p.Name)) return $"pack '{p.Name}' listed twice";
            var want = new[] { p.Name + ".idx", p.Name + ".idd" };
            if (p.Files == null || p.Files.Count != 2
                || !want.All(w => p.Files.Any(f => string.Equals(f.Path, w, StringComparison.OrdinalIgnoreCase))))
                return $"pack '{p.Name}' must list exactly {want[0]} and {want[1]}";
            foreach (var f in p.Files)
            {
                if (f.Size < 0 || f.Size > MaxFileBytes) return $"{f.Path}: size {f.Size} out of range";
                if (f.Sha256 == null || !Sha256Hex.IsMatch(f.Sha256)) return $"{f.Path}: bad sha256";
                if (string.IsNullOrWhiteSpace(f.Url)) return $"{f.Path}: no url";
            }
        }
        return null;
    }

    public static async Task<Result> SyncAsync(ILogger logger, HttpClient http, string baseUrl, string gameDir, Version launcherVersion, CancellationToken ct = default)
    {
        string folder = FolderPath(gameDir);

        ResourceManifest manifest;
        try
        {
            using var resp = await http.GetAsync($"{baseUrl}/api/updates/resources", ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                logger.LogInformation("[RESOURCES] may chu khong co /api/updates/resources");
                ClearFolder(logger, folder);
                return new Result(Outcome.NotOffered, 0, 0, null);
            }
            resp.EnsureSuccessStatusCode();
            manifest = await resp.Content.ReadFromJsonAsync<ResourceManifest>(cancellationToken: ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[RESOURCES] khong lay duoc manifest");
            return new Result(Outcome.Failed, 0, 0, "Không lấy được danh sách tài nguyên máy chủ: " + ex.Message);
        }

        if (manifest == null) return new Result(Outcome.Failed, 0, 0, "Danh sách tài nguyên máy chủ rỗng hoặc sai định dạng.");

        // A server newer than this launcher: do not guess. Leave what is there, say so.
        if (manifest.Schema > SupportedSchema
            || (Version.TryParse(manifest.MinLauncher, out var min) && min > launcherVersion))
        {
            logger.LogWarning("[RESOURCES] may chu doi schema {Schema} / launcher >= {Min}, launcher nay la {Mine}",
                manifest.Schema, manifest.MinLauncher, launcherVersion);
            return new Result(Outcome.Unsupported, 0, 0,
                $"Máy chủ này cần launcher mới hơn (≥ {manifest.MinLauncher ?? "?"}) để tải tài nguyên bổ sung. Hãy cập nhật launcher ở tab Trang dự án.");
        }

        string problem = Validate(manifest);
        if (problem != null)
        {
            logger.LogError("[RESOURCES] manifest khong hop le: {Problem}", problem);
            return new Result(Outcome.Failed, 0, 0, "Danh sách tài nguyên máy chủ không hợp lệ: " + problem);
        }

        // Another server than last time: its packs are not ours to keep.
        Directory.CreateDirectory(folder);
        string markerPath = Path.Combine(folder, ServerMarker);
        string marker = baseUrl.TrimEnd('/').ToLowerInvariant();
        string previous = File.Exists(markerPath) ? File.ReadAllText(markerPath).Trim() : null;
        if (previous != marker)
        {
            logger.LogInformation("[RESOURCES] doi may chu ({Old} -> {New}): don {Folder}", previous ?? "(chua co)", marker, folder);
            ClearFolder(logger, folder);
            Directory.CreateDirectory(folder);
            File.WriteAllText(markerPath, marker);
        }

        int downloaded = 0, removed = 0;
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var good = new List<ResourcePack>();
        foreach (var pack in manifest.Packs)
        {
            bool ok = true;
            foreach (var f in pack.Files)
            {
                string name = Path.GetFileName(f.Path);          // validated: pack name + .idx/.idd
                wanted.Add(name);
                string local = Path.Combine(folder, name);
                try
                {
                    if (HashOf(local) == f.Sha256.ToLowerInvariant()) continue;
                    string url = f.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? f.Url : baseUrl + f.Url;
                    string tmp = Path.Combine(folder, $".{Guid.NewGuid():N}.part");
                    try
                    {
                        using (var r = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
                        {
                            r.EnsureSuccessStatusCode();
                            await using var s = await r.Content.ReadAsStreamAsync(ct);
                            await using var o = File.Create(tmp);
                            await s.CopyToAsync(o, ct);
                        }
                        if (HashOf(tmp) != f.Sha256.ToLowerInvariant())
                            throw new InvalidDataException("SHA-256 khong khop");
                        File.Move(tmp, local, overwrite: true);
                        downloaded++;
                        logger.LogInformation("[RESOURCES] {Name}: da tai ({Size} byte)", name, f.Size);
                    }
                    finally { try { File.Delete(tmp); } catch { } }
                }
                catch (Exception ex)
                {
                    ok = false;
                    logger.LogError(ex, "[RESOURCES] {Name}: KHONG cap nhat duoc", name);
                }
            }
            if (ok) good.Add(pack);
        }

        // Match the manifest: whatever the server no longer lists goes (only inside our own folder).
        foreach (string file in Directory.EnumerateFiles(folder))
        {
            string name = Path.GetFileName(file);
            if (name == ServerMarker || name == LoadOrderFile || wanted.Contains(name)) continue;
            try { File.Delete(file); removed++; logger.LogInformation("[RESOURCES] {Name}: da xoa (may chu khong phat nua)", name); }
            catch (Exception ex) { logger.LogWarning(ex, "[RESOURCES] khong xoa duoc {Name}", name); }
        }

        // What CAPlay reads: only packs whose two files are verified, in the server's order.
        var sb = new StringBuilder();
        foreach (var p in good) sb.Append(p.Name).Append(' ').Append(p.Seed).Append('\n');
        File.WriteAllText(Path.Combine(folder, LoadOrderFile), sb.ToString(), new UTF8Encoding(false));

        bool failed = good.Count != manifest.Packs.Count;
        string message = failed ? $"Có {manifest.Packs.Count - good.Count} gói tài nguyên của máy chủ không cập nhật được." : null;
        return new Result(failed ? Outcome.Failed : Outcome.Synced, downloaded, removed, message);
    }

    private static string HashOf(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        }
        catch { return null; }
    }

    /// <summary>Empties the folder (only our own); the launcher owns everything in it.</summary>
    public static void ClearFolder(ILogger logger, string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch (Exception ex) { logger.LogWarning(ex, "[RESOURCES] khong don duoc {Folder}", folder); }
    }
}
