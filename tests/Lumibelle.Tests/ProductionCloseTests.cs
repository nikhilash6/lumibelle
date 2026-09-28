using System.Reflection;
using System.Text.Json;
using lumibelle;
using lumibelle.Components.Pages;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Story;

namespace Lumibelle.Tests;

public sealed class ProductionCloseTests
{
    [Fact]
    public async Task NativeCloseWaitsForTheCurrentPlanningReviewToSave()
    {
        var reviews = new ControlledReviews { Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var (studio, save, shot) = Fixture(reviews);
        var registry = new EditSessionRegistry();
        using var registration = registry.Register(save);

        var close = registry.SaveAllAsync();
        await reviews.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(close.IsCompleted);
        reviews.Release.SetResult();

        Assert.True(await close);
        var saved = Assert.Single(reviews.Saved!.Read<ShotPlanningReviewDraft>()!.Shots);
        Assert.Equal(shot.Id, saved.Id);
        Assert.Equal("Ríley 日本語", Assert.Single(saved.Characters).Name);
        Assert.False(Field<bool>(studio, "_planningDraftDirty"));
        Assert.True(await registry.SaveAllAsync());
        Assert.Equal(1, reviews.Writes);
    }

    [Fact]
    public async Task FailedReviewSaveBlocksShutdownAndRetainsDecisionsForRetry()
    {
        var reviews = new ControlledReviews { Fail = true };
        var (studio, save, shot) = Fixture(reviews);
        var registry = new EditSessionRegistry();
        using var registration = registry.Register(save);
        var laterSave = false;
        using var later = registry.Register(() => { laterSave = true; return Task.FromResult(true); });

        Assert.False(await registry.SaveAllAsync());
        Assert.False(laterSave);
        Assert.Null(reviews.Saved);
        Assert.True(Field<bool>(studio, "_planningDraftDirty"));
        Assert.Equal("Review storage unavailable", Field<string>(studio, "_planningDraftError"));
        Assert.Same(shot, Assert.Single(Field<ShotPlanningResult>(studio, "_proposal").Shots));

        reviews.Fail = false;
        Assert.True(await registry.SaveAllAsync());
        Assert.True(laterSave);
        Assert.Equal(shot.Id, Assert.Single(reviews.Saved!.Read<ShotPlanningReviewDraft>()!.Shots).Id);
        Assert.False(Field<bool>(studio, "_planningDraftDirty"));
    }

    [Fact]
    public async Task AnEditDuringReviewSavingKeepsTheWindowOpenUntilThatEditIsSaved()
    {
        var reviews = new ControlledReviews { Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var (studio, save, shot) = Fixture(reviews);

        var close = save();
        await reviews.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        shot.Characters = [new(Guid.NewGuid(), "Newer decision")];
        reviews.Release.SetResult();

        Assert.False(await close);
        Assert.True(Field<bool>(studio, "_planningDraftDirty"));
        Assert.Equal("Ríley 日本語", reviews.Saved!.Read<ShotPlanningReviewDraft>()!.Shots[0].Characters[0].Name);
        Assert.True(await save());
        Assert.Equal("Newer decision", reviews.Saved!.Read<ShotPlanningReviewDraft>()!.Shots[0].Characters[0].Name);
    }

    [Fact]
    public async Task ExistingReviewConflictBlocksCloseWithoutOverwritingIt()
    {
        var reviews = new ControlledReviews();
        var (studio, save, _) = Fixture(reviews);
        Set(studio, "_planningDraftConflict", true);

        Assert.False(await save());
        Assert.Equal(0, reviews.Writes);
        Assert.True(Field<bool>(studio, "_planningDraftDirty"));
    }

    private static (ProductionStudio Studio, Func<Task<bool>> Save, Shot Shot) Fixture(ControlledReviews reviews)
    {
        // Exercise the actual native-close callback without a WebView or live providers.
        var studio = new ProductionStudio { ReviewDrafts = reviews };
        var shot = new Shot { Title = "Review draft", Characters = [new(Guid.NewGuid(), "Ríley 日本語")] };
        Set(studio, "_proposal", new ShotPlanningResult([shot], "fixture", []));
        Set(studio, "_planningJob", new AiJobHeader
        {
            Id = Guid.NewGuid(), Kind = AiJobKind.ShotPlanning, Backend = AiBackend.Codex,
            Target = new(ProjectId: Guid.NewGuid()), ProjectName = "Fixture", TargetName = "Draft shots",
            OriginTabId = Guid.NewGuid(), RequestFingerprint = "fixture", State = AiJobState.Completed
        });
        Set(studio, "_planningDraftDirty", true);
        var save = typeof(ProductionStudio).GetMethod("SaveForCloseAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<Task<bool>>>(studio);
        return (studio, save, shot);
    }

    private static void Set(ProductionStudio studio, string name, object value) =>
        typeof(ProductionStudio).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(studio, value);
    private static T Field<T>(ProductionStudio studio, string name) =>
        (T)typeof(ProductionStudio).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(studio)!;

    private sealed class ControlledReviews : IAiJobReviewStore
    {
        public bool Fail { get; set; }
        public int Writes { get; private set; }
        public AiJobReviewDraft? Saved { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Release { get; init; }
        public Task<AiJobReviewDraft> LoadAsync(Guid jobId, CancellationToken ct = default) => Task.FromResult(Saved ?? new());
        public async Task<AiJobReviewDraft> SaveAsync<T>(Guid jobId, T value, long expectedRevision, CancellationToken ct = default)
        {
            Writes++;
            var captured = JsonSerializer.SerializeToElement(value, AtomicJsonFile.Options);
            Entered.TrySetResult();
            if (Release is not null) await Release.Task.WaitAsync(ct);
            if (Fail) throw new WorkspaceStoreException("Review storage unavailable");
            return Saved = new(expectedRevision + 1, captured);
        }
    }
}
