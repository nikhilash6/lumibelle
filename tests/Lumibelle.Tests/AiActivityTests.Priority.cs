using Bunit;
using lumibelle.Components.AI;
using lumibelle.Models;
using lumibelle.Services.AI;

namespace Lumibelle.Tests;

public sealed partial class AiActivityTests
{
    private static Task PriorityButton(IRenderedComponent<AiActivity> ui, Guid job, string text) =>
        ui.InvokeAsync(() => ui.Find($"[data-job-id='{job}']").QuerySelectorAll("button")
            .Single(b => b.TextContent.Trim() == text).ClickAsync());

    [Fact]
    public async Task PriorityActionReordersActivityAndRemovalRestoresOrder()
    {
        var a = await Add("Normal request"); var b = await Add("Quick text request");
        var ui = Render<AiActivity>(); await Open(ui);
        await PriorityButton(ui, b.Id, "Prioritize");
        ui.WaitForAssertion(() => Assert.Equal(b.Id.ToString(), ui.FindAll(".ai-activity-job")[0].GetAttribute("data-job-id")));
        Assert.Contains("position 1", ui.Find($"[data-job-id='{b.Id}']").TextContent);
        Assert.Contains("position 2", ui.Find($"[data-job-id='{a.Id}']").TextContent);
        Assert.Contains("Priority", ui.Find($"[data-job-id='{b.Id}'] .ai-job-state").TextContent);
        Assert.Equal("true", ui.Find($"[data-job-id='{b.Id}'] .ai-job-priority-action button").GetAttribute("aria-pressed"));
        await PriorityButton(ui, b.Id, "Remove priority");
        ui.WaitForAssertion(() => Assert.Equal(a.Id.ToString(), ui.FindAll(".ai-activity-job")[0].GetAttribute("data-job-id")));
        Assert.False((await _store.ReadAsync(_ct)).Jobs.Single(j => j.Id == b.Id).Priority);
    }

    [Theory]
    [InlineData(AiBackend.ComfyUI)]
    [InlineData(AiBackend.OpenRouter)]
    [InlineData(AiBackend.Codex)]
    public async Task PriorityControlIsAvailableForEveryProvider(AiBackend provider)
    {
        var job = await _store.EnqueueAsync(AiJobSubmission.Create(Guid.NewGuid(), AiJobKind.ScriptAssistant,
            provider, new(Guid.NewGuid()), "QA", "Any provider", Guid.NewGuid(), new { prompt = "Original" }), _ct);
        var ui = Render<AiActivity>(); await Open(ui); await PriorityButton(ui, job.Id, "Prioritize");
        ui.WaitForAssertion(() => Assert.Contains("Remove priority", ui.Find($"[data-job-id='{job.Id}']").TextContent));
        Assert.True((await _store.ReadAsync(_ct)).Jobs.Single().Priority);
    }

    [Fact]
    public async Task RunNextPromotesPastExistingPriorityAndArrowsStayInTheirBand()
    {
        var a = await Add("Normal first"); var b = await Add("Existing priority"); var c = await Add("Normal second");
        await _queue.SetPriorityAsync(b.Id, true, _ct);
        var ui = Render<AiActivity>(); await Open(ui);
        var normal = ui.Find($"[data-job-id='{a.Id}']");
        Assert.True(normal.QuerySelector("button[aria-label='Move request up']")!.HasAttribute("disabled"));
        Assert.False(normal.QuerySelectorAll("button").Single(b => b.TextContent == "Run next").HasAttribute("disabled"));
        await PriorityButton(ui, a.Id, "Run next");
        ui.WaitForAssertion(() => Assert.Equal(a.Id.ToString(), ui.FindAll(".ai-activity-job")[0].GetAttribute("data-job-id")));
        Assert.True((await _store.ReadAsync(_ct)).Jobs.Single(j => j.Id == a.Id).Priority);
        Assert.False((await _store.ReadAsync(_ct)).Jobs.Single(j => j.Id == c.Id).Priority);
    }

