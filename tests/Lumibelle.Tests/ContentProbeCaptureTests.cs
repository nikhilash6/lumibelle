using System.Text.Json;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumibelle.Tests;

public sealed class ContentProbeCaptureTests
{
    [Fact]
    public async Task CrossProductCapturesProfilesAndTestsWithoutGeneratingAndEnqueueIsIdempotent()
    {
        using var f = new ContentProbeFixture();
        var a = ContentProbeFixture.Profile("Reasoning"); var b = a with { ProfileId = Guid.NewGuid(), Name = "No reasoning", ReasoningEffort = "none", Temperature = 0 };
        f.Settings.Value = f.Settings.Value with { TextModelProfiles = [a, b] };
        var tests = ContentProbeBuiltIns.All(await f.Probes.LoadLibraryAsync(f.Ct)).Take(2).ToArray();
        var batch = Guid.NewGuid();
        var plan = await f.Capture.PrepareAsync(batch, Guid.NewGuid(), tests.Select(p => p.Id).ToArray(), [a, b], 3, 0, f.Settings.Value.Revision, f.Ct);
        Assert.Equal(12, plan.Count); Assert.Equal(12, plan.Select(s => s.Id).Distinct().Count());
        Assert.Equal(12, plan.Select(s => s.Target.LockKey(s.Kind)).Distinct().Count());
        Assert.Equal(0, f.Providers.Creates); Assert.Empty((await f.Jobs.ReadAsync(f.Ct)).Jobs);
        f.Settings.Value.TextModelProfiles.Clear();
        foreach (var submission in plan)
        {
            var first = await f.Jobs.EnqueueAsync(submission, f.Ct);
            Assert.Equal(first.Id, (await f.Jobs.EnqueueAsync(submission, f.Ct)).Id);
            var captured = ContentProbePolicy.Read(first, await f.Jobs.ReadSnapshotAsync(first.Id, f.Ct));
            Assert.Equal(batch, captured.BatchId);
            Assert.Equal(2, captured.Settings.TextModelProfiles.Count);
            Assert.Contains(captured.Model.ProfileId, new[] { a.ProfileId, b.ProfileId });
            Assert.EndsWith(ContentProbePolicy.RefusalInstruction, captured.SubmittedPrompt);
            Assert.DoesNotContain(captured.Probe.SuccessCriteria, captured.SubmittedPrompt);
        }
        Assert.Equal(12, (await f.Jobs.ReadAsync(f.Ct)).Jobs.Count);
        Assert.NotNull(await f.Jobs.ClaimNextAsync(AiBackend.OpenRouter, 2, f.Ct));
        Assert.NotNull(await f.Jobs.ClaimNextAsync(AiBackend.OpenRouter, 2, f.Ct));
        Assert.Null(await f.Jobs.ClaimNextAsync(AiBackend.OpenRouter, 2, f.Ct));
    }
    [Fact]
    public async Task ChangedDefinitionsAndInvalidSelectionsDoNotQueueAnything()
    {
        using var f = new ContentProbeFixture(); var model = ContentProbeFixture.Profile();
        var starter = ContentProbeBuiltIns.All(new()).First();
        await f.Probes.SetEnabledAsync(starter.Id, false, 0, f.Ct);
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Capture.PrepareAsync(Guid.NewGuid(), Guid.NewGuid(), [starter.Id], [model], 1, 0, 0, f.Ct));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Capture.PrepareAsync(Guid.NewGuid(), Guid.NewGuid(), [starter.Id], [model], 1, 1, 0, f.Ct));
        await Assert.ThrowsAsync<WorkspaceStoreException>(() => f.Capture.PrepareAsync(Guid.NewGuid(), Guid.NewGuid(), [starter.Id], [model], 4, 1, 0, f.Ct));
        Assert.Empty((await f.Jobs.ReadAsync(f.Ct)).Jobs); Assert.Equal(0, f.Providers.Creates);
    }
    [Fact]
    public async Task CancellingAWaitingProbeDoesNotGenerateOrMutateItsPrompt()
    {
        using var f = new ContentProbeFixture(); var request = ContentProbeFixture.Request();
        var handler = new ContentProbeJobHandler(f.Providers, new TestHttpFactory(new ScriptedHttpHandler((_, _) => throw new InvalidOperationException())),
            new ComfyJobExecution(TestComfy.Monitor()), TimeProvider.System);
        using var queue = new AiJobCoordinator(f.Jobs, f.Settings, [handler], TimeProvider.System, NullLogger<AiJobCoordinator>.Instance);
        var submission = ContentProbeCapture.Submission(request, Guid.NewGuid());
        await queue.EnqueueAsync(submission, f.Ct); await queue.CancelAsync(submission.Id, f.Ct);
        var job = Assert.Single((await f.Jobs.ReadAsync(f.Ct)).Jobs);
        Assert.Equal(AiJobState.Cancelled, job.State); Assert.Equal(0, f.Providers.Creates);
        Assert.Equal(request.SubmittedPrompt, ContentProbePolicy.Read(job, await f.Jobs.ReadSnapshotAsync(job.Id, f.Ct)).SubmittedPrompt);
    }
    [Fact]
    public void RepeatKeepsCapturedConfigurationAndCriteriaButHasANewIdentityAndBatch()
    {
        var request = ContentProbeFixture.Request();
        var repeated = ContentProbeCapture.Repeat(request, Guid.NewGuid()).Snapshot.Deserialize<ContentProbeRequest>(AtomicJsonFile.Options)!;
        Assert.NotEqual(request.JobId, repeated.JobId); Assert.NotEqual(request.BatchId, repeated.BatchId);
        Assert.Equal(request.Model, repeated.Model); Assert.Equal(request.Probe.SuccessCriteria, repeated.Probe.SuccessCriteria);
        Assert.Equal(request.SubmittedPrompt, repeated.SubmittedPrompt);
    }
}
