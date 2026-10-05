using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace CloudTales.Narrator.Core;

/// <summary>A narratable unit of an article, in reading order.</summary>
public abstract record ArticleBlock;

/// <summary>A section heading (h2–h4).</summary>
public sealed record HeadingBlock(string Text) : ArticleBlock;

/// <summary>A paragraph or list item.</summary>
public sealed record ParagraphBlock(string Text) : ArticleBlock;

/// <summary>Content that is not read aloud (code, tables); announced instead.</summary>
public sealed record OmittedBlock(string Kind) : ArticleBlock;

/// <summary>Converts WordPress post HTML into ordered, narratable blocks.</summary>
public static partial class ArticleParser
{
    private const string BlockXPath = "//h2|//h3|//h4|//p|//li|//pre|//table";

    /// <summary>Parses rendered post HTML into blocks in document order.</summary>
    public static IReadOnlyList<ArticleBlock> Parse(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        foreach (var node in doc.DocumentNode.SelectNodes("//script|//style|//figcaption")?.ToList() ?? [])
            node.Remove();

        var blocks = new List<ArticleBlock>();
        var nodes = doc.DocumentNode.SelectNodes(BlockXPath);
        if (nodes is null) return blocks;

        foreach (var node in nodes)
        {
            // Anything inside a code block or table is covered by the container's announcement
            if (IsInside(node, "pre", "table")) continue;

            if (node.Name is "pre") { AddOmitted(blocks, "code example"); continue; }
            if (node.Name is "table") { AddOmitted(blocks, "table"); continue; }

            // A list item that wraps paragraphs: read the paragraphs, not the item, to avoid duplicates
            if (node.Name is "li" && node.SelectSingleNode(".//p") is not null) continue;

            // Inline <code> stays: its text is part of the sentence (e.g. "run az login")
            var text = ToText(node);
            if (text.Length == 0) continue;

            blocks.Add(node.Name is "h2" or "h3" or "h4"
                ? new HeadingBlock(text)
                : new ParagraphBlock(text));
        }

        return blocks;
    }

    private static void AddOmitted(List<ArticleBlock> blocks, string kind)
    {
        // Several code blocks in a row are announced once
        if (blocks.Count > 0 && blocks[^1] is OmittedBlock last && last.Kind == kind) return;
        blocks.Add(new OmittedBlock(kind));
    }

    private static bool IsInside(HtmlNode node, params string[] names) =>
        node.Ancestors().Any(a => names.Contains(a.Name));

    private static string ToText(HtmlNode node) =>
        Whitespace().Replace(HtmlEntity.DeEntitize(node.InnerText), " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
