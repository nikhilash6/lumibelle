using System.Net;
using System.Text;
using lumibelle.Models;
using lumibelle.Services.AI;
using Microsoft.Extensions.AI;

namespace Lumibelle.Tests;

public sealed class TextInactivityTests
{
    [Fact]
    public void AdvancingTextReasoningAndTokensHaveNoTotalDeadline()
    {
        var clock = new TimerClock(); using var watcher = new TextInactivityWatchdog(10, clock: clock);
        for (var i = 0; i < 20; i++)
        {
            clock.Advance(9);
            watcher.Observe((i % 3) switch {
                0 => new ProgressingChatUpdate(new ChatResponseUpdate(ChatRole.Assistant, "answer")),
                1 => new ProgressingChatUpdate(Activity: true),
                _ => new ProgressingChatUpdate(Progress: new(GenerationPhase.Generating, "Writing", i, 100, "tokens")) });
            Assert.False(watcher.Token.IsCancellationRequested);
        }
        clock.Advance(10); Assert.True(watcher.Expired); Assert.Contains("10 seconds", watcher.Message);
    }

    [Fact]
    public void HeartbeatsEmptyDeltasRepeatedCountersAndLabelsDoNotKeepARequestAlive()
    {
        var clock = new TimerClock(); using var watcher = new TextInactivityWatchdog(10, clock: clock);
        var progress = new GenerationProgress(GenerationPhase.Generating, "Writing", 10, 100, "tokens") { ExecutionStageId = "2" };
        watcher.Observe(progress);
        for (var i = 0; i < 9; i++)
        {
            clock.Advance(1);
            watcher.Observe(progress with { Label = "Still writing " + i, Elapsed = TimeSpan.FromSeconds(i), EstimatedRemaining = TimeSpan.FromSeconds(i) });
            watcher.Observe(new ChatResponseUpdate(ChatRole.Assistant, ""));
            watcher.Observe(progress with { Current = 9 });
        }
        clock.Advance(1); Assert.True(watcher.Expired);
    }

    [Fact]
    public void GenuineNodeTransitionResetsEvenWithinTheSamePhase()
    {
        var clock = new TimerClock(); using var watcher = new TextInactivityWatchdog(10, clock: clock);
        var tracker = new GenerationProgressTracker(clock);
        watcher.Observe(tracker.SetStage(GenerationPhase.Preparing, "Loading", "1")); clock.Advance(9);
        watcher.Observe(tracker.SetStage(GenerationPhase.Preparing, "Loading", "2")); clock.Advance(9);
        Assert.False(watcher.Expired); watcher.Observe(tracker.Snapshot()); clock.Advance(1); Assert.True(watcher.Expired);
    }

    [Fact]
    public void InitialSilenceExpiresAndRecoveredObserverGetsAFreshInterval()
    {
        var clock = new TimerClock(); using var original = new TextInactivityWatchdog(10, clock: clock);
        clock.Advance(100); Assert.True(original.Expired);
        using var recovered = new TextInactivityWatchdog(10, clock: clock);
        clock.Advance(9); Assert.False(recovered.Expired); clock.Advance(1); Assert.True(recovered.Expired);
    }

    [Fact]
    public void ProviderCompletionStopsMonitoringButManualCancellationRemainsImmediate()
    {
        var clock = new TimerClock(); using var caller = new CancellationTokenSource();
        using var watcher = new TextInactivityWatchdog(10, caller.Token, clock);
        watcher.Observe(new ChatResponseUpdate { FinishReason = ChatFinishReason.Stop });
        clock.Advance(100); Assert.False(watcher.Expired); caller.Cancel(); Assert.True(watcher.Token.IsCancellationRequested);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task OpenRouterSdkSurvivesMultipleTimeoutIntervalsAndDoesNotSaveReasoning(bool reasoning)
    {
        var chunks = Enumerable.Range(0, 20).Select(i => "data: {\"id\":\"x\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"test/model\",\"choices\":[{\"index\":0,\"delta\":{\"" + (reasoning ? "reasoning" : "content") + "\":\"tick\"},\"finish_reason\":null}]}\n\n")
            .Append("data: {\"id\":\"x\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"test/model\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"done\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n");
        using var handler = new ScriptedHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new PacedStream(chunks)) }));
        var settings = new FakeAiSettingsStore { Value = new() { TimeoutSeconds = 5 } };
        var assistant = new ScriptAssistant(new AiProviderRegistry(new TestHttpFactory(handler), settings, TestComfy.Monitor()), settings);
        var answer = new StringBuilder();
        await foreach (var update in assistant.GenerateAsync(new(new() { Backend = AiBackend.OpenRouter, Model = "test/model", Operation = WritingOperation.Discuss }, new() { ProjectId = Guid.NewGuid() }, []), TestContext.Current.CancellationToken)) answer.Append(update.Text);
        Assert.Equal(reasoning ? "done" : string.Concat(Enumerable.Repeat("tick", 20)) + "done", answer.ToString());
        Assert.Single(handler.Requests);
    }

    private sealed class PacedStream(IEnumerable<string> chunks) : Stream
    {
        private readonly Queue<byte[]> _chunks = new(chunks.Select(Encoding.UTF8.GetBytes));
        private int _offset;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_chunks.Count == 0) return 0;
            if (_offset == 0) await Task.Delay(600, ct);
            var bytes = _chunks.Peek(); var size = Math.Min(buffer.Length, bytes.Length - _offset);
            bytes.AsMemory(_offset, size).CopyTo(buffer); _offset += size;
            if (_offset == bytes.Length) { _chunks.Dequeue(); _offset = 0; }
            return size;
        }
        public override Task<int> ReadAsync(byte[] b, int offset, int count, CancellationToken ct) => ReadAsync(b.AsMemory(offset, count), ct).AsTask();
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException(); public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException(); public override void SetLength(long v) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}

internal sealed class TimerClock : TimeProvider
{
    private long _ticks;
    private readonly List<Timer> _timers = [];
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _ticks;
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_ticks);
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    { var timer = new Timer(this, callback, state); _timers.Add(timer); timer.Change(dueTime, period); return timer; }
    public void Advance(int seconds)
    {
        var target = _ticks + TimeSpan.FromSeconds(seconds).Ticks;
        while (_timers.Where(t => t.Due <= target).MinBy(t => t.Due) is { } next)
        { _ticks = next.Due; next.Due = long.MaxValue; next.Callback(next.State); }
        _ticks = target;
    }
    private sealed class Timer(TimerClock clock, TimerCallback callback, object? state) : ITimer
    {
        public long Due = long.MaxValue;
        public TimerCallback Callback = callback; public object? State = state;
        public bool Change(TimeSpan due, TimeSpan period) { Due = due == Timeout.InfiniteTimeSpan ? long.MaxValue : clock._ticks + due.Ticks; return true; }
        public void Dispose() => Due = long.MaxValue;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
