using Bunit;
using lumibelle.Components.AI;
using lumibelle.Models;
using lumibelle.Services.AI;

namespace Lumibelle.Tests;

public sealed partial class AiActivityTests
{
    [Fact]
    public async Task HistoryClearsTheSelectedProjectAcrossPagesAndCanBeRestored()
    {
        var project = Guid.NewGuid();
        for (var i = 0; i < 21; i++)
        {
            var job = await Add("Finished " + i, project);
            await _store.UpdateAsync(job.Id, j => j with { State = AiJobState.Completed, Unread = true }, _ct);
        }
        var other = await Add("Another project");
        await _store.UpdateAsync(other.Id, j => j with { State = AiJobState.NeedsAttention, Unread = true }, _ct);
        var active = await Add("Keep waiting", project);
        var ui = Render<AiActivity>(); await Open(ui); await Button(ui, "History");
        Assert.DoesNotContain("@if", ui.Find(".ai-activity-header").TextContent);
        await ui.InvokeAsync(() => ui.Find("#ai-activity-project").Change(project.ToString()));
        await Button(ui, "Clear history");
        ui.WaitForAssertion(() => Assert.Empty(ui.FindAll(".ai-activity-job")));
        Assert.Equal(21, (await _store.ReadAsync(_ct)).Jobs.Count(j => j.ActivityClearedUtc is not null));
        Assert.True((await _store.ReadAsync(_ct)).Jobs.Single(j => j.Id == other.Id).Unread);
        Assert.Equal(AiJobState.Waiting, (await _store.ReadAsync(_ct)).Jobs.Single(j => j.Id == active.Id).State);
        await Button(ui, "Undo"); ui.WaitForAssertion(() => Assert.Equal(20, ui.FindAll(".ai-activity-job").Count));
        Assert.DoesNotContain((await _store.ReadAsync(_ct)).Jobs.Where(j => j.Target.ProjectId == project), j => j.Unread);
        await Button(ui, "Clear history");
        await ui.InvokeAsync(() => ui.Find(".ai-activity-tools input").Change(true));
        ui.WaitForAssertion(() => Assert.Equal(20, ui.FindAll(".ai-job-cleared").Count));
        await ui.InvokeAsync(() => ui.FindAll(".ai-job-actions button").First(b => b.TextContent == "Restore to activity").ClickAsync(new()));
        Assert.Equal(20, (await _store.ReadAsync(_ct)).Jobs.Count(j => j.ActivityClearedUtc is not null));
    }

    [Fact]
    public async Task ReviewAcknowledgementRequiresVisibilityAndTheDisplayedVersion()
    {
        var visible = JSInterop.SetupModule("./_content/Lumibelle.UI/ai-jobs.js").Setup<bool>("isReviewVisible", _ => true);
        visible.SetResult(false);
        var job = await Add("Observed response");
        job = await _store.UpdateAsync(job.Id, j => j with { State = AiJobState.Completed, Unread = true }, _ct);
        var observer = Render<AiResultAcknowledgement>(p => p.Add(c => c.Observed, new[] { job }));
        Assert.True((await _store.ReadAsync(_ct)).Jobs.Single().Unread);
        var newer = await _store.UpdateAsync(job.Id, j => j with { Version = j.Version + 1 }, _ct);
        visible.SetResult(true);
        observer.Render(p => p.Add(c => c.Observed, new[] { job }));
        Assert.True((await _store.ReadAsync(_ct)).Jobs.Single().Unread);
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Changed += () => { if (_queue.View.Jobs.Single().Unread == false) acknowledged.TrySetResult(); };
        observer.Render(p => p.Add(c => c.Observed, new[] { newer }));
        await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(5), _ct);
        Assert.False(_queue.View.Jobs.Single().Unread);
    }
}
