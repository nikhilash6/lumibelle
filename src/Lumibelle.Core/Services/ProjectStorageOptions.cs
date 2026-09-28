namespace lumibelle.Services;

public sealed class ProjectStorageOptions
{
    public const string SectionName = "Projects";
    public string RootDirectory { get; set; } = "App_Data/Projects";
}
