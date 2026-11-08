using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenCrazyArcade.AutoUpdate.Managers;

/// <summary>One choice of a <see cref="CaPlayFlag"/>; <c>Risk</c> is "hook" when it patches game code.</summary>
internal sealed record CaPlayFlagOption(
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("risk")] string Risk = null);

/// <summary>One dev-only switch CAPlay understands, passed to the game as environment variable <c>Env</c>.</summary>
internal sealed record CaPlayFlag(
    [property: JsonPropertyName("env")] string Env,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("desc")] string Desc,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("default")] string Default,
    [property: JsonPropertyName("options")] List<CaPlayFlagOption> Options);

internal sealed record CaPlayFlagManifest(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("flags")] List<CaPlayFlag> Flags);

/// <summary>
/// Reads the flag list CAPlay.dll carries as RCDATA resource "FLAGS" (CAPlay/flags.json), so the launcher never
/// hardcodes an environment variable name. The DLL is mapped as a data file: <c>DllMain</c> does not run and the
/// file stays readable while ca.exe has it loaded.
/// </summary>
internal static class CaPlayFlags
{
    const uint LOAD_LIBRARY_AS_DATAFILE = 0x00000002;
    const uint LOAD_LIBRARY_AS_IMAGE_RESOURCE = 0x00000020;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr LoadLibraryExW(string file, IntPtr h, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool FreeLibrary(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr FindResourceW(IntPtr h, string name, IntPtr type);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr LoadResource(IntPtr h, IntPtr res);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr LockResource(IntPtr res);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint SizeofResource(IntPtr h, IntPtr res);

    static readonly IntPtr RT_RCDATA = (IntPtr)10;

    /// <summary>Flag list of the CAPlay.dll at <paramref name="dllPath"/>; empty (with <paramref name="error"/>) if unreadable.</summary>
    public static IReadOnlyList<CaPlayFlag> Load(string dllPath, out string error)
    {
        error = null;
        if (!File.Exists(dllPath)) { error = "khong co CAPlay.dll"; return Array.Empty<CaPlayFlag>(); }

        IntPtr module = LoadLibraryExW(dllPath, IntPtr.Zero, LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE);
        if (module == IntPtr.Zero) { error = $"LoadLibraryEx loi {Marshal.GetLastWin32Error()}"; return Array.Empty<CaPlayFlag>(); }
        try
        {
            IntPtr res = FindResourceW(module, "FLAGS", RT_RCDATA);
            if (res == IntPtr.Zero) { error = "CAPlay.dll khong co resource FLAGS (ban cu)"; return Array.Empty<CaPlayFlag>(); }
            uint size = SizeofResource(module, res);
            IntPtr data = LockResource(LoadResource(module, res));
            if (data == IntPtr.Zero || size == 0) { error = "resource FLAGS rong"; return Array.Empty<CaPlayFlag>(); }

            var bytes = new byte[size];
            Marshal.Copy(data, bytes, 0, (int)size);
            var manifest = JsonSerializer.Deserialize<CaPlayFlagManifest>(bytes);
            return manifest?.Flags ?? (IReadOnlyList<CaPlayFlag>)Array.Empty<CaPlayFlag>();
        }
        catch (Exception ex)
        {
            error = "doc FLAGS that bai: " + ex.Message;
            return Array.Empty<CaPlayFlag>();
        }
        finally
        {
            FreeLibrary(module);
        }
    }

    /// <summary>
    /// Drops saved values for flags the DLL no longer has (or whose value is no longer a valid choice), so a retired
    /// flag is never handed to the game unnoticed. Returns the env names removed.
    /// </summary>
    public static List<string> Reconcile(Dictionary<string, string> saved, IReadOnlyList<CaPlayFlag> flags)
    {
        var removed = new List<string>();
        var byEnv = new Dictionary<string, CaPlayFlag>(StringComparer.Ordinal);
        foreach (var f in flags) byEnv[f.Env] = f;

        foreach (var key in new List<string>(saved.Keys))
        {
            if (!byEnv.TryGetValue(key, out var def) || !IsValid(def, saved[key]))
            {
                saved.Remove(key);
                removed.Add(key);
            }
        }
        return removed;
    }

    public static bool IsValid(CaPlayFlag flag, string value)
    {
        if (flag.Type == "choice") return flag.Options != null && flag.Options.Exists(o => o.Value == value);
        if (flag.Type == "bool") return value is "0" or "1";
        return !string.IsNullOrEmpty(value);
    }
}
