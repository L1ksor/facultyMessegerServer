using System.Net;
using FacultyMessenger.Api.Configuration;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace FacultyMessenger.Api.Services.Email;

public interface IEmailService
{
    Task SendTemporaryPasswordAsync(string toEmail, string fullName, string temporaryPassword, CancellationToken ct = default);
}

public class MailKitEmailService : IEmailService
{
    private readonly SmtpOptions _options;
    private readonly ILogger<MailKitEmailService> _logger;

    public MailKitEmailService(IOptions<SmtpOptions> options, ILogger<MailKitEmailService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task SendTemporaryPasswordAsync(string toEmail, string fullName, string temporaryPassword, CancellationToken ct = default)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_options.FromName, _options.FromAddress));
        message.To.Add(new MailboxAddress(fullName, toEmail));
        message.Subject = "Доступ к академическому мессенджеру факультета";

        var encodedName = WebUtility.HtmlEncode(fullName);
        var encodedEmail = WebUtility.HtmlEncode(toEmail);
        var encodedPassword = WebUtility.HtmlEncode(temporaryPassword);

        var bodyBuilder = new BodyBuilder
        {
            TextBody = $"""
                Здравствуйте, {fullName}!

                Для вас создана учётная запись в академическом мессенджере факультета.

                Логин (Email): {toEmail}
                Временный пароль: {temporaryPassword}

                При первом входе система потребует сменить временный пароль на постоянный.
                """,
            HtmlBody = $"""
                <p>Здравствуйте, <strong>{encodedName}</strong>!</p>
                <p>Для вас создана учётная запись в академическом мессенджере факультета.</p>
                <ul>
                  <li><strong>Логин (Email):</strong> {encodedEmail}</li>
                  <li><strong>Временный пароль:</strong> <code>{encodedPassword}</code></li>
                </ul>
                <p>При первом входе система потребует сменить временный пароль на постоянный.</p>
                """
        };

        message.Body = bodyBuilder.ToMessageBody();

        using var client = new SmtpClient { Timeout = 10_000 };
        var socketOptions = _options.UseStartTls
            ? SecureSocketOptions.StartTlsWhenAvailable
            : SecureSocketOptions.Auto;

        await client.ConnectAsync(_options.Host, _options.Port, socketOptions, ct);

        if (!string.IsNullOrWhiteSpace(_options.UserName) && !string.IsNullOrWhiteSpace(_options.Password))
        {
            await client.AuthenticateAsync(_options.UserName, _options.Password, ct);
        }

        await client.SendAsync(new SendRequest(message), ct);
        await client.DisconnectAsync(true, ct);

        _logger.LogInformation("Письмо с временным паролем отправлено на {Email}", toEmail);
    }
}
