using System.Globalization;

namespace lumibelle;

// Prompts, validation and numeric inputs use invariant decimals ("14.375 seconds").
// Display numbers the same way on every host while keeping regional dates and names.
public static class DisplayCulture
{
    public static CultureInfo Create(CultureInfo regional)
    {
        var culture = (CultureInfo)regional.Clone();
        culture.NumberFormat = (NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone();
        return culture;
    }

    // Call once at host startup, before any circuit or window renders.
    public static void Apply()
    {
        var culture = Create(CultureInfo.CurrentCulture);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.CurrentCulture = culture;
    }
}
