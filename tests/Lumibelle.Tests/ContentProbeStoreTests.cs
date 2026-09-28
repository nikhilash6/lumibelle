using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed class ContentProbeStoreTests
{
    [Fact]
    public async Task CustomCrudAndStarterDisableDoNotChangeHistoricalRequests()
    {
        using var f = new ContentProbeFixture();
        var library = await f.Probes.LoadLibraryAsync(f.Ct);
        var test = ContentProbeFixture.Test();
        library = await f.Probes.SaveProbeAsync(test, library.Revision, f.Ct);
        var request = ContentProbeFixture.Request(test);
        var submission = ContentProbeCapture.Submission(request, Guid.NewGuid());
        await f.Jobs.EnqueueAsync(submission, f.Ct);
        var saved = Assert.Single(library.Custom);
        library = await f.Probes.SaveProbeAsync(saved with { Prompt = "A revised test." }, library.Revision, f.Ct);
        Assert.Equal(2, Assert.Single(library.Custom).Revision);
        library = await f.Probes.DeleteProbeAsync(saved.Id, library.Revision, f.Ct);
        Assert.Empty(library.Custom);
        var starter = ContentProbeBuiltIns.All(library).First();
        library = await f.Probes.SetEnabledAsync(starter.Id, false, library.Revision, f.Ct);
        Assert.False(ContentProbeBuiltIns.All(await f.Probes.LoadLibraryAsync(f.Ct)).Single(p => p.Id == starter.Id).Enabled);
        var header = Assert.Single((await f.Jobs.ReadAsync(f.Ct)).Jobs);
        Assert.Equal(test.Prompt, ContentProbePolicy.Read(header, await f.Jobs.ReadSnapshotAsync(header.Id, f.Ct)).Probe.Prompt);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Probes.SaveProbeAsync(starter, library.Revision, f.Ct));
    }
    [Fact]
    public async Task InvalidConflictingAndCancelledSavesPreserveTheLibrary()
    {
        using var f = new ContentProbeFixture(); var test = ContentProbeFixture.Test();
        var saved = await f.Probes.SaveProbeAsync(test, 0, f.Ct);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => f.Probes.SaveProbeAsync(test with { Prompt = "Lost update" }, 0, f.Ct));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Probes.SaveProbeAsync(test with { RequiredPhrases = ["same", "SAME"] }, saved.Revision, f.Ct));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Probes.DeleteProbeAsync(test.Id, saved.Revision, cancelled.Token));
        Assert.Equal(test.Prompt, Assert.Single((await f.Probes.LoadLibraryAsync(f.Ct)).Custom).Prompt);
    }
    [Fact]
    public async Task RatingsPersistClearAndUsePerResponseOptimisticConcurrency()
    {
        using var f = new ContentProbeFixture();
        var a = await f.CompletedAsync(); var b = await f.CompletedAsync();
        ContentProbeReview Draft(ContentProbeRow row, int score) => row.Review with
        { Score = score, Notes = "A useful scene.", OutputFingerprint = ContentProbePolicy.OutputFingerprint(row.Result!) };
        var results = await Task.WhenAll(f.Probes.SaveReviewAsync(Draft(a, 5), f.Ct), f.Probes.SaveReviewAsync(Draft(b, 3), f.Ct));
        Assert.Equal(5, (await f.Probes.LoadReviewAsync(a.Job.Id, f.Ct)).Score);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => f.Probes.SaveReviewAsync(Draft(a, 1), f.Ct));
        var clear = await f.Probes.SaveReviewAsync(results[0] with { Score = null }, f.Ct);
        Assert.Null(clear.Score); Assert.Equal("A useful scene.", clear.Notes);
        Assert.Equal(3, (await f.Probes.LoadReviewAsync(b.Job.Id, f.Ct)).Score);
        Assert.Equal(0, f.Providers.Creates);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public async Task InvalidScoresCannotBeSaved(int score)
    {
        using var f = new ContentProbeFixture(); var row = await f.CompletedAsync();
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Probes.SaveReviewAsync(row.Review with
        { Score = score, OutputFingerprint = ContentProbePolicy.OutputFingerprint(row.Result!) }, f.Ct));
        Assert.Null((await f.Probes.LoadReviewAsync(row.Job.Id, f.Ct)).Score);
    }
    [Theory]
    [InlineData("length")]
    [InlineData("content_filter")]
    [InlineData(null)]
    public async Task InconclusiveAndBlockedResponsesCannotAcquireRatings(string? finish)
    {
        using var f = new ContentProbeFixture(); var row = await f.CompletedAsync(result: new("NO", true, finish));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Probes.SaveReviewAsync(row.Review with
        { Score = 1, OutputFingerprint = ContentProbePolicy.OutputFingerprint(row.Result!) }, f.Ct));
    }
    [Fact]
    public async Task AStaleOutputFingerprintCannotRateANewerResponse()
    {
        using var f = new ContentProbeFixture(); var row = await f.CompletedAsync();
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Probes.SaveReviewAsync(row.Review with
        { Score = 5, OutputFingerprint = new('B', 64) }, f.Ct));
        Assert.Equal(0, (await f.Probes.LoadReviewAsync(row.Job.Id, f.Ct)).Revision);
    }
}
