using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed class TextModelProfileTests
{
    private static TextModelReference Profile(string name = "Careful") => new(AiBackend.OpenRouter, "test/model", name)
    { ProfileId = Guid.NewGuid(), ReasoningEffort = "high", Temperature = .3f, MaxOutputTokens = 4096 };

    [Fact]
    public void ProfilesHaveSeparateChoiceIdentityButShareProviderIdentityAndAliases()
    {
        var first = Profile(); var second = first with { ProfileId = Guid.NewGuid(), Name = "Creative", Temperature = 1.2f };
        var bare = TextModelProfiles.ModelOnly(first);
        var settings = new AiSettings { TextModelAliases = new() { [TextModelPolicy.Key(bare)] = "Friendly model" } };
        Assert.True(TextModelPolicy.Same(first, second));
        Assert.Equal(TextModelPolicy.Key(first), TextModelPolicy.Key(bare));
        Assert.NotEqual(TextModelProfiles.ChoiceKey(first), TextModelProfiles.ChoiceKey(second));
        Assert.NotEqual(TextModelProfiles.ChoiceKey(first), TextModelProfiles.ChoiceKey(bare));
        Assert.Equal("Careful", TextModelPolicy.DisplayName(first, settings, "Catalog model"));
        Assert.Equal("Friendly model", TextModelPolicy.DisplayName(bare, settings));
    }

    [Fact]
    public void EditedAndRenamedSnapshotsRemainDistinctChoices()
    {
        var original = Profile();
        foreach (var updated in new[] { original with { Temperature = .8f }, original with { MaxOutputTokens = 8192 },
            original with { ReasoningEffort = "low" }, original with { Name = "Renamed" },
            original with { ReasoningEffort = null, ReasoningMaxTokens = 1024 } })
        {
            Assert.NotEqual(TextModelProfiles.ChoiceKey(original), TextModelProfiles.ChoiceKey(updated));
            Assert.False(TextModelProfiles.SameConfiguration(original, updated));
            Assert.False(TextModelSession.SameChoice(new(original, true), new(updated, true)));
        }
    }

    [Fact]
    public void AdditiveSerializationPreservesOldChoicesAndRoundTripsProfiles()
    {
        var bare = new TextModelReference(AiBackend.OpenRouter, "test/model", "Original");
        var legacyJson = JsonSerializer.Serialize(bare, AtomicJsonFile.Options);
        Assert.DoesNotContain("profileId", legacyJson);
        Assert.DoesNotContain("temperature", legacyJson);
        Assert.Equal(bare, JsonSerializer.Deserialize<TextModelReference>(legacyJson, AtomicJsonFile.Options));
        var profile = Profile();
        var settings = new AiSettings { TextModelProfiles = [profile], TextDefault = profile, StarredTextModels = [bare] };
        var loaded = JsonSerializer.Deserialize<AiSettings>(JsonSerializer.Serialize(settings, AtomicJsonFile.Options), AtomicJsonFile.Options)!;
        FileAiSettingsStore.Validate(loaded);
        Assert.Equal(profile, Assert.Single(loaded.TextModelProfiles));
        Assert.Equal(profile, TextModelPolicy.Default(loaded));
        var deleted = loaded with { TextModelProfiles = [] };
        FileAiSettingsStore.Validate(deleted);
        Assert.Equal(profile, TextModelPolicy.Default(deleted));
        var legacy = JsonSerializer.Deserialize<AiSettings>("{}", AtomicJsonFile.Options)!;
        Assert.Empty(legacy.TextModelProfiles); Assert.Null(legacy.TextDefault);
    }

    [Fact]
    public void InvalidCombinationsFailInsteadOfBeingSilentlyIgnored()
    {
        var profile = Profile();
        foreach (var invalid in new[] { profile with { ProfileId = Guid.Empty }, profile with { Temperature = float.NaN },
            profile with { Temperature = float.PositiveInfinity }, profile with { Temperature = -1 }, profile with { Temperature = 2.1f },
            profile with { MaxOutputTokens = 0 }, profile with { ReasoningMaxTokens = 1024 },
            profile with { ReasoningEffort = null, ReasoningMaxTokens = 4096 },
            profile with { ReasoningEffort = null, ReasoningMaxTokens = -1 },
            profile with { ReasoningEffort = "invented" }, profile with { Name = new string('x', 121) },
            profile with { Backend = AiBackend.Codex },
            profile with { Backend = AiBackend.ComfyUI, ComfyUrl = "http://localhost:8188" } })
            Assert.Throws<WorkspaceStoreException>(() => TextModelPolicy.Validate(invalid));
        TextModelPolicy.Validate(profile with { Temperature = 0 });
        TextModelPolicy.Validate(profile with { ReasoningEffort = null, ReasoningMaxTokens = 1024 });
        var local = new TextModelReference(AiBackend.ComfyUI, "local", "Local", "http://localhost:8188") { ProfileId = Guid.NewGuid() };
        Assert.Throws<WorkspaceStoreException>(() => TextModelPolicy.Validate(local with { Temperature = 0 }));
        Assert.Throws<WorkspaceStoreException>(() => TextModelPolicy.Validate(local with { MaxOutputTokens = 32769 }));
        TextModelPolicy.Validate(local with { Temperature = .01f, MaxOutputTokens = 32768 });
    }

    [Fact]
    public void LibraryRejectsDuplicateIdsAndNamesWithoutCollapsingDistinctProfiles()
    {
        var first = Profile(); var second = first with { ProfileId = Guid.NewGuid(), Name = "Fast", ReasoningEffort = "low" };
        TextModelProfiles.ValidateSettings(new() { TextModelProfiles = [first, second] });
        Assert.Throws<WorkspaceStoreException>(() => TextModelProfiles.ValidateSettings(new() { TextModelProfiles = [null!] }));
        Assert.Throws<WorkspaceStoreException>(() => TextModelProfiles.ValidateSettings(new() { TextModelProfiles = [first, second with { ProfileId = first.ProfileId }] }));
        Assert.Throws<WorkspaceStoreException>(() => TextModelProfiles.ValidateSettings(new() { TextModelProfiles = [first, second with { Name = " careful " }] }));
        Assert.Throws<WorkspaceStoreException>(() => TextModelProfiles.ValidateSettings(new() { TextModelProfiles = [first with { ProfileId = null }] }));
    }

    [Fact]
    public void HostedDefaultsRemainOmittedAndComfyOverridesBeatExtractionTemperature()
    {
        var settings = new AiSettings { Temperature = 1.7f, MaxOutputTokens = 24000 };
        var defaults = TextGenerationOptions.Create(AiBackend.OpenRouter, settings, selection: Profile() with { Temperature = null, MaxOutputTokens = null, ReasoningEffort = null });
        Assert.Null(defaults.Temperature); Assert.Null(defaults.MaxOutputTokens); Assert.Null(defaults.RawRepresentationFactory);
        var local = new TextModelReference(AiBackend.ComfyUI, "local", "Creative", "http://localhost:8188")
        { ProfileId = Guid.NewGuid(), Temperature = .9f, MaxOutputTokens = 5000 };
        var configured = TextGenerationOptions.Create(AiBackend.ComfyUI, settings, temperature: .2f, selection: local);
        Assert.Equal(.9f, configured.Temperature); Assert.Equal(5000, configured.MaxOutputTokens);
        var fallback = TextGenerationOptions.Create(AiBackend.ComfyUI, settings, temperature: .2f, selection: local with { Temperature = null, MaxOutputTokens = null });
        Assert.Equal(.2f, fallback.Temperature); Assert.Equal(24000, fallback.MaxOutputTokens);
    }

    [Fact]
    public void CapturedRequestUsesModelSnapshotNotCurrentLibraryAndPreservesVersionOne()
    {
        var profile = Profile();
        var settings = new AiSettings { Temperature = .6f, MaxOutputTokens = 1000, TextModelProfiles = [profile with { Temperature = 1.5f }] };
        var request = new AiTextJobRequest(3, AiJobKind.ScriptAssistant, profile, false, settings, "template", .3f, 123,
            JsonSerializer.SerializeToElement(new { }), []);
        var options = TextGenerationOptions.Captured(request);
        Assert.Equal(.3f, options.Temperature); Assert.Equal(4096, options.MaxOutputTokens); Assert.Equal(123L, options.Seed);
        options = TextGenerationOptions.Captured(request with { Version = 1, Temperature = .6f });
        Assert.Equal(.6f, options.Temperature); Assert.Equal(1000, options.MaxOutputTokens); Assert.Null(options.RawRepresentationFactory);
    }

    [Fact]
    public void CodexProfilesCanChooseTheModelDefaultWithoutInheritingGlobalEffort()
    {
        var settings = new AiSettings { Codex = new() { TextEffort = "high" } };
        var bare = new TextModelReference(AiBackend.Codex, "test", "Codex");
        var profile = bare with { ProfileId = Guid.NewGuid(), Name = "Model default" };
        Assert.Equal("high", TextModelPolicy.WithDefaultEffort(bare, settings).ReasoningEffort);
        Assert.Null(TextModelPolicy.WithDefaultEffort(profile, settings).ReasoningEffort);
        var options = TextGenerationOptions.Create(AiBackend.Codex, settings, selection: profile);
        Assert.True(options.AdditionalProperties!.ContainsKey(TextGenerationOptions.ReasoningEffortKey));
        Assert.Null(options.AdditionalProperties[TextGenerationOptions.ReasoningEffortKey]);
    }

    [Fact]
    public void CatalogReportsCapabilitiesWithoutTreatingMissingMetadataAsFalse()
    {
        using var json = JsonDocument.Parse("""
            {"id":"test/model","supported_parameters":["reasoning","max_tokens"],"top_provider":{"max_completion_tokens":8192},
             "reasoning":{"supported_efforts":["low","high"],"supports_max_tokens":true,"mandatory":true}}
            """);
        var info = OpenRouterModelMetadata.Read(json.RootElement);
        Assert.True(info.SupportsReasoning); Assert.True(info.SupportsReasoningBudget); Assert.True(info.ReasoningMandatory);
        Assert.Equal(new[] { "low", "high" }, info.SupportedReasoningEfforts);
        var model = new AiModel("test/model", "Test", Catalog: info);
        Assert.NotNull(TextModelProfiles.CatalogIssue(Profile(), model)); // temperature not advertised
        Assert.Null(TextModelProfiles.CatalogIssue(Profile() with { Temperature = null }, model));
        Assert.NotNull(TextModelProfiles.CatalogIssue(Profile() with { Temperature = null, ReasoningEffort = "none" }, model));
        Assert.NotNull(TextModelProfiles.CatalogIssue(Profile() with { Temperature = null, ReasoningEffort = null, ReasoningMaxTokens = 8192, MaxOutputTokens = null }, model));
        using var unknown = JsonDocument.Parse("{}");
        var unreported = OpenRouterModelMetadata.Read(unknown.RootElement);
        Assert.Null(unreported.SupportedParameters); Assert.Null(unreported.SupportsReasoningBudget);
        Assert.Null(TextModelProfiles.CatalogIssue(Profile(), new("test/model", "Test", Catalog: unreported)));
        using var omitted = JsonDocument.Parse("""{"reasoning":{}}""");
        Assert.Empty(OpenRouterModelMetadata.Read(omitted.RootElement).SupportedReasoningEfforts!);
        Assert.False(OpenRouterModelMetadata.Read(omitted.RootElement).SupportsReasoningBudget);
        using var all = JsonDocument.Parse("""{"reasoning":{"supported_efforts":null}}""");
        Assert.Null(OpenRouterModelMetadata.Read(all.RootElement).SupportedReasoningEfforts);
    }
}
