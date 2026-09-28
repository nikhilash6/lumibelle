using System.Globalization;
using lumibelle;

namespace Lumibelle.Tests;

public class DisplayCultureTests
{
    [Theory]
    [InlineData("en-SE")]
    [InlineData("sv-SE")]
    [InlineData("de-DE")]
    public void RegionalCulturesDisplayInvariantNumbersAndKeepTheirDates(string name)
    {
        var regional = new CultureInfo(name);
        var culture = DisplayCulture.Create(regional);
        Assert.Equal(name, culture.Name);
        Assert.Equal("14.375 s", string.Format(culture, "{0:0.###} s", 14.375));
        Assert.Equal("-1,234.5", (-1234.5).ToString("N1", culture));
        Assert.Equal(regional.DateTimeFormat.ShortDatePattern, culture.DateTimeFormat.ShortDatePattern);
        Assert.Equal(regional.DateTimeFormat.GetAbbreviatedMonthName(9), culture.DateTimeFormat.GetAbbreviatedMonthName(9));
        Assert.Equal(",", regional.NumberFormat.NumberDecimalSeparator);
    }
}
