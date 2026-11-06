using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace OpenCrazyArcade.AutoUpdate.Managers;

/// <summary>
/// Brings the client files of the game folder (<see cref="ClientComponents"/>) to the version of THIS launcher.
///
/// A launcher of version X carries the release v X of the project: when it starts it reads that release's
/// attachments.json, compares the SHA-256 of each file with what is installed, and downloads and installs the ones
/// that differ or are missing. That is what makes "copy only AutoUpdate.exe into the game folder" enough.
///
/// It only follows the launcher's OWN version: it never looks for a newer launcher (installing any version, older
/// ones included, stays the project page's job). The download address is built here from a fixed repository and the
/// launcher's own tag; attachments.json supplies only names and hashes, so a doctored file cannot point elsewhere.
/// A release without attachments.json (1.0.x) has nothing to sync.
/// </summary>
internal static class ComponentSync
{
    public const string ReleaseBase = "https://github.com/OpenCrazyArcade/AutoUpdate/releases/download";
    private static readonly Regex Sha256Hex = new("^[0-9a-fA-F]{64}$", RegexOptions.Compiled);

    internal sealed record Attachments(
        [property: JsonPropertyName("schema")] int Schema,
        [property: JsonPropertyName("release")] string Release,
        [property: JsonPropertyName("files")] List<AttachedFile> Files);

    internal sealed record AttachedFile(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("sha256")] string Sha256);

    public enum Outcome { UpToDate, Synced, NoAttachments, Unsupported, Failed }

    public sealed record Result(Outcome Outcome, int Installed, string Message);

    /// <summary>True when something is worth a look at start: the version changed since the last sync, or a file is missing.</summary>
    public static bool NeedsSync(string syncedFor, string version, string gameDir)
    {
        if (syncedFor != version) return true;
        foreach (var c in ClientComponents.All)
            if (ClientComponents.InstalledHash(gameDir, c) == null) return true;
        return false;
    }

    public static async Task<Result> SyncAsync(ILogger logger, HttpClient http, string releaseBase, string version, string gameDir,
        Action<string> status = null, CancellationToken ct = default)
    {
        string tag = "v" + version;
        Attachments att;
        try
        {
            using var resp = await http.GetAsync($"{releaseBase}/{tag}/attachments.json", ct);
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                logger.LogInformation("[COMPONENTS] {Tag} khong co attachments.json: khong co gi de dong bo", tag);
                return new Result(Outcome.NoAttachments, 0, null);
            }
            resp.EnsureSuccessStatusCode();
            att = await resp.Content.ReadFromJsonAsync<Attachments>(cancellationToken: ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[COMPONENTS] khong lay duoc attachments.json cua {Tag}", tag);
            return new Result(Outcome.Failed, 0, "Không lấy được danh sách tệp của bản " + tag + ": " + ex.Message);
        }

        if (att == null || att.Files == null) return new Result(Outcome.Failed, 0, "attachments.json của " + tag + " rỗng hoặc sai định dạng.");
        if (att.Schema != 1) return new Result(Outcome.Unsupported, 0, $"attachments.json của {tag} dùng định dạng mới hơn launcher hiểu.");
        if (att.Release != version) return new Result(Outcome.Failed, 0, $"attachments.json nói bản {att.Release}, không phải {version}.");

        int installed = 0;
        var errors = new List<string>();
        foreach (var f in att.Files)
        {
            var component = ClientComponents.Find(f.Name);
            if (component == null) continue;                                   // AutoUpdate.exe, SHA256SUMS.txt, newer names
            if (f.Sha256 == null || !Sha256Hex.IsMatch(f.Sha256)) { errors.Add($"{f.Name}: mã SHA-256 không hợp lệ"); continue; }
            if (ClientComponents.InstalledHash(gameDir, component) == f.Sha256.ToLowerInvariant()) continue;

            status?.Invoke($"Đang tải {component.Name}...");
            string staged = await ClientComponents.StageAsync(logger, component, $"{releaseBase}/{tag}/{component.Name}", f.Sha256,
                pct => status?.Invoke($"Đang tải {component.Name}... {pct}%"), e => errors.Add(e), ct);
            if (staged == null) continue;
            if (ClientComponents.Apply(logger, component, staged, gameDir, e => errors.Add(e))) installed++;
        }

        if (errors.Count > 0) return new Result(Outcome.Failed, installed, string.Join("\n", errors));
        return new Result(installed > 0 ? Outcome.Synced : Outcome.UpToDate, installed, null);
    }
}
