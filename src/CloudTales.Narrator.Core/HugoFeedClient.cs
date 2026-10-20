using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using HtmlAgilityPack;

namespace CloudTales.Narrator.Core;

/// <summary>
/// Reads posts from a static site's JSON feed, for example Hugo's <c>/narration.json</c>.
/// <c>HttpClient.BaseAddress</c> is the feed URL; one request returns every post:
/// <code>{"posts":[{"slug":"...","title":"...","modified":"2026-10-19T00:00:00Z","html":"&lt;p&gt;...&lt;/p&gt;"}]}</code>
/// </summary>
public sealed class HugoFeedClient(HttpClient http) : IArticleSource
{
    public async Task<Article?> GetBySlugAsync(string slug, CancellationToken ct = default) =>
        (await LoadAsync(ct)).FirstOrDefault(a => string.Equals(a.Slug, slug, StringComparison.OrdinalIgnoreCase));

    public async IAsyncEnumerable<Article> GetAllAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var article in await LoadAsync(ct)) yield return article;
    }

    private async Task<IReadOnlyList<Article>> LoadAsync(CancellationToken ct)
    {
        var feed = await http.GetFromJsonAsync<Feed>(http.BaseAddress, ct)
            ?? throw new InvalidOperationException($"The article feed at {http.BaseAddress} is empty.");

        return [.. feed.Posts
            .Where(p => !string.IsNullOrWhiteSpace(p.Slug) && !string.IsNullOrWhiteSpace(p.Html))
            .Select(p => new Article(p.Slug, HtmlEntity.DeEntitize(p.Title).Trim(), p.Html, p.Modified))];
    }

    private sealed record Feed(FeedPost[] Posts);
    private sealed record FeedPost(string Slug, string Title, string Html, DateTime Modified);
}
