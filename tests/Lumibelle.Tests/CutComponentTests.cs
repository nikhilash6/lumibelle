using Bunit;
using lumibelle.Components.Pages;
using lumibelle.Components.Shots;
using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Components.Sections;
using MudBlazor;
using MudBlazor.Services;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    [Fact]
    public async Task CutSaveAcknowledgementPreservesNewerTrimsAndFailedDraftIsRetryable()
    {
        var f = Fixture(); var shot = Ready(); await f.Shots.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        var shots = await AddTake(f.Project.Id, f.Shots, shot);
        var real = new FileCutStore(f.Files, f.Shots, _clock);
        await real.SaveAsync(f.Project.Id, [CutClip.From(shot, shots.Takes[0])], 0, _ct);
        var controlled = new ControlledCutStore(real);
        await using var ui = new BunitContext(); ui.JSInterop.Mode = JSRuntimeMode.Loose;
        ui.Services.AddSingleton<ICutExporter>(new LunaUiExporter());
        ui.Services.AddMudServices(); ui.Services.AddSingleton<ICutStore>(controlled); ui.Services.AddSingleton<IShotStore>(f.Shots);
        ui.Services.AddSingleton<IProjectStore>(new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) });
        ui.JSInterop.SetupModule("./_content/Lumibelle.UI/cut-player.js").SetupModule("attach", _ => true);
        ui.Render<MudPopoverProvider>(); ui.Render<MudDialogProvider>();
        var actions = ui.Render<SectionOutlet>(p => p.Add(x => x.SectionName, "studio-actions"));
        var page = ui.Render<CutStudio>(p => p.Add(x => x.Id, f.Project.Id));
        page.WaitForElement(".cut-trims input").Change("2");
        controlled.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var save = page.InvokeAsync(() => actions.FindAll("button").Single(b => b.TextContent.Trim() == "Save").ClickAsync(new()));
        await controlled.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), _ct);
        await page.InvokeAsync(() => page.Find(".cut-trims input").Change("5"));
        controlled.Hold.SetResult(); await save;
        page.WaitForAssertion(() => Assert.Equal("5", page.Find(".cut-trims input").GetAttribute("value")));
        Assert.Equal(4, (await real.LoadAsync(f.Project.Id, _ct)).Clips[0].StartFrame);
        Assert.Equal(2, controlled.Saves.Count); Assert.Equal(1, controlled.Saves[0][0].StartFrame); Assert.Equal(4, controlled.Saves[1][0].StartFrame);
        controlled.Fail = true;
        page.Find(".cut-trims input").Change("8");
        await page.InvokeAsync(() => actions.FindAll("button").Single(b => b.TextContent.Trim() == "Save").ClickAsync(new()));
        page.WaitForAssertion(() => Assert.Contains("Save failed · draft retained", actions.Markup));
        Assert.Equal("8", page.Find(".cut-trims input").GetAttribute("value"));
        Assert.Equal(4, (await real.LoadAsync(f.Project.Id, _ct)).Clips[0].StartFrame);
        controlled.Fail = false;
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.TextContent.Trim() == "Retry save").ClickAsync(new()));
        Assert.Equal(7, (await real.LoadAsync(f.Project.Id, _ct)).Clips[0].StartFrame);
    }

    [Fact]
    public async Task CutGesturesRejectStaleVersionsAndUndoAsOneEdit()
    {
        var f = Fixture(); var shot = Ready(); await f.Shots.SaveAsync(f.Project.Id, [shot], 0, ct: _ct);
        var shots = await AddTake(f.Project.Id, f.Shots, shot);
        var store = new FileCutStore(f.Files, f.Shots, _clock);
        var first = CutClip.From(shot, shots.Takes[0]); var second = CutClip.From(shot, shots.Takes[0]);
        await store.SaveAsync(f.Project.Id, [first, second], 0, _ct);
        await using var ui = new BunitContext(); ui.JSInterop.Mode = JSRuntimeMode.Loose;
        ui.Services.AddSingleton<ICutExporter>(new LunaUiExporter());
        ui.Services.AddMudServices(); ui.Services.AddSingleton<ICutStore>(store); ui.Services.AddSingleton<IShotStore>(f.Shots);
        ui.Services.AddSingleton<IProjectStore>(new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) });
        ui.JSInterop.SetupModule("./_content/Lumibelle.UI/cut-player.js").SetupModule("attach", _ => true);
        ui.Render<MudPopoverProvider>(); ui.Render<MudDialogProvider>();
        var actions = ui.Render<SectionOutlet>(p => p.Add(x => x.SectionName, "studio-actions"));
        var page = ui.Render<CutStudio>(p => p.Add(x => x.Id, f.Project.Id));
        page.WaitForElement(".cut-trims input");
        var player = page.FindComponent<CutPlayer>().Instance;
        await page.InvokeAsync(() => player.TrimClip(first.Id, 5, 15, player.Version - 1));
        Assert.Equal("1", page.Find(".cut-trims input").GetAttribute("value"));
        Assert.Contains("changed during this drag", page.Markup);
        await page.InvokeAsync(() => player.TrimClip(first.Id, 5, 15, player.Version));
        Assert.Equal("6", page.Find(".cut-trims input").GetAttribute("value"));
        await page.InvokeAsync(() => actions.FindAll("button").Single(b => b.TextContent.Trim() == "Undo").ClickAsync(new()));
        Assert.Equal("1", page.Find(".cut-trims input").GetAttribute("value"));
        await page.InvokeAsync(() => player.ReorderClip(first.Id, null, player.Version));
        Assert.Equal(second.Id, player.Clips[0].Id);
        await page.InvokeAsync(() => actions.FindAll("button").Single(b => b.TextContent.Trim() == "Undo").ClickAsync(new()));
        Assert.Equal(first.Id, player.Clips[0].Id);
    }

    private sealed class ControlledCutStore(ICutStore inner) : ICutStore
    {
        public TaskCompletionSource? Hold;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Fail;
        public List<List<CutClip>> Saves = [];
        public Task<CutDocument> LoadAsync(Guid id, CancellationToken ct = default) => inner.LoadAsync(id, ct);
        public async Task<CutDocument> SaveAsync(Guid id, IReadOnlyList<CutClip> clips, long revision, CancellationToken ct = default)
        {
            Saves.Add(ShotCopy.Of(clips.ToList()));
            if (Fail) throw new WorkspaceStoreException("Disk unavailable.");
            if (Hold is { } hold && Saves.Count == 1) { Entered.TrySetResult(); await hold.Task.WaitAsync(ct); }
            return await inner.SaveAsync(id, clips, revision, ct);
        }
    }
}
