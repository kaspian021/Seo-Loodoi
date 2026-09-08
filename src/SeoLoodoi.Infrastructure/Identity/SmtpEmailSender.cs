using System.Net;
using System.Net.Mail;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SeoLoodoi.Application.Monitoring;
using SeoLoodoi.Infrastructure.Persistence;

namespace SeoLoodoi.Infrastructure.Identity;

public sealed class EmailOptions
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "SEO Loodoi";
}

/// <summary>Identity's confirmation/reset flows use this adapter; development can stay email-free without pretending a message was delivered.</summary>
public sealed class SmtpEmailSender(IOptions<EmailOptions> options, IConfiguration configuration, ILogger<SmtpEmailSender> logger) : IEmailSender<ApplicationUser>, IEmailDelivery
{
    private readonly EmailOptions _options = options.Value;
    private readonly string _webBaseUrl = (configuration["Application:WebBaseUrl"] ?? configuration["AllowedOrigins:0"] ?? "http://localhost:5173").TrimEnd('/');

    public Task SendConfirmationLinkAsync(ApplicationUser user, string email, string confirmationLink) => SendEmailAsync(email, "تأیید ایمیل SEO Loodoi", $"<p>برای تأیید ایمیل حساب خود روی این لینک کلیک کنید:</p><p><a href=\"{WebUtility.HtmlEncode(confirmationLink)}\">تأیید ایمیل</a></p>");
    public Task SendPasswordResetLinkAsync(ApplicationUser user, string email, string resetLink) => SendEmailAsync(email, "بازیابی رمز عبور SEO Loodoi", $"<p>برای انتخاب رمز جدید روی این لینک کلیک کنید:</p><p><a href=\"{WebUtility.HtmlEncode(resetLink)}\">بازیابی رمز</a></p>");
    public Task SendPasswordResetCodeAsync(ApplicationUser user, string email, string resetCode)
    {
        var resetLink = $"{_webBaseUrl}/?email={Uri.EscapeDataString(email)}&resetCode={Uri.EscapeDataString(resetCode)}";
        return SendEmailAsync(email, "بازیابی رمز عبور SEO Loodoi", $"<p>برای انتخاب رمز جدید روی این لینک کلیک کنید:</p><p><a href=\"{WebUtility.HtmlEncode(resetLink)}\">بازیابی رمز عبور</a></p>");
    }
    public Task SendAsync(string email, string subject, string htmlMessage, CancellationToken ct) => SendEmailAsync(email, subject, htmlMessage, ct, requireEnabled: true);

    public Task SendEmailAsync(string email, string subject, string htmlMessage) => SendEmailAsync(email, subject, htmlMessage, CancellationToken.None);

    private async Task SendEmailAsync(string email, string subject, string htmlMessage, CancellationToken ct, bool requireEnabled = false)
    {
        if (!_options.Enabled)
        {
            if (requireEnabled) throw new InvalidOperationException("Email delivery is disabled.");
            logger.LogInformation("Email delivery is disabled; message for {Email} was not sent.", email); return;
        }
        if (string.IsNullOrWhiteSpace(_options.Host) || string.IsNullOrWhiteSpace(_options.FromAddress)) throw new InvalidOperationException("Email delivery is enabled but SMTP is not configured.");
        using var message = new MailMessage { From = new MailAddress(_options.FromAddress, _options.FromName), Subject = subject, Body = htmlMessage, IsBodyHtml = true };
        message.To.Add(new MailAddress(email));
        using var smtp = new SmtpClient(_options.Host, _options.Port) { EnableSsl = _options.EnableSsl };
        if (!string.IsNullOrWhiteSpace(_options.UserName)) smtp.Credentials = new NetworkCredential(_options.UserName, _options.Password);
        await smtp.SendMailAsync(message, ct);
    }
}
