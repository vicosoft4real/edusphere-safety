namespace EduSphere.Safety.Api;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    // Development-only stub token minting. Default false; production must use
    // a real provider via Authority (Issuer/Audience validation stays real).
    public bool DevStubEnabled { get; set; }

    public string Issuer { get; set; } = string.Empty;

    public string Audience { get; set; } = string.Empty;

    // Dev/local only: HS256 key for validating stub tokens.
    public string IssuerSigningKey { get; set; } = string.Empty;

    // Production: real OIDC provider (e.g. https://issuer.example.com).
    public string Authority { get; set; } = string.Empty;
}
