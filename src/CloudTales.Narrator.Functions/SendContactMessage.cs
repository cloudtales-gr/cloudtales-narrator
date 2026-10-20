using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using CloudTales.Narrator.Core;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CloudTales.Narrator.Functions;

/// <summary>
/// POST /api/contact: the blog's contact form. Emails the message through Microsoft Graph as the
/// Contact__Sender mailbox (managed identity, no secret) to Contact__Recipient, with Reply-To set to the sender.
/// Spam defences without third parties: Origin allow list, honeypot field, minimum fill time, per-IP rate limit.
/// Disabled (404) until the three Contact__* settings exist.
/// </summary>
public sealed class SendContactMessage(IServiceProvider services, IConfiguration config, ILogger<SendContactMessage> logger)
{
    private const int MaxBodyBytes = 32 * 1024;
    private const int MinFillMilliseconds = 3000;
    private const int MaxPerWindow = 3;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private static readonly ConcurrentDictionary<string, List<DateTimeOffset>> Recent = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Function(nameof(SendContactMessage))]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "contact")] HttpRequestData req,
        CancellationToken ct)
    {
        var sender = config["Contact:Sender"];
        var recipient = config["Contact:Recipient"];
        var origins = (config["Contact:AllowedOrigins"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (string.IsNullOrEmpty(sender) || string.IsNullOrEmpty(recipient) || origins.Length == 0)
            return req.CreateResponse(HttpStatusCode.NotFound);

        // Browsers always send Origin on a cross-site POST; anything else is not our form
        var origin = req.Headers.TryGetValues("Origin", out var o) ? o.FirstOrDefault() : null;
        if (origin is null || !origins.Contains(origin, StringComparer.OrdinalIgnoreCase))
            return await ReplyAsync(req, HttpStatusCode.Forbidden, "This form only works on cloudtales.gr.", ct);

        ContactForm? form;
        try
        {
            using var reader = new StreamReader(req.Body);
            var buffer = new char[MaxBodyBytes + 1];
            var read = await reader.ReadBlockAsync(buffer, ct);
            if (read > MaxBodyBytes) return await ReplyAsync(req, HttpStatusCode.RequestEntityTooLarge, "The message is too long.", ct);
            form = JsonSerializer.Deserialize<ContactForm>(new string(buffer, 0, read), Json);
        }
        catch (JsonException)
        {
            form = null;
        }
        if (form is null) return await ReplyAsync(req, HttpStatusCode.BadRequest, "The form could not be read.", ct);

        // Bots fill hidden fields and submit instantly: accept politely, send nothing
        if (!string.IsNullOrEmpty(form.Website) || form.ElapsedMs < MinFillMilliseconds)
        {
            logger.LogInformation("Contact form: dropped a likely automated submission");
            return await ReplyAsync(req, HttpStatusCode.OK, null, ct);
        }

        var message = new ContactMessage(form.FirstName ?? "", form.LastName ?? "", form.Email ?? "", form.Subject, form.Message ?? "");
        if (ContactValidator.Validate(message) is { } error) return await ReplyAsync(req, HttpStatusCode.BadRequest, error, ct);

        if (!Allow(ClientIp(req)))
            return await ReplyAsync(req, (HttpStatusCode)429, "Too many messages in a short time. Please try again later.", ct);

        if (services.GetService<GraphMailer>() is not { } mailer) return req.CreateResponse(HttpStatusCode.NotFound);
        try
        {
            await mailer.SendAsync(sender, recipient, message, ContactValidator.OneLine(form.Page, 300), ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Contact form: sending failed");
            return await ReplyAsync(req, HttpStatusCode.BadGateway, "Your message could not be sent. Please try again later.", ct);
        }

        // The content is personal data: it goes to the mailbox, never to the logs
        logger.LogInformation("Contact form: message sent");
        return await ReplyAsync(req, HttpStatusCode.OK, null, ct);
    }

    private static async Task<HttpResponseData> ReplyAsync(HttpRequestData req, HttpStatusCode status, string? error, CancellationToken ct)
    {
        var response = req.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        response.Headers.Add("Cache-Control", "no-store");
        await response.Body.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(new { ok = error is null, error }, Json), ct);
        return response;
    }

    private static string ClientIp(HttpRequestData req)
    {
        var forwarded = req.Headers.TryGetValues("X-Forwarded-For", out var v) ? v.FirstOrDefault() : null;
        var first = forwarded?.Split(',')[0].Trim() ?? "unknown";
        // "1.2.3.4:5678" and "[2001:db8::1]:443" carry a port; keep only the address
        return IPEndPoint.TryParse(first, out var endpoint) ? endpoint.Address.ToString() : first;
    }

    /// <summary>Per-instance sliding window; enough for a blog, no shared state needed.</summary>
    private static bool Allow(string ip)
    {
        var now = DateTimeOffset.UtcNow;
        if (Recent.Count > 10_000) Recent.Clear();   // bounded memory, even under a flood
        var hits = Recent.GetOrAdd(ip, _ => []);
        lock (hits)
        {
            hits.RemoveAll(t => now - t > Window);
            if (hits.Count >= MaxPerWindow) return false;
            hits.Add(now);
            return true;
        }
    }

    private sealed record ContactForm(
        string? FirstName, string? LastName, string? Email, string? Subject, string? Message,
        string? Website, string? Page, long ElapsedMs);
}
