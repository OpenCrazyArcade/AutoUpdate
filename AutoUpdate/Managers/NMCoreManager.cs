using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenCrazyArcade.AutoUpdate.Models;

namespace OpenCrazyArcade.AutoUpdate.Managers;

internal partial class NMCoreManager
{
    private readonly ILogger<NMCoreManager> _logger;
    private readonly LauncherConfig _config;
    private HubConnection _hubConnection;
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    public NMCoreManager(ILogger<NMCoreManager> logger, LauncherConfig config)
    {
        _logger = logger;
        _config = config;
    }

    /// <summary>Raised with the server's update-set token: pushed by the hub (UpdatesChanged) and after every reconnect.</summary>
    public event Action<string> UpdatesChanged;

    public record ServerEndpoints(string LoginIp, int LoginPort, int GamePort);

    /// <summary>
    /// Who is playing on this machine, pushed by the server. Mirrors OCAServer.Hubs.PresenceInfo.
    /// State: LoggingIn (a name was claimed over the pipe), Playing (in a channel - authenticated
    /// by the game socket), ChannelList (game socket closed, client still up), Offline.
    /// </summary>
    public record PresenceInfo(string Username, uint AccountId, string State, string Detail);

    /// <summary>The latest presence the server told us about; null before the first and after Offline.</summary>
    public PresenceInfo CurrentPresence { get; private set; }

    /// <summary>Raised (on a worker thread) whenever the server reports a change.</summary>
    public event Action<PresenceInfo> PresenceChanged;

    /// <summary>
    /// CA.exe exited: tell the server, which ends the presence. The surest "not playing" signal -
    /// the pipe's NMCOLogout is not sent every time the client goes back to the login screen.
    /// </summary>
    public async Task NotifyClientExitedAsync()
    {
        try
        {
            if (_hubConnection?.State == HubConnectionState.Connected)
                await _hubConnection.InvokeAsync("ClientExited");
        }
        catch (Exception ex) { _logger.LogDebug("ClientExited that bai: {Message}", ex.Message); }
    }

    /// <summary>
    /// What the hub returns from "Login". Mirrors OCAServer.Web.Hubs.LoginOutcome field for field.
    ///
    /// AccountId is the server-assigned characters.id — the same number opcode 4 hands the game.
    /// It has to come from the server: a constant invented here would make every player the same
    /// person to anything that keys by id, friend lists first among them.
    /// </summary>
    public record LoginOutcome(bool Success, uint AccountId, string ScreenName, string Reason);

