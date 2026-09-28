namespace lumibelle.Models;

public sealed record CodexSettings
{
    public bool Enabled { get; init; }
    public string ExecutablePath { get; init; } = "";
    public int Concurrency { get; init; } = 1;
    public string TextModel { get; init; } = "";
    public string? TextEffort { get; init; }
    public string ImageModel { get; init; } = "";
    public string? ImageEffort { get; init; }
}
public sealed record CodexWindow(double UsedPercent, int? WindowDurationMins, DateTimeOffset? ResetsAt)
{
    public double RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
}
public sealed record CodexLimit(string Id, string Name, CodexWindow? Primary, CodexWindow? Secondary, string? Reached = null)
{
    public bool Exhausted => Primary?.UsedPercent >= 100 || Secondary?.UsedPercent >= 100 || Reached is not null;
}
public sealed record CodexUsage(DateTimeOffset CheckedUtc, IReadOnlyList<CodexLimit> Limits, string? Error = null)
{
    // The general Codex allowance is separate from model-specific/reserve buckets.
    public bool Exhausted => Limits.Any(l => l.Id == "codex" && l.Exhausted);
}
public sealed record CodexConnection(bool Success, string Message, string Version, string? AccountId,
    string? AccountLabel, bool ImageGeneration, IReadOnlyList<AiModel> Models, CodexUsage? Usage = null, string? AccountType = null)
{
    // Only ChatGPT sign-ins have a plan allowance. API keys, Bedrock and other
    // configured providers are billed or limited by that account instead.
    public bool ReportsAllowance => AccountType == "chatgpt";
}
public sealed record CodexCapture(string AccountId, string Version, string Model, string Effort);
public sealed record CodexImageDetails(string Model, string Version, string Effort, int Width, int Height, string? RevisedPrompt, CodexImageTiming? Timing = null);
public sealed record CodexRequest(CodexSettings Settings, CodexCapture Capture, string Directory,
    IReadOnlyList<AiTextMessage> Messages, bool GenerateImage = false);
public sealed record CodexUpdate(string? Text = null, byte[]? Image = null, string? RevisedPrompt = null,
    string? ThreadId = null, string? TurnId = null, bool Complete = false, GenerationProgress? Progress = null, CodexImageTiming? Timing = null, bool Activity = false);
public sealed record CodexImageOutput(string Text = "", byte[]? Image = null, string? RevisedPrompt = null, bool Complete = false, CodexImageTiming? Timing = null);
public sealed record CodexReceipt(string? ThreadId = null, string? TurnId = null, bool Completed = false, int NativeImageCount = 0, CodexImageTiming? Timing = null);
