using System.Net;
using System.Text.RegularExpressions;
using CloudTales.Narrator.Core;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;

namespace CloudTales.Narrator.Functions;

/// <summary>
/// GET /api/audio/{slug}: the only way to play an MP3. The container is private; this endpoint
/// checks that the request comes from a page on an allowed site and redirects to a 2-hour, read-only SAS link.
/// Blocks hotlinking and direct access; it is not DRM (a listener can always save what they hear).
/// </summary>
public sealed partial class GetAudio(AudioStore store, IConfiguration config)
{
    // Long enough to finish a long article with pauses; short enough that a copied link soon dies
    private static readonly TimeSpan LinkLifetime = TimeSpan.FromHours(2);

    [Function(nameof(GetAudio))]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "head", Route = "audio/{slug}")] HttpRequestData req,
        string slug,
        CancellationToken ct)
    {
        if (!IsFromAllowedSite(req)) return req.CreateResponse(HttpStatusCode.Forbidden);
        if (!SlugPattern().IsMatch(slug)) return req.CreateResponse(HttpStatusCode.BadRequest);

        var link = await store.GetReadUriAsync(slug, LinkLifetime, ct);
        if (link is null) return req.CreateResponse(HttpStatusCode.NotFound);

        var response = req.CreateResponse(HttpStatusCode.Redirect);
        response.Headers.Add("Location", link.ToString());
        response.Headers.Add("Cache-Control", "no-store");
        return response;
    }

    /// <summary>Browsers send Referer (or Origin) with the blog's host when an audio tag on its pages loads.</summary>
    private bool IsFromAllowedSite(HttpRequestData req)
    {
        var allowedHosts = (config["Audio:AllowedHosts"] ?? string.Empty) // validated at startup in Program.cs
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var header in new[] { "Referer", "Origin" })
        {
            if (req.Headers.TryGetValues(header, out var values)
                && Uri.TryCreate(values.FirstOrDefault(), UriKind.Absolute, out var source)
                && allowedHosts.Contains(source.Host, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex("^[a-z0-9-]{1,200}$")]
    private static partial Regex SlugPattern();
}
