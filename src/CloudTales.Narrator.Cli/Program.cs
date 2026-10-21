using CloudTales.Narrator.Core;
using Microsoft.Extensions.Configuration;

// Usage: dotnet run -- --slug <slug-or-url> [--dryRun true] [--check true] [--force true]
//   --dryRun  writes the SSML only; no Azure calls, no cost
//   --check   compares the SSML hash with the stored audio; Storage only, no Speech cost
//   --force   re-synthesizes even when the stored audio is current
var config = new ConfigurationBuilder()
    .AddUserSecrets<Program>()
    .AddCommandLine(args)
    .Build();

var slug = (config["slug"] ?? throw new ArgumentException("Missing --slug <post-slug>."))
    .TrimEnd('/').Split('/')[^1];
var dryRun = Flag("dryRun");
var check = Flag("check");
var force = Flag("force");

var ssml = new SsmlOptions(
    Voice: config["Speech:Voice"] ?? "en-US-Andrew:DragonHDLatestNeural",
    SiteName: Required("Narrator:SiteName"),
    SpokenAddress: Required("Narrator:SpokenAddress"));

// Article source: Site:FeedUrl (static site JSON feed) wins over WordPress:BaseUrl
var feedUrl = config["Site:FeedUrl"];
using var http = new HttpClient
{
    BaseAddress = new Uri(feedUrl is { Length: > 0 } ? feedUrl : Required("WordPress:BaseUrl"))
};
http.DefaultRequestHeaders.UserAgent.ParseAdd("CloudTales-Narrator/1.0");
IArticleSource source = feedUrl is { Length: > 0 } ? new HugoFeedClient(http) : new WordPressClient(http);

var article = await source.GetBySlugAsync(slug)
    ?? throw new InvalidOperationException($"No post found with slug '{slug}'.");

var credential = NarratorCredential.Create();
var pipeline = new NarrationPipeline(
    new AudioStore(new Uri(Required("Storage:BlobEndpoint")), credential),
    new SpeechNarrator(credential, Required("Speech:ResourceId"), Required("Speech:Region")),
    ssml);

var chunks = pipeline.BuildSsml(article);
var ssmlPath = Path.GetFullPath($"{slug}.ssml.txt");
await File.WriteAllTextAsync(ssmlPath, string.Join("\n\n<!-- ===== next chunk ===== -->\n\n", chunks));

Console.WriteLine($"Post {article.Slug} '{article.Title}': {chunks.Count} chunk(s), {chunks.Sum(c => c.Length)} SSML characters");
Console.WriteLine($"SSML written to {ssmlPath}");

if (dryRun)
{
    Console.WriteLine("Dry run: no synthesis, no cost.");
    return 0;
}

if (check)
{
    Console.WriteLine(await pipeline.NeedsSynthesisAsync(article)
        ? "Hash differs: this post WOULD be re-synthesized."
        : "Unchanged: the SSML hash matches the stored audio.");
    return 0;
}

var result = await pipeline.ProcessAsync(article, force);
Console.WriteLine(result.Outcome switch
{
    NarrationOutcome.Unchanged => $"Unchanged since last synthesis, skipping (no cost): {result.AudioUrl}",
    NarrationOutcome.OverBudget => "Monthly Speech budget reached: not synthesized (no cost).",
    _ => $"Synthesized {result.Duration?.ToString(@"mm\:ss")} in {result.Chunks} chunk(s), uploaded: {result.AudioUrl}"
});
return 0;

bool Flag(string name) => bool.TryParse(config[name], out var value) && value;

string Required(string key) =>
    config[key] is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"Missing user secret '{key}' (dotnet user-secrets set \"{key}\" <value>).");
