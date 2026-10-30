using EduSphere.Safety.Application;
using EduSphere.Safety.Infrastructure;
using EduSphere.Safety.Worker;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Services.Configure<PostgresOptions>(builder.Configuration.GetSection("Postgres"));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<PostgresOptions>>().Value);
builder.Services.Configure<OutboxOptions>(builder.Configuration.GetSection(OutboxOptions.SectionName));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<OutboxOptions>>().Value);
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection(EmailOptions.SectionName));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<EmailOptions>>().Value);
builder.Services.Configure<NotificationOptions>(builder.Configuration.GetSection(NotificationOptions.SectionName));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<NotificationOptions>>().Value);
builder.Services.Configure<RetentionOptions>(builder.Configuration.GetSection(RetentionOptions.SectionName));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<RetentionOptions>>().Value);

var storage = builder.Configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new StorageOptions();
StorageOptions.Validate(storage);
builder.Services.AddSingleton(storage);

builder.Services.AddSharedKernel();
builder.Services.AddScoped<IOutboxDispatcherStore, OutboxDispatcherStore>();
builder.Services.AddScoped<IIncidentStore, IncidentRepository>();
builder.Services.AddScoped<IConfigStore, ConfigStore>();
builder.Services.AddScoped<INotificationStore, NotificationStore>();
if (string.Equals(storage.Backend, "S3", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddScoped<IEvidenceStore, S3EvidenceStore>();
else
    builder.Services.AddScoped<IEvidenceStore, FileEvidenceStore>();
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
builder.Services.AddHostedService<OutboxDispatcher>();
builder.Services.AddHostedService<EscalationDispatcher>();
builder.Services.AddHostedService<RetentionJob>();

var host = builder.Build();

// Fail fast before dispatching anything: empty recipients or an ambiguous
// email transport must crash startup, never drain the outbox silently.
WorkerStartupValidator.Validate(
    host.Services.GetRequiredService<NotificationOptions>(),
    host.Services.GetRequiredService<EmailOptions>());
host.Run();
