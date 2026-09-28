namespace lumibelle.Models;

public sealed record ProjectLanguage(string Code, string Name);
public sealed record ProjectLanguages
{
    public int SchemaVersion { get; init; } = 1;
    public Guid ProjectId { get; init; }
    public long Revision { get; init; }
    // Existing projects have no assumed master language. Configuring a language never edits the script.
    public ProjectLanguage? Main { get; init; }
    public IReadOnlyList<ProjectLanguage> Dubs { get; init; } = [];
    public string TranslationNotes { get; init; } = "";
}

public sealed record DubLine(Guid Id, string Text);
public sealed record ShotDubRequest(int Version, Guid ProjectId, Guid ShotId, Guid SourceTakeId,
    Guid VariantId, long ExpectedVariantVersion, string SourceFingerprint, string ReferenceFingerprint,
    ProjectLanguage Main, ProjectLanguage Target, string SourcePrompt, IReadOnlyList<ShotDialogue> Dialogue,
    double Duration, string SceneContext, string Instructions, string LanguageFingerprint);
public sealed record ShotDubTranslation(IReadOnlyList<DubLine> Lines, IReadOnlyList<string> Notes);
public sealed record ShotDubVariant(Guid Id, long Version, ShotDubRequest Request,
    ShotDubTranslation Translation, string Prompt, DateTimeOffset UpdatedUtc,
    Guid? TranslationJobId = null, TextModelReference? Model = null);
public sealed record ShotDubDocument
{
    public int SchemaVersion { get; init; } = 1;
    public Guid ProjectId { get; init; }
    public long Revision { get; init; }
    public IReadOnlyList<ShotDubVariant> Variants { get; init; } = [];
}

// Small provenance record on a take; no recursive source snapshot and no mutable library links.
public sealed record VideoDubContext(Guid VariantId, long VariantVersion, Guid SourceTakeId,
    string SourceFingerprint, string ReferenceFingerprint, ProjectLanguage Main, ProjectLanguage Target,
    string SourcePrompt, IReadOnlyList<ShotDialogue> SourceDialogue, Guid? TranslationJobId = null);
public sealed record ShotDubSource(ShotTake Take, AiVideoJobRequest Request);