    public async Task<ServerEndpoints> GetGameServerEndpointsAsync()
    {
        // 1. First attempt: Query via SignalR if connected
        try
        {
            await EnsureConnectToOCAServer();
            if (_hubConnection != null && _hubConnection.State == HubConnectionState.Connected)
            {
                var ep = await _hubConnection.InvokeAsync<ServerEndpoints>("GetServerConfig");
                if (ep != null && !string.IsNullOrEmpty(ep.LoginIp))
                {
                    _logger.LogInformation("Fetched game server config via SignalR: {Ip}:{Port}", ep.LoginIp, ep.LoginPort);
                    return ep;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("SignalR GetServerConfig skipped ({Msg}), falling back to HTTP.", ex.Message);
        }

        // 2. Second attempt: Query via HTTP GET {baseUrl}/api/config
        try
        {
            string baseUrl = _config.GetNormalizedServerUrl();
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var res = await System.Net.Http.Json.HttpClientJsonExtensions.GetFromJsonAsync<JsonElement>(http, $"{baseUrl}/api/config");
            string ip = res.GetProperty("loginIp").GetString() ?? "127.0.0.1";
            int loginPort = res.GetProperty("loginPort").GetInt32();
            int gamePort = res.GetProperty("gamePort").GetInt32();
            _logger.LogInformation("Fetched game server config via HTTP: {Ip}:{Port}", ip, loginPort);
            return new ServerEndpoints(ip, loginPort, gamePort);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("HTTP /api/config failed: {Msg}", ex.Message);
        }

        // 3. Fallback: Parse host from ServerUrl directly
        string fallbackHost = "127.0.0.1";
        try
        {
            var uri = new Uri(_config.GetNormalizedServerUrl());
            fallbackHost = uri.Host;
            if (!IPAddress.TryParse(fallbackHost, out _))
            {
                var addrs = await Dns.GetHostAddressesAsync(fallbackHost);
                var ipv4 = Array.Find(addrs, a => a.AddressFamily == AddressFamily.InterNetwork);
                if (ipv4 != null) fallbackHost = ipv4.ToString();
            }
        }
        catch { }

        return new ServerEndpoints(fallbackHost, 3838, 4949);
    }

    /// <summary>
    /// The hub is built once for the server URL of the moment. When the player points the launcher at
    /// another server the old connection must go, or presence and update pushes keep coming from (and
    /// going to) the previous one. The next <see cref="EnsureConnectToOCAServer"/> builds a new one.
    /// </summary>
    public async Task ResetServerConnectionAsync()
    {
        await _connectLock.WaitAsync();
        try
        {
            var old = _hubConnection;
            _hubConnection = null;
            CurrentPresence = null;
            if (old != null)
            {
                try { await old.StopAsync(); await old.DisposeAsync(); }
                catch (Exception ex) { _logger.LogDebug("Dong ket noi hub cu that bai: {Message}", ex.Message); }
            }
        }
        finally { _connectLock.Release(); }
    }

    public async Task EnsureConnectToOCAServer()
    {
        await _connectLock.WaitAsync();
        try
        {
            if (_hubConnection != null && _hubConnection.State == HubConnectionState.Connected)
                return;

            string baseUrl = _config.GetNormalizedServerUrl();
            if (string.IsNullOrEmpty(baseUrl)) return;   // no server configured: nothing to connect to
            string hubUrl = $"{baseUrl}/hubs/community";

            if (_hubConnection == null)
            {
                _hubConnection = new HubConnectionBuilder()
                    .WithUrl(hubUrl)
                    .WithAutomaticReconnect()
                    .Build();

                _hubConnection.On<string>("UpdatesChanged", version => UpdatesChanged?.Invoke(version));

                _hubConnection.On<PresenceInfo>("PresenceChanged", p =>
                {
                    CurrentPresence = p.State == "Offline" ? null : p;
                    _logger.LogInformation("[PRESENCE] '{User}' (id {Id}) -> {State}: {Detail}",
                        p.Username, p.AccountId, p.State, p.Detail);
                    PresenceChanged?.Invoke(p);
                });

                _hubConnection.Closed += async (error) =>
                {
                    _logger.LogWarning("SignalR connection to CAServer closed: {Error}", error?.Message);
                    await Task.CompletedTask;
                };

                _hubConnection.Reconnecting += (error) =>
                {
                    _logger.LogInformation("SignalR reconnecting to CAServer...");
                    return Task.CompletedTask;
                };

                _hubConnection.Reconnected += async (connectionId) =>
                {
                    _logger.LogInformation("SignalR reconnected to CAServer. ConnectionId: {ConnId}", connectionId);
                    // A reconnect usually means the server was redeployed: ask what it serves now.
                    try
                    {
                        string version = await _hubConnection.InvokeAsync<string>("GetUpdatesVersion");
                        UpdatesChanged?.Invoke(version);
                    }
                    catch (Exception ex) { _logger.LogDebug("GetUpdatesVersion sau reconnect that bai: {Message}", ex.Message); }
                };
            }

            _logger.LogInformation("Connecting to CAServer SignalR Hub at {Url}...", hubUrl);
            using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _hubConnection.StartAsync(connectCts.Token);
            _logger.LogInformation("Connected to CAServer SignalR Hub successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not connect to CAServer SignalR Hub ({Message}). Running in local/offline mode.", ex.Message);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <summary>
    /// Ask the server to sign <paramref name="account"/> in. Null when the hub is not reachable (the caller logs
    /// why and answers the game with a failed login).
    /// </summary>
    public async Task<LoginOutcome> LoginAsync(string account, string password)
    {
        await EnsureConnectToOCAServer();
        if (_hubConnection == null || _hubConnection.State != HubConnectionState.Connected)
        {
            _logger.LogWarning("Login for '{User}' skipped: hub not connected.", account);
            return null;
        }
        return await _hubConnection.InvokeAsync<LoginOutcome>("Login", account, password, Environment.MachineName);
    }

    /// <summary>
    /// The player logged out in the client: forwarded, it ends the presence early. Its ABSENCE proves nothing - the
    /// client does not always send it - so the server also ends a presence on CA.exe exit, a different name from the
    /// same launcher, or the launcher leaving.
    /// </summary>
    public async Task LogoutAsync()
    {
        try
        {
            if (_hubConnection?.State == HubConnectionState.Connected)
                await _hubConnection.InvokeAsync("Logout");
        }
        catch (Exception ex) { _logger.LogDebug("Logout that bai: {Message}", ex.Message); }
    }
}

internal partial class NMCoreManager
{
    public class HostedService(NMCoreManager nmCoreManager) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _ = Task.Run(async () =>
            {
                await nmCoreManager.EnsureConnectToOCAServer();
            }, cancellationToken);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
