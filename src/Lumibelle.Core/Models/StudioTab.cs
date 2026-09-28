namespace lumibelle.Models;

/// <summary>A stable pane destination; labels can change without losing layout preferences.</summary>
public sealed record StudioTab(string Id, string Label, int? Count = null, string? Issue = null);
