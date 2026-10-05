using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using HtmlAgilityPack;

namespace CloudTales.Narrator.Core;

/// <summary>A published post, with the title already decoded to plain text.</summary>
public sealed record Article(int Id, string Slug, string Title, string Html, DateTime Modified);

/// <summary>Reads published posts from the WordPress REST API (no authentication needed).</summary>
public sealed class WordPressClient(HttpClient http)
{
    private const string Fields = "id,slug,title,content,modified";
    private const int PageSize = 50;

    /// <summary>Returns the post with this slug, or null if none exists.</summary>
    public async Task<Article?> GetBySlugAsync(string slug, CancellationToken ct = default)
    {
        var posts = await http.GetFromJsonAsync<WpPost[]>(
            $"wp-json/wp/v2/posts?slug={Uri.EscapeDataString(slug)}&_fields={Fields}", ct);
        return posts?.Select(ToArticle).FirstOrDefault();
    }

    /// <summary>Streams every published post, page by page.</summary>
    public async IAsyncEnumerable<Article> GetAllAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        for (var page = 1; ; page++)
        {
            using var response = await http.GetAsync(
                $"wp-json/wp/v2/posts?per_page={PageSize}&page={page}&_fields={Fields}", ct);

            // WordPress answers 400 when asked for a page past the last one
            if (response.StatusCode == HttpStatusCode.BadRequest) yield break;
            response.EnsureSuccessStatusCode();

            var posts = await response.Content.ReadFromJsonAsync<WpPost[]>(ct) ?? [];
            foreach (var post in posts) yield return ToArticle(post);

            var totalPages = response.Headers.TryGetValues("X-WP-TotalPages", out var values)
                && int.TryParse(values.FirstOrDefault(), out var n) ? n : page;
            if (page >= totalPages) yield break;
        }
    }

    private static Article ToArticle(WpPost p) =>
        new(p.Id, p.Slug, HtmlEntity.DeEntitize(p.Title.Rendered).Trim(), p.Content.Rendered, p.Modified);

    private sealed record WpPost(int Id, string Slug, WpRendered Title, WpRendered Content, DateTime Modified);
    private sealed record WpRendered(string Rendered);
}
