using lumibelle.Services.Story;

namespace lumibelle.Services.AI;

// An assisted prompt is LLM input the author reviews, not a derived value that must match
// its request exactly. Automatic application keeps an exact guard. An explicit Apply names
// what changed since the request, in the author's terms, and applies when the author chooses
// Apply anyway. A superseded request is never applied; callers refuse it before checking here.
public static class AssistedApply
{
    // Returns whether the response may be applied now. Explicit applies throw so the review
    // can name the changes and offer Apply anyway.
    public static bool Allows(IReadOnlyList<string> changes, bool automatic, bool acceptChangedInputs, string subject, string response = "this prompt")
    {
        if (changes.Count == 0) return true;
        if (automatic) return false;
        return acceptChangedInputs ? true : throw new AssistedInputsChangedException(changes, subject, response);
    }
}

public sealed class AssistedInputsChangedException(IReadOnlyList<string> changes, string subject, string response = "this prompt")
    : WorkspaceStoreException($"Since {response} was written, {string.Join("; ", changes)}. It may not match {subject} as it is now.")
{
    public IReadOnlyList<string> Changes { get; } = changes;
}
