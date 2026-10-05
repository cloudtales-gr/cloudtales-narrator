using CloudTales.Narrator.Core;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CloudTales.Narrator.Functions;

/// <summary>
/// Daily, seconds-long check: finds posts whose audio is missing or stale and queues one message per post.
/// The actual synthesis happens in <see cref="NarrateArticle"/>, one short execution per message.
/// The daily cap is the cost guardrail: at most N syntheses are ever requested per day.
/// </summary>
public sealed class EnqueueChangedArticles(
    WordPressClient wordPress,
    NarrationPipeline pipeline,
    IConfiguration config,
    ILogger<EnqueueChangedArticles> logger)
{
    public const string QueueName = "narration-requests";
    private const int DefaultMaxPerDay = 3;

    [Function(nameof(EnqueueChangedArticles))]
    [QueueOutput(QueueName)]
    public async Task<string[]> RunAsync([TimerTrigger("0 0 4 * * *")] TimerInfo timer, CancellationToken ct)
    {
        var max = int.TryParse(config["Narrator:MaxSynthesesPerRun"], out var m) ? m : DefaultMaxPerDay;
        var queued = new List<string>();
        var checkedPosts = 0;

        await foreach (var article in wordPress.GetAllAsync(ct))
        {
            checkedPosts++;
            if (!await pipeline.NeedsSynthesisAsync(article, ct)) continue;

            queued.Add(article.Slug);
            logger.LogInformation("Queued {Slug} for narration", article.Slug);

            if (queued.Count >= max)
            {
                logger.LogInformation("Daily cap of {Max} reached; remaining posts are queued on the next run", max);
                break;
            }
        }

        logger.LogInformation("Checked {Checked} posts, queued {Queued}", checkedPosts, queued.Count);
        return [.. queued];
    }
}
