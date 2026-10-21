using CloudTales.Narrator.Core;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CloudTales.Narrator.Functions;

/// <summary>
/// Hourly, seconds-long when there is nothing new: emails each newly published article or recipe to every confirmed
/// subscriber, one message per subscriber (each with its own unsubscribe link), through Microsoft Graph.
/// <list type="bullet">
/// <item>First run: records every existing article as already sent, so subscribers never get the archive.</item>
/// <item>"New" = not sent before and published in the last Newsletter__MaxPostAgeDays days (default 3);
///   older unsent articles (for example after a long outage) are recorded as sent, not mailed.</item>
/// <item>Progress is saved after every message, so a crash or timeout resumes without duplicates.</item>
/// <item>Paced under Exchange Online's 30 messages per minute; stops after 20 minutes and continues next hour.</item>
/// <item>Deletes sign-ups that were never confirmed after 7 days.</item>
/// </list>
/// </summary>
public sealed class NewsletterDispatch(
    HugoFeedClient feed,
    NewsletterStore store,
    GraphMailer mailer,
    IConfiguration config,
    ILogger<NewsletterDispatch> logger)
{
    private static readonly TimeSpan Pace = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan RunLimit = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan PendingLifetime = TimeSpan.FromDays(7);

    [Function(nameof(NewsletterDispatch))]
    public async Task RunAsync([TimerTrigger("0 15 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        var siteUrl = config["Newsletter:SiteUrl"];
        var sender = config["Newsletter:Sender"] ?? config["Contact:Sender"];
        if (string.IsNullOrEmpty(siteUrl) || string.IsNullOrEmpty(sender)) return;   // newsletter not configured
        var siteName = config["Narrator:SiteName"] ?? "CloudTales";
        var maxAgeDays = int.TryParse(config["Newsletter:MaxPostAgeDays"], out var d) ? d : 3;

        var started = DateTimeOffset.UtcNow;
        var posts = await feed.GetSiteItemsAsync(ct);   // articles and recipes
        var subscribers = new List<NewsletterSubscriber>();
        var expired = 0;
        await foreach (var s in store.ListAsync(ct))
        {
            if (s.Status == NewsletterSubscriber.Active) subscribers.Add(s);
            else if (started - s.CreatedUtc > PendingLifetime)
            {
                await store.DeleteAsync(s.Id, ct);
                expired++;
            }
        }
        if (expired > 0) logger.LogInformation("Newsletter: deleted {Expired} unconfirmed sign-up(s)", expired);

        var state = await store.GetStateAsync(ct);
        if (state is null)
        {
            await store.SaveStateAsync(new NewsletterState([.. posts.Select(p => p.Slug)], null, []), ct);
            logger.LogInformation("Newsletter: first run, {Count} existing articles recorded as sent", posts.Count);
            return;
        }

        var sent = new HashSet<string>(state.Sent, StringComparer.OrdinalIgnoreCase);
        var cutoff = started.UtcDateTime.AddDays(-maxAgeDays);
        var stale = posts.Where(p => !sent.Contains(p.Slug) && p.Published < cutoff).Select(p => p.Slug).ToList();
        if (stale.Count > 0)
        {
            state.Sent.AddRange(stale);
            sent.UnionWith(stale);
            logger.LogInformation("Newsletter: {Count} older unsent article(s) recorded as sent without mailing", stale.Count);
        }

        var due = posts
            .Where(p => !sent.Contains(p.Slug) && p.Published <= started.UtcDateTime)
            .OrderBy(p => p.Published)
            .ToList();
        if (due.Count == 0)
        {
            if (stale.Count > 0) await store.SaveStateAsync(state, ct);
            return;
        }

        foreach (var post in due)
        {
            if (state.CurrentSlug != post.Slug) state = state with { CurrentSlug = post.Slug, CurrentDone = [] };
            var done = new HashSet<string>(state.CurrentDone);
            var mailed = 0;

            foreach (var s in subscribers.Where(s => !done.Contains(s.Id)))
            {
                if (DateTimeOffset.UtcNow - started > RunLimit)
                {
                    await store.SaveStateAsync(state, ct);
                    logger.LogInformation("Newsletter: time limit, {Slug} continues next hour", post.Slug);
                    return;
                }

                try
                {
                    var (subject, html) = NewsletterMail.NewPost(siteName, siteUrl, post, s);
                    await mailer.SendHtmlAsync(sender, s.Email, subject, html, ct);
                    mailed++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One bad address must not hold up everyone else; it is not retried for this article
                    logger.LogWarning(ex, "Newsletter: one message for {Slug} failed", post.Slug);
                }

                state.CurrentDone.Add(s.Id);
                await store.SaveStateAsync(state, ct);
                await Task.Delay(Pace, ct);
            }

            state.Sent.Add(post.Slug);
            state = state with { CurrentSlug = null, CurrentDone = [] };
            await store.SaveStateAsync(state, ct);
            logger.LogInformation("Newsletter: {Slug} mailed to {Mailed} of {Subscribers} subscriber(s)", post.Slug, mailed, subscribers.Count);
        }
    }
}
