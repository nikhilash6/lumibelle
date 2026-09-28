using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;

namespace Lumibelle.Tests;

public sealed class TextModelReasoningOffProfileTests
{
    private static readonly TextModelReference Off = new(AiBackend.OpenRouter, "deepseek/deepseek-v4-flash", "No thinking")
    { ProfileId = Guid.NewGuid(), ReasoningEffort = "none" };

    [Fact]
    public void OptionalReasoningProfileAllowsOffWithoutAdvertisingItAsAnEffort()
    {
        using var json = JsonDocument.Parse("""
            {"id":"deepseek/deepseek-v4-flash","supported_parameters":["reasoning","reasoning_effort","temperature"],
             "reasoning":{"mandatory":false,"supported_efforts":["xhigh","high"],"default_effort":"high"}}
            """);
        var info = OpenRouterModelMetadata.Read(json.RootElement);
        var catalog = new AiModel(Off.Model, "DeepSeek V4 Flash", Catalog: info);
        Assert.Equal(new[] { "xhigh", "high" }, info.SupportedReasoningEfforts);
        Assert.Equal(new[] { "none", "xhigh", "high" }, TextModelProfiles.OpenRouterEffortChoices(catalog));
        Assert.Null(TextModelProfiles.CatalogIssue(Off, catalog));
        Assert.Null(TextModelProfiles.CatalogIssue(Off with { ReasoningEffort = "high" }, catalog));
        Assert.Null(TextModelProfiles.CatalogIssue(Off with { ReasoningEffort = "xhigh" }, catalog));
        Assert.NotNull(TextModelProfiles.CatalogIssue(Off with { ReasoningEffort = "low" }, catalog));
        Assert.Equal(new[] { "xhigh", "high" }, info.SupportedReasoningEfforts); // No catalog mutation.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    [InlineData(true)]
    public void ProfileOffUsesMandatoryFlagRatherThanEffortMembership(bool? mandatory)
    {
        var info = new AiModelCatalogInfo(SupportsReasoning: true)
        { ReasoningMandatory = mandatory, SupportedParameters = ["reasoning"], SupportedReasoningEfforts = ["xhigh", "high"] };
        var catalog = new AiModel(Off.Model, "Test", Catalog: info);
        Assert.Equal(mandatory != true, TextModelProfiles.OpenRouterEffortChoices(catalog).Contains("none"));
        Assert.Equal(mandatory != true, TextModelProfiles.CatalogIssue(Off, catalog) is null);
    }

    [Fact]
    public void MandatoryReasoningProfilesNeverOfferOffEvenWithConflictingOrUnknownEffortMetadata()
    {
        foreach (var efforts in new IReadOnlyList<string>?[] { null, new[] { "none", "high" }, Array.Empty<string>() })
        {
            var info = new AiModelCatalogInfo(SupportsReasoning: true)
            { ReasoningMandatory = true, SupportedReasoningEfforts = efforts };
            var catalog = new AiModel(Off.Model, "Mandatory", Catalog: info);
            Assert.DoesNotContain("none", TextModelProfiles.OpenRouterEffortChoices(catalog));
            Assert.Contains("requires reasoning", TextModelProfiles.CatalogIssue(Off, catalog)!);
        }
    }

    [Fact]
    public void OptionalReasoningProfileWithoutEffortSelectionStillOffersOff()
    {
        using var json = JsonDocument.Parse("""
            {"supported_parameters":["reasoning"],"reasoning":{"mandatory":false}}
            """);
        var info = OpenRouterModelMetadata.Read(json.RootElement);
        Assert.Empty(info.SupportedReasoningEfforts!);
        var catalog = new AiModel(Off.Model, "Toggle only", Catalog: info);
        Assert.Equal(new[] { "none" }, TextModelProfiles.OpenRouterEffortChoices(catalog));
        Assert.Null(TextModelProfiles.CatalogIssue(Off, catalog));
        Assert.NotNull(TextModelProfiles.CatalogIssue(Off with { ReasoningEffort = "high" }, catalog));
    }

    [Fact]
    public void KnownNonReasoningProfilesDoNotOfferOrAcceptReasoningOverrides()
    {
        var info = new AiModelCatalogInfo { SupportedParameters = ["temperature"] };
        var catalog = new AiModel(Off.Model, "Non-reasoning", Catalog: info);
        Assert.Empty(TextModelProfiles.OpenRouterEffortChoices(catalog));
        Assert.Contains("does not advertise reasoning", TextModelProfiles.CatalogIssue(Off, catalog)!);
    }

    [Fact]
    public void ProfileOffKeepsUnknownMetadataFallbackAndDoesNotDuplicateNone()
    {
        Assert.Equal(TextModelProfiles.OpenRouterEfforts, TextModelProfiles.OpenRouterEffortChoices(null));
        Assert.Null(TextModelProfiles.CatalogIssue(Off, null));
        var info = new AiModelCatalogInfo(SupportsReasoning: true)
        { ReasoningMandatory = false, SupportedReasoningEfforts = ["high", "none", "none"] };
        var catalog = new AiModel(Off.Model, "Test", Catalog: info);
        Assert.Equal(new[] { "none", "high" }, TextModelProfiles.OpenRouterEffortChoices(catalog));
    }

    [Fact]
    public void ProfileOffDoesNotBypassOtherCapabilityChecks()
    {
        var info = new AiModelCatalogInfo(MaxOutputTokens: 4096, SupportsReasoning: true)
        { SupportedParameters = ["reasoning"], ReasoningMandatory = false, SupportedReasoningEfforts = ["high"] };
        var catalog = new AiModel(Off.Model, "Test", Catalog: info);
        Assert.Contains("temperature", TextModelProfiles.CatalogIssue(Off with { Temperature = .5f }, catalog)!);
        Assert.Contains("output limit", TextModelProfiles.CatalogIssue(Off with { MaxOutputTokens = 8192 }, catalog)!);
    }
}
