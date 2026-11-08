using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenCrazyArcade.Ipc;

/// <summary>
/// The NMCO pipe: nmco3.dll (our replacement for Nexon's messenger library, loaded by CA.exe) talks to the launcher over the
/// raw named pipe <see cref="Name"/>. One JSON object per line, answered by one JSON object per line.
///
/// Request  {"action":"login","payload":{...},"v":1}
/// Reply    {"status":"ok", ...}
///
/// The shape is the one the first nmco3.dll already used, so a launcher and a dll built at different times keep working
/// together: unknown fields are ignored by both sides, and a missing "v" means version 0 (the original dll).
/// Because the dll may one day be updated on its own, every request says which protocol version it speaks.
/// </summary>
public static class NmcoPipe
{
    public const string Name = "OpenCrazyArcade_NMCO";

    /// <summary>Bumped when a message changes in a way an older peer cannot ignore.</summary>
    public const int ProtocolVersion = 1;
}

/// <summary>The "action" names, as they travel on the wire. The launcher compares them case-insensitively.</summary>
public static class NmcoAction
{
    public const string Init = "init";
    public const string InitWithMultiServer = "initWithMultiServer";
    public const string Login = "login";
    public const string Logout = "logout";
}

// ── dll -> launcher: payloads ────────────────────────────────────────────────────────────────
public sealed record InitPayload(string Mode);
public sealed record InitWithMultiServerPayload(int A1, int A2, int A3, int A4);

/// <summary>What the player typed. <c>Username</c> still carries the "@vina.bnb" the client appends.</summary>
public sealed record LoginPayload(string Username, string Password);

public sealed record EmptyPayload();

/// <summary>A request as the dll writes it: the payload is typed.</summary>
public sealed record NmcoRequest<TPayload>(string Action, TPayload Payload, int V = NmcoPipe.ProtocolVersion);

/// <summary>A request as the launcher first reads it: the payload stays raw until the action says what it is.</summary>
public sealed class NmcoEnvelope
{
    public string Action { get; set; }
    public int V { get; set; }
    public JsonElement Payload { get; set; }
}

// ── launcher -> dll: replies ─────────────────────────────────────────────────────────────────
public static class NmcoStatus
{
    public const string Ok = "ok";
    public const string Error = "error";
}

public sealed record NmcoReply(string Status);

/// <summary>The identity the server answered with, for NMCOGetMyIdCode / NMCOGetMyScreenName.</summary>
public sealed record LoginReply(string Status, bool Success, long IdCode, string ScreenName);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(NmcoRequest<InitPayload>))]
[JsonSerializable(typeof(NmcoRequest<InitWithMultiServerPayload>))]
[JsonSerializable(typeof(NmcoRequest<LoginPayload>))]
[JsonSerializable(typeof(NmcoRequest<EmptyPayload>))]
[JsonSerializable(typeof(LoginPayload))]
[JsonSerializable(typeof(NmcoEnvelope))]
[JsonSerializable(typeof(NmcoReply))]
[JsonSerializable(typeof(LoginReply))]
public partial class NmcoJson : JsonSerializerContext;
