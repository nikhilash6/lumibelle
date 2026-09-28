using System.Runtime.CompilerServices;
using Bunit;

namespace Lumibelle.Tests;

internal static class BunitDefaults
{
    // Component tests run alongside the rest of the suite. bUnit's one-second default
    // for WaitFor* is too tight under that load; a passing wait still returns at once.
    [ModuleInitializer]
    internal static void Initialize() => BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(5);
}
