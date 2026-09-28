using Microsoft.AspNetCore.DataProtection;
namespace Lumibelle.Desktop;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        DisplayCulture.Apply();
        var builder = MauiApp.CreateBuilder().UseMauiApp<App>();
        var data = Environment.GetEnvironmentVariable("LUMIBELLE_DATA_DIRECTORY");
        var projects = Environment.GetEnvironmentVariable("LUMIBELLE_PROJECTS_DIRECTORY");
        var paths = new ApplicationPaths(data ?? ApplicationPaths.UserDefault().Data, projects);
        builder.Services.AddMauiBlazorWebView();
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.Replace(builder.Services,
            ServiceDescriptor.Singleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new DesktopHostEnvironment()));
        // The account key ring and application/purpose names match the existing Web edition.
        builder.Services.AddDataProtection().SetApplicationName("Lumibelle");
        builder.Services.AddSingleton<ISecretProtector, DesktopSecretProtector>();
        builder.Services.AddScoped<IHostActions, DesktopHostActions>();
        builder.Services.AddLumibelleCore(paths).AddLumibelleUI();
        builder.Services.AddSingleton<DesktopRuntime>();
        builder.Services.AddSingleton<MainPage>();
#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif
        return builder.Build();
    }
}

internal sealed class DesktopSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    private readonly IDataProtector protector = provider.CreateProtector("Lumibelle.AiCredentials.v1");
    public string Protect(string value) => protector.Protect(value);
    public string Unprotect(string value) => protector.Unprotect(value);
}
