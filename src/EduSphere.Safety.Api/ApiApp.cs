using System.Text;
using EduSphere.Safety.Application;
using EduSphere.Safety.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace EduSphere.Safety.Api;

public static class ApiApp
{
    /// <summary>
    /// Builds the API WebApplication. Test and probe hooks allow extra
    /// configuration plus service overrides (e.g. fake stores); the app is
    /// returned unstarted so callers can inspect or run it.
    /// </summary>
    public static WebApplication Build(
        string[] args,
        Action<IConfigurationBuilder>? configureAppConfig = null,
        Action<IServiceCollection>? overrideServices = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configureAppConfig?.Invoke(builder.Configuration);

        // Payload cap: every JSON body in this API is small (rules bundles are
        // the largest legitimate payload by far); 1 MiB stops body-bloat abuse.
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 1_048_576);

        // Framework console logging (Serilog is a later plan).
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();

        // JSON uses camelCase to match the outbox payload keys.
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
            options.SerializerOptions.DictionaryKeyPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        });

        builder.Services.Configure<PostgresOptions>(builder.Configuration.GetSection("Postgres"));
        builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<PostgresOptions>>().Value);

        var storage = builder.Configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new StorageOptions();
        StorageOptions.Validate(storage);
        builder.Services.AddSingleton(storage);

        builder.Services.AddSharedKernel();
        builder.Services.AddScoped<IIncidentStore, IncidentRepository>();
        builder.Services.AddScoped<IDeviceStore, DeviceStore>();
        builder.Services.AddScoped<ICameraStore, CameraStore>();
        builder.Services.AddScoped<IConfigStore, ConfigStore>();
        builder.Services.AddScoped<INotificationStore, NotificationStore>();
        if (string.Equals(storage.Backend, "S3", StringComparison.OrdinalIgnoreCase))
            builder.Services.AddScoped<IEvidenceStore, S3EvidenceStore>();
        else
            builder.Services.AddScoped<IEvidenceStore, FileEvidenceStore>();
        builder.Services.AddSingleton<IIncidentBroadcaster, SignalRIncidentBroadcaster>();
        builder.Services.AddSignalR();

        var auth = builder.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();
        ValidateAuthOptions(auth);
        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // Every endpoint reads raw JWT names ("tid", "sub", "campus"):
                // keep them unmapped so lookups never depend on handler defaults.
                options.MapInboundClaims = false;
                if (!string.IsNullOrWhiteSpace(auth.Authority))
                {
                    options.Authority = auth.Authority;
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidIssuer = auth.Issuer,
                        ValidateAudience = true,
                        ValidAudience = auth.Audience,
                    };
                }
                else
                {
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(auth.IssuerSigningKey)),
                        ValidateIssuer = true,
                        ValidIssuer = auth.Issuer,
                        ValidateAudience = true,
                        ValidAudience = auth.Audience,
                        ValidateLifetime = true,
                        ClockSkew = TimeSpan.FromMinutes(1),
                    };
                }
            });
        builder.Services.AddAuthorization();
        builder.Services.AddSafetyRateLimiting(builder.Configuration);

        overrideServices?.Invoke(builder.Services);

        var app = builder.Build();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        IncidentsEndpoints.Map(app);
        DeviceEndpoints.Map(app);
        CameraEndpoints.Map(app);
        ConfigEndpoints.Map(app);
        EvidenceEndpoints.Map(app);
        return app;
    }

    /// <summary>
    /// Fail-fast auth validation shared by Api startup (<see cref="Build"/>)
    /// and in-process probes: when the dev stub is enabled, the HS256 signing
    /// key must be at least 32 bytes (UTF-8).
    /// </summary>
    public static void ValidateAuthOptions(AuthOptions auth)
    {
        if (auth.DevStubEnabled
            && Encoding.UTF8.GetByteCount(auth.IssuerSigningKey ?? string.Empty) < 32)
            throw new InvalidOperationException(
                "Auth:IssuerSigningKey must be at least 32 bytes (UTF-8) when Auth:DevStubEnabled is true.");
        if (string.IsNullOrWhiteSpace(auth.Authority))
        {
            if (string.IsNullOrWhiteSpace(auth.IssuerSigningKey)
                || string.IsNullOrWhiteSpace(auth.Issuer)
                || string.IsNullOrWhiteSpace(auth.Audience))
                throw new InvalidOperationException(
                    "Auth is not configured: set Auth:Authority (+ Issuer/Audience) for a real " +
                    "provider, or Auth:IssuerSigningKey with Issuer/Audience for local dev.");
        }
        else if (string.IsNullOrWhiteSpace(auth.Issuer) || string.IsNullOrWhiteSpace(auth.Audience))
        {
            throw new InvalidOperationException(
                "Auth:Authority is set but Issuer/Audience are missing.");
        }
    }
}
