using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace EduSphere.Safety.Api;

/// <summary>
/// Rate limiting for the anonymous code-guessing surface: enrolment-code
/// redemption and the local dev-token minter. Authenticated staff/device
/// endpoints (including high-frequency ingest and heartbeat) are NOT
/// throttled here — their credentials are the control, and false 429s on
/// the safety path would be worse than the abuse they prevent.
/// Limits are configuration (<c>RateLimit:CodeGatedPermitLimit</c> per
/// <c>RateLimit:CodeGatedWindowMinutes</c>); tests raise the ceiling so
/// unrelated suites never trip it.
/// </summary>
public static class RateLimiting
{
    /// <summary>Policy for single-use code/token endpoints.</summary>
    public const string CodeGatedPolicy = "code-gated";

    public sealed class RateLimitOptions
    {
        public const string SectionName = "RateLimit";

        public int CodeGatedPermitLimit { get; set; } = 10;

        public int CodeGatedWindowMinutes { get; set; } = 1;
    }

    public static IServiceCollection AddSafetyRateLimiting(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.Configure<RateLimitOptions>(configuration.GetSection(RateLimitOptions.SectionName));
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) =>
            {
                var limits = context.HttpContext.RequestServices
                    .GetRequiredService<Microsoft.Extensions.Options.IOptions<RateLimitOptions>>().Value;
                var fallback = Math.Max(1, limits.CodeGatedWindowMinutes) * 60;
                var seconds = fallback;
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    seconds = Math.Max(1, (int)retryAfter.TotalSeconds);
                context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
                return ValueTask.CompletedTask;
            };
            options.AddPolicy(CodeGatedPolicy, context =>
            {
                var limits = context.RequestServices
                    .GetRequiredService<Microsoft.Extensions.Options.IOptions<RateLimitOptions>>().Value;
                var window = TimeSpan.FromMinutes(Math.Max(1, limits.CodeGatedWindowMinutes));
                return RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = Math.Max(1, limits.CodeGatedPermitLimit),
                        Window = window,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0,
                    });
            });
        });
        return services;
    }
}
