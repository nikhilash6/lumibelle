using System.Text.Json;
using lumibelle.Services.Story;
using Microsoft.Extensions.AI;

namespace lumibelle.Models;

public sealed record AiTextPart(string? Text = null, byte[]? Image = null, string? MediaType = null);
public sealed record AiTextMessage(string Role, IReadOnlyList<AiTextPart> Parts)
{
    public static AiTextMessage Capture(ChatMessage message) => new(message.Role.ToString(), message.Contents.Select(content => content switch
    {
        TextContent text => new AiTextPart(Text: text.Text),
        DataContent data when data.MediaType == "image/png" => new AiTextPart(Image: data.Data.ToArray(), MediaType: data.MediaType),
        _ => throw new WorkspaceStoreException("This text request contains an unsupported message part.")
    }).ToArray());
    public ChatMessage ToMessage() => new(new ChatRole(Role), Parts.Select<AiTextPart, AIContent>(part => part.Image is { } bytes
        ? new DataContent(bytes, part.MediaType!) : new TextContent(part.Text!)).ToList());
}

// Both the task data and the exact outgoing instructions are retained. Later profile
// changes do not rewrite queued messages or the parser context for an existing result.
// Version 1 keeps captured sampling overrides; version 2 uses hosted provider defaults.
// Version 3 adds explicit LLM-profile overrides. Profile below is the prompt-template ID.
public sealed record AiTextJobRequest(int Version, AiJobKind Kind, TextModelReference Model, bool FollowsDefault,
    AiSettings Settings, string Profile, float Temperature, long Seed, JsonElement Task,
    IReadOnlyList<AiTextMessage> Messages, string? GuidanceBaseline = null)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public CodexCapture? Codex { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public TextModelSelectionSource? SelectionSource { get; init; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public AiTextRepair? Repair { get; init; }

    public T Payload<T>() => Task.Deserialize<T>(AtomicJsonFile.Options) ?? throw new WorkspaceStoreException("The saved text request is incomplete.");
    public bool InspectsImages => Messages.Any(m => m.Parts.Any(p => p.Image is not null));
}
public sealed record AiTextJobResult(string Raw, bool Complete = false, string? FinishReason = null, JsonElement? Value = null, string? Error = null)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public OpenRouterRequestUsage? OpenRouterUsage { get; init; }
    public T? Read<T>() => Value is { } value ? value.Deserialize<T>(AtomicJsonFile.Options) : default;
}
