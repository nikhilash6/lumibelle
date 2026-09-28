namespace lumibelle.Models;

// Post-publication storage metadata. Never edits the original generation snapshot.
public sealed record FrameArchiveFile(string FileName, long Bytes);
public sealed record FrameArchiveRemoval(DateTimeOffset RequestedUtc, IReadOnlyList<FrameArchiveFile> Files,
    DateTimeOffset? CompletedUtc = null, string? Error = null);
