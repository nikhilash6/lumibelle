using Bunit;
using Bunit.TestDoubles;
using lumibelle.Components.Assets;
using lumibelle.Models;

namespace Lumibelle.Tests;

public sealed partial class AssetComponentTests
{
    [Fact]
    public async Task BackgroundLibraryRefreshKeepsSidebarControlsStable()
    {
        var asset = Asset("Mira");
        _assets.Library = _assets.Library with { Assets = [asset] };
        var page = Page();
        page.WaitForElement("#image-prompt");
        var refresh = page.FindComponent<Stub<ReferenceReelsPanel>>().Instance.Parameters.Get(p => p.Mutate);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _assets.BeforeLoad = () => { started.TrySetResult(); return release.Task; };

        // Reel job notifications use this callback even while an image job is running.
        var updating = page.InvokeAsync(() => refresh(_ => _assets.LoadAsync(_projectId)));
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken);
            // Another progress callback renders while the library read is still in flight.
            await page.InvokeAsync(() => page.Render());
            Assert.False(page.Find(".asset-library-extraction .request-action-button").HasAttribute("disabled"));
            Assert.False(page.Find(".asset-list-row.selected .asset-drag").HasAttribute("disabled"));
            Assert.Equal("true", page.Find(".asset-list-row.selected .asset-choice").GetAttribute("aria-pressed"));
            Assert.Contains("Saved", _actions.Markup);

            _assets.Library = _assets.Library with
            {
                Revision = _assets.Library.Revision + 1,
                Assets = [asset with { Images = [new AssetImage { Id = Guid.NewGuid(), FileName = "result.png", ContentType = "image/png", Width = 3, Height = 2 }] }]
            };
        }
        finally { release.TrySetResult(); }
        await updating;
        page.WaitForAssertion(() => Assert.Contains("1 image", page.Find(".asset-list-row.selected").TextContent));
        Assert.Single(page.FindAll(".reference-card"));
        Assert.Equal(0, _assets.SaveCalls);
    }
}
