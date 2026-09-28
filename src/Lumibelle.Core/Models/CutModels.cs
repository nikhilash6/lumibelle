namespace lumibelle.Models;

public sealed record CutClip
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ShotId { get; set; }
    public Guid TakeId { get; set; }
    public string ShotTitle { get; set; } = "";
    public string TakeLabel { get; set; } = "";
    public int FrameCount { get; set; }
    public double Fps { get; set; }
    public int StartFrame { get; set; }
    public int EndFrameExclusive { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public double Duration => (EndFrameExclusive - StartFrame) / Fps;
    public static CutClip From(Shot shot, ShotTake take) => new()
    {
        ShotId = shot.Id, TakeId = take.Id, ShotTitle = shot.Title,
        TakeLabel = $"Take {take.Candidate} · {take.CreatedUtc:yyyy-MM-dd HH:mm} · {take.Id.ToString()[..8]}",
        FrameCount = take.FrameCount, Fps = take.Fps, EndFrameExclusive = take.FrameCount
    };
}

public sealed record CutDocument
{
    public int SchemaVersion { get; set; } = 1;
    public Guid ProjectId { get; set; }
    public long Revision { get; set; }
    public DateTimeOffset? UpdatedUtc { get; set; }
    public List<CutClip> Clips { get; set; } = [];
    public CutDocument Copy() => ShotCopy.Of(this);
}
