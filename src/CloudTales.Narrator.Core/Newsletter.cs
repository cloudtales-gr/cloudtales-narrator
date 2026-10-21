using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace CloudTales.Narrator.Core;

/// <summary>One newsletter subscriber. Only what the newsletter needs: the address and the dates of consent.</summary>
/// <param name="Token">Secret in the confirm and unsubscribe links (the id alone is not enough).</param>
public sealed record NewsletterSubscriber(
    string Email, string Status, string Token, DateTimeOffset CreatedUtc, DateTimeOffset? ConfirmedUtc, DateTimeOffset LastMailUtc)
{
    public const string Pending = "pending", Active = "active";

    public string Id => NewsletterStore.IdFor(Email);
}

/// <summary>Which posts were already mailed, and the progress of the one being mailed right now.</summary>
public sealed record NewsletterState(List<string> Sent, string? CurrentSlug, List<string> CurrentDone);

/// <summary>
/// Subscribers and send state in the "newsletter" container of the audio storage account:
/// <c>subscribers/{id}.json</c> (id = SHA-256 of the lower-cased address) and <c>state.json</c>.
/// </summary>
public sealed class NewsletterStore(BlobContainerClient container)
{
    private const string SubscriberPrefix = "subscribers/";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private bool _containerReady;

    public static string IdFor(string email) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant())))[..32];

    public static string NewToken() => RandomNumberGenerator.GetHexString(40, lowercase: true);

    /// <summary>Constant-time token check.</summary>
    public static bool TokenMatches(NewsletterSubscriber s, string? token) =>
        token is { Length: > 0 }
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(s.Token), Encoding.UTF8.GetBytes(token));

    public async Task<NewsletterSubscriber?> GetAsync(string id, CancellationToken ct = default) =>
        IsId(id) ? await ReadAsync<NewsletterSubscriber>($"{SubscriberPrefix}{id}.json", ct) : null;

    public Task SaveAsync(NewsletterSubscriber s, CancellationToken ct = default) =>
        WriteAsync($"{SubscriberPrefix}{s.Id}.json", s, ct);

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        if (!IsId(id)) return;
        await EnsureContainerAsync(ct);
        await container.DeleteBlobIfExistsAsync($"{SubscriberPrefix}{id}.json", cancellationToken: ct);
    }

    public async IAsyncEnumerable<NewsletterSubscriber> ListAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await EnsureContainerAsync(ct);
        await foreach (var item in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, SubscriberPrefix, ct))
        {
            if (await ReadAsync<NewsletterSubscriber>(item.Name, ct) is { } s) yield return s;
        }
    }

    public Task<NewsletterState?> GetStateAsync(CancellationToken ct = default) => ReadAsync<NewsletterState>("state.json", ct);

    public Task SaveStateAsync(NewsletterState state, CancellationToken ct = default) => WriteAsync("state.json", state, ct);

    private static bool IsId(string? id) => id is { Length: 32 } && id.All(char.IsAsciiHexDigitLower);

    private async Task<T?> ReadAsync<T>(string name, CancellationToken ct) where T : class
    {
        await EnsureContainerAsync(ct);
        try
        {
            var result = await container.GetBlobClient(name).DownloadContentAsync(ct);
            return result.Value.Content.ToObjectFromJson<T>(Json);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    private async Task WriteAsync<T>(string name, T value, CancellationToken ct)
    {
        await EnsureContainerAsync(ct);
        await container.GetBlobClient(name).UploadAsync(BinaryData.FromObjectAsJson(value, Json), new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" }
        }, ct);
    }

    private async Task EnsureContainerAsync(CancellationToken ct)
    {
        if (_containerReady) return;
        await container.CreateIfNotExistsAsync(cancellationToken: ct);
        _containerReady = true;
    }
}

/// <summary>The two kinds of newsletter email, as HTML. Pure: no I/O.</summary>
public static class NewsletterMail
{
    private const string Accent = "#C4262C";

    public static string ConfirmLink(string siteUrl, NewsletterSubscriber s) =>
        $"{siteUrl.TrimEnd('/')}/newsletter/?action=confirm&id={s.Id}&t={s.Token}";

