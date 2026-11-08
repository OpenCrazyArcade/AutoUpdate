using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace OpenCrazyArcade.AutoUpdate.Models;

public class LauncherConfig
{
    public static readonly string BaseDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "OpenCrazyArcade"
    );

    public static readonly string UpdatesTempDir = Path.Combine(BaseDataDir, "Updates");
    public static readonly string WebView2DataDir = Path.Combine(BaseDataDir, "WebView2");
    public static readonly string ConfigPath = Path.Combine(BaseDataDir, "launcher.json");

    /// <summary>
    /// Everything the player may be asked to send back lives under <c>logs</c>, one folder per
    /// producer: <c>ca</c> (CAPlay, the game side), <c>launcher</c> and <c>nmco3</c> (the
    /// nmco3.dll replacement writes its own, same scheme). Every file is named
    /// "[&lt;host&gt;] &lt;yyyyMMdd_HHmmss&gt;.log" so logs from several machines never collide.
    /// </summary>
    public static readonly string LogsRoot = Path.Combine(BaseDataDir, "logs");
    public static readonly string LogDir = Path.Combine(LogsRoot, "ca");
    public static readonly string LauncherLogDir = Path.Combine(LogsRoot, "launcher");
    public static readonly string Nmco3LogDir = Path.Combine(LogsRoot, "nmco3");

    public static readonly string LauncherLogPath = Path.Combine(
        LauncherLogDir, $"[{Environment.MachineName}] {DateTime.Now:yyyyMMdd_HHmmss}.log");

    /// <summary>The community server the player plays on. Empty until they set one (the launcher ships without any).</summary>
    public string ServerUrl { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasServer => !string.IsNullOrWhiteSpace(GetNormalizedServerUrl());

    /// <summary>Ask CAPlay (when it is injected) to write the game-side log into <see cref="LogDir"/>.</summary>
    public bool EnableClientLog { get; set; } = true;

    /// <summary>
    /// Dev-only CAPlay switches (environment variable name -> value), edited in the hidden Dev Config window. The list of
    /// valid names comes from the flags resource inside CAPlay.dll; a value equal to the flag's default is not stored.
    /// </summary>
    public Dictionary<string, string> DevFlags { get; set; } = new();
    public bool AutoCheckUpdateAtStartup { get; set; } = true;
    public bool WindowedMode { get; set; } = true;

    /// <summary>Launcher version (X.Y.Z) whose client files were last synced into the game folder (ComponentSync).
    /// A different version means the files are looked at again at the next start.</summary>
    public string ComponentsSyncedFor { get; set; }

    public string GetNormalizedServerUrl()
    {
        string url = ServerUrl?.Trim();
        if (string.IsNullOrEmpty(url)) return "";   // no server configured

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "http://" + url;
        }

        return url.TrimEnd('/');
    }

    public static void EnsureDirectories()
    {
        try
        {
            Directory.CreateDirectory(BaseDataDir);
            Directory.CreateDirectory(UpdatesTempDir);
            Directory.CreateDirectory(LogDir);
            Directory.CreateDirectory(LauncherLogDir);
            Directory.CreateDirectory(Nmco3LogDir);
            Directory.CreateDirectory(WebView2DataDir);
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", WebView2DataDir);
        }
        catch { }
    }

    public static LauncherConfig Load()
    {
        EnsureDirectories();

        try
        {
            if (File.Exists(ConfigPath))
            {
                string json = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<LauncherConfig>(json);
                if (cfg != null) return cfg;
            }
        }
        catch { }

        var def = new LauncherConfig();
        def.Save();
        return def;
    }

    public void Save()
    {
        EnsureDirectories();
        try
        {
            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
        catch { }
    }
}
