using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fluentia.Models;

public class WsMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("token")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Token { get; set; }

    [JsonPropertyName("deviceId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeviceId { get; set; }

    [JsonPropertyName("role")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Role { get; set; }

    [JsonPropertyName("publicKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PublicKey { get; set; }

    [JsonPropertyName("payload")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Payload { get; set; }

    [JsonPropertyName("nonce")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Nonce { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }

    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; set; }

    [JsonPropertyName("min_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MinVersion { get; set; }

    [JsonPropertyName("expiresAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExpiresAt { get; set; }

    [JsonPropertyName("seq")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Seq { get; set; }

    // Device code auth
    [JsonPropertyName("deviceCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeviceCode { get; set; }

    [JsonPropertyName("verifyId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VerifyId { get; set; }

    [JsonPropertyName("userAgent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UserAgent { get; set; }

    [JsonPropertyName("approved")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Approved { get; set; }

    public string Serialize() => JsonSerializer.Serialize(this);

    public static WsMessage? Deserialize(string json) =>
        JsonSerializer.Deserialize<WsMessage>(json);
}

public class InputCommand
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    [JsonPropertyName("count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Count { get; set; }

    [JsonPropertyName("seed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Seed { get; set; }

    [JsonPropertyName("publicKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PublicKey { get; set; }

    // File transfer fields
    [JsonPropertyName("transferId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TransferId { get; set; }

    [JsonPropertyName("fileName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FileName { get; set; }

    [JsonPropertyName("fileSize")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long FileSize { get; set; }

    [JsonPropertyName("mimeType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MimeType { get; set; }

    [JsonPropertyName("chunkIndex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ChunkIndex { get; set; }

    [JsonPropertyName("chunkData")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ChunkData { get; set; }  // base64-encoded chunk bytes

    [JsonPropertyName("isLast")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsLast { get; set; }

    public string Serialize() => JsonSerializer.Serialize(this);

    public static InputCommand? Deserialize(string json) =>
        JsonSerializer.Deserialize<InputCommand>(json);
}

public static class MsgTypes
{
    public const string ProtocolVersion  = "1.4.1";
    public const string CreateSession    = "create_session";
    public const string SessionCreated   = "session_created";
    public const string JoinSession      = "join_session";
    public const string Joined           = "joined";
    public const string RejoinSession    = "rejoin_session";
    public const string Rejoined         = "rejoined";
    public const string PeerJoined       = "peer_joined";
    public const string PeerLeft         = "peer_left";
    public const string Preempted        = "preempted";
    public const string KeyExchange      = "key_exchange";
    public const string Encrypted        = "encrypted";
    public const string Ping             = "ping";
    public const string Pong             = "pong";
    public const string Error            = "error";

    // Device code auth
    public const string DeviceCodeRequest = "device_code_request";
    public const string DeviceCodeCreated = "device_code_created";
    public const string DeviceCodePending = "device_code_pending";
    public const string DeviceCodeConfirm = "device_code_confirm";
    public const string DeviceCodeReject  = "device_code_reject";
}
