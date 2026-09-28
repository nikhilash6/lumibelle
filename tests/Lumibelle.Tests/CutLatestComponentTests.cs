using Bunit;
using lumibelle.Components.Pages;
using lumibelle.Components.Shots;
using lumibelle.Models;
using lumibelle.Services;
using lumibelle.Services.Shots;
using lumibelle.Services.Story;
using Lumibelle.Testing;
using Microsoft.AspNetCore.Components.Sections;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Lumibelle.Tests;

public sealed partial class ShotTests
{
    [Fact]
    public async Task CutLatestSelectionStagesFiltersCancelsAndAppliesAsOneUndoableEdit()
    {
        var f = Fixture();
        var firstShot = Ready() with { Title = "First" };
        var missingShot = Ready() with { Title = "Missing" };
        var keptShot = Ready() with { Title = "Kept" };
        var unmatchedShot = Ready() with { Title = "Unmatched" };
        var emptyShot = Ready() with { Title = "Empty" };
        await f.Shots.SaveAsync(f.Project.Id, [firstShot, missingShot, keptShot, unmatchedShot, emptyShot], 0, ct: _ct);
        var oldPreview = await AddCutResolutionTake(f.Project.Id, f.Shots, firstShot, 832, 480);
        var latestNative = await AddCutResolutionTake(f.Project.Id, f.Shots, firstShot, 1344, 768);
        var newestPreview = await AddCutResolutionTake(f.Project.Id, f.Shots, firstShot, 832, 480);
        var missingNative = await AddCutResolutionTake(f.Project.Id, f.Shots, missingShot, 1024, 1024);
        var keptPreview = await AddCutResolutionTake(f.Project.Id, f.Shots, keptShot, 832, 480);
        var unmatchedPreview = await AddCutResolutionTake(f.Project.Id, f.Shots, unmatchedShot, 832, 480);
        var shots = await f.Shots.LoadAsync(f.Project.Id, _ct);
        shots.Shots.Single(s => s.Id == firstShot.Id).SelectedTakeId = newestPreview.Id;
        shots.Shots.Single(s => s.Id == unmatchedShot.Id).SelectedTakeId = unmatchedPreview.Id;
        await f.Shots.SaveAsync(f.Project.Id, shots.Shots, shots.Revision, ct: _ct);

        var first = CutClip.From(firstShot, oldPreview) with { StartFrame = 2, EndFrameExclusive = 10 };
        var repeat = first with { Id = Guid.NewGuid(), StartFrame = 5, EndFrameExclusive = 12 };
        var kept = CutClip.From(keptShot, keptPreview) with { StartFrame = 3, EndFrameExclusive = 9 };
        var store = new FileCutStore(f.Files, f.Shots, _clock);
        await store.SaveAsync(f.Project.Id, [first, repeat, kept], 0, _ct);

        await using var ui = new BunitContext(); ui.JSInterop.Mode = JSRuntimeMode.Loose;
        ui.Services.AddSingleton<ICutExporter>(new LunaUiExporter());
        ui.Services.AddMudServices(); ui.Services.AddSingleton<ICutStore>(store); ui.Services.AddSingleton<IShotStore>(f.Shots);
        ui.Services.AddSingleton<IProjectStore>(new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) });
        ui.JSInterop.SetupModule("./_content/Lumibelle.UI/cut-player.js").SetupModule("attach", _ => true);
        ui.Render<MudPopoverProvider>(); var dialogs = ui.Render<MudDialogProvider>();
        var actions = ui.Render<SectionOutlet>(p => p.Add(x => x.SectionName, "studio-actions"));
        var page = ui.Render<CutStudio>(p => p.Add(x => x.Id, f.Project.Id));
        page.WaitForElement(".cut-trims input");
        var player = page.FindComponent<CutPlayer>().Instance;

        await page.InvokeAsync(() => page.Find(".cut-add").ClickAsync(new()));
        dialogs.WaitForElement(".cut-latest-controls select[aria-describedby=cut-latest-help]");
        Assert.Equal("", dialogs.Find(".cut-latest-controls select[aria-describedby=cut-latest-help]").GetAttribute("value"));
        await page.InvokeAsync(() => dialogs.Find(".cut-latest-controls button").ClickAsync(new()));
        Assert.Equal(newestPreview.Id.ToString(), dialogs.Find("select[aria-label='Take for First']").GetAttribute("value"));
        Assert.Equal(unmatchedPreview.Id.ToString(), dialogs.Find("select[aria-label='Take for Unmatched']").GetAttribute("value"));

        // Changing the filter alone never changes the cut or the pending choices.
        dialogs.Find(".cut-latest-controls select[aria-describedby=cut-latest-help]").Change("1.0");
        Assert.Equal(newestPreview.Id.ToString(), dialogs.Find("select[aria-label='Take for First']").GetAttribute("value"));
        Assert.Contains("2 of 5", dialogs.Find(".cut-latest-summary").TextContent);
        await page.InvokeAsync(() => dialogs.Find(".cut-latest-controls button").ClickAsync(new()));
        Assert.Equal(latestNative.Id.ToString(), dialogs.Find("select[aria-label='Take for First']").GetAttribute("value"));
        Assert.Equal("", dialogs.Find("select[aria-label='Take for Kept']").GetAttribute("value"));
        Assert.Equal("", dialogs.Find("select[aria-label='Take for Unmatched']").GetAttribute("value"));
        Assert.Equal("", dialogs.Find("select[aria-label='Take for Empty']").GetAttribute("value"));
        Assert.Equal(3, player.Clips.Count);
        Assert.Equal(1L, (await store.LoadAsync(f.Project.Id, _ct)).Revision);
        await page.InvokeAsync(() => dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Cancel").ClickAsync(new()));
        dialogs.WaitForAssertion(() => Assert.Empty(dialogs.FindAll(".cut-latest-controls")));
        Assert.Equal([first.Id, repeat.Id, kept.Id], player.Clips.Select(c => c.Id));

        await page.InvokeAsync(() => page.Find(".cut-add").ClickAsync(new()));
        dialogs.WaitForElement(".cut-latest-controls select[aria-describedby=cut-latest-help]");
        Assert.Equal("", dialogs.Find(".cut-latest-controls select[aria-describedby=cut-latest-help]").GetAttribute("value"));
        dialogs.Find("select[aria-label='Action for First']").Change(repeat.Id.ToString());
        dialogs.Find(".cut-latest-controls select[aria-describedby=cut-latest-help]").Change("1.0");
        await page.InvokeAsync(() => dialogs.Find(".cut-latest-controls button").ClickAsync(new()));
        // Manual overrides remain possible; selecting latest again restores the filtered proposal.
        dialogs.Find("select[aria-label='Take for First']").Change(newestPreview.Id.ToString());
        Assert.Equal(newestPreview.Id.ToString(), dialogs.Find("select[aria-label='Take for First']").GetAttribute("value"));
        await page.InvokeAsync(() => dialogs.Find(".cut-latest-controls button").ClickAsync(new()));
        Assert.Equal("Apply changes (2)", dialogs.FindAll("button").Single(b => b.TextContent.Trim().StartsWith("Apply changes (")).TextContent.Trim());
        await page.InvokeAsync(() => dialogs.FindAll("button").Single(b => b.TextContent.Trim().StartsWith("Apply changes (")).ClickAsync(new()));

        page.WaitForAssertion(() => Assert.Equal(4, player.Clips.Count));
        Assert.Equal([first.Id, repeat.Id, kept.Id], player.Clips.Where(c => c.ShotId != missingShot.Id).Select(c => c.Id));
        Assert.Equal([oldPreview.Id, latestNative.Id, missingNative.Id, keptPreview.Id], player.Clips.Select(c => c.TakeId));
        Assert.Equal(2, player.Clips[0].StartFrame); Assert.Equal(10, player.Clips[0].EndFrameExclusive);
        Assert.Equal(0, player.Clips[1].StartFrame); Assert.Equal(latestNative.FrameCount, player.Clips[1].EndFrameExclusive);
        Assert.Equal(3, player.Clips[3].StartFrame); Assert.Equal(9, player.Clips[3].EndFrameExclusive);
        var insertedId = player.Clips[2].Id;
        await page.InvokeAsync(() => actions.FindAll("button").Single(b => b.TextContent.Trim() == "Undo").ClickAsync(new()));
        Assert.Equal([first.Id, repeat.Id, kept.Id], player.Clips.Select(c => c.Id));
        Assert.Equal(oldPreview.Id, player.Clips[1].TakeId);
        Assert.Equal(5, player.Clips[1].StartFrame); Assert.Equal(12, player.Clips[1].EndFrameExclusive);
        Assert.True(actions.FindAll("button").Single(b => b.TextContent.Trim() == "Undo").HasAttribute("disabled"));
        await page.InvokeAsync(() => actions.FindAll("button").Single(b => b.TextContent.Trim() == "Redo").ClickAsync(new()));
        Assert.Equal([first.Id, repeat.Id, insertedId, kept.Id], player.Clips.Select(c => c.Id));
        Assert.Equal(latestNative.Id, player.Clips[1].TakeId);
        var unchangedShots = await f.Shots.LoadAsync(f.Project.Id, _ct);
        Assert.Equal(newestPreview.Id, unchangedShots.Shots.Single(s => s.Id == firstShot.Id).SelectedTakeId);
        Assert.Equal(unmatchedPreview.Id, unchangedShots.Shots.Single(s => s.Id == unmatchedShot.Id).SelectedTakeId);
    }

    [Fact]
    public async Task CutLatestSelectionRevalidatesAllTakesBeforeApplyingAndRefreshesOnReopen()
    {
        var f = Fixture(); var first = Ready() with { Title = "Available" }; var second = Ready() with { Title = "Discarded" };
        await f.Shots.SaveAsync(f.Project.Id, [first, second], 0, ct: _ct);
        var available = await AddCutResolutionTake(f.Project.Id, f.Shots, first, 1344, 768);
        var discarded = await AddCutResolutionTake(f.Project.Id, f.Shots, second, 1344, 768);
        var store = new FileCutStore(f.Files, f.Shots, _clock);

        await using var ui = new BunitContext(); ui.JSInterop.Mode = JSRuntimeMode.Loose;
        ui.Services.AddSingleton<ICutExporter>(new LunaUiExporter());
        ui.Services.AddMudServices(); ui.Services.AddSingleton<ICutStore>(store); ui.Services.AddSingleton<IShotStore>(f.Shots);
        ui.Services.AddSingleton<IProjectStore>(new FakeProjectStore { Get = _ => Task.FromResult<ProjectInfo?>(f.Project) });
        ui.JSInterop.SetupModule("./_content/Lumibelle.UI/cut-player.js").SetupModule("attach", _ => true);
        ui.Render<MudPopoverProvider>(); var dialogs = ui.Render<MudDialogProvider>();
        ui.Render<SectionOutlet>(p => p.Add(x => x.SectionName, "studio-actions"));
        var page = ui.Render<CutStudio>(p => p.Add(x => x.Id, f.Project.Id));
        page.WaitForElement(".cut-add");
        var player = page.FindComponent<CutPlayer>().Instance;
        await page.InvokeAsync(() => page.Find(".cut-add").ClickAsync(new()));
        dialogs.WaitForElement(".cut-latest-controls select[aria-describedby=cut-latest-help]");
        await page.InvokeAsync(() => dialogs.Find(".cut-latest-controls button").ClickAsync(new()));
        var shots = await f.Shots.LoadAsync(f.Project.Id, _ct);
        await f.Shots.DiscardAsync(f.Project.Id, discarded.Id, ShotTrashKind.Take, shots.Revision, _ct);
        await page.InvokeAsync(() => dialogs.FindAll("button").Single(b => b.TextContent.Trim().StartsWith("Apply changes (")).ClickAsync(new()));

        dialogs.WaitForAssertion(() => Assert.Contains("no longer available", dialogs.Find("[role='alert']").TextContent));
        Assert.Empty(player.Clips);
        Assert.Equal(0L, (await store.LoadAsync(f.Project.Id, _ct)).Revision);
        await page.InvokeAsync(() => dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Cancel").ClickAsync(new()));
        dialogs.WaitForAssertion(() => Assert.Empty(dialogs.FindAll(".cut-latest-controls")));
        await page.InvokeAsync(() => page.Find(".cut-add").ClickAsync(new()));
        dialogs.WaitForElement(".cut-latest-controls select[aria-describedby=cut-latest-help]");
        Assert.Contains("1 of 2", dialogs.Find(".cut-latest-summary").TextContent);
        await page.InvokeAsync(() => dialogs.Find(".cut-latest-controls button").ClickAsync(new()));
        await page.InvokeAsync(() => dialogs.FindAll("button").Single(b => b.TextContent.Trim().StartsWith("Apply changes (")).ClickAsync(new()));
        Assert.Equal(available.Id, Assert.Single(player.Clips).TakeId);
    }

    private async Task<ShotTake> AddCutResolutionTake(Guid project, IShotStore store, Shot shot, int width, int height)
    {
        var snapshot = Snapshot(project, shot) with { Width = width, Height = height };
        var run = Guid.NewGuid();
        var directory = Path.Combine(await store.RunDirectoryAsync(project, run, _ct), "candidate-1");
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, "video.mp4"), [1, 2, 3], _ct);
        var frames = await MockFrameArchive.WriteAsync(directory, snapshot.FrameCount, width, height, _ct);
        var published = _clock.Now = _clock.Now.AddMinutes(1);
        var document = await store.PublishTakeAsync(project, new()
        {
            ShotId = shot.Id, RunId = run, Candidate = 1, Snapshot = snapshot, Width = width, Height = height,
            CreatedUtc = published, Frames = frames, Bytes = 3 + frames.DistinctBy(f => f.FileName).Sum(f => f.Bytes)
        }, directory, _ct);
        return document.Takes.Single(t => t.RunId == run);
    }
}
