using System.Text.Json;
using System.Threading.Channels;
using lumibelle.Models;
using lumibelle.Services.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumibelle.Tests;

public sealed class AiQueuePriorityCoordinatorTests
{
    [Fact]
    public async Task PriorityRunsAfterCurrentWorkWithoutInterruptingOrChangingCapturedInput()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "Lumibelle.PriorityCoordinator", Guid.NewGuid().ToString("N"));
        var store = new FileAiJobStore(root, TimeProvider.System); var handler = new HeldHandler();
        using var queue = new AiJobCoordinator(store, new FakeAiSettingsStore(), [handler], TimeProvider.System,
            NullLogger<AiJobCoordinator>.Instance);
        AiJobSubmission Request(string text) => AiJobSubmission.Create(Guid.NewGuid(), AiJobKind.ScriptAssistant,
            AiBackend.ComfyUI, new(Guid.NewGuid()), "QA", text, Guid.NewGuid(), new { prompt = text, seed = 42 });
        try
        {
            var first = await queue.EnqueueAsync(Request("Already running"), ct);
            await queue.StartAsync(ct); var running = await handler.Next(ct);
            var normal = await queue.EnqueueAsync(Request("Normal waiting"), ct);
            var priority = await queue.EnqueueAsync(Request("Quick text request"), ct);
            await queue.SetPriorityAsync(priority.Id, true, ct);
            Assert.Equal(first.Id, running.Context.Job.Id);
            Assert.False(running.Context.Cancellation.IsCancellationRequested);
            Assert.Equal(1, handler.Calls); Assert.Equal(0, handler.Cancellations);
            running.Done.SetResult(AiJobOutcome.Complete());
            var urgent = await handler.Next(ct);
            Assert.Equal(priority.Id, urgent.Context.Job.Id); Assert.False(urgent.Context.Recovering);
            Assert.Equal("Quick text request", urgent.Snapshot.GetProperty("prompt").GetString());
            Assert.Equal(42, urgent.Snapshot.GetProperty("seed").GetInt32());
            urgent.Done.SetResult(AiJobOutcome.Complete());
            var next = await handler.Next(ct); Assert.Equal(normal.Id, next.Context.Job.Id);
            next.Done.SetResult(AiJobOutcome.Complete());
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(8));
            while ((await store.ReadAsync(deadline.Token)).Jobs.Any(j => j.State != AiJobState.Completed))
                await Task.Delay(20, deadline.Token);
            Assert.Equal(3, handler.Calls); Assert.Equal(0, handler.Cancellations);
        }
        finally
        {
            await queue.StopAsync(CancellationToken.None);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed record Call(AiJobContext Context, JsonElement Snapshot, TaskCompletionSource<AiJobOutcome> Done);
    private sealed class HeldHandler : IAiJobHandler
    {
        private readonly Channel<Call> _calls = Channel.CreateUnbounded<Call>();
        private int _count, _cancelled;
        public int Calls => Volatile.Read(ref _count);
        public int Cancellations => Volatile.Read(ref _cancelled);
        public IReadOnlyCollection<AiJobKind> Kinds => [AiJobKind.ScriptAssistant];
        public async Task<Call> Next(CancellationToken ct) =>
            await _calls.Reader.ReadAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(8), ct);
        public async Task<AiJobOutcome> ExecuteAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct)
        {
            Interlocked.Increment(ref _count);
            var call = new Call(context, snapshot, new(TaskCreationOptions.RunContinuationsAsynchronously));
            await _calls.Writer.WriteAsync(call, ct); return await call.Done.Task.WaitAsync(ct);
        }
        public Task<AiJobOutcome> RecoverAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct) =>
            throw new InvalidOperationException("Priority must not start a recovery worker.");
        public Task<bool> CancelRemoteAsync(AiJobContext context, JsonElement snapshot, CancellationToken ct)
        { Interlocked.Increment(ref _cancelled); return Task.FromResult(true); }
    }
}
