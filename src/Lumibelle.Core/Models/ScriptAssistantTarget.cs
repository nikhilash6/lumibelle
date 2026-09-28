using lumibelle.Services.Story;

namespace lumibelle.Models;

/// <summary>Browser-visit target choice, independent of the moving editor caret.</summary>
public sealed record ScriptAssistantTarget(ScriptScope Scope, Guid? SectionId = null, ScriptSelection? Passage = null, List<Guid>? PassageBlockIds = null)
{
    public static ScriptAssistantTarget From(ScriptDocument document, ScriptScope scope, ScriptSelection? selection)
    {
        var captured = ScriptStructure.Capture(document, scope, selection);
        return scope switch
        {
            ScriptScope.Document => new(scope),
            ScriptScope.Passage => new(scope, Passage: selection, PassageBlockIds: captured.OriginalBlocks.Select(b => b.Id).ToList()),
            _ => new(scope, captured.OriginalBlocks[0].Id)
        };
    }

    public ScriptTarget Capture(ScriptDocument document)
    {
        if (Scope == ScriptScope.Document) return ScriptStructure.Capture(document, Scope, null);
        if (Scope == ScriptScope.Passage)
        {
            var captured = ScriptStructure.Capture(document, Scope, Passage);
            if (PassageBlockIds is null || !captured.OriginalBlocks.Select(b => b.Id).SequenceEqual(PassageBlockIds))
                throw new WorkspaceStoreException("The selected passage changed structure. Select it again before requesting a revision.");
            return captured;
        }
        var kind = Scope == ScriptScope.Act ? ScriptBlockKind.Act : ScriptBlockKind.Scene;
        var section = ScriptStructure.Sections(document.Blocks).SingleOrDefault(s => s.Id == SectionId && s.Kind == kind)
            ?? throw new WorkspaceStoreException("The selected section was deleted or changed type. Choose another target.");
        return new(Scope, section.Title, document.Blocks.Skip(section.Start).Take(section.Count).Select(b => b.Copy()).ToList());
    }
}
