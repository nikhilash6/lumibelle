using lumibelle.Services.AI;
using lumibelle.Services.Story;
using MudBlazor.Services;
namespace lumibelle;
public static class LumibelleUiServices
{
 public static IServiceCollection AddLumibelleUI(this IServiceCollection services)
 {
  // Top center keeps snackbars clear of dialog close buttons and footer actions.
  services.AddMudServices(config => config.SnackbarConfiguration.PositionClass = MudBlazor.Defaults.Classes.Position.TopCenter);
  services.AddSingleton<MarkdownRenderer>();
  services.AddScoped<IAiReviewGate, AiReviewGate>();
  services.AddScoped<TextRequestReviews>();
  services.AddScoped<EditSessionRegistry>();
  services.AddScoped<lumibelle.Services.WorkspacePositions>();
  services.AddScoped<lumibelle.Services.AppearanceState>();
  Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddScoped<IHostActions, BrowserHostActions>(services);
  return services;
 }
}
public static class UiAssets
{
 public const string Root = "./_content/Lumibelle.UI/";
 public static string Module(string name) => Root + name;
}
