using Microsoft.AspNetCore.Components.WebView.Maui;
using lumibelle.Models;
namespace Lumibelle.Desktop;

public sealed class MainPage : ContentPage
{
    private readonly DesktopRuntime runtime;
    private readonly AiJobCoordinator jobs;
    private readonly BlazorWebView webView;
    private bool loaded, closing;
    public bool CanClose { get; private set; }
    public MainPage(DesktopRuntime runtime, AiJobCoordinator jobs, MediaResources media)
    {
        this.runtime = runtime; this.jobs = jobs;
        webView = new BlazorWebView { HostPage = "wwwroot/index.html" };
        webView.RootComponents.Add(new RootComponent { Selector = "#app", ComponentType = typeof(lumibelle.Components.Routes) });
        var resources = new PlatformMediaAdapter(media);
        resources.Attach(webView);
        webView.UrlLoading += (_, args) =>
        {
            if (args.Url.Host != "0.0.0.1" && args.Url.Scheme is "http" or "https")
            { args.UrlLoadingStrategy = Microsoft.AspNetCore.Components.WebView.UrlLoadingStrategy.CancelLoad; _ = Launcher.Default.OpenAsync(args.Url); }
        };
        Unloaded += (_, _) => resources.Dispose();
        var file = new MenuBarItem { Text = "File" };
        file.Add(new MenuFlyoutItem { Text = "Quit safely", Command = new Command(async () => { if (await RequestCloseAsync()) Application.Current!.CloseWindow(Window); }) });
        MenuBarItems.Add(file);
        Content = new Label { Text = "Opening Lumibelle…", Margin = 32 };
    }
    protected override async void OnAppearing()
    {
        base.OnAppearing(); if (loaded) return; loaded = true;
        try { await runtime.StartAsync(); Content = webView; }
        catch (Exception ex) { Content = new VerticalStackLayout { Padding = 32, Children = { new Label { Text = "Lumibelle could not open this library." }, new Label { Text = ex.Message } } }; }
    }
    public async Task<bool> RequestCloseAsync()
    {
        if (CanClose) return true;
        if (closing) return false; closing = true;
        try
        {
            if (Content == webView)
            {
                var saved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!await webView.TryDispatchAsync(services => _ = SaveAsync(services, saved))) return false;
                if (!await saved.Task)
                { await DisplayAlertAsync("Couldn’t save", "Resolve the save error in the editor before quitting.", "Keep editing"); return false; }
            }
            var active = jobs.View.Jobs.Count(j => j.State is AiJobState.Waiting or AiJobState.Running);
            if (active > 0 && !await DisplayAlertAsync("Quit Lumibelle?", $"{active} queued or active request(s) will be interrupted locally and recovered on restart. A remote provider may continue working.", "Quit", "Keep open")) return false;
            await runtime.StopAsync(); CanClose = true; return true;
        }
        catch (Exception ex) { await DisplayAlertAsync("Couldn’t quit safely", ex.Message, "Keep open"); return false; }
        finally { closing = false; }
    }
    private static async Task SaveAsync(IServiceProvider services, TaskCompletionSource<bool> saved)
    {
        try { saved.TrySetResult(await services.GetRequiredService<EditSessionRegistry>().SaveAllAsync()); }
        catch (Exception ex) { saved.TrySetException(ex); }
    }
}
