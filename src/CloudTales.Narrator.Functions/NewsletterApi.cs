using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using CloudTales.Narrator.Core;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CloudTales.Narrator.Functions;

/// <summary>
/// The blog's newsletter, double opt-in:
///   POST /api/newsletter/subscribe    {email, website, elapsedMs}  → pending + confirmation email
///   POST /api/newsletter/confirm      {id, t}                       → active
///   POST /api/newsletter/unsubscribe  {id, t}                       → deleted (nothing is kept)
/// Confirm and unsubscribe are POSTs from the site's /newsletter/ page, not GET links: mail scanners open
/// links in emails, and a GET would confirm or unsubscribe on their behalf.
/// Same spam defences as the contact form. Disabled (404) until Newsletter__SiteUrl and a sender are set.
/// Addresses never go to the logs.
/// </summary>
public sealed class NewsletterApi(NewsletterStore store, GraphMailer mailer, IConfiguration config, ILogger<NewsletterApi> logger)
{
    private const int MaxBodyBytes = 4 * 1024;
    private const int MinFillMilliseconds = 2000;
    private const int MaxPerIpPerHour = 5;
    private const int MaxConfirmationsPerDay = 100;   // per instance: caps what a flood could make us send
    private static readonly TimeSpan ResendAfter = TimeSpan.FromMinutes(15);
    private static readonly ConcurrentDictionary<string, List<DateTimeOffset>> Recent = new();
    private static readonly List<DateTimeOffset> Confirmations = [];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Function("NewsletterSubscribe")]
    public async Task<HttpResponseData> SubscribeAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "newsletter/subscribe")] HttpRequestData req, CancellationToken ct)
    {
        if (Settings() is not { } s) return req.CreateResponse(HttpStatusCode.NotFound);
        if (!OriginAllowed(req)) return await ReplyAsync(req, HttpStatusCode.Forbidden, "This form only works on the blog.", ct);

        var form = await ReadAsync<SubscribeForm>(req, ct);
        if (form is null) return await ReplyAsync(req, HttpStatusCode.BadRequest, "The form could not be read.", ct);

        // Bots fill hidden fields and submit instantly: accept politely, send nothing
        if (!string.IsNullOrEmpty(form.Website) || form.ElapsedMs < MinFillMilliseconds)
        {
            logger.LogInformation("Newsletter: dropped a likely automated sign-up");
            return await ReplyAsync(req, HttpStatusCode.OK, null, ct);
        }

        var email = form.Email?.Trim() ?? "";
        if (!ContactValidator.IsEmail(email)) return await ReplyAsync(req, HttpStatusCode.BadRequest, "Please enter a valid email address.", ct);
        if (!Allow(ClientIp(req))) return await ReplyAsync(req, (HttpStatusCode)429, "Too many attempts. Please try again later.", ct);

        var now = DateTimeOffset.UtcNow;
        var existing = await store.GetAsync(NewsletterStore.IdFor(email), ct);

        // Same answer whether or not the address is already subscribed: the form must not reveal who is
        if (existing is { Status: NewsletterSubscriber.Active } || (existing is not null && now - existing.LastMailUtc < ResendAfter))
            return await ReplyAsync(req, HttpStatusCode.OK, null, ct);

        if (!AllowConfirmation(now))
        {
            logger.LogWarning("Newsletter: daily confirmation cap reached on this instance");
            return await ReplyAsync(req, (HttpStatusCode)429, "Too many sign-ups right now. Please try again tomorrow.", ct);
        }

        var subscriber = existing is null
            ? new NewsletterSubscriber(email, NewsletterSubscriber.Pending, NewsletterStore.NewToken(), now, null, now)
            : existing with { LastMailUtc = now };
        await store.SaveAsync(subscriber, ct);

        try
        {
            var (subject, html) = NewsletterMail.Confirmation(s.SiteName, s.SiteUrl, subscriber);
            await mailer.SendHtmlAsync(s.Sender, subscriber.Email, subject, html, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Newsletter: confirmation email failed");
            return await ReplyAsync(req, HttpStatusCode.BadGateway, "The confirmation email could not be sent. Please try again later.", ct);
        }

        logger.LogInformation("Newsletter: confirmation sent");
        return await ReplyAsync(req, HttpStatusCode.OK, null, ct);
    }

    [Function("NewsletterConfirm")]
    public async Task<HttpResponseData> ConfirmAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "newsletter/confirm")] HttpRequestData req, CancellationToken ct)
    {
        if (Settings() is null) return req.CreateResponse(HttpStatusCode.NotFound);
        if (!OriginAllowed(req)) return await ReplyAsync(req, HttpStatusCode.Forbidden, "This page only works on the blog.", ct);

        var form = await ReadAsync<TokenForm>(req, ct);
        var subscriber = form?.Id is { } id ? await store.GetAsync(id, ct) : null;
        if (subscriber is null || !NewsletterStore.TokenMatches(subscriber, form!.T))
            return await ReplyAsync(req, HttpStatusCode.BadRequest, "This link is no longer valid. Please subscribe again.", ct);

        if (subscriber.Status != NewsletterSubscriber.Active)
        {
            await store.SaveAsync(subscriber with { Status = NewsletterSubscriber.Active, ConfirmedUtc = DateTimeOffset.UtcNow }, ct);
            logger.LogInformation("Newsletter: subscription confirmed");
        }
        return await ReplyAsync(req, HttpStatusCode.OK, null, ct);
    }

    [Function("NewsletterUnsubscribe")]
    public async Task<HttpResponseData> UnsubscribeAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "newsletter/unsubscribe")] HttpRequestData req, CancellationToken ct)
    {
        if (Settings() is null) return req.CreateResponse(HttpStatusCode.NotFound);
        if (!OriginAllowed(req)) return await ReplyAsync(req, HttpStatusCode.Forbidden, "This page only works on the blog.", ct);

        var form = await ReadAsync<TokenForm>(req, ct);
        if (form?.Id is not { } id) return await ReplyAsync(req, HttpStatusCode.BadRequest, "This link is not complete.", ct);

        var subscriber = await store.GetAsync(id, ct);
        if (subscriber is null) return await ReplyAsync(req, HttpStatusCode.OK, null, ct);   // already gone
        if (!NewsletterStore.TokenMatches(subscriber, form.T))
            return await ReplyAsync(req, HttpStatusCode.BadRequest, "This link is not valid.", ct);

        await store.DeleteAsync(id, ct);
        logger.LogInformation("Newsletter: unsubscribed and deleted");
        return await ReplyAsync(req, HttpStatusCode.OK, null, ct);
    }

    internal sealed record NewsletterSettings(string SiteUrl, string SiteName, string Sender);

    private NewsletterSettings? Settings() =>
        (config["Newsletter:SiteUrl"], config["Newsletter:Sender"] ?? config["Contact:Sender"]) is ({ Length: > 0 } url, { Length: > 0 } sender)
            ? new(url, config["Narrator:SiteName"] ?? "CloudTales", sender)
            : null;

    private bool OriginAllowed(HttpRequestData req)
    {
        var origins = (config["Contact:AllowedOrigins"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var origin = req.Headers.TryGetValues("Origin", out var o) ? o.FirstOrDefault() : null;
        return origin is not null && origins.Contains(origin, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<T?> ReadAsync<T>(HttpRequestData req, CancellationToken ct) where T : class
    {
        try
        {
            using var reader = new StreamReader(req.Body);
            var buffer = new char[MaxBodyBytes + 1];
            var read = await reader.ReadBlockAsync(buffer, ct);
            return read > MaxBodyBytes ? null : JsonSerializer.Deserialize<T>(new string(buffer, 0, read), Json);
        }
        catch (JsonException)
        {
            return null;
        }
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
        return IPEndPoint.TryParse(first, out var endpoint) ? endpoint.Address.ToString() : first;
    }

    private static bool Allow(string ip)
    {
        var now = DateTimeOffset.UtcNow;
        if (Recent.Count > 10_000) Recent.Clear();
        var hits = Recent.GetOrAdd(ip, _ => []);
        lock (hits)
        {
            hits.RemoveAll(t => now - t > TimeSpan.FromHours(1));
            if (hits.Count >= MaxPerIpPerHour) return false;
            hits.Add(now);
            return true;
        }
    }

    private static bool AllowConfirmation(DateTimeOffset now)
    {
        lock (Confirmations)
        {
            Confirmations.RemoveAll(t => now - t > TimeSpan.FromDays(1));
            if (Confirmations.Count >= MaxConfirmationsPerDay) return false;
            Confirmations.Add(now);
            return true;
        }
    }

    private sealed record SubscribeForm(string? Email, string? Website, long ElapsedMs);
    private sealed record TokenForm(string? Id, string? T);
}
