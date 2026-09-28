namespace Lumibelle.Desktop;
public sealed class App(MainPage page) : Application
{
    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(page) { Title = "Lumibelle", Width = 1440, Height = 980, MinimumWidth = 480, MinimumHeight = 600 };
        window.HandlerChanged += (_, _) => PlatformWindow.Attach(window, page);
        return window;
    }
}