    [Fact]
    public async Task PriorityWhilePausedDoesNotResumeTheQueue()
    {
        var job = await Add("Retain pause"); await _store.SetPausedAsync(AiBackend.ComfyUI, true, _ct);
        var ui = Render<AiActivity>(); await Open(ui); await PriorityButton(ui, job.Id, "Prioritize");
        ui.WaitForAssertion(() => Assert.Contains("This provider queue is paused", ui.Markup));
        Assert.Contains(AiBackend.ComfyUI, (await _store.ReadAsync(_ct)).Paused);
        Assert.Null(await _store.ClaimNextAsync(AiBackend.ComfyUI, 1, _ct));
    }

    [Fact]
    public async Task RunningRequestDoesNotOfferPriorityOrRestartControls()
    {
        var job = await Add("Running"); await _store.ClaimNextAsync(AiBackend.ComfyUI, 1, _ct);
        var ui = Render<AiActivity>(); await Open(ui);
        var buttons = ui.Find($"[data-job-id='{job.Id}']").QuerySelectorAll("button").Select(b => b.TextContent.Trim()).ToArray();
        Assert.DoesNotContain("Prioritize", buttons); Assert.DoesNotContain("Run next", buttons);
        Assert.Equal(AiJobState.Running, (await _store.ReadAsync(_ct)).Jobs.Single().State);
    }

    [Fact]
    public async Task StalePriorityButtonShowsAnErrorWithoutChangingAClaimedRequest()
    {
        var job = await Add("Race");
        var ui = Render<AiJobPriorityAction>(p => p.Add(c => c.Job, job));
        var claimed = await _store.ClaimNextAsync(AiBackend.ComfyUI, 1, _ct);
        await ui.InvokeAsync(() => ui.Find("button").Click());
        ui.WaitForAssertion(() => Assert.Contains("Only waiting requests", ui.Find("[role='alert']").TextContent));
        Assert.Equal(claimed, (await _store.ReadAsync(_ct)).Jobs.Single());
    }

    [Fact]
    public async Task PromotionFollowsTheRequestAcrossPagination()
    {
        AiJobHeader last = null!;
        for (var i = 0; i < 21; i++) last = await Add($"Queued {i}");
        var ui = Render<AiActivity>(); await Open(ui); await Button(ui, "Next");
        Assert.Single(ui.FindAll(".ai-activity-job"));
        await PriorityButton(ui, last.Id, "Prioritize");
        ui.WaitForAssertion(() => Assert.Equal(last.Id.ToString(), ui.FindAll(".ai-activity-job")[0].GetAttribute("data-job-id")));
        Assert.Contains("Page 1 of 2", ui.Markup); Assert.Equal(20, ui.FindAll(".ai-activity-job").Count);
    }

    private Task<AiJobHeader> AddGuidance(string label, Guid project) => _store.EnqueueAsync(AiJobSubmission.Create(
        Guid.NewGuid(), AiJobKind.Guidance, AiBackend.ComfyUI,
        new(project, AssetId: Guid.NewGuid(), GuidanceScope: GuidanceScope.Look, LookId: Guid.NewGuid()),
        "QA project", label, Guid.NewGuid(), new { prompt = "Captured" }), _ct);

    [Fact]
    public async Task ProjectFilteringKeepsGlobalProviderPositions()
    {
        var project = Guid.NewGuid(); var a = await AddGuidance("Own first", project);
        var other = await Add("Other project"); var b = await AddGuidance("Own second", project);
        await _queue.SetPriorityAsync(other.Id, true, _ct);
        var ui = Render<AiActivity>(); await Open(ui);
        await ui.InvokeAsync(() => ui.Find("#ai-activity-project").Change(project.ToString()));
        Assert.Equal(2, ui.FindAll(".ai-activity-job").Count);
        Assert.Contains("position 2", ui.Find($"[data-job-id='{a.Id}']").TextContent);
        Assert.Contains("position 3", ui.Find($"[data-job-id='{b.Id}']").TextContent);
        Assert.Contains("After Other project", ui.Find($"[data-job-id='{a.Id}']").TextContent);
    }
}
