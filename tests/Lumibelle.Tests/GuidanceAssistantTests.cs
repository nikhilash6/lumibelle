using lumibelle.Models;
using lumibelle.Services.AI;
using Microsoft.Extensions.AI;

namespace Lumibelle.Tests;
public sealed class GuidanceAssistantTests
{
    private readonly Guid _project = Guid.NewGuid();
    private readonly ReferenceAsset _asset = new() { Id = Guid.NewGuid(), Name = "Mira", Description = "Round face; wears a hoodie.", Category = AssetCategory.Character,
        Looks = [new() { Name = "Armor", Description = "Gold armor", PreservationGuidance = "Gold trim" }],
        Images = [new() { Id = Guid.NewGuid(), FileName = "ref.png", ContentType = "image/png", Width = 80, Height = 40, PreservationGuidance = "Explicit image note" }] };
    private readonly EnhancementProviders _providers = new();
    private readonly FakeAiSettingsStore _settings = new() { Value = new() { DefaultBackend = AiBackend.OpenRouter, OpenRouterModel = "test/model", HasOpenRouterKey = true } };
    private FakeAssetStore Store => new(_project) { Library = new() { ProjectId = _project, Assets = [_asset] } };
    private GuidanceRequest Request(GuidanceScope scope) => new(GuidanceContext.From(new(_project, _asset.Id, scope, scope == GuidanceScope.Look ? _asset.Looks[0].Id : null, scope == GuidanceScope.Image ? _asset.Images[0].Id : null), _asset)!, new(AiBackend.OpenRouter, "test/model", "Vision"));
    private static async Task<List<PromptEnhancementUpdate>> Run(IGuidanceAssistant assistant, GuidanceRequest request, CancellationToken? cancellation = null)
    { List<PromptEnhancementUpdate> results = []; await foreach (var u in assistant.SuggestAsync(request, cancellation ?? TestContext.Current.CancellationToken)) results.Add(u); return results; }

