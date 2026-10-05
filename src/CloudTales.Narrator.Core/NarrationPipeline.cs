namespace CloudTales.Narrator.Core;

/// <summary>What happened to one article.</summary>
public enum NarrationOutcome
{
    /// <summary>Stored audio matches the current SSML; nothing was synthesized.</summary>
    Unchanged,

    /// <summary>Audio was synthesized and uploaded.</summary>
    Synthesized
}

/// <summary>Result of processing one article.</summary>
public sealed record NarrationResult(
    Article Article, NarrationOutcome Outcome, int Chunks, Uri AudioUrl, TimeSpan? Duration);

/// <summary>Article → SSML → (cache check) → speech → Blob Storage. Shared by the CLI and the Function.</summary>
public sealed class NarrationPipeline(AudioStore store, SpeechNarrator narrator, string voice)
{
    /// <summary>Builds the SSML chunks for an article without calling any Azure service.</summary>
    public IReadOnlyList<string> BuildSsml(Article article) =>
        SsmlBuilder.Build(article.Title, ArticleParser.Parse(article.Html), voice);

    /// <summary>Synthesizes and uploads the article unless identical audio is already stored.</summary>
    public async Task<NarrationResult> ProcessAsync(Article article, bool force = false, CancellationToken ct = default)
    {
        var chunks = BuildSsml(article);
        var hash = AudioStore.ComputeHash(chunks);

        if (!force && await store.FindCurrentAsync(article.Slug, hash, ct) is { } existing)
            return new(article, NarrationOutcome.Unchanged, chunks.Count, existing, null);

        var (audio, duration) = await narrator.SynthesizeAsync(chunks, ct);
        await using (audio)
        {
            var url = await store.UploadAsync(article.Slug, hash, audio, ct);
            return new(article, NarrationOutcome.Synthesized, chunks.Count, url, duration);
        }
    }
}
