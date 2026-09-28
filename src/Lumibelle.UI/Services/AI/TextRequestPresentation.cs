using lumibelle.Models;

namespace lumibelle.Services.AI;

public enum TextRequestOutcome { Proposal, Response, Resolved, Invalid }

public sealed record TextRequestPresentation(AiJobHeader Job, string WorkingLabel, TextRequestOutcome Outcome = TextRequestOutcome.Proposal, string? QueuedAction = null)
{
    public static bool IsActive(AiJobHeader job) => job.State is AiJobState.Waiting or AiJobState.Running || job.RemoteUnconfirmed;
    public bool Active => IsActive(Job);
    public bool Inspectable => Active || Job.State != AiJobState.Cancelled && !Job.CancelRequested && Outcome != TextRequestOutcome.Resolved;
    public string Label => Job.CancelRequested && Active ? "Cancellation requested…" : Job.RemoteUnconfirmed && Job.State != AiJobState.Running ? "Awaiting confirmation…" : Job.State switch {
        AiJobState.Waiting => "Queued · " + (QueuedAction ?? WorkingLabel.TrimEnd('…')),
        AiJobState.Running => WorkingLabel,
        AiJobState.NeedsAttention => "Needs attention",
        _ => Outcome switch { TextRequestOutcome.Invalid => "Needs attention", TextRequestOutcome.Response => "View response", _ => "Review changes" }
    };
}
