using Bunit;
using lumibelle.Components.AI;
using lumibelle.Models;
using lumibelle.Services.AI;

namespace Lumibelle.Tests;

public sealed partial class AiActivityTests
{
    [Fact]
    public async Task ComfyPauseWithActiveWorkRequiresConfirmationAndKeepsTheRequest()
    {
        var job = await Add("Retained generation"); await _store.ClaimNextAsync(AiBackend.ComfyUI, 1, _ct);
        var ui = Render<AiActivity>(); await Open(ui);
        await ui.InvokeAsync(() => ui.Find("button[aria-label='Pause queue ComfyUI']").Click());
        ui.WaitForAssertion(() => Assert.Contains("Pause and keep the current request?", ui.Markup));
        Assert.DoesNotContain(AiBackend.ComfyUI, (await _store.ReadAsync(_ct)).Paused);
        await Button(ui, "Keep running");
        Assert.DoesNotContain(AiBackend.ComfyUI, (await _store.ReadAsync(_ct)).Paused);
        await ui.InvokeAsync(() => ui.Find("button[aria-label='Pause queue ComfyUI']").Click());
        await Button(ui, "Pause and keep request");
        ui.WaitForAssertion(() => Assert.Contains("Stopping for pause", ui.Markup));
        var retained = (await _store.ReadAsync(_ct)).Jobs.Single(j => j.Id == job.Id);
        Assert.True(retained.ComfyControl!.PauseRequested);
        Assert.False(retained.CancelRequested);
        Assert.Contains(AiBackend.ComfyUI, (await _store.ReadAsync(_ct)).Paused);
    }

    [Fact]
    public async Task BlockedComfyRequestCanArmRetryWithoutCancellingOrSendingInference()
    {
        var job = await Add("Lost acceptance");
        await _store.UpdateAsync(job.Id, j => j with { State = AiJobState.NeedsAttention,
            RemoteUnconfirmed = true, Recovery = AiJobRecovery.CheckStatus }, _ct);
        var ui = Render<AiActivity>(); await Open(ui);
        ui.WaitForAssertion(() => Assert.Contains(ui.FindAll("button"), b => b.TextContent.Trim() == "Retry when connected"));
        await Button(ui, "Retry when connected");
        ui.WaitForAssertion(() => Assert.Contains("completed computation may run again", ui.Markup));
        Assert.Null((await _store.ReadAsync(_ct)).Jobs.Single().ComfyControl);
        await Button(ui, "Confirm retry");
        ui.WaitForAssertion(() => Assert.Contains("Retry is armed", ui.Markup));
        var armed = (await _store.ReadAsync(_ct)).Jobs.Single();
        Assert.NotNull(armed.ComfyControl);
        Assert.False(armed.CancelRequested);
        Assert.True(armed.RemoteUnconfirmed);
        Assert.Equal(AiJobState.NeedsAttention, armed.State); // No execution owner was started in this fixture.
        Assert.Single(ui.FindAll(".ai-activity-job"));
    }

    [Fact]
    public async Task PermanentCancellationRemovesComfyRetryControls()
    {
        var job = await Add("Cancelled unknown request");
        await _store.UpdateAsync(job.Id, j => j with { State = AiJobState.NeedsAttention,
            RemoteUnconfirmed = true, Recovery = AiJobRecovery.CheckStatus }, _ct);
        var ui = Render<AiActivity>(); await Open(ui);
        ui.WaitForAssertion(() => Assert.Single(ui.FindAll(".comfy-retry-action")));
        await _queue.CancelAsync(job.Id, _ct);
        ui.WaitForAssertion(() => Assert.Empty(ui.FindAll(".comfy-retry-action")));
        Assert.True((await _store.ReadAsync(_ct)).Jobs.Single().CancelRequested);
    }
}
