using System.Net;
using CloudTales.Narrator.Core;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CloudTales.Narrator.Functions;

/// <summary>
/// GET /api/podcast.xml: the podcast RSS feed (Apple Podcasts, Spotify, any podcast app).
/// Episodes are the posts that already have narration; show details come from the site's feed.
/// The site serves https://example.com/podcast.xml as a redirect here, so the public feed URL stays on the blog.
/// </summary>
public sealed class GetPodcastFeed(IServiceProvider services, AudioStore store, ILogger<GetPodcastFeed> logger)
{
    // Podcast apps poll every few minutes to hours; one build per quarter hour is plenty
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(15);
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static (byte[] Xml, DateTimeOffset Built, string Host)? _cache;

    [Function(nameof(GetPodcastFeed))]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "head", Route = "podcast.xml")] HttpRequestData req,
        CancellationToken ct)
    {
        // Only a static site feed carries podcast details
        if (services.GetService<HugoFeedClient>() is not { } site) return req.CreateResponse(HttpStatusCode.NotFound);

        var host = req.Url.GetLeftPart(UriPartial.Authority);
        var xml = await GetXmlAsync(site, host, ct);
        if (xml is null) return req.CreateResponse(HttpStatusCode.NotFound);

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/rss+xml; charset=utf-8");
        response.Headers.Add("Cache-Control", "public, max-age=900");
        if (!req.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)) await response.Body.WriteAsync(xml, ct);
        return response;
    }

    private async Task<byte[]?> GetXmlAsync(HugoFeedClient site, string host, CancellationToken ct)
    {
        if (_cache is { } c && c.Host == host && DateTimeOffset.UtcNow - c.Built < CacheFor) return c.Xml;

        await Gate.WaitAsync(ct);
        try
        {
            if (_cache is { } again && again.Host == host && DateTimeOffset.UtcNow - again.Built < CacheFor) return again.Xml;

            var (show, episodes) = await site.GetPodcastAsync(ct);
            if (show is null)
            {
                logger.LogWarning("The site feed has no podcast section; /api/podcast.xml is disabled");
                return null;
            }

            // A handful of HEAD-sized metadata reads, a few at a time
            var withAudio = new List<(PodcastEpisode, AudioInfo)>();
            foreach (var batch in episodes.Chunk(8))
            {
                var infos = await Task.WhenAll(batch.Select(e => store.GetInfoAsync(e.Slug, ct)));
                withAudio.AddRange(batch.Zip(infos).Where(x => x.Second is not null).Select(x => (x.First, x.Second!)));
            }

            var xml = PodcastFeedBuilder.Build(show, withAudio, slug => $"{host}/api/podcast/{slug}.mp3", DateTimeOffset.UtcNow);
            _cache = (xml, DateTimeOffset.UtcNow, host);
            logger.LogInformation("Podcast feed built: {Episodes} episodes of {Posts} posts", withAudio.Count, episodes.Count);
            return xml;
        }
        finally
        {
            Gate.Release();
        }
    }
}
