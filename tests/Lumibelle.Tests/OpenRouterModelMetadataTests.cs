using System.Globalization;
using System.Text.Json;
using lumibelle.Services.AI;

namespace Lumibelle.Tests;

public sealed class OpenRouterModelMetadataTests
{
    [Fact]
    public void CatalogDatesAndPopularityPositionsAreOptionalAndPreciselyLabeled()
    {
        var date = new DateTimeOffset(2026, 9, 2, 8, 30, 0, TimeSpan.Zero);
        var model = JsonSerializer.SerializeToElement(new { created = date.ToUnixTimeSeconds() });
        var metadata = OpenRouterModelMetadata.Read(model, 17);
        Assert.Equal(date, metadata.AddedUtc); Assert.Equal(17, metadata.PopularityOrder);
        Assert.Null(OpenRouterModelMetadata.Read(model).PopularityOrder);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"created\":null}")]
    [InlineData("{\"created\":\"invalid\"}")]
    [InlineData("{\"created\":-1}")]
    [InlineData("{\"created\":0}")]
    [InlineData("{\"created\":123.5}")]
    [InlineData("{\"created\":253402300800}")]
    public void InvalidCatalogDatesRemainUnknown(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Null(OpenRouterModelMetadata.Read(doc.RootElement).AddedUtc);
    }

    [Theory]
    [InlineData("0", "$0 / 1M", true)]
    [InlineData("0.000000000001", "$0.000001 / 1M", false)]
    [InlineData("2.5e-7", "$0.25 / 1M", false)]
    [InlineData("-1", "Not reported", false)]
    [InlineData("garbage", "Not reported", false)]
    [InlineData("1e-35", "Not reported", false)]
    [InlineData("1e30", "Not reported", false)]
    public void PricesUseExactDecimalsAndNeverRoundPositiveOrUnknownRatesToFree(string price, string display, bool free)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE");
            var model = JsonSerializer.SerializeToElement(new { pricing = new { prompt = price, completion = "0" } });
            var pricing = OpenRouterModelMetadata.Read(model).Pricing!;
            Assert.Equal(display, OpenRouterModelMetadata.PerMillion(pricing.InputPerToken));
            Assert.Equal(free, pricing.FreeText);
            Assert.Equal(price == "-1", pricing.Variable);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"pricing\":null}")]
    [InlineData("{\"pricing\":{\"prompt\":0,\"completion\":false}}")]
    [InlineData("{\"id\":\"openrouter/auto\",\"pricing\":{\"prompt\":0,\"completion\":0}}")]
    [InlineData("{\"pricing\":{\"prompt\":0,\"completion\":0,\"request\":\"0.001\"}}")]
    [InlineData("{\"pricing\":{\"prompt\":0,\"completion\":0,\"internal_reasoning\":\"invalid\"}}")]
    [InlineData("{\"pricing\":{\"prompt\":0,\"completion\":0,\"overrides\":[{}]}}")]
    public void MissingDynamicAndConditionalRatesAreNotFree(string json) =>
        Assert.False(OpenRouterModelMetadata.Read(JsonDocument.Parse(json).RootElement).Pricing!.FreeText);

    [Fact]
    public void MetadataRetainsCapabilitiesAndAdditionalCharges()
    {
        using var json = JsonDocument.Parse("""
            {"description":"A model", "context_length":131072,"top_provider":{"max_completion_tokens":8192},
             "supported_parameters":["reasoning"],"pricing":{"prompt":"0.0000002","completion":"0.0000007",
             "image":"0.001","web_search":"0.005","request":"bad","input_cache_read":"0.00000002",
             "overrides":[{"condition":"long context"}]}}
            """);
        var info = OpenRouterModelMetadata.Read(json.RootElement);
        Assert.True(info.SupportsReasoning); Assert.Equal(131072, info.ContextLength); Assert.Equal(8192, info.MaxOutputTokens);
        Assert.Equal("A model", info.Description); Assert.True(info.Pricing!.Conditional);
        Assert.Equal("$0.001 / image", OpenRouterModelMetadata.AdditionalPrice("image", info.Pricing.Additional["image"]));
        Assert.Equal("$0.02 / 1M tokens", OpenRouterModelMetadata.AdditionalPrice("input_cache_read", info.Pricing.Additional["input_cache_read"]));
        Assert.Null(info.Pricing.Additional["request"]);
        Assert.Equal("https://openrouter.ai/test/model%3Afree", OpenRouterModelMetadata.ModelUrl("test/model:free"));
    }
}
