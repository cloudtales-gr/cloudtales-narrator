using System.Security;
using System.Text;

namespace CloudTales.Narrator.Core;

/// <summary>Voice plus the site-specific words of the spoken intro and outro.</summary>
/// <param name="Voice">Azure neural voice name, e.g. en-US-Andrew:DragonHDLatestNeural.</param>
/// <param name="SiteName">How the intro names the site: "...the audio version of this {SiteName} article."</param>
/// <param name="SpokenAddress">How the outro reads the address aloud, e.g. "example dot com".</param>
public sealed record SsmlOptions(string Voice, string SiteName, string SpokenAddress);

/// <summary>
/// Turns article blocks into SSML documents, each small enough for one real-time synthesis call
/// (the service caps real-time output at 10 minutes of audio per request).
/// </summary>
public static class SsmlBuilder
{
    /// <summary>~1 minute of audio per 1,000 characters → 5,000 keeps each chunk well under the cap.</summary>
    public const int DefaultMaxCharsPerChunk = 5000;

    /// <summary>Builds the SSML chunks for one article, intro and outro included.</summary>
    public static IReadOnlyList<string> Build(
        string title,
        IReadOnlyList<ArticleBlock> blocks,
        SsmlOptions options,
        int maxCharsPerChunk = DefaultMaxCharsPerChunk)
    {
        List<string> fragments =
        [
            $"""{Esc(title)}.<break time="700ms"/>This is the audio version of this {Esc(options.SiteName)} article.<break time="1s"/>""",
            .. blocks.Select(ToFragment),
            $"""<break time="1s"/>Thanks for listening. The full article, including code examples and diagrams, is on {Esc(options.SpokenAddress)}."""
        ];

        var chunks = new List<string>();
        var current = new StringBuilder();

        foreach (var fragment in fragments)
        {
            // Split only between fragments, so no sentence is ever cut in half
            if (current.Length > 0 && current.Length + fragment.Length > maxCharsPerChunk)
            {
                chunks.Add(Wrap(current.ToString(), options.Voice));
                current.Clear();
            }
            current.Append(fragment);
        }

        if (current.Length > 0) chunks.Add(Wrap(current.ToString(), options.Voice));
        return chunks;
    }

    private static string ToFragment(ArticleBlock block) => block switch
    {
        HeadingBlock h => $"""<break time="800ms"/>{Esc(h.Text)}<break time="500ms"/>""",
        ParagraphBlock p => $"""{Esc(p.Text)}<break time="400ms"/>""",
        OmittedBlock o => $"""<break time="300ms"/>The article includes a {o.Kind} here.<break time="400ms"/>""",
        _ => throw new NotSupportedException($"Unknown block type {block.GetType().Name}.")
    };

    private static string Wrap(string body, string voice) =>
        $"""<speak version="1.0" xmlns="http://www.w3.org/2001/10/synthesis" xml:lang="en-US"><voice name="{Esc(voice)}">{body}</voice></speak>""";

    private static string Esc(string text) => SecurityElement.Escape(text) ?? string.Empty;
}
