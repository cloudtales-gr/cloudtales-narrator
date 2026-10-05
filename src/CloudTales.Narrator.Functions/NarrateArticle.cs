using CloudTales.Narrator.Core;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace CloudTales.Narrator.Functions;

/// <summary>
/// One message = one post = one short, independent execution. Exceptions are not caught on purpose:
/// the platform retries the message (maxDequeueCount in host.json) and then moves it to the poison queue.
/// Re-processing is safe: the SSML hash check skips posts that already have current audio.
/// </summary>
public sealed class NarrateArticle(
    WordPressClient wordPress,
    NarrationPipeline pipeline,
    ILogger<NarrateArticle> logger)
{
    [Function(nameof(NarrateArticle))]
    public async Task RunAsync([QueueTrigger(EnqueueChangedArticles.QueueName)] string slug, CancellationToken ct)
    {
        var article = await wordPress.GetBySlugAsync(slug, ct);
        if (article is null)
        {
            logger.LogWarning("Post {Slug} no longer exists; nothing to narrate", slug);
            return;
        }

        logger.LogInformation("Narrating {Slug}", slug);
        var result = await pipeline.ProcessAsync(article, ct: ct);

        logger.LogInformation("{Outcome} {Slug}: {Duration}, {Chunks} chunk(s)",
            result.Outcome, slug, result.Duration, result.Chunks);
    }
}
