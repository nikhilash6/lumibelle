using lumibelle.Models;
using lumibelle.Services.AI;

namespace Lumibelle.Tests;

public sealed partial class AiTextJobTests
{
    private static readonly OpenRouterRequestUsage ReportedUsage = new("generation", "actual/model", "Provider", 30, 50, 10, 2, .0000001m);

    [Fact]
    public async Task CancellationCannotDiscardAReceiptObservedByTheCurrentWorker()
    {
        var request = await Request(AiJobKind.Guidance); var context = await Claim(request);
        await context.SaveResultAsync(new AiTextJobResult("Partial answer"));
        var before = await Store.UpdateAsync(request.Id, j => j with { CancelRequested = true }, _ct);
        await context.SaveObservedUsageAsync(ReportedUsage);
        var saved = (await Store.ReadArtifactAsync<AiTextJobResult>(request.Id, AiJobArtifact.Result, _ct))!;
        Assert.Equal("Partial answer", saved.Raw); Assert.Equal(ReportedUsage, saved.OpenRouterUsage);
        Assert.Equal(before.Version, (await Store.ReadAsync(_ct)).Jobs.Single().Version);
        await Assert.ThrowsAsync<AiJobLeaseException>(() => Store.WriteObservedUsageAsync(request.Id, Guid.NewGuid(), ReportedUsage, _ct));
    }

    [Theory]
    [InlineData(AiJobKind.ScriptAssistant)] [InlineData(AiJobKind.AssetExtraction)] [InlineData(AiJobKind.ShotPlanning)]
    [InlineData(AiJobKind.PromptEnhancement)] [InlineData(AiJobKind.Guidance)]
    public async Task EveryHostedTextPathRetainsReportedUsageInParsedAndImmutableResults(AiJobKind kind)
    {
        var request = await Request(kind); var context = await Claim(request);
        _providers.Chat.Output = Response(kind); _providers.Chat.Usage = ReportedUsage;
        await Handler().ExecuteAsync(context, request.Snapshot, _ct);
        Assert.Equal(ReportedUsage, (await Store.ReadArtifactAsync<AiTextJobResult>(request.Id, AiJobArtifact.Result, _ct))!.OpenRouterUsage);
        Assert.Equal(ReportedUsage, (await Store.ReadOperationAsync<AiTextJobResult>(request.Id, "text", AiOperationArtifact.Output, _ct))!.OpenRouterUsage);
        _providers.Created = 0;
        await Handler().RecoverAsync(await Recover(context), request.Snapshot, _ct);
        Assert.Equal(0, _providers.Created);
        Assert.Equal(ReportedUsage, (await Store.ReadArtifactAsync<AiTextJobResult>(request.Id, AiJobArtifact.Result, _ct))!.OpenRouterUsage);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ObservedUsageSurvivesDisconnectionOrCancellation(bool cancel)
    {
        var request = await Request(AiJobKind.Guidance); var context = await Claim(request);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        _providers.Chat.Output = "Partial reply"; _providers.Chat.Usage = ReportedUsage;
        _providers.Chat.FailAfterUsage = !cancel;
        if (cancel) _providers.Chat.AfterUsage = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<Exception>(() => Handler().ExecuteAsync(context, request.Snapshot, cancellation.Token));
        var saved = (await Store.ReadArtifactAsync<AiTextJobResult>(request.Id, AiJobArtifact.Result, _ct))!;
        Assert.Equal(ReportedUsage, saved.OpenRouterUsage); Assert.Equal("Partial reply", saved.Raw); Assert.False(saved.Complete);
    }

    [Fact]
    public async Task InvalidJsonAndOutputRecoveryRetainChargesWithoutAnotherRequest()
    {
        var request = await Request(AiJobKind.ScriptAssistant); var context = await Claim(request);
        await context.SaveOperationAsync("text", AiOperationArtifact.Output,
            new AiTextJobResult("invalid json", true, "stop") { OpenRouterUsage = ReportedUsage }, _ct);
        await Handler().RecoverAsync(await Recover(context), request.Snapshot, _ct);
        var result = (await Store.ReadArtifactAsync<AiTextJobResult>(request.Id, AiJobArtifact.Result, _ct))!;
        Assert.NotNull(result.Error); Assert.Equal(ReportedUsage, result.OpenRouterUsage); Assert.Equal(0, _providers.Created);
    }
}
