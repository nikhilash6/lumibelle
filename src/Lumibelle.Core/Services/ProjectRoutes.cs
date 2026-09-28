using System.Globalization;
using System.Text;
using lumibelle.Services.Story;

namespace lumibelle.Services;

public sealed record ProjectRoute(Guid ProjectId, string Name, string Slug, string[] Aliases);
public sealed record ProjectRouteIndex(int Version, List<ProjectRoute> Projects);

public interface IProjectRoutes
{
    Task RefreshAsync(CancellationToken ct = default);
    Task<ProjectRoute?> ResolveAsync(string key, CancellationToken ct = default);
    string Path(Guid projectId, string suffix = "");
    string Canonicalize(string localUrl);
}

public sealed class FileProjectRoutes(ApplicationPaths paths, IProjectStore projects) : IProjectRoutes
{
    private IReadOnlyList<ProjectRoute> _routes = [];
    public async Task RefreshAsync(CancellationToken ct = default) => _routes = (await SynchronizeAsync(paths.Projects, projects, ct)).Projects;
    public async Task<ProjectRoute?> ResolveAsync(string key, CancellationToken ct = default)
    {
        await RefreshAsync(ct);
        var route = Guid.TryParse(key, out var id) ? _routes.FirstOrDefault(r => r.ProjectId == id)
            : _routes.FirstOrDefault(r => r.Slug.Equals(key, StringComparison.OrdinalIgnoreCase) || r.Aliases.Contains(key, StringComparer.OrdinalIgnoreCase));
        // Reserved names belonging to removed projects must never open a different project.
        return route is not null && (await projects.ListAsync(ct)).Projects.Any(p => p.Id == route.ProjectId) ? route : null;
    }
    public string Path(Guid projectId, string suffix = "")
    {
        if (suffix == "/production" || suffix.StartsWith("/production?", StringComparison.Ordinal) || suffix.StartsWith("/production#", StringComparison.Ordinal)) suffix = "/shots" + suffix[11..];
        return $"/projects/{Uri.EscapeDataString(_routes.FirstOrDefault(r => r.ProjectId == projectId)?.Slug ?? projectId.ToString("D"))}{suffix}";
    }
    public string Canonicalize(string localUrl)
    {
        if (!localUrl.StartsWith("/projects/", StringComparison.Ordinal)) return localUrl;
        var end = localUrl.IndexOfAny(['/', '?', '#'], 10);
        if (end < 0) end = localUrl.Length;
        var key = Uri.UnescapeDataString(localUrl[10..end]);
        var route = Guid.TryParse(key, out var id) ? _routes.FirstOrDefault(r => r.ProjectId == id)
            : _routes.FirstOrDefault(r => r.Slug == key || r.Aliases.Contains(key));
        return route is null ? localUrl : Path(route.ProjectId, localUrl[end..]);
    }
    public static string Slug(string name)
    {
        var text = new StringBuilder();
        foreach (var rune in name.Normalize(NormalizationForm.FormD).EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark) continue;
            if (Rune.IsLetterOrDigit(rune)) text.Append(Rune.ToLowerInvariant(rune));
            else if (text.Length > 0 && text[^1] != '-') text.Append('-');
        }
        var slug = text.ToString().Trim('-').Normalize(NormalizationForm.FormC);
        if (slug.Length == 0) slug = "project";
        return Guid.TryParse(slug, out _) ? "project-" + slug : slug;
    }
    public static async Task<ProjectRouteIndex> SynchronizeAsync(string root, IProjectStore projects, CancellationToken ct)
    {
        try { return await SynchronizeCoreAsync(root, projects, ct); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new WorkspaceStoreException("Couldn’t update project URLs. Check the library folder’s permissions and available disk space, then try again.", e); }
    }
    private static async Task<ProjectRouteIndex> SynchronizeCoreAsync(string root, IProjectStore projects, CancellationToken ct)
    {
        Directory.CreateDirectory(root);
        var path = System.IO.Path.Combine(root, "project-routes.json");
        using var lease = await ProjectFiles.LockAsync(path, ct);
        // Coordinate allocations with other app processes sharing this library, too.
        await using var diskLease = await LockFileAsync(path + ".lock", ct);
        var index = await AtomicJsonFile.ReadAsync<ProjectRouteIndex>(path, ct) ?? new(1, []);
        if (index.Version != 1 || index.Projects is null || index.Projects.Any(r => r is null || r.ProjectId == Guid.Empty || string.IsNullOrWhiteSpace(r.Slug) || Slug(r.Slug) != r.Slug || r.Aliases is null || r.Aliases.Any(a => string.IsNullOrWhiteSpace(a) || Slug(a) != a))
            || index.Projects.Select(r => r.ProjectId).Distinct().Count() != index.Projects.Count
            || index.Projects.SelectMany(r => r.Aliases.Append(r.Slug).Select(s => (Slug: s, r.ProjectId))).GroupBy(r => r.Slug, StringComparer.OrdinalIgnoreCase).Any(g => g.Select(r => r.ProjectId).Distinct().Count() > 1))
            throw new WorkspaceStoreException("The project URL index could not be read. It has not been replaced.");
        var library = await projects.ListAsync(ct);
        var used = index.Projects.SelectMany(r => r.Aliases.Append(r.Slug).Select(s => (Slug: s, r.ProjectId))).ToList();
        var changed = false;
        foreach (var project in library.Projects.OrderBy(p => p.CreatedUtc).ThenBy(p => p.Id))
        {
            var previous = index.Projects.FirstOrDefault(r => r.ProjectId == project.Id);
            if (previous?.Name == project.Name) continue;
            var basis = Slug(project.Name); var slug = basis; var number = 2;
            while (used.Any(r => r.Slug.Equals(slug, StringComparison.OrdinalIgnoreCase) && r.ProjectId != project.Id)) slug = $"{basis}-{number++}";
            var aliases = (previous?.Aliases ?? []).Concat(previous is null ? [] : new[] { previous.Slug }).Where(s => s != slug).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (previous is not null) index.Projects.Remove(previous);
            index.Projects.Add(new(project.Id, project.Name, slug, aliases)); used.Add((slug, project.Id)); changed = true;
        }
        if (changed) await AtomicJsonFile.WriteAsync(path, index, ct);
        return index;
    }
    private static async Task<FileStream> LockFileAsync(string path, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 100) { await Task.Delay(30, ct); }
        }
    }
}
