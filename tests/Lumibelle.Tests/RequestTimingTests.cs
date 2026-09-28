using lumibelle.Models;
using lumibelle.Services.AI;

namespace Lumibelle.Tests;

public sealed class RequestTimingTests
{
    private readonly ManualClock _clock = new(DateTimeOffset.Parse("2026-09-13T10:00:00Z"));
    private GenerationProgress Step(GenerationProgressTracker tracker, double value, double seconds = 2, double total = 20, string node = "sample", string scope = "sampling")
    {
        _clock.Advance(TimeSpan.FromSeconds(seconds));
        return tracker.SetProgress(GenerationPhase.Generating, "Sampling", value, total, "steps", node, scope);
    }

    [Fact]
    public void SlowSamplingAndColdStartUseThreeIntervalsAndMedianRatherThanAWindow()
    {
        var tracker = new GenerationProgressTracker(_clock);
        Step(tracker, 0, 0);
        Assert.Null(Step(tracker, 1, 120).EstimatedRemaining);
        Assert.Null(Step(tracker, 2, 20).EstimatedRemaining);
        var progress = Step(tracker, 3, 20);
        Assert.Equal(TimeSpan.FromSeconds(340), progress.EstimatedRemaining);
        Assert.Equal("sampling", progress.EstimateScope);
        Assert.Equal(_clock.GetUtcNow(), progress.EstimateObservedUtc);
        // Five advancing intervals, even when they span much more than eight seconds.
        Step(tracker, 4, 10); Step(tracker, 5, 10); Step(tracker, 6, 10);
        Assert.Equal(TimeSpan.FromSeconds(140), tracker.Snapshot().EstimatedRemaining);
    }

    [Fact]
    public void DuplicateUpdatesAndHeartbeatsDoNotManufactureObservations()
    {
        var tracker = new GenerationProgressTracker(_clock);
        Step(tracker, 0, 0); Step(tracker, 1); Step(tracker, 1); Step(tracker, 1);
        tracker.MarkEmitted(); Assert.Null(tracker.Snapshot().EstimatedRemaining);
        Step(tracker, 2); var progress = Step(tracker, 3);
        Assert.Equal(TimeSpan.FromSeconds(34), progress.EstimatedRemaining);
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal("≈32s left in sampling", ProgressTiming.Estimate(tracker.Snapshot(), _clock.GetUtcNow()));
        Assert.Equal(progress.EstimateObservedUtc, tracker.Snapshot().EstimateObservedUtc);
    }

    [Theory]
    [InlineData("node")]
    [InlineData("total")]
    [InlineData("counter")]
    [InlineData("phase")]
    [InlineData("scope")]
    [InlineData("disconnect")]
    public void NewStagesAndDisconnectionRequireFreshObservations(string change)
    {
        var tracker = new GenerationProgressTracker(_clock);
        for (var i = 0; i < 4; i++) Step(tracker, i);
        Assert.NotNull(tracker.Snapshot().EstimatedRemaining);
        switch (change) {
            case "node": Step(tracker, 4, node: "candidate-two"); break;
            case "total": Step(tracker, 4, total: 40); break;
            case "counter": Step(tracker, 0); break;
            case "phase": tracker.SetStage(GenerationPhase.Finalizing, "Decoding"); break;
            case "scope": Step(tracker, 4, scope: "other stage"); break;
            case "disconnect": tracker.SetLiveUpdatesAvailable(false); tracker.SetLiveUpdatesAvailable(true); Step(tracker, 4); break;
        }
        Assert.Null(tracker.Snapshot().EstimatedRemaining);
    }

    [Fact]
    public void StallsExpiryAndOldCheckpointsHideTheEstimate()
    {
        var tracker = new GenerationProgressTracker(_clock);
        for (var i = 0; i < 4; i++) Step(tracker, i);
        var checkpoint = tracker.Snapshot();
        Assert.Null(ProgressTiming.Estimate(checkpoint with { EstimateScope = null }, _clock.GetUtcNow()));
        Assert.Null(ProgressTiming.Estimate(checkpoint with { EstimateObservedUtc = null }, _clock.GetUtcNow()));
        Assert.Null(ProgressTiming.Estimate(checkpoint with { LiveUpdatesAvailable = false }, _clock.GetUtcNow()));
        _clock.Advance(TimeSpan.FromSeconds(16));
        Assert.Null(tracker.Snapshot().EstimatedRemaining);
        Assert.Null(ProgressTiming.Estimate(checkpoint, _clock.GetUtcNow()));
        Assert.Null(Step(tracker, 4).EstimatedRemaining);
        Step(tracker, 5); Step(tracker, 6);
        Assert.NotNull(Step(tracker, 7).EstimatedRemaining);
        var nearEnd = Step(tracker, 19);
        _clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Null(ProgressTiming.Estimate(nearEnd, _clock.GetUtcNow()));
        Assert.Null(tracker.Snapshot().EstimatedRemaining);
    }

    [Fact]
    public void UnknownTextOutputLengthNeverUsesTheTokenBudgetAsAnEstimate()
    {
        var tracker = new GenerationProgressTracker(_clock);
        for (var i = 0; i < 8; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(3));
            var progress = tracker.SetProgress(GenerationPhase.Generating, "Writing", i * 100, 4000, "tokens", "text");
            Assert.Null(progress.EstimatedRemaining);
            Assert.Null(ProgressTiming.Estimate(progress, _clock.GetUtcNow()));
        }
    }

    [Fact]
    public void MinimumSpanAndQueueVsRunClocksAreExplicit()
    {
        var tracker = new GenerationProgressTracker(_clock);
        for (var i = 0; i < 4; i++) Step(tracker, i, .1);
        Assert.Null(tracker.Snapshot().EstimatedRemaining);
        var now = _clock.GetUtcNow();
        var job = new AiJobHeader { Id = Guid.NewGuid(), Kind = AiJobKind.ShotPlanning, Backend = AiBackend.OpenRouter, Target = new(Guid.NewGuid()), ProjectName = "Test", TargetName = "Draft shots", OriginTabId = Guid.NewGuid(), RequestFingerprint = "test", State = AiJobState.Waiting, CreatedUtc = now.AddSeconds(-42) };
        Assert.Equal("42s waiting", ProgressTiming.ForJob(job, null, now));
        job = job with { State = AiJobState.Running, StartedUtc = now.AddSeconds(-10) };
        Assert.Equal("10s elapsed", ProgressTiming.ForJob(job, null, now));
    }
}
