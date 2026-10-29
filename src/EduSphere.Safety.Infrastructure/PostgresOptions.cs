namespace EduSphere.Safety.Infrastructure;

// Connection string comes from configuration, never hardcoded here.
public sealed class PostgresOptions
{
    public string ConnectionString { get; set; } = string.Empty;
}
