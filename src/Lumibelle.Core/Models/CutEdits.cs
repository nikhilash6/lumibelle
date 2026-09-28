namespace lumibelle.Models;

// UI gestures carry the draft version they began against; they are never persisted.
public sealed record CutTrimEdit(Guid ClipId, int Start, int End, int Version);
public sealed record CutOrderEdit(Guid ClipId, Guid? BeforeId, int Version);
