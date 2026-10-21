using System.Globalization;
using CloudTales.Narrator.Core;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CloudTales.Narrator.Functions;

/// <summary>
/// One message = one post = one short, independent execution. Exceptions are not caught on purpose:
/// the platform retries the message (maxDequeueCount in host.json) and then moves it to the poison queue.
/// Re-processing is safe: the SSML hash check skips posts that already have current audio.
/// The monthly Speech budget (<see cref="UsageMeter"/>) is checked before every synthesis; at 80% and when it
/// runs out, one email per month goes to Contact__Recipient (from Contact__Sender, when both are set).
/// </summary>
public sealed class NarrateArticle(
    IArticleSource articles,
    NarrationPipeline pipeline,
    GraphMailer mailer,
    IConfiguration config,
    ILogger<NarrateArticle> logger)
{
    [Function(nameof(NarrateArticle))]
    public async Task RunAsync([QueueTrigger(EnqueueChangedArticles.QueueName)] string slug, CancellationToken ct)
    {
        var article = await articles.GetBySlugAsync(slug, ct);
        if (article is null)
        {
            logger.LogWarning("Post {Slug} no longer exists; nothing to narrate", slug);
            return;
        }

        logger.LogInformation("Narrating {Slug}", slug);
        var result = await pipeline.ProcessAsync(article, ct: ct);

        if (result.Outcome == NarrationOutcome.OverBudget)
        {
            var u = result.Usage!;
            logger.LogWarning("Monthly Speech budget reached: {Slug} needs {Needed} characters, {Used}/{Budget} used in {Month}. Not synthesized.",
                slug, u.Requested, u.Usage.Characters, u.Budget, u.Usage.Month);
        }
        else
        {
            logger.LogInformation("{Outcome} {Slug}: {Duration}, {Chunks} chunk(s){Usage}",
                result.Outcome, slug, result.Duration, result.Chunks,
                result.Usage is { } usage ? $", month {usage.Usage.Month}: {usage.Usage.Characters}/{usage.Budget} characters" : "");
        }

        if (result.Usage is { Notice: not BudgetNotice.None } decision)
            await NotifyAsync(article, decision, ct);
    }

    private async Task NotifyAsync(Article article, UsageDecision d, CancellationToken ct)
    {
        var sender = config["Contact:Sender"];
        var recipient = config["Contact:Recipient"];
        if (string.IsNullOrEmpty(sender) || string.IsNullOrEmpty(recipient))
        {
            logger.LogWarning("Budget notice {Notice} not emailed: Contact__Sender/Contact__Recipient not set", d.Notice);
            return;
        }

        var used = d.Usage.Characters.ToString("N0", CultureInfo.InvariantCulture);
        var budget = d.Budget.ToString("N0", CultureInfo.InvariantCulture);
        var nextMonth = DateTime.ParseExact(d.Usage.Month, "yyyy-MM", CultureInfo.InvariantCulture).AddMonths(1)
            .ToString("1 MMMM yyyy", CultureInfo.InvariantCulture);

        var (subject, body) = d.Notice == BudgetNotice.Warning
            ? ($"[CloudTales narrator] 80% of this month's Speech budget used",
               $"The narrator has used {used} of its {budget} character budget for {d.Usage.Month} " +
               $"({d.Usage.Syntheses} syntheses so far; the last one was \"{article.Title}\").\n\n" +
               $"Nothing is blocked yet. Once the budget runs out, new or changed articles wait until {nextMonth}.")
            : ($"[CloudTales narrator] Speech budget reached; narration paused until {nextMonth}",
               $"\"{article.Title}\" ({article.Slug}) needs about {d.Requested:N0} characters, but {used} of the " +
               $"{budget} character budget for {d.Usage.Month} are already used, so it was not synthesized and nothing was billed.\n\n" +
               $"Articles that are waiting get their audio automatically after {nextMonth}; the reader sees the previous audio, or no player, until then.\n" +
               "To allow more now, raise the Function App setting Narrator__MonthlyCharacterBudget (0 = no limit).");

        try
        {
            await mailer.SendTextAsync(sender, recipient, subject, body + "\n\n--\nSent by the CloudTales narrator (Azure Functions).", ct: ct);
            logger.LogInformation("Budget notice {Notice} emailed to {Recipient}", d.Notice, recipient);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never fail (and so retry) the message because of the email: the audio is already stored
            logger.LogError(ex, "Budget notice {Notice} could not be emailed", d.Notice);
        }
    }
}
