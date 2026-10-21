using System.Globalization;
using System.Text;
using System.Xml;

namespace CloudTales.Narrator.Core;

/// <summary>Show-level podcast details, published by the site (Hugo <c>narration.json</c> → <c>podcast</c>).</summary>
public sealed record PodcastShow(
    string Title, string Description, string Author, string Email,
    string Category, string Language, string Image, string Link, string FeedUrl);

/// <summary>One article as a podcast episode (its audio is looked up separately).</summary>
public sealed record PodcastEpisode(string Slug, string Title, string Url, string Summary, DateTime Published);

/// <summary>An article or a recipe as the newsletter sees it.</summary>
public sealed record SiteItem(string Slug, string Title, string Url, string Summary, DateTime Published, string Kind)
{
    public const string Article = "article", Recipe = "recipe";
    public bool IsRecipe => Kind == Recipe;
}

/// <summary>Size and date of a stored narration MP3.</summary>
public sealed record AudioInfo(long Bytes, DateTimeOffset LastModified);

/// <summary>
/// Builds an RSS 2.0 podcast feed with the iTunes and Podcasting 2.0 tags that Apple Podcasts and Spotify read.
/// Pure: no I/O, so it is easy to test.
/// </summary>
public static class PodcastFeedBuilder
{
    /// <summary>Narrations are constant-bitrate MP3 (Audio24Khz96KBitRateMonoMp3), so duration = size / bitrate.</summary>
    public const int BitsPerSecond = 96_000;

    private const string ItunesNs = "http://www.itunes.com/dtds/podcast-1.0.dtd";
    private const string AtomNs = "http://www.w3.org/2005/Atom";
    private const string PodcastNs = "https://podcastindex.org/namespace/1.0";

    /// <param name="audioUrl">Public URL of an episode's MP3, from its slug.</param>
    public static byte[] Build(
        PodcastShow show,
        IEnumerable<(PodcastEpisode Episode, AudioInfo Audio)> episodes,
        Func<string, string> audioUrl,
        DateTimeOffset now)
    {
        var items = episodes.OrderByDescending(e => e.Episode.Published).ToList();
        using var buffer = new MemoryStream();
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true };

        using (var w = XmlWriter.Create(buffer, settings))
        {
            w.WriteStartDocument();
            w.WriteStartElement("rss");
            w.WriteAttributeString("version", "2.0");
            w.WriteAttributeString("xmlns", "itunes", null, ItunesNs);
            w.WriteAttributeString("xmlns", "atom", null, AtomNs);
            w.WriteAttributeString("xmlns", "podcast", null, PodcastNs);
            w.WriteStartElement("channel");

            w.WriteElementString("title", show.Title);
            w.WriteElementString("link", show.Link);
            w.WriteElementString("language", show.Language);
            w.WriteElementString("description", show.Description);
            w.WriteElementString("copyright", $"© {now.Year} {show.Author}");
            w.WriteElementString("lastBuildDate", Rfc822(items.Count > 0 ? items.Max(i => i.Audio.LastModified) : now));
            w.WriteElementString("generator", "CloudTales Narrator");

            w.WriteStartElement("atom", "link", AtomNs);
            w.WriteAttributeString("href", show.FeedUrl);
            w.WriteAttributeString("rel", "self");
            w.WriteAttributeString("type", "application/rss+xml");
            w.WriteEndElement();

            w.WriteStartElement("image");
            w.WriteElementString("url", show.Image);
            w.WriteElementString("title", show.Title);
            w.WriteElementString("link", show.Link);
            w.WriteEndElement();

            w.WriteElementString("itunes", "author", ItunesNs, show.Author);
            w.WriteElementString("itunes", "summary", ItunesNs, show.Description);
            w.WriteElementString("itunes", "type", ItunesNs, "episodic");
            w.WriteElementString("itunes", "explicit", ItunesNs, "false");
            w.WriteStartElement("itunes", "owner", ItunesNs);
            w.WriteElementString("itunes", "name", ItunesNs, show.Author);
            w.WriteElementString("itunes", "email", ItunesNs, show.Email);
            w.WriteEndElement();
            w.WriteStartElement("itunes", "image", ItunesNs);
            w.WriteAttributeString("href", show.Image);
            w.WriteEndElement();
            w.WriteStartElement("itunes", "category", ItunesNs);
            w.WriteAttributeString("text", show.Category);
            w.WriteEndElement();

            // Stops other hosting platforms from importing the show without the owner's email confirmation
            w.WriteStartElement("podcast", "locked", PodcastNs);
            w.WriteAttributeString("owner", show.Email);
            w.WriteString("yes");
            w.WriteEndElement();

            foreach (var (e, audio) in items)
            {
                w.WriteStartElement("item");
                w.WriteElementString("title", e.Title);
                w.WriteElementString("link", e.Url);
                w.WriteStartElement("guid");
                w.WriteAttributeString("isPermaLink", "false");
                w.WriteString($"cloudtales:{e.Slug}");   // stable across domain or URL changes
                w.WriteEndElement();
                w.WriteElementString("pubDate", Rfc822(e.Published));

                var notes = $"<p>{Escape(e.Summary)}</p><p><a href=\"{Escape(e.Url)}\">Read the full article, with code and diagrams, on CloudTales</a></p>";
                w.WriteStartElement("description");
                w.WriteCData(notes);
                w.WriteEndElement();
                w.WriteElementString("itunes", "summary", ItunesNs, e.Summary);

                w.WriteStartElement("enclosure");
                w.WriteAttributeString("url", audioUrl(e.Slug));
                w.WriteAttributeString("length", audio.Bytes.ToString(CultureInfo.InvariantCulture));
                w.WriteAttributeString("type", "audio/mpeg");
                w.WriteEndElement();

                w.WriteElementString("itunes", "duration", ItunesNs,
                    (audio.Bytes * 8 / BitsPerSecond).ToString(CultureInfo.InvariantCulture));
                w.WriteElementString("itunes", "episodeType", ItunesNs, "full");
                w.WriteElementString("itunes", "explicit", ItunesNs, "false");
                w.WriteEndElement();
            }

            w.WriteEndElement(); // channel
            w.WriteEndElement(); // rss
        }

        return buffer.ToArray();
    }

    private static string Rfc822(DateTimeOffset value) => value.ToUniversalTime().ToString("r", CultureInfo.InvariantCulture);
    private static string Rfc822(DateTime value) => Rfc822(new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)));
    private static string Escape(string text) => System.Net.WebUtility.HtmlEncode(text);
}
