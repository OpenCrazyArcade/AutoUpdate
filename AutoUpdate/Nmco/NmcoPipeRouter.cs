using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenCrazyArcade.AutoUpdate.Managers;
using OpenCrazyArcade.Ipc;

namespace OpenCrazyArcade.AutoUpdate.Nmco;

/// <summary>
/// What the launcher does with each request nmco3.dll sends over the NMCO pipe (the contract is in OpenCrazyArcade.Ipc).
/// One request line in, one reply line out; the connection handler only moves lines, this class decides.
/// </summary>
internal sealed class NmcoPipeRouter(ILogger<NmcoPipeRouter> logger, NMCoreManager nmCore)
{
    public async Task<string> HandleAsync(string line)
    {
        NmcoEnvelope request;
        try
        {
            request = JsonSerializer.Deserialize(line, NmcoJson.Default.NmcoEnvelope);
        }
        catch (JsonException ex)
        {
            logger.LogWarning("[PIPE] dong khong doc duoc: {Message}", ex.Message);
            return Reply(NmcoStatus.Error);
        }
        if (request == null || string.IsNullOrEmpty(request.Action)) return Reply(NmcoStatus.Error);

        try
        {
            // Every message nmco3.dll sends is logged by name, whether or not it is handled: which calls CA.exe makes
            // (and in what order) decides whether the pipe can tell us about logout at all. Payload KEYS only - the
            // values may hold the password.
            string keys = request.Payload.ValueKind == JsonValueKind.Object
                ? string.Join(",", request.Payload.EnumerateObject().Select(o => o.Name))
                : "-";
            logger.LogInformation("[PIPE] action='{Action}' payload={Keys} v={Version} (tien trinh {Elapsed:0.0}s)",
                request.Action, keys, request.V, (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalSeconds);

            switch (request.Action.ToLowerInvariant())
            {
                case "login":
                    return await LoginAsync(request.Payload.ValueKind == JsonValueKind.Object
                        ? request.Payload.Deserialize(NmcoJson.Default.LoginPayload) : null);

                case "logout":
                    // Not every client path sends it; the server ends the presence in other ways too.
                    logger.LogInformation("[PIPE] NMCOLogout nhan duoc tu client");
                    await nmCore.LogoutAsync();
                    return Reply(NmcoStatus.Ok);

                // "init" / "initWithMultiServer" need no work; unknown actions are answered "ok" so a newer dll
                // that adds a call does not break against an older launcher.
                default:
                    return Reply(NmcoStatus.Ok);
            }
        }
        catch (Exception ex)
        {
            logger.LogError("Error handling pipe message: {Error}", ex.Message);
            return Reply(NmcoStatus.Error);
        }
    }

    private async Task<string> LoginAsync(LoginPayload payload)
    {
        string username = payload?.Username ?? "CrazyPlayer";
        string password = payload?.Password ?? "";

        // The client appends "@vina.bnb" to whatever the player typed (sub_9A9410), so the bare account name is
        // everything before the first '@'.
        string account = username.Contains('@') ? username[..username.IndexOf('@')] : username;

        bool success = false;
        long idCode = 0;
        string screenName = account;
        try
        {
            var outcome = await nmCore.LoginAsync(account, password);
            success = outcome is { Success: true };
            if (success)
            {
                idCode = outcome.AccountId;
                if (!string.IsNullOrEmpty(outcome.ScreenName)) screenName = outcome.ScreenName;
                logger.LogInformation("Login OK: '{User}' id={Id} name='{Name}'", account, idCode, screenName);
            }
            else if (outcome != null)
            {
                logger.LogWarning("Login refused for '{User}': {Reason}", account, outcome.Reason ?? "no answer");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("SignalR Login error: {Message}", ex.Message);
        }

        return JsonSerializer.Serialize(new LoginReply(NmcoStatus.Ok, success, idCode, screenName), NmcoJson.Default.LoginReply);
    }

    private static string Reply(string status) => JsonSerializer.Serialize(new NmcoReply(status), NmcoJson.Default.NmcoReply);
}
