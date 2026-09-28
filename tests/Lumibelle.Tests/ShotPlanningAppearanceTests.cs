using System.Text.Json;
using System.Text.Json.Nodes;
using lumibelle.Models;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed class ShotPlanningAppearanceTests
{
    private static (ShotPlanningRequest Request, JsonArray Response, ReferenceAsset Character) Fixture()
    {
        var character = LookFixtures.Character();
        var guard = new ReferenceAsset { Id = Guid.NewGuid(), Name = "Noxian Guard" };
        var doc = ScriptFixtures.Document();
        var request = new ShotPlanningRequest(ScriptFixtures.Approved(doc.Blocks, doc.ProjectId),
            new() { ProjectId = doc.ProjectId, Assets = [character, guard] }, [doc.Blocks[0].Id], 10, "",
            new(AiBackend.OpenRouter, "mock", "Mock"));
        var response = JsonSerializer.SerializeToNode(new object[] {
            new { title = "Transformation", sceneId = doc.Blocks[0].Id, sourceBlockIds = doc.Blocks.Select(b => b.Id),
                duration = 10, description = "Juniper's hoodie becomes armor as the camera holds.",
                dialogue = new[] { new { speaker = "JUNIPER", language = "English", text = "You called?" } },
                characters = new[] { new { name = "JUNIPER", appearance = new { assetId = character.Id, lookId = (Guid?)character.Looks[0].Id, endLookId = (Guid?)character.Looks[1].Id } } } },
            new { title = "The guard arrives", sceneId = doc.Blocks[0].Id, sourceBlockIds = doc.Blocks.Select(b => b.Id),
                duration = 10, description = "Juniper turns toward the guard in one continuous take.",
                dialogue = Array.Empty<object>(),
                characters = new[] {
                    new { name = "JUNIPER", appearance = new { assetId = character.Id, lookId = (Guid?)character.Looks[1].Id, endLookId = (Guid?)null } },
                    new { name = "NOXIAN GUARD", appearance = new { assetId = guard.Id, lookId = (Guid?)null, endLookId = (Guid?)null } }
                } }
        }, AtomicJsonFile.Options)!.AsArray();
        return (request, response, character);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KnownCharacterWithoutANamedLookDoesNotRejectTheWholeBreakdown(bool omitLook)
    {
        var (request, response, character) = Fixture();
        if (omitLook) response[1]!["characters"]![1]!["appearance"]!.AsObject().Remove("lookId");
        var raw = "```json\n" + response.ToJsonString() + "\n```";
        var result = ShotPlanner.Parse(raw, request);
        Assert.Null(result.Error);
        Assert.Equal(2, result.Shots.Count);
        Assert.Equal(raw, result.Raw);
        Assert.Empty(result.UncoveredDialogue);
        Assert.Equal("You called?", result.Shots[0].Dialogue[0].Text);
        Assert.Equal(character.Looks[0].Id, result.Shots[0].Characters[0].Appearance!.LookId);
        Assert.Equal(character.Looks[1].Id, result.Shots[0].Characters[0].Appearance!.EndLookId);
        Assert.Equal(character.Looks[1].Id, result.Shots[1].Characters[0].Appearance!.LookId);
        Assert.Null(result.Shots[1].Characters[0].Appearance!.EndLookId);
        var guard = result.Shots[1].Characters[1];
        Assert.Equal("NOXIAN GUARD", guard.Name);
        Assert.Null(guard.Appearance);
        Assert.NotEqual(Guid.Empty, guard.Id);
        Assert.All(result.Shots, shot => {
            H3Policy.Validate(shot, true);
            ShotLooks.Validate(shot, request.Assets);
            Assert.Contains("One continuous", H3Policy.Compile(shot, [], ShotLooks.Capture(shot, request.Assets)));
        });
    }

    [Theory]
    [InlineData("unknown-asset")]
    [InlineData("non-character-asset")]
    [InlineData("missing-start")]
    [InlineData("unknown-look")]
    [InlineData("malformed-look")]
    [InlineData("same-looks")]
    public void InvalidAppearancesStillRejectTheEntireResponse(string problem)
    {
        var (request, response, character) = Fixture();
        var appearance = response[1]!["characters"]![1]!["appearance"]!;
        switch (problem)
        {
            case "unknown-asset": appearance["assetId"] = Guid.NewGuid(); break;
            case "non-character-asset":
                request.Assets.Assets[1] = request.Assets.Assets[1] with { Category = AssetCategory.Prop }; break;
            case "missing-start": appearance["endLookId"] = character.Looks[1].Id; break;
            case "unknown-look": appearance["lookId"] = Guid.NewGuid(); break;
            case "malformed-look": appearance["lookId"] = "not-a-uuid"; break;
            case "same-looks":
                appearance = response[0]!["characters"]![0]!["appearance"]!;
                appearance["endLookId"] = character.Looks[0].Id; break;
        }
        var raw = response.ToJsonString();
        var result = ShotPlanner.Parse(raw, request);
        Assert.NotNull(result.Error);
        Assert.Empty(result.Shots);
        Assert.Equal(raw, result.Raw);
    }
}
