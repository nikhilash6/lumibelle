namespace lumibelle.Models;

// User-authorized recovery, independent of permanent cancellation and the captured request.
// Each retry ID permits at most one new attempt of each stopped/missing workflow.
public sealed record ComfyQueueControl(Guid RetryId, bool PauseRequested = false);
