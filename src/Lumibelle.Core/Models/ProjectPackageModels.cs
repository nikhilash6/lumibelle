namespace lumibelle.Models;

public sealed record ProjectExportOptions(bool IncludeReferencedTrashImages = true);
public sealed record ProjectPackageProgress(string Message, long Bytes = 0, int Files = 0);
public sealed record ProjectPackageFile(string Path, long Bytes, string Sha256);
public sealed record ProjectPackageManifest
{
    public string Format { get; init; } = "lumibelle-project";
    public int Version { get; init; } = 1;
    public string PrivacyProfile { get; init; } = "project-loras-only-v1";
    public required Guid ProjectId { get; init; }
    public required DateTimeOffset ExportedUtc { get; init; }
    public bool IncludedReferencedTrashImages { get; init; }
    public int ReferencedTrashImages { get; init; }
    public int IncludedTrashImages { get; init; }
    public int RemovedLoraSelections { get; init; }
    public IReadOnlyList<string> Notices { get; init; } = [];
    public IReadOnlyList<ProjectPackageFile> Files { get; init; } = [];
}
public sealed record ProjectPackageExport(Guid Id, Guid ProjectId, string Name, long Bytes, ProjectPackageManifest Manifest)
{
    public string Url => $"/downloads/projects/{ProjectId:D}/packages/{Id:D}.zip";
}
public sealed record ProjectPackageImport(Guid Token, ProjectInfo Project, long Bytes, int FileCount,
    IReadOnlyList<string> Notices, bool AlreadyExists);
