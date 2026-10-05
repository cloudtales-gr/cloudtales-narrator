using CloudTales.Narrator.Core;
using Microsoft.Extensions.Configuration;

// Usage: dotnet run -- --slug <slug-or-url> [--dryRun true] [--force true]
var config = new ConfigurationBuilder()
    .AddUserSecrets<Program>()
    .AddCommandLine(args)
    .Build();

var slug = (config["slug"] ?? throw new ArgumentException("Missing --slug <wordpress-post-slug>."))
    .TrimEnd('/').Split('/')[^1];
var dryRun = bool.TryParse(config["dryRun"], out var d) && d;
var force = bool.TryParse(config["force"], out var f) && f;
var resourceId = config["Speech:ResourceId"] ?? throw new InvalidOperationException("Missing user secret Speech:ResourceId.");
var region = config["Speech:Region"] ?? "westeurope";
var voice = config["Speech:Voice"] ?? "en-US-Andrew:DragonHDLatestNeural";
var blobEndpoint = config["Storage:BlobEndpoint"] ?? throw new InvalidOperationException("Missing user secret Storage:BlobEndpoint.");

using var http = new HttpClient { BaseAddress = new Uri("https://cloudtales.gr/") };
http.DefaultRequestHeaders.UserAgent.ParseAdd("CloudTales-Narrator/0.2");

var article = await new WordPressClient(http).GetBySlugAsync(slug)
    ?? throw new InvalidOperationException($"No post found with slug '{slug}'.");

var credential = NarratorCredential.Create();
var pipeline = new NarrationPipeline(
    new AudioStore(new Uri(blobEndpoint), credential),
    new SpeechNarrator(credential, resourceId, region),
    voice);

var chunks = pipeline.BuildSsml(article);
var ssmlPath = Path.GetFullPath($"{slug}.ssml.txt");
await File.WriteAllTextAsync(ssmlPath, string.Join("\n\n<!-- ===== next chunk ===== -->\n\n", chunks));

Console.WriteLine($"Post {article.Id} '{article.Title}': {chunks.Count} chunk(s), {chunks.Sum(c => c.Length)} SSML characters");
Console.WriteLine($"SSML written to {ssmlPath}");

if (dryRun)
{
    Console.WriteLine("Dry run: no synthesis, no cost.");
    return 0;
}

var result = await pipeline.ProcessAsync(article, force);
Console.WriteLine(result.Outcome switch
{
    NarrationOutcome.Unchanged => $"Unchanged since last synthesis, skipping (no cost): {result.AudioUrl}",
    _ => $"Synthesized {result.Duration?.ToString(@"mm\:ss")} in {result.Chunks} chunk(s), uploaded: {result.AudioUrl}"
});
return 0;
