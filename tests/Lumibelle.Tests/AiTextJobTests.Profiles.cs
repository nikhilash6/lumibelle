using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed partial class AiTextJobTests
{
    [Theory]
    [InlineData(AiJobKind.ScriptAssistant)]
    [InlineData(AiJobKind.AssetExtraction)]
    [InlineData(AiJobKind.ShotPlanning)]
    [InlineData(AiJobKind.PromptEnhancement)]
    [InlineData(AiJobKind.Guidance)]
    public async Task TextProfilesFreezeOverridesForEveryQueuedTextOperation(AiJobKind kind)
    {
        _model = _model with { ProfileId = Guid.NewGuid(), Name = "Careful", Temperature = 0, MaxOutputTokens = 4096, ReasoningMaxTokens = 1024 };
        var submittedModel = _model;
        _settings.Value = _settings.Value with { TextModelProfiles = [_model] };
        var submission = await Request(kind);
        var captured = submission.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        Assert.Equal(3, captured.Version); Assert.Equal(submittedModel, captured.Model); Assert.Equal(0f, captured.Temperature);
        // Changing the library/default after queueing must not alter the outgoing request.
        _model = _model with { Temperature = 1.5f, MaxOutputTokens = 8000, ReasoningMaxTokens = 2048 };
        _settings.Value = _settings.Value with { TextModelProfiles = [], TextDefault = _model };
        _providers.Chat.Output = Response(kind);
        var context = await Claim(submission);
        Assert.Throws<WorkspaceStoreException>(() => AiTextJobHandler.Read(context.Job,
            JsonSerializer.SerializeToElement(captured with { Version = 2 }, AtomicJsonFile.Options)));
        var outcome = await Handler().ExecuteAsync(context, submission.Snapshot, _ct);
        Assert.Equal(AiJobState.Completed, outcome.State);
        Assert.Equal(0f, _providers.Chat.Options!.Temperature);
        Assert.Equal(4096, _providers.Chat.Options.MaxOutputTokens);
        Assert.NotNull(_providers.Chat.Options.RawRepresentationFactory);
        Assert.Equal(submittedModel, captured.Model);
    }

    [Fact]
    public async Task AttributionRejectsAProfileChangedBetweenSelectionAndCapture()
    {
        _model = _model with { ProfileId = Guid.NewGuid(), Temperature = .3f, Name = "Careful" };
        var submission = await Request(AiJobKind.ScriptAssistant);
        var selected = new TextModelSelectionState(_model with { Temperature = .7f }, true);
        Assert.Throws<WorkspaceStoreException>(() => TextModelSession.Attribute(submission, selected));
        var accepted = TextModelSession.Attribute(submission, new(_model, true, Source: TextModelSelectionSource.ProjectDefault));
        Assert.Equal(TextModelSelectionSource.ProjectDefault, accepted.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!.SelectionSource);
    }

    [Fact]
    public async Task CodexProfileModelDefaultIsCapturedIndependentlyOfGlobalEffort()
    {
        var mock = new Lumibelle.Testing.MockCodexTransport { Text = Response(AiJobKind.PromptEnhancement) };
        await using var client = new CodexClient(mock, TimeProvider.System); _codex = client;
        _settings.Value = _settings.Value with { Codex = new() { Enabled = true, TextEffort = "low" } };
        _model = new(AiBackend.Codex, "mock-codex", "Model default") { ProfileId = Guid.NewGuid() };
        _providers.Model = _model.Model;
        var expectedEffort = (await client.CheckAsync(_settings.Value.Codex, _ct)).Models.Single(m => m.Id == _model.Model).DefaultEffort;
        Assert.NotNull(expectedEffort);
        var submission = await Request(AiJobKind.PromptEnhancement);
        var captured = submission.Snapshot.Deserialize<AiTextJobRequest>(AtomicJsonFile.Options)!;
        Assert.Null(captured.Model.ReasoningEffort);
        Assert.Equal(expectedEffort, captured.Codex!.Effort);
        _settings.Value = _settings.Value with { Codex = _settings.Value.Codex with { TextEffort = "high" } };
        Assert.Equal(AiJobState.Completed, (await Handler().ExecuteAsync(await Claim(submission), submission.Snapshot, _ct)).State);
        Assert.Equal(expectedEffort, Assert.Single(mock.Inputs).GetProperty("effort").GetString());
    }
}
