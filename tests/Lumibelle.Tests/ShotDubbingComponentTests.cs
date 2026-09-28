using Bunit;
using lumibelle.Components.Pages;
using lumibelle.Services.Production;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Lumibelle.Tests;

public sealed partial class ShotDubbingStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LanguageEditorIgnoresEmptyRowsButRetainsIncompleteDrafts(bool incomplete)
    {
        var f = await StoreFixture();
        await using var ui = new BunitContext(); ui.JSInterop.Mode = JSRuntimeMode.Loose;
        ui.Services.AddMudServices();
        ui.Services.AddSingleton<IProjectDubbingStore>(f.Store);
        var page = ui.Render<ProjectLanguagesPanel>(p => p.Add(c => c.ProjectId, f.Project.Id));
        page.WaitForElement(".language-row input");
        // Each step runs on the renderer's dispatcher, so a re-render cannot swap an element between finding and using it.
        await page.InvokeAsync(() => page.FindAll(".language-row input")[0].Input("en"));
        await page.InvokeAsync(() => page.FindAll(".language-row input")[1].Input("English"));
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.TextContent == "Add dub language").Click());
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll(".language-row").Count));
        if (incomplete) await page.InvokeAsync(() => page.FindAll(".language-row input")[2].Input("sv"));
        await page.InvokeAsync(() => page.FindAll("button").Single(b => b.TextContent.Trim() == "Save languages").ClickAsync(new()));
        if (incomplete) page.WaitForAssertion(() => Assert.Contains("language code", page.Find("[role=alert]").TextContent));
        // Poll from the test thread: blocking on the store inside WaitForAssertion would deadlock the renderer.
        else for (var i = 0; i < 100 && (await f.Store.LanguagesAsync(f.Project.Id, _ct)).Revision == 0; i++) await Task.Delay(20, _ct);

        var saved = await f.Store.LanguagesAsync(f.Project.Id, _ct);
        if (incomplete)
        {
            Assert.Null(saved.Main); Assert.Equal(0, saved.Revision);
            Assert.Contains("language code", page.Find("[role=alert]").TextContent);
            Assert.Equal("sv", page.FindAll(".language-row input")[2].GetAttribute("value"));
            Assert.False(page.FindAll("button").Single(b => b.TextContent.Trim() == "Save languages").HasAttribute("disabled"));
        }
        else
        {
            Assert.Equal(ShotDubbingTests.English, saved.Main); Assert.Empty(saved.Dubs);
            Assert.Equal(1, saved.Revision); Assert.Empty(page.FindAll("[role=alert]"));
        }
    }
}
