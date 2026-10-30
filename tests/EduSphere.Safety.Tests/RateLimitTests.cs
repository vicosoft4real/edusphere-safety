using System.Net;
using System.Net.Http.Json;
using Xunit;

/// <summary>
/// Full-stack proof that the code-guessing surface is throttled: with a low
/// ceiling, rapid enrol-code redemptions flip from 400 (bad code) to 429 with
/// a Retry-After hint. Connected-CI only (WebApplicationFactory is env-gated
/// in-sandbox); the same policy is executed directly by /tmp/task8probe.
/// </summary>
public sealed class RateLimitTests : IClassFixture<RateLimitTests.ThrottledFactory>
{
    public sealed class ThrottledFactory : SafetyApiFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("RateLimit:CodeGatedPermitLimit", "2");
        }
    }

    private readonly ThrottledFactory _factory;

    public RateLimitTests(ThrottledFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Enrol_OverLimit_Returns429WithRetryAfter()
    {
        var client = _factory.CreateClient();
        var first = await client.PostAsJsonAsync("/v1/devices/enrol", new { code = "bogus-1" });
        var second = await client.PostAsJsonAsync("/v1/devices/enrol", new { code = "bogus-2" });
        Assert.Equal(HttpStatusCode.BadRequest, first.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);

        var throttled = await client.PostAsJsonAsync("/v1/devices/enrol", new { code = "bogus-3" });
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.True(throttled.Headers.Contains("Retry-After"));
    }
}