    [Fact]
    public void ChangesSinceARequestAreNamedForTheAuthor()
    {
        var was = Request(GuidanceScope.Look).Context;
        Assert.Empty(GuidanceAssistant.Changes(was, was.Capture()));
        Assert.Equal(["the guidance was edited"], GuidanceAssistant.Changes(was, was with { Guidance = "Edited" }));
        Assert.Equal(["the name changed", "the notes changed"], GuidanceAssistant.Changes(was, was with { Name = "Robe", Notes = "Blue robe" }));
        Assert.Equal(["the source evidence changed"], GuidanceAssistant.Changes(was, was with { Evidence = [new("Scene", null, "Mira appears.")] }));
        Assert.Equal(["other target details changed, such as its category"], GuidanceAssistant.Changes(was, was with { Category = AssetCategory.Prop }));
        var description = Request(GuidanceScope.Image).Context with { Target = was.Target with { Scope = GuidanceScope.ImageDescription } };
        Assert.Equal(["the description was edited"], GuidanceAssistant.Changes(description, description with { Guidance = "Edited" }));
        Assert.Equal("the look", GuidanceAssistant.Subject(GuidanceScope.Look));
    }
    [Theory]
    [InlineData(GuidanceScope.CharacterIdentity, "Exclude clothing")]
    [InlineData(GuidanceScope.Look, "named look's clothing")]
    [InlineData(GuidanceScope.Image, "THIS reference image")]
    [InlineData(GuidanceScope.AssetFeatures, "layout, proportions, materials")]
    public async Task GuidanceScopesCaptureOnlyRelevantNotesAndNeverAttachImagesImplicitly(GuidanceScope scope, string contract)
    {
        var request = Request(scope); var context = request.Context;
        var evidence = new List<AssetSourceEvidence> { new("Scene", Guid.NewGuid(), "Mira appears.", Guid.NewGuid()) };
        request = request with { Context = context with { Evidence = evidence } };
        _providers.BeforeCheck = () => { evidence.Clear(); _settings.Value = _settings.Value with { Temperature = 1.9f }; };
        var results = await Run(new GuidanceAssistant(_providers, _settings, Store), request);
        Assert.NotNull(results[^1].Result); Assert.Contains(contract, _providers.Chat.Messages[0].Text);
        Assert.Contains("Mira appears.", _providers.Chat.Messages[1].Text);
        Assert.Null(_providers.Chat.Options!.Temperature); Assert.Null(_providers.Chat.Options.MaxOutputTokens); Assert.Equal("test/model", _providers.CreatedModel);
        Assert.Empty(_providers.Chat.Messages.SelectMany(m => m.Contents).OfType<DataContent>());
        if (scope == GuidanceScope.CharacterIdentity) Assert.DoesNotContain("Gold armor", _providers.Chat.Messages[1].Text);
        if (scope == GuidanceScope.Look) Assert.Equal("Gold armor", context.Notes);
        if (scope == GuidanceScope.Image) Assert.Equal("Explicit image note", context.Guidance);
        Assert.Equal(0, _settings.SaveCalls);
    }
    [Fact]
    public async Task ExplicitVisionUsesExactActiveImageAndRechecksCapabilities()
    {
        var request = Request(GuidanceScope.CharacterIdentity) with { InspectionImage = new(_asset.Id, _asset.Images[0].Id) };
        var store = Store; var service = new GuidanceAssistant(_providers, _settings, store);
        await Run(service, request); var image = Assert.Single(_providers.Chat.Messages[1].Contents.OfType<DataContent>());
        var info = SixLabors.ImageSharp.Image.Identify(image.Data.Span); Assert.Equal((80, 40), (info.Width, info.Height));
        Assert.DoesNotContain("/media/", _providers.Chat.Messages[1].Text);
        _providers.Models = [new("test/model", "Text only")]; _providers.CreatedModel = null;
        await Assert.ThrowsAsync<AiGenerationException>(() => Run(service, request)); Assert.Null(_providers.CreatedModel);
        _providers.Models = [new("test/model", "Vision", SupportsImages: true)]; store.Library = store.Library with { Assets = [_asset with { Images = [] }] };
        await Assert.ThrowsAsync<AiGenerationException>(() => Run(service, request)); Assert.Null(_providers.CreatedModel);
    }
    [Fact]
    public async Task InvalidTruncatedCancelledAndFailedRequestsCannotReturnGuidance()
    {
        var service = new GuidanceAssistant(_providers, _settings, Store); var request = Request(GuidanceScope.Look);
        _providers.Chat.Finish = ChatFinishReason.Length;
        var results = await Run(service, request); Assert.Null(results[^1].Result); Assert.Contains(results, u => u.Text is not null);
        _providers.Chat.Finish = ChatFinishReason.Stop; _providers.Chat.Output = "unfinished";
        Assert.Null((await Run(service, request))[^1].Result);
        _providers.Chat.Output = "{\"kind\":\"NeedsInput\",\"text\":\"Which detail?\"}";
        Assert.Equal(PromptEnhancementKind.NeedsInput, (await Run(service, request))[^1].Result!.Kind);
        _providers.Chat.Fail = true; await Assert.ThrowsAsync<AiGenerationException>(() => Run(service, request));
        _providers.Chat.Fail = false; _providers.Chat.Wait = true;
        using var cancel = new CancellationTokenSource(); var task = Run(service, request, cancel.Token); cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }
    [Fact]
    public async Task TargetSnapshotsDetectRelevantEditsAndDisappearance()
    {
        var request = Request(GuidanceScope.Look); var store = Store; var service = new GuidanceAssistant(_providers, _settings, store);
        var before = await service.ReadTargetAsync(request.Context.Target, TestContext.Current.CancellationToken);
        store.Library = store.Library with { Assets = [_asset with { Looks = [_asset.Looks[0] with { Description = "Silver armor" }] }] };
        Assert.NotEqual(before.Fingerprint(), (await service.ReadTargetAsync(request.Context.Target, TestContext.Current.CancellationToken)).Fingerprint());
        Assert.Equal("Gold armor", before.Notes);
        store.Library = store.Library with { Assets = [_asset with { Looks = [_asset.Looks[0] with { Archived = true }] }] };
        await Assert.ThrowsAsync<AiGenerationException>(() => service.ReadTargetAsync(request.Context.Target, TestContext.Current.CancellationToken));
    }
}
