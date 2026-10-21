namespace CloudTales.Narrator.Core;

/// <summary>What happened to one article.</summary>
public enum NarrationOutcome
{
    /// <summary>Stored audio matches the current SSML; nothing was synthesized.</summary>
    Unchanged,

    /// <summary>Audio was synthesized and uploaded.</summary>
    Synthesized,

    /// <summary>Audio is stale or missing, but this month's Speech budget has no room: nothing was synthesized.</summary>
    OverBudget
}

/// <summary>Result of processing one article.</summary>
public sealed record NarrationResult(
    Article Article, NarrationOutcome Outcome, int Chunks, Uri? AudioUrl, TimeSpan? Duration, UsageDecision? Usage = null);

/// <summary>
/// Article → SSML → (cache check) → (monthly budget) → speech → Blob Storage. Shared by the CLI and the Function.
/// Without a <see cref="UsageMeter"/> there is no monthly limit.
/// </summary>
public sealed class NarrationPipeline(AudioStore store, SpeechNarrator narrator, SsmlOptions ssml, UsageMeter? meter = null)
{
    /// <summary>Builds the SSML chunks for an article without calling any Azure service.</summary>
    public IReadOnlyList<string> BuildSsml(Article article) =>
        SsmlBuilder.Build(article.Title, ArticleParser.Parse(article.Html), ssml);

    /// <summary>True when the stored audio is missing or was built from different SSML. No Speech cost.</summary>
    public async Task<bool> NeedsSynthesisAsync(Article article, CancellationToken ct = default) =>
        await store.FindCurrentAsync(article.Slug, AudioStore.ComputeHash(BuildSsml(article)), ct) is null;

    /// <summary>
    /// Marks existing audio as current for the article's present SSML, without synthesis (no Speech cost).
    /// Returns false when the article has no audio yet.
    /// </summary>
    public Task<bool> AdoptExistingAsync(Article article, CancellationToken ct = default) =>
        store.AdoptAsync(article.Slug, AudioStore.ComputeHash(BuildSsml(article)), ct);

    /// <summary>Synthesizes and uploads the article unless identical audio is already stored.</summary>
    public async Task<NarrationResult> ProcessAsync(Article article, bool force = false, CancellationToken ct = default)
    {
        var chunks = BuildSsml(article);
        var hash = AudioStore.ComputeHash(chunks);

        if (!force && await store.FindCurrentAsync(article.Slug, hash, ct) is { } existing)
            return new(article, NarrationOutcome.Unchanged, chunks.Count, existing, null);

        UsageDecision? usage = null;
        if (meter is not null)
        {
            usage = await meter.TryReserveAsync(UsageMeter.BillableCharacters(chunks), ct);
            if (!usage.Allowed)
                return new(article, NarrationOutcome.OverBudget, chunks.Count, null, null, usage);
        }

        var (audio, duration) = await narrator.SynthesizeAsync(chunks, ct);
        await using (audio)
        {
            var url = await store.UploadAsync(article.Slug, hash, audio, ct);
            return new(article, NarrationOutcome.Synthesized, chunks.Count, url, duration, usage);
        }
    }
}
