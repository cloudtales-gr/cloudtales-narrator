using CloudTales.Narrator.Core;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CloudTales.Narrator.Functions;

/// <summary>
/// Daily, seconds-long check: finds posts whose audio is missing or stale and queues one message per post.
/// The actual synthesis happens in <see cref="NarrateArticle"/>, one short execution per message.
/// The daily cap is the cost guardrail: at most N syntheses are ever requested per day.
/// With Narrator__AdoptExistingAudio=true, posts that already have audio are re-tagged with their new
/// SSML hash instead of being queued: a one-off setting for switching the article source without paying
/// to re-synthesize every post. Turn it off again after one run.
/// When the monthly Speech budget is used up, nothing is queued until the next month.
/// </summary>
public sealed class EnqueueChangedArticles(
    IArticleSource articles,
    NarrationPipeline pipeline,
    UsageMeter meter,
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
        var adopt = bool.TryParse(config["Narrator:AdoptExistingAudio"], out var a) && a;
        var usage = await meter.GetAsync(ct);
        if (meter.Budget > 0)
        {
            logger.LogInformation("Speech budget {Month}: {Used}/{Budget} characters used", usage.Month, usage.Characters, meter.Budget);
            if (usage.Characters >= meter.Budget && !adopt)
            {
                logger.LogWarning("Monthly Speech budget used up; nothing queued until next month");
                return [];
            }
        }

        var queued = new List<string>();
        var checkedPosts = 0;
        var adopted = 0;

        await foreach (var article in articles.GetAllAsync(ct))
        {
            checkedPosts++;
            if (!await pipeline.NeedsSynthesisAsync(article, ct)) continue;

            if (adopt && await pipeline.AdoptExistingAsync(article, ct))
            {
                adopted++;
                logger.LogInformation("Adopted existing audio for {Slug} (no synthesis)", article.Slug);
                continue;
            }

            queued.Add(article.Slug);
            logger.LogInformation("Queued {Slug} for narration", article.Slug);

            if (queued.Count >= max)
            {
                logger.LogInformation("Daily cap of {Max} reached; remaining posts are queued on the next run", max);
                break;
            }
        }

        logger.LogInformation("Checked {Checked} posts, adopted {Adopted}, queued {Queued}", checkedPosts, adopted, queued.Count);
        return [.. queued];
    }
}
