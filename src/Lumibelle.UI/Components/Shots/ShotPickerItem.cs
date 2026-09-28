namespace lumibelle.Components.Shots;

public sealed record ShotPickerItem(
    Guid Id, int Number, string Title, Guid? SceneId, string SceneTitle,
    string Duration, int TakeCount, string? ThumbnailUrl,
    bool HasProductionTake = false, string? UnavailableReason = null);
