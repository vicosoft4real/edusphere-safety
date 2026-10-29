using System.Net.Mail;

namespace EduSphere.Safety.Worker;

public interface IEmailSender
{
    Task SendAsync(string to, string subject, string body, CancellationToken ct);
}

public sealed class SmtpEmailSender(EmailOptions options, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    private readonly EmailOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly ILogger<SmtpEmailSender> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(to);
        using var message = new MailMessage(_options.From, to, subject, body);
        using var client = CreateClient();
        _logger.LogInformation("Sending email to {To} subject {Subject}", to, subject);
        await client.SendMailAsync(message, ct);
    }

    private SmtpClient CreateClient()
    {
        if (!string.IsNullOrWhiteSpace(_options.PickupDirectory))
        {
            Directory.CreateDirectory(_options.PickupDirectory);
            return new SmtpClient
            {
                DeliveryMethod = SmtpDeliveryMethod.SpecifiedPickupDirectory,
                PickupDirectoryLocation = _options.PickupDirectory,
            };
        }

        if (string.IsNullOrWhiteSpace(_options.Host))
            throw new InvalidOperationException("Email is not configured: set Email:PickupDirectory or Email:Host.");
        return new SmtpClient(_options.Host, _options.Port);
    }
}
