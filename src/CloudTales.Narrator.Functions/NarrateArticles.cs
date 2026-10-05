using CloudTales.Narrator.Core;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CloudTales.Narrator.Functions;

/// <summary>
/// Daily run: walks every published post, synthesizes only new or changed ones.
/// A per-run cap spreads the initial backfill over several days, so the first run
/// can never synthesize the whole blog in one go.
/// </summary>
public sealed class NarrateArticles(
    WordPressClient wordPress,
    NarrationPipeline pipeline,
    IConfiguration config,
    ILogger<NarrateArticles> logger)
{
    private const int DefaultMaxSynthesesPerRun = 3;

    [Function(nameof(NarrateArticles))]
    public async Task RunAsync([TimerTrigger("0 0 4 * * *")] TimerInfo timer, CancellationToken ct)
    {
        var maxSyntheses = int.TryParse(config["Narrator:MaxSynthesesPerRun"], out var m) ? m : DefaultMaxSynthesesPerRun;
        int unchanged = 0, synthesized = 0, failed = 0;

        await foreach (var article in wordPress.GetAllAsync(ct))
        {
            if (synthesized >= maxSyntheses)
            {
                logger.LogInformation("Synthesis cap of {Max} reached; remaining posts are picked up on the next run", maxSyntheses);
                break;
            }

            try
            {
                var result = await pipeline.ProcessAsync(article, ct: ct);

                if (result.Outcome == NarrationOutcome.Synthesized)
                {
                    synthesized++;
                    logger.LogInformation("Synthesized {Slug} ({Duration}, {Chunks} chunks) → {Url}",
                        article.Slug, result.Duration, result.Chunks, result.AudioUrl);
                }
                else
                {
                    unchanged++;
                }
            }
            // Batch job: one bad post must not stop the others. Cancellation still propagates.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                logger.LogError(ex, "Failed to narrate {Slug}", article.Slug);
            }
        }

        logger.LogInformation(
            "Narration run complete: {Synthesized} synthesized, {Unchanged} unchanged, {Failed} failed",
            synthesized, unchanged, failed);
    }
}
