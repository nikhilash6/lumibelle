namespace lumibelle.Models;

public sealed record ClaudeCodeSettings
{
    public bool Enabled { get; init; }
    public string ExecutablePath { get; init; } = "";
    public int Concurrency { get; init; } = 1;
    public string TextModel { get; init; } = "";
    public string? TextEffort { get; init; }
}
public sealed record ClaudeCodeConnection(bool Success, string Message, string Version, string? AccountLabel,
    string? AuthMethod, IReadOnlyList<AiModel> Models, string? ApiProvider = null);
public sealed record ClaudeCodeRequest(ClaudeCodeSettings Settings, string Model, string? Effort, string Directory,
    IReadOnlyList<AiTextMessage> Messages);
public sealed record ClaudeCodeUpdate(string? Text = null, GenerationProgress? Progress = null, bool Activity = false,
    string? Model = null, bool Complete = false, bool Truncated = false);
