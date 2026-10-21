using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using HtmlAgilityPack;

namespace CloudTales.Narrator.Core;

/// <summary>
/// Reads posts from a static site's JSON feed, for example Hugo's <c>/narration.json</c>.
/// <c>HttpClient.BaseAddress</c> is the feed URL; one request returns every post:
/// <code>{"posts":[{"slug":"...","title":"...","modified":"2026-10-19T00:00:00Z","html":"&lt;p&gt;...&lt;/p&gt;"}]}</code>
/// Optional fields feed the podcast: <c>podcast</c> (show details) and per post <c>url</c>, <c>summary</c>, <c>published</c>.
/// <c>kind</c> is "article" (default) or "recipe": recipes are never narrated or put in the podcast,
/// but the newsletter mails them like articles.
/// </summary>
public sealed class HugoFeedClient(HttpClient http) : IArticleSource
{
    public async Task<Article?> GetBySlugAsync(string slug, CancellationToken ct = default) =>
        (await LoadAsync(ct)).FirstOrDefault(a => string.Equals(a.Slug, slug, StringComparison.OrdinalIgnoreCase));

    public async IAsyncEnumerable<Article> GetAllAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var article in await LoadAsync(ct)) yield return article;
    }

    /// <summary>Show details and every post as an episode; Show is null when the site publishes no podcast section.</summary>
    public async Task<(PodcastShow? Show, IReadOnlyList<PodcastEpisode> Episodes)> GetPodcastAsync(CancellationToken ct = default)
    {
        var feed = await FetchAsync(ct);
        var p = feed.Podcast;
        var show = p is { Title.Length: > 0, Email.Length: > 0, Image.Length: > 0, FeedUrl.Length: > 0 }
            ? new PodcastShow(p.Title, p.Description ?? "", p.Author ?? "", p.Email, p.Category ?? "Technology",
                p.Language ?? "en", p.Image, p.Link ?? "", p.FeedUrl)
            : null;

        IReadOnlyList<PodcastEpisode> episodes = [.. feed.Posts
            .Where(x => !x.IsRecipe && !string.IsNullOrWhiteSpace(x.Slug) && !string.IsNullOrWhiteSpace(x.Url))
            .Select(x => new PodcastEpisode(x.Slug, HtmlEntity.DeEntitize(x.Title).Trim(), x.Url!,
                x.Summary ?? "", x.Published ?? x.Modified))];
        return (show, episodes);
    }

    /// <summary>Everything the newsletter can mail: articles and recipes, each with its kind.</summary>
    public async Task<IReadOnlyList<SiteItem>> GetSiteItemsAsync(CancellationToken ct = default)
    {
        var feed = await FetchAsync(ct);
        return [.. feed.Posts
            .Where(x => !string.IsNullOrWhiteSpace(x.Slug) && !string.IsNullOrWhiteSpace(x.Url))
            .Select(x => new SiteItem(x.Slug, HtmlEntity.DeEntitize(x.Title).Trim(), x.Url!, x.Summary ?? "",
                x.Published ?? x.Modified, x.IsRecipe ? SiteItem.Recipe : SiteItem.Article))];
    }

    private async Task<Feed> FetchAsync(CancellationToken ct) =>
        await http.GetFromJsonAsync<Feed>(http.BaseAddress, ct)
            ?? throw new InvalidOperationException($"The article feed at {http.BaseAddress} is empty.");

    private async Task<IReadOnlyList<Article>> LoadAsync(CancellationToken ct)
    {
        var feed = await FetchAsync(ct);

        return [.. feed.Posts
            .Where(p => !p.IsRecipe && !string.IsNullOrWhiteSpace(p.Slug) && !string.IsNullOrWhiteSpace(p.Html))
            .Select(p => new Article(p.Slug, HtmlEntity.DeEntitize(p.Title).Trim(), p.Html!, p.Modified))];
    }

    private sealed record Feed(FeedPost[] Posts, FeedPodcast? Podcast);
    private sealed record FeedPost(string Slug, string Title, string? Html, DateTime Modified,
        string? Url, string? Summary, DateTime? Published, string? Kind)
    {
        public bool IsRecipe => string.Equals(Kind, SiteItem.Recipe, StringComparison.OrdinalIgnoreCase);
    }
    private sealed record FeedPodcast(string? Title, string? Description, string? Author, string? Email,
        string? Category, string? Language, string? Image, string? Link, string? FeedUrl);
}
