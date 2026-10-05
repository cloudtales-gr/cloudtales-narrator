using Azure.Core;
using Azure.Identity;
using HtmlAgilityPack;
using Microsoft.CognitiveServices.Speech;
using Microsoft.Extensions.Configuration;
using System.Net.Http.Json;

// Usage: dotnet run -- --slug <wordpress-post-slug> [--maxChars 3000]
var config = new ConfigurationBuilder()
    .AddUserSecrets<Program>()
    .AddCommandLine(args)
    .Build();

var slug = config["slug"] ?? throw new ArgumentException("Missing --slug <wordpress-post-slug>.");
var maxChars = int.TryParse(config["maxChars"], out var m) ? m : 3000;
var resourceId = config["Speech:ResourceId"] ?? throw new InvalidOperationException("Missing user secret Speech:ResourceId.");
var region = config["Speech:Region"] ?? "westeurope";
var voice = config["Speech:Voice"] ?? "en-US-Andrew:DragonHDLatestNeural";

// 1. Fetch the post from the WordPress REST API
using var http = new HttpClient { BaseAddress = new Uri("https://cloudtales.gr/") };
http.DefaultRequestHeaders.UserAgent.ParseAdd("CloudTales-Narrator/0.1");

var posts = await http.GetFromJsonAsync<WpPost[]>(
    $"wp-json/wp/v2/posts?slug={Uri.EscapeDataString(slug)}&_fields=id,title,content,modified");
var post = posts?.FirstOrDefault()
    ?? throw new InvalidOperationException($"No post found with slug '{slug}'.");

var title = HtmlEntity.DeEntitize(post.Title.Rendered).Trim();
var body = Truncate(ToPlainText(post.Content.Rendered), maxChars);
var text = $"{title}.\n\n{body}";

Console.WriteLine($"Post {post.Id} '{title}' (modified {post.Modified:u}) → {text.Length} characters");

// 2. Authenticate with Entra ID (az login locally, Managed Identity later) — no keys
var credential = new DefaultAzureCredential();
var token = await credential.GetTokenAsync(
    new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]));

var speechConfig = SpeechConfig.FromAuthorizationToken($"aad#{resourceId}#{token.Token}", region);
speechConfig.SpeechSynthesisVoiceName = voice;
speechConfig.SetSpeechSynthesisOutputFormat(SpeechSynthesisOutputFormat.Audio24Khz96KBitRateMonoMp3);

// 3. Synthesize to memory (null AudioConfig = no speaker playback), then write the MP3
using var synthesizer = new SpeechSynthesizer(speechConfig, null);
using var result = await synthesizer.SpeakTextAsync(text);

if (result.Reason == ResultReason.Canceled)
{
    var details = SpeechSynthesisCancellationDetails.FromResult(result);
    Console.Error.WriteLine($"Synthesis canceled: {details.Reason} | {details.ErrorCode} | {details.ErrorDetails}");
    return 1;
}

var outputPath = Path.GetFullPath($"{slug}.mp3");
await File.WriteAllBytesAsync(outputPath, result.AudioData);
Console.WriteLine($"Saved {outputPath} ({result.AudioDuration:mm\\:ss})");
return 0;

/// <summary>Extracts readable block text from WordPress HTML, dropping code, figures and scripts.</summary>
static string ToPlainText(string html)
{
    var doc = new HtmlDocument();
    doc.LoadHtml(html);

    foreach (var node in Select(doc, "//pre|//code|//figure|//script|//style").ToList())
        node.Remove();

    var blocks = Select(doc, "//h1|//h2|//h3|//h4|//p|//li")
        .Select(n => HtmlEntity.DeEntitize(n.InnerText).Trim())
        .Where(t => t.Length > 0);

    return string.Join("\n\n", blocks);
}

static IEnumerable<HtmlNode> Select(HtmlDocument doc, string xpath) =>
    doc.DocumentNode.SelectNodes(xpath) ?? Enumerable.Empty<HtmlNode>();

/// <summary>Cuts at the last paragraph boundary before maxChars; 0 means no limit.</summary>
static string Truncate(string text, int maxChars)
{
    if (maxChars <= 0 || text.Length <= maxChars) return text;
    var cut = text.LastIndexOf("\n\n", maxChars, StringComparison.Ordinal);
    return text[..(cut > 0 ? cut : maxChars)];
}

internal sealed record WpPost(int Id, WpRendered Title, WpRendered Content, DateTime Modified);
internal sealed record WpRendered(string Rendered);