    public static string UnsubscribeLink(string siteUrl, NewsletterSubscriber s) =>
        $"{siteUrl.TrimEnd('/')}/newsletter/?action=unsubscribe&id={s.Id}&t={s.Token}";

    public static (string Subject, string Html) Confirmation(string siteName, string siteUrl, NewsletterSubscriber s)
    {
        var link = ConfirmLink(siteUrl, s);
        var body =
            P($"Someone, hopefully you, asked to receive new {E(siteName)} articles by email at <strong>{E(s.Email)}</strong>.") +
            Button("Confirm subscription", link) +
            P("If it wasn't you, ignore this email: without confirmation nothing will be sent, and the address is deleted after 7 days.", muted: true);
        return ($"Confirm your {siteName} subscription", Layout(siteName, siteUrl, "One click to confirm", body, footer: null));
    }

    public static (string Subject, string Html) NewPost(string siteName, string siteUrl, PodcastEpisode post, NewsletterSubscriber s)
    {
        var url = $"{post.Url}{(post.Url.Contains('?') ? '&' : '?')}utm_source=newsletter&utm_medium=email";
        var body =
            (post.Summary.Length > 0 ? P(E(Trim(post.Summary, 420))) : "") +
            Button("Read the article", url) +
            P("Prefer to listen? Every article has an audio version on the page.", muted: true);
        var footer =
            $"You get this because you subscribed to {E(siteName)} at {E(s.Email)}. " +
            $"<a href=\"{E(UnsubscribeLink(siteUrl, s))}\" style=\"color:#6b6b6b;\">Unsubscribe</a> with one click.";
        return (post.Title, Layout(siteName, siteUrl, post.Title, body, footer));
    }

    private static string Layout(string siteName, string siteUrl, string heading, string body, string? footer) =>
        "<!doctype html><html><body style=\"margin:0;padding:0;background:#f4f4f2;\">" +
        "<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background:#f4f4f2;\"><tr><td align=\"center\" style=\"padding:24px 12px;\">" +
        "<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"max-width:560px;background:#ffffff;border-top:3px solid " + Accent + ";\">" +
        $"<tr><td style=\"padding:24px 28px 0;font:600 13px/1.4 Segoe UI,Helvetica,Arial,sans-serif;letter-spacing:.06em;text-transform:uppercase;\"><a href=\"{E(siteUrl)}\" style=\"color:{Accent};text-decoration:none;\">{E(siteName)}</a></td></tr>" +
        $"<tr><td style=\"padding:8px 28px 0;font:700 22px/1.3 Segoe UI,Helvetica,Arial,sans-serif;color:#1a1a1a;\">{E(heading)}</td></tr>" +
        $"<tr><td style=\"padding:8px 28px 24px;\">{body}</td></tr>" +
        (footer is null ? "" : $"<tr><td style=\"padding:16px 28px 24px;border-top:1px solid #eeeeec;font:12px/1.5 Segoe UI,Helvetica,Arial,sans-serif;color:#6b6b6b;\">{footer}</td></tr>") +
        "</table></td></tr></table></body></html>";

    private static string P(string html, bool muted = false) =>
        $"<p style=\"margin:12px 0;font:{(muted ? "14px/1.55" : "16px/1.6")} Segoe UI,Helvetica,Arial,sans-serif;color:{(muted ? "#6b6b6b" : "#333333")};\">{html}</p>";

    private static string Button(string text, string url) =>
        $"<p style=\"margin:20px 0;\"><a href=\"{E(url)}\" style=\"display:inline-block;padding:12px 22px;background:{Accent};color:#ffffff;" +
        $"font:600 15px/1 Segoe UI,Helvetica,Arial,sans-serif;text-decoration:none;border-radius:6px;\">{E(text)}</a></p>";

    private static string E(string s) => WebUtility.HtmlEncode(s);

    private static string Trim(string s, int max)
    {
        s = WebUtility.HtmlDecode(s).Trim();
        if (s.Length <= max) return s;
        var cut = s.LastIndexOf(' ', max);
        return s[..(cut > 0 ? cut : max)] + "…";
    }
}
