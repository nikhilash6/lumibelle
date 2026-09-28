using lumibelle.Models;

namespace lumibelle.Services.Story;

public static class ScriptOutline
{
    public static ScriptDocument Insert(ScriptDocument doc, ScriptBlockKind kind, Guid? anchor, string position, out Guid created)
    {
        var sections = ScriptStructure.Sections(doc.Blocks);
        var section = sections.FirstOrDefault(s => s.Id == anchor);
        var at = section is null ? doc.Blocks.Count : position == "before" ? section.Start : section.Start + section.Count;
        if (kind == ScriptBlockKind.Act && position == "default" && section?.ActId is { } act)
        { var parent = sections.Single(s => s.Id == act); at = parent.Start + parent.Count; }
        var heading = ScriptBlock.Create(kind, kind == ScriptBlockKind.Scene ? "INT. LOCATION — DAY" : $"ACT {sections.Count(s => s.Kind == ScriptBlockKind.Act) + 1}");
        created = heading.Id; var blocks = doc.Blocks.Select(b => b.Copy()).ToList();
        blocks.Insert(at, heading);
        if (kind == ScriptBlockKind.Scene) blocks.Insert(at + 1, ScriptBlock.Create(ScriptBlockKind.Action, ""));
        return doc with { Blocks = blocks };
    }
    public static ScriptDocument Move(ScriptDocument doc, Guid id, Guid? anchor, string position)
    {
        var sections = ScriptStructure.Sections(doc.Blocks);
        var moved = sections.Single(s => s.Id == id); var target = sections.FirstOrDefault(s => s.Id == anchor);
        if (anchor == id) return doc;
        if (moved.Kind == ScriptBlockKind.Act && target?.Kind != ScriptBlockKind.Act && target is not null)
            throw new WorkspaceStoreException("Move acts before or after another act.");
        var at = target is null ? (position == "before" ? 0 : sections.FirstOrDefault(s => s.Kind == ScriptBlockKind.Act)?.Start ?? doc.Blocks.Count)
            : position == "before" ? target.Start : position == "start" && target.Kind == ScriptBlockKind.Act ? target.Start + 1 : target.Start + target.Count;
        if (moved.Kind == ScriptBlockKind.Act && target is null) at = position == "before" ? sections.First(s => s.Kind == ScriptBlockKind.Act).Start : doc.Blocks.Count;
        if (at >= moved.Start && at <= moved.Start + moved.Count) return doc;
        var blocks = doc.Blocks.Select(b => b.Copy()).ToList(); var part = blocks.GetRange(moved.Start, moved.Count);
        blocks.RemoveRange(moved.Start, moved.Count); if (at > moved.Start) at -= moved.Count;
        blocks.InsertRange(at, part); return doc with { Blocks = blocks };
    }
}
