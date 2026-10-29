namespace EduSphere.Safety.Application;

public sealed record DeadLetterRow(
    Guid Id,
    string TenantId,
    string Type,
    string Payload,
    int Attempts,
    DateTimeOffset CreatedAt);
