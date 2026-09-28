using System.Net;
using Bunit;
using lumibelle.Components;
using lumibelle.Components.AI;
using lumibelle.Components.Pages;
using lumibelle.Components.Script;
using lumibelle.Models;
using lumibelle.Services.AI;
using lumibelle.Services.Assets;
using lumibelle.Services.Story;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace Lumibelle.Tests;

public sealed class ComfyAccessModalTests
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [Fact]
    public async Task CheckOpensModalAndSavePersistsUrlAndTokenThenRetriesWithoutAnotherBasePageSave()
    {
        await using var f = new MainPageFixture();
        var page = f.Ui.Render<AiSettingsPage>();
        page.WaitForElement("#comfy-url").Change("https://comfy.example");
        Click(page, "Check connection");
        f.Dialogs.WaitForElement("#access-dialog-client-id").Input("test-id.access");
        Assert.Contains("https://comfy.example", f.Dialogs.Markup);
        f.Dialogs.Find("#access-dialog-client-secret").Input("test-secret");
        var save = f.Dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Save connection and retry");
        Assert.Contains("mud-button-filled", save.ClassName ?? "");
        f.Dialogs.Find("form.comfy-access-token-form").Submit();
        page.WaitForAssertion(() => Assert.Contains("Connected to ComfyUI", page.Markup));
        f.Dialogs.WaitForAssertion(() => Assert.Empty(f.Dialogs.FindAll("#access-dialog-client-secret")));
        Assert.Equal("https://comfy.example", f.Settings.Value.ComfyUrl);
        Assert.Equal("test-secret", (await f.Access.Store.ResolveAsync(new("https://comfy.example/prompt"), Ct))!.ClientSecret);
        Assert.Contains("WebSocket check passed", page.Markup);
        Assert.DoesNotContain("test-secret", page.Markup + f.Dialogs.Markup);
        Assert.All(f.Requests, p => Assert.True(p is "/object_info" or "/system_stats"));
        // The ordinary save must not erase authentication, including when revisited.
        page.Find("#connection-comfyui-form").Submit();
        page.WaitForAssertion(() => Assert.Contains("connection saved", page.Markup));
        Click(page, "Check connection");
        page.WaitForAssertion(() => Assert.Contains("Connected to ComfyUI", page.Markup));
        Assert.Empty(f.Dialogs.FindAll("#access-dialog-client-secret"));
    }

    [Fact]
    public async Task CancellingAutoPromptDoesNotSaveEitherTheDraftUrlOrCredentials()
    {
        await using var f = new MainPageFixture();
        var original = f.Settings.Value.ComfyUrl;
        var page = f.Ui.Render<AiSettingsPage>();
        page.WaitForElement("#comfy-url").Change("https://comfy.example");
        Click(page, "Check connection");
        f.Dialogs.WaitForElement("#access-dialog-client-secret").Input("unsaved-secret");
        Click(f.Dialogs, "Cancel");
        f.Dialogs.WaitForAssertion(() => Assert.Empty(f.Dialogs.FindAll("#access-dialog-client-secret")));
        Assert.Empty((await f.Access.Store.LoadAsync(Ct)).Entries);
        Assert.Equal(original, f.Settings.Value.ComfyUrl);
        Assert.Equal("https://comfy.example", page.Find("#comfy-url").GetAttribute("value"));
    }

    [Fact]
    public async Task FailedRetryDoesNotReopenTheCredentialsModalInALoop()
    {
        await using var f = new MainPageFixture { Mode = "reject-saved" };
        var page = f.Ui.Render<AiSettingsPage>();
        page.WaitForElement("#comfy-url").Change("https://comfy.example");
        Click(page, "Check connection");
        f.Dialogs.WaitForElement("#access-dialog-client-id").Input("id");
        f.Dialogs.Find("#access-dialog-client-secret").Input("bad-secret");
        f.Dialogs.Find("form").Submit();
        page.WaitForAssertion(() => Assert.Contains("rejected the saved Access credentials", page.Markup));
        f.Dialogs.WaitForAssertion(() => Assert.Empty(f.Dialogs.FindAll("#access-dialog-client-secret")));
        Assert.Equal(2, f.Requests.Count); // first discovery, then one retry only
        Click(page, "Authentication…");
        f.Dialogs.WaitForElement("#access-dialog-client-secret");
    }

    [Theory]
    [InlineData("redirect")]
    [InlineData("denied")]
    [InlineData("waf")]
    public async Task AmbiguousCloudflareResponsesDoNotAutomaticallyAskForSecrets(string mode)
    {
        await using var f = new MainPageFixture { Mode = mode };
        var page = f.Ui.Render<AiSettingsPage>();
        page.WaitForElement("#comfy-url").Change("https://comfy.example");
        Click(page, "Check connection");
        page.WaitForAssertion(() => Assert.False(page.Find("#comfy-url").HasAttribute("disabled")));
        Assert.Empty(f.Dialogs.FindAll("#access-dialog-client-secret"));
        Assert.Single(f.Requests);
        Click(page, "Authentication…");
        f.Dialogs.WaitForElement("#access-dialog-client-secret");
    }

    [Fact]
    public async Task PartialSaveReportsExactlyWhatPersistedAndRetryDoesNotRequireReenteringTheToken()
    {
        await using var f = new MainPageFixture();
        var original = f.Settings.Value.ComfyUrl;
        f.Settings.SaveError = new WorkspaceStoreException("Simulated disk failure");
        var page = f.Ui.Render<AiSettingsPage>();
        page.WaitForElement("#comfy-url").Change("https://comfy.example");
        Click(page, "Authentication…");
        f.Dialogs.WaitForElement("#access-dialog-client-id").Input("id");
        f.Dialogs.Find("#access-dialog-client-secret").Input("saved-once");
        f.Dialogs.Find("form").Submit();
        f.Dialogs.WaitForAssertion(() => Assert.Contains("Credentials are saved, but the connection URL could not be saved", f.Dialogs.Markup));
        var revision = (await f.Access.Store.LoadAsync(Ct)).Revision;
        Assert.Equal(original, f.Settings.Value.ComfyUrl);
        Assert.Equal("", f.Dialogs.Find("#access-dialog-client-secret").GetAttribute("value") ?? "");
        Assert.Contains("Retry saving connection", f.Dialogs.Markup);
        f.Settings.SaveError = null;
        f.Dialogs.Find("form").Submit();
        page.WaitForAssertion(() => Assert.Contains("Connected to ComfyUI", page.Markup));
        Assert.Equal("https://comfy.example", f.Settings.Value.ComfyUrl);
        Assert.Equal(revision, (await f.Access.Store.LoadAsync(Ct)).Revision);
    }

    [Fact]
    public async Task BlankFieldsKeepAnExistingTokenAndAHalfEnteredPairCannotReplaceIt()
    {
        await using var f = new MainPageFixture();
        await f.Access.Store.SaveAsync("https://comfy.example", "existing-id", "existing-secret", 0, Ct);
        var page = f.Ui.Render<AiSettingsPage>();
        page.WaitForElement("#comfy-url").Change("https://comfy.example");
        Click(page, "Authentication…");
        f.Dialogs.WaitForElement("#access-dialog-client-id");
        Assert.Contains("already saved", f.Dialogs.Markup);
        Assert.DoesNotContain("existing-secret", f.Dialogs.Markup);
        f.Dialogs.Find("#access-dialog-client-id").Input("only-half");
        f.Dialogs.Find("form").Submit();
        f.Dialogs.WaitForAssertion(() => Assert.Contains("Enter both", f.Dialogs.Markup));
        Assert.Equal("existing-secret", (await f.Access.Store.ResolveAsync(new("https://comfy.example/queue"), Ct))!.ClientSecret);
        f.Dialogs.Find("#access-dialog-client-id").Input("");
        f.Dialogs.Find("form").Submit();
        page.WaitForAssertion(() => Assert.Contains("Connected to ComfyUI", page.Markup));
        Assert.Equal(1, (await f.Access.Store.LoadAsync(Ct)).Revision);
    }

    [Fact]
    public async Task StaleCredentialRotationRetainsInputAndRequiresExplicitReload()
    {
        await using var f = new MainPageFixture();
        var page = f.Ui.Render<AiSettingsPage>();
        page.WaitForElement("#comfy-url").Change("https://comfy.example");
        Click(page, "Authentication…");
        f.Dialogs.WaitForElement("#access-dialog-client-id").Input("draft-id");
        f.Dialogs.Find("#access-dialog-client-secret").Input("draft-secret");
        await f.Access.Store.SaveAsync("https://comfy.example", "other-id", "other-secret", 0, Ct);
        f.Dialogs.Find("form").Submit();
        f.Dialogs.WaitForAssertion(() => Assert.Contains("another window", f.Dialogs.Markup));
        Assert.Equal("draft-secret", f.Dialogs.Find("#access-dialog-client-secret").GetAttribute("value"));
        Click(f.Dialogs, "Reload saved credentials");
        f.Dialogs.WaitForAssertion(() => Assert.Contains("status reloaded", f.Dialogs.Markup));
        f.Dialogs.Find("form").Submit();
        page.WaitForAssertion(() => Assert.Contains("Connected to ComfyUI", page.Markup));
        Assert.Equal("draft-secret", (await f.Access.Store.ResolveAsync(new("https://comfy.example/view"), Ct))!.ClientSecret);
    }

    [Fact]
    public async Task RemovingFromTheModalRequiresConfirmationAndPreservesTheConnectionUrl()
    {
        await using var f = new MainPageFixture();
        await f.Access.Store.SaveAsync("https://comfy.example", "id", "secret", 0, Ct);
        var original = f.Settings.Value.ComfyUrl;
        var page = f.Ui.Render<AiSettingsPage>();
        page.WaitForElement("#comfy-url").Change("https://comfy.example");
        Click(page, "Authentication…");
        f.Dialogs.WaitForElement("#access-dialog-client-id");
        Click(f.Dialogs, "Remove saved credentials…");
        Assert.Single((await f.Access.Store.LoadAsync(Ct)).Entries);
        Click(f.Dialogs, "Keep credentials");
        Assert.Single((await f.Access.Store.LoadAsync(Ct)).Entries);
        Click(f.Dialogs, "Remove saved credentials…");
        Click(f.Dialogs, "Confirm removal");
        f.Dialogs.WaitForAssertion(() => Assert.Contains("Saved credentials removed", f.Dialogs.Markup));
        Assert.Empty((await f.Access.Store.LoadAsync(Ct)).Entries);
        Assert.Equal(original, f.Settings.Value.ComfyUrl);
    }

    [Fact]
    public async Task DialogNeverRetargetsSecretsWhenItsParametersChange()
    {
        await using var f = new MainPageFixture();
        string? savedUrl = null; var completions = 0;
        var editor = f.Ui.Render<ComfyAccessEditor>(p => p
            .Add(x => x.ServerUrl, "https://original.example/prefix")
            .Add(x => x.SaveConnection, (string url) => { savedUrl = url; return Task.FromResult<string?>(null); })
            .Add(x => x.Completed, () => completions++));
        editor.WaitForElement("#access-dialog-client-id");
        editor.Render(p => p.Add(x => x.ServerUrl, "https://different.example"));
        editor.Find("#access-dialog-client-id").Input("id");
        editor.Find("#access-dialog-client-secret").Input("secret");
        editor.Find("form").Submit();
        editor.WaitForAssertion(() => Assert.Equal(1, completions));
        Assert.Equal("https://original.example/prefix", savedUrl);
        Assert.NotNull(await f.Access.Store.ResolveAsync(new("https://original.example/queue"), Ct));
        Assert.Null(await f.Access.Store.ResolveAsync(new("https://different.example/queue"), Ct));
    }

    [Fact]
    public async Task InsecureServerCannotOpenACredentialForm()
    {
        await using var f = new MainPageFixture();
        var page = f.Ui.Render<AiSettingsPage>();
        page.WaitForElement("#comfy-url").Change("http://comfy.example");
        Click(page, "Authentication…");
        Assert.Contains("require an HTTPS", page.Markup);
        Assert.Empty(f.Dialogs.FindAll("#access-dialog-client-secret"));
        Assert.Empty((await f.Access.Store.LoadAsync(Ct)).Entries);
    }

    // Confirmations render after the click that asks for them, so wait for the button to appear.
    private static void Click<T>(IRenderedComponent<T> component, string text) where T : Microsoft.AspNetCore.Components.IComponent
    {
        AngleSharp.Dom.IElement? button = null;
        component.WaitForAssertion(() => button = component.FindAll("button").Single(b => b.TextContent.Trim() == text && b.Closest("[hidden]") is null));
        button!.Click();
    }

    private sealed class MainPageFixture : IAsyncDisposable
    {
        public ComfyAccessFixture Access { get; } = new();
        public BunitContext Ui { get; } = new();
        public FakeAiSettingsStore Settings { get; } = new();
        public IRenderedComponent<MudDialogProvider> Dialogs { get; }
        public List<string> Requests { get; } = [];
        public string Mode { get; set; } = "access";
        private readonly ModelTestQueueFixture _queue = new();

        public MainPageFixture()
        {
            var clients = new ComfyAccessTestClients(() => ComfyAccessTestHttp.Client(Access.Store, Send));
            _queue.Register(Ui.Services);
            Ui.Services.AddMudServices();
            Ui.Services.AddSingleton<IAiSettingsStore>(Settings);
            Ui.Services.AddSingleton<IAiProviderRegistry>(new AiProviderRegistry(clients, Settings, TestComfy.Monitor()));
            Ui.Services.AddSingleton<IComfyAccessCredentialStore>(Access.Store);
            Ui.Services.AddSingleton<IComfyAccessProbe>(new SocketProbe());
            Ui.Services.AddSingleton<IProjectAiPreferencesStore>(new FakeProjectAiPreferencesStore());
            Ui.Services.AddSingleton<IReferenceImageGenerator>(new FakeReferenceImageGenerator());
            Ui.Services.AddSingleton<IReferenceImageEditor>(new FakeReferenceImageEditor());
            Ui.Services.AddSingleton<IComfyLoraCatalog>(new FakeLoraCatalog());
            Ui.Services.AddSingleton<ApplicationSession>(); Ui.Services.AddSingleton(TimeProvider.System);
            Ui.Services.AddSingleton<MarkdownRenderer>();
            Ui.Services.AddSingleton<lumibelle.Services.IProjectStore>(new FakeProjectStore());
            Ui.JSInterop.Mode = JSRuntimeMode.Loose;
            Ui.JSInterop.SetupModule("./_content/Lumibelle.UI/ai-jobs.js").Setup<bool>("isReviewVisible", _ => true).SetResult(true);
            Ui.Render<MudPopoverProvider>();
            Dialogs = Ui.Render<MudDialogProvider>();
            _queue.Start(Ui.Services);
        }

        private Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri!.AbsolutePath);
            var authenticated = request.Headers.Contains(ComfyAccessTransport.ClientSecretHeader);
            if (Mode == "waf")
            {
                var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
                response.Headers.TryAddWithoutValidation("cf-mitigated", "challenge");
                return Task.FromResult(response);
            }
            if (Mode == "denied" || Mode == "reject-saved" && authenticated)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            if (!authenticated || Mode == "redirect")
            {
                var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                response.Headers.TryAddWithoutValidation("CF-Ray", "example");
                response.Headers.Location = new(Mode == "redirect" ? "https://comfy.example/new-location"
                    : "https://team.cloudflareaccess.com/cdn-cgi/access/login/comfy.example?do-not-echo=private");
                return Task.FromResult(response);
            }
            return Task.FromResult(ComfyAccessTestHttp.Json(request.RequestUri.AbsolutePath == "/object_info"
                ? "{\"CLIPLoader\":{\"input\":{\"required\":{\"clip_name\":[[\"encoder\"]]}}},\"TextGenerate\":{},\"PreviewAny\":{}}"
                : "{\"system\":{\"comfyui_version\":\"test-version\"},\"devices\":[]}"));
        }

        // MudBlazor's PopoverService is async-only, so the rendered context must be disposed asynchronously.
        public async ValueTask DisposeAsync() { _queue.Dispose(); await Ui.DisposeAsync(); Access.Dispose(); }
    }

    private sealed class SocketProbe : IComfyAccessProbe
    {
        public Task<ComfyAccessCheck> CheckAsync(string url, CancellationToken ct = default) =>
            Task.FromResult(new ComfyAccessCheck(true, true, "HTTP check passed", "WebSocket check passed"));
    }
}
