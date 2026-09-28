using Microsoft.UI.Xaml;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Lumibelle.Desktop.WinUI;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : MauiWinUIApplication
{
	/// <summary>
	/// Initializes the singleton application object.  This is the first line of authored code
	/// executed, and as such is the logical equivalent of main() or WinMain().
	/// </summary>
	public App()
	{
		UnhandledException += (_, e) => WriteStartupError(e.Exception);
		AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteStartupError(e.ExceptionObject as Exception);
		this.InitializeComponent();
	}

	protected override MauiApp CreateMauiApp()
	{
		try { return MauiProgram.CreateMauiApp(); }
		catch (Exception e) { WriteStartupError(e); throw; }
	}
	private static void WriteStartupError(Exception? error)
	{
		try {
			var path = Path.Combine(Environment.GetEnvironmentVariable("LUMIBELLE_DATA_DIRECTORY") ?? ApplicationPaths.UserDefault().Data, "logs");
			Directory.CreateDirectory(path); File.AppendAllText(Path.Combine(path, "desktop-startup.log"), $"{DateTimeOffset.UtcNow:O} {error}\n");
		} catch { }
	}
}
