using System.Text.Json.Serialization;

namespace AiCli.A2aServer.A2a;

// ─── Task State ────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TaskState
{
    [JsonPropertyName("submitted")] Submitted,
    [JsonPropertyName("working")] Working,
    [JsonPropertyName("input-required")] InputRequired,
    [JsonPropertyName("completed")] Completed,
    [JsonPropertyName("failed")] Failed,
    [JsonPropertyName("canceled")] Canceled,
}

// ─── Parts ─────────────────────────────────────────────────────────────────

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(TextPart), "text")]
[JsonDerivedType(typeof(DataPart), "data")]
[JsonDerivedType(typeof(FilePart), "file")]
public abstract record Part
{
    [JsonPropertyName("kind")]
    public abstract string Kind { get; }
}

public record TextPart : Part
{
    [JsonPropertyName("kind")]
    public override string Kind => "text";

    [JsonPropertyName("text")]
    public required string Text { get; init; }
}

public record DataPart : Part
{
    [JsonPropertyName("kind")]
    public override string Kind => "data";

    [JsonPropertyName("data")]
    public required Dictionary<string, object?> Data { get; init; }
}

public record FilePart : Part
{
    [JsonPropertyName("kind")]
    public override string Kind => "file";

    [JsonPropertyName("file")]
    public required FileContent File { get; init; }
}

public record FileContent
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("mimeType")]
    public string? MimeType { get; init; }

    [JsonPropertyName("uri")]
    public string? Uri { get; init; }

    [JsonPropertyName("bytes")]
    public string? Bytes { get; init; }
}

// ─── Message ───────────────────────────────────────────────────────────────

public record A2aMessage
{
    [JsonPropertyName("kind")]
    public string Kind => "message";

    [JsonPropertyName("messageId")]
    public required string MessageId { get; init; }

    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("parts")]
    public required IReadOnlyList<Part> Parts { get; init; }

    [JsonPropertyName("taskId")]
    public string? TaskId { get; init; }

    [JsonPropertyName("contextId")]
    public string? ContextId { get; init; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, object?>? Metadata { get; init; }
}

// ─── Artifact ──────────────────────────────────────────────────────────────

public record Artifact
{
    [JsonPropertyName("artifactId")]
    public required string ArtifactId { get; init; }

    [JsonPropertyName("parts")]
    public required IReadOnlyList<Part> Parts { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

// ─── Task Status ───────────────────────────────────────────────────────────

public record TaskStatus
{
    [JsonPropertyName("state")]
    [JsonConverter(typeof(TaskStateJsonConverter))]
    public required TaskState State { get; init; }

    [JsonPropertyName("message")]
    public A2aMessage? Message { get; init; }

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; init; } = DateTime.UtcNow.ToString("O");
}

// ─── A2A Task ──────────────────────────────────────────────────────────────

public record A2aTask
{
    [JsonPropertyName("kind")]
    public string Kind => "task";

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("contextId")]
    public required string ContextId { get; init; }

    [JsonPropertyName("status")]
    public required TaskStatus Status { get; set; }

    [JsonPropertyName("history")]
    public List<A2aMessage> History { get; set; } = new();

    [JsonPropertyName("artifacts")]
    public List<Artifact> Artifacts { get; set; } = new();

    [JsonPropertyName("metadata")]
    public Dictionary<string, object?> Metadata { get; set; } = new();
}

// ─── Events ────────────────────────────────────────────────────────────────

public record TaskStatusUpdateEvent
{
    [JsonPropertyName("kind")]
    public string Kind => "status-update";

    [JsonPropertyName("taskId")]
    public required string TaskId { get; init; }

    [JsonPropertyName("contextId")]
    public required string ContextId { get; init; }

    [JsonPropertyName("status")]
    public required TaskStatus Status { get; init; }

    [JsonPropertyName("final")]
    public bool Final { get; init; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, object?>? Metadata { get; init; }
}

public record TaskArtifactUpdateEvent
{
    [JsonPropertyName("kind")]
    public string Kind => "artifact-update";

    [JsonPropertyName("taskId")]
    public required string TaskId { get; init; }

    [JsonPropertyName("contextId")]
    public required string ContextId { get; init; }

    [JsonPropertyName("artifact")]
    public required Artifact Artifact { get; init; }

    [JsonPropertyName("append")]
    public bool Append { get; init; }

    [JsonPropertyName("lastChunk")]
    public bool LastChunk { get; init; }
}

// ─── Agent Card ────────────────────────────────────────────────────────────

public record AgentCard
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("url")]
    public required string Url { get; set; }

    [JsonPropertyName("provider")]
    public AgentProvider? Provider { get; init; }

    [JsonPropertyName("protocolVersion")]
    public required string ProtocolVersion { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("capabilities")]
    public required AgentCapabilities Capabilities { get; init; }

    [JsonPropertyName("defaultInputModes")]
    public IReadOnlyList<string> DefaultInputModes { get; init; } = new[] { "text" };

    [JsonPropertyName("defaultOutputModes")]
    public IReadOnlyList<string> DefaultOutputModes { get; init; } = new[] { "text" };

    [JsonPropertyName("skills")]
    public IReadOnlyList<AgentSkill> Skills { get; init; } = Array.Empty<AgentSkill>();
}

public record AgentProvider
{
    [JsonPropertyName("organization")]
    public required string Organization { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }
}

public record AgentCapabilities
{
    [JsonPropertyName("streaming")]
    public bool Streaming { get; init; }

    [JsonPropertyName("pushNotifications")]
    public bool PushNotifications { get; init; }

    [JsonPropertyName("stateTransitionHistory")]
    public bool StateTransitionHistory { get; init; }
}

public record AgentSkill
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    [JsonPropertyName("examples")]
    public IReadOnlyList<string> Examples { get; init; } = Array.Empty<string>();
}

// ─── JSON-RPC Types ────────────────────────────────────────────────────────

public class JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public object? Id { get; set; }

    [JsonPropertyName("method")]
    public required string Method { get; set; }

    [JsonPropertyName("params")]
    public System.Text.Json.JsonElement? Params { get; set; }
}

public class JsonRpcResponse
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public object? Id { get; set; }

    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Result { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonRpcError? Error { get; set; }
}

public class JsonRpcError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public required string Message { get; set; }

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Data { get; set; }
}

// ─── Request Params ────────────────────────────────────────────────────────

public record MessageSendParams
{
    [JsonPropertyName("message")]
    public required A2aMessage Message { get; init; }
}

public record TasksGetParams
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }
}

public record TasksCancelParams
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }
}

// ─── JSON converter for TaskState ──────────────────────────────────────────

public class TaskStateJsonConverter : System.Text.Json.Serialization.JsonConverter<TaskState>
{
    public override TaskState Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        var str = reader.GetString();
        return str switch
        {
            "submitted" => TaskState.Submitted,
            "working" => TaskState.Working,
            "input-required" => TaskState.InputRequired,
            "completed" => TaskState.Completed,
            "failed" => TaskState.Failed,
            "canceled" => TaskState.Canceled,
            _ => TaskState.Submitted,
        };
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, TaskState value, System.Text.Json.JsonSerializerOptions options)
    {
        var str = value switch
        {
            TaskState.Submitted => "submitted",
            TaskState.Working => "working",
            TaskState.InputRequired => "input-required",
            TaskState.Completed => "completed",
            TaskState.Failed => "failed",
            TaskState.Canceled => "canceled",
            _ => "submitted",
        };
        writer.WriteStringValue(str);
    }
}
