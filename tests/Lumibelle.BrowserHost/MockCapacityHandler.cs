using System.Net;
using System.Text;

public sealed class MockCapacityState { public int ComfyChecks, RouterChecks; }
public sealed class MockCapacityHandler(MockCapacityState state) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string json;
        if (request.Method != HttpMethod.Get) throw new InvalidOperationException("Capacity only permits read-only discovery.");
        if (request.RequestUri!.AbsolutePath == "/system_stats")
        {
            Interlocked.Increment(ref state.ComfyChecks);
            json = """{"devices":[{"name":"Primary GPU","type":"cuda","index":0,"vram_total":21474836480,"vram_free":7516192768},{"name":"Secondary GPU","type":"cuda","index":1,"vram_total":8589934592,"vram_free":1073741824}]}""";
        }
        else if (request.RequestUri.AbsolutePath == "/api/v1/key")
        {
            Interlocked.Increment(ref state.RouterChecks);
            json = """{"data":{"limit":20,"limit_remaining":12.25,"limit_reset":"monthly","usage":7.75,"usage_daily":0.12,"usage_weekly":2.5,"usage_monthly":7.75}}""";
        }
        else throw new InvalidOperationException("Unexpected live HTTP request: " + request.RequestUri.AbsolutePath);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}
