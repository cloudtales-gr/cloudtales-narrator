using System.Net;
using System.Text.RegularExpressions;
using CloudTales.Narrator.Core;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CloudTales.Narrator.Functions;

/// <summary>
/// GET /api/podcast/{slug}.mp3: episode audio for podcast apps. Unlike <see cref="GetAudio"/> there is no
/// Referer check (podcast apps and Spotify's ingestion send none); everything else is the same: a short-lived,
/// read-only user delegation SAS link to the private blob.
/// </summary>
public sealed partial class GetPodcastAudio(AudioStore store)
{
    // Apps download whole episodes, sometimes slowly on mobile networks
    private static readonly TimeSpan LinkLifetime = TimeSpan.FromHours(6);

    [Function(nameof(GetPodcastAudio))]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "head", Route = "podcast/{slug}.mp3")] HttpRequestData req,
        string slug,
        CancellationToken ct)
    {
        if (!SlugPattern().IsMatch(slug)) return req.CreateResponse(HttpStatusCode.BadRequest);

        var link = await store.GetReadUriAsync(slug, LinkLifetime, ct);
        if (link is null) return req.CreateResponse(HttpStatusCode.NotFound);

        var response = req.CreateResponse(HttpStatusCode.Redirect);
        response.Headers.Add("Location", link.ToString());
        response.Headers.Add("Cache-Control", "no-store");
        return response;
    }

    [GeneratedRegex("^[a-z0-9-]{1,200}$")]
    private static partial Regex SlugPattern();
}
