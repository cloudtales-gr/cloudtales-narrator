using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Text.Json.Nodes;
using Azure.Core;

namespace CloudTales.Narrator.Core;

/// <summary>A message from the site's contact form.</summary>
public sealed record ContactMessage(string FirstName, string LastName, string Email, string? Subject, string Message);

/// <summary>Server-side checks for contact messages (the browser checks are only a convenience).</summary>
public static class ContactValidator
{
    public const int MaxName = 100, MaxSubject = 200, MinMessage = 10, MaxMessage = 5000;

    /// <summary>Returns a reader-facing error, or null when the message is acceptable.</summary>
    public static string? Validate(ContactMessage m)
    {
        if (string.IsNullOrWhiteSpace(m.FirstName) || string.IsNullOrWhiteSpace(m.LastName)) return "Please enter your first and last name.";
        if (m.FirstName.Length > MaxName || m.LastName.Length > MaxName) return "Your name is too long.";
        if (!IsEmail(m.Email)) return "Please enter a valid email address.";
        if ((m.Subject?.Length ?? 0) > MaxSubject) return "The subject is too long.";
        var length = m.Message?.Trim().Length ?? 0;
        if (length < MinMessage) return "Please write a slightly longer message.";
        if (length > MaxMessage) return $"Please keep the message under {MaxMessage} characters.";
        return null;
    }

    /// <summary>A single, plausible email address (no display name, a dot in the domain).</summary>
    public static bool IsEmail(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 254
        && MailAddress.TryCreate(value.Trim(), out var a) && a.Address == value.Trim() && a.Host.Contains('.');

    /// <summary>One line, no control characters: safe for a mail subject.</summary>
    public static string OneLine(string? value, int max) =>
        new string((value ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim() is var s && s.Length > max ? s[..max] : s;
}

/// <summary>
/// Sends mail through Microsoft Graph as one mailbox, with the app's managed identity (no secret).
/// Exchange Online RBAC for Applications limits the identity to that single mailbox.
/// </summary>
public sealed class GraphMailer(HttpClient http, TokenCredential credential)
{
    private static readonly TokenRequestContext Scope = new(["https://graph.microsoft.com/.default"]);

    public async Task SendAsync(string sender, string recipient, ContactMessage m, string? page, CancellationToken ct = default)
    {
        var name = ContactValidator.OneLine($"{m.FirstName} {m.LastName}", 200);
        var subject = ContactValidator.OneLine(m.Subject, ContactValidator.MaxSubject);
        var body =
            $"From: {name} <{m.Email.Trim()}>\n" +
            (string.IsNullOrEmpty(page) ? "" : $"Page: {page}\n") +
            $"Subject: {(subject.Length > 0 ? subject : "(none)")}\n\n" +
            m.Message.Trim() +
            "\n\n--\nSent from the contact form on cloudtales.gr. Reply to this email to answer the sender.";

        await SendTextAsync(sender, recipient, $"[CloudTales] {(subject.Length > 0 ? subject : "Contact form")}", body,
            replyTo: (m.Email.Trim(), name), ct);
    }

    /// <summary>Plain-text mail from <paramref name="sender"/> to <paramref name="recipient"/>, optionally with Reply-To.</summary>
    public Task SendTextAsync(string sender, string recipient, string subject, string body,
        (string Address, string Name)? replyTo = null, CancellationToken ct = default) =>
        SendMailAsync(sender, recipient, subject, body, "Text", replyTo, ct);

    /// <summary>HTML mail from <paramref name="sender"/> to <paramref name="recipient"/>.</summary>
    public Task SendHtmlAsync(string sender, string recipient, string subject, string html, CancellationToken ct = default) =>
        SendMailAsync(sender, recipient, subject, html, "HTML", null, ct);

    private async Task SendMailAsync(string sender, string recipient, string subject, string body, string contentType,
        (string Address, string Name)? replyTo, CancellationToken ct)
    {
        var message = new JsonObject
        {
            ["subject"] = subject,
            ["body"] = new JsonObject { ["contentType"] = contentType, ["content"] = body },
            ["toRecipients"] = new JsonArray(new JsonObject { ["emailAddress"] = new JsonObject { ["address"] = recipient } })
        };
        if (replyTo is { } r)
            message["replyTo"] = new JsonArray(new JsonObject
            {
                ["emailAddress"] = new JsonObject { ["address"] = r.Address, ["name"] = r.Name }
            });
        var payload = new JsonObject { ["message"] = message, ["saveToSentItems"] = false };

        var token = await credential.GetTokenAsync(Scope, ct);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://graph.microsoft.com/v1.0/users/{sender}/sendMail")
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Graph sendMail returned {(int)response.StatusCode}: {detail}");
        }
    }
}
