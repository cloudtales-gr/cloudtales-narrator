namespace CloudTales.Narrator.Core;

/// <summary>A published post, with the title already decoded to plain text.</summary>
/// <param name="Slug">URL slug; also the audio blob name (<c>{slug}.mp3</c>).</param>
public sealed record Article(string Slug, string Title, string Html, DateTime Modified);

/// <summary>Where published posts come from: a WordPress REST API or a static site's JSON feed.</summary>
public interface IArticleSource
{
    /// <summary>Returns the post with this slug, or null if none exists.</summary>
    Task<Article?> GetBySlugAsync(string slug, CancellationToken ct = default);

    /// <summary>Streams every published post, newest first.</summary>
    IAsyncEnumerable<Article> GetAllAsync(CancellationToken ct = default);
}
