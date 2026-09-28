namespace Lumibelle.Desktop;
internal static class PlatformWindow
{
    public static void Attach(Window window, MainPage page)
    {
        // Mac Catalyst native termination cannot be vetoed by MAUI Window.Destroying.
        // A native termination delegate is required before this host passes release acceptance.
        window.Destroying += (_, _) => System.Diagnostics.Debug.WriteLine("Mac termination requires native close/save validation.");
    }
}
