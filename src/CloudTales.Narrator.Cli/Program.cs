using Azure.Core;
using Azure.Identity;
using CloudTales.Narrator.Cli;
using HtmlAgilityPack;
using Microsoft.CognitiveServices.Speech;
using Microsoft.Extensions.Configuration;
using System.Net.Http.Json;

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

// 1. Fetch the post from the WordPress REST API
using var http = new HttpClient { BaseAddress = new Uri("https://cloudtales.gr/") };
http.DefaultRequestHeaders.UserAgent.ParseAdd("CloudTales-Narrator/0.1");

var posts = await http.GetFromJsonAsync<WpPost[]>(
    $"wp-json/wp/v2/posts?slug={Uri.EscapeDataString(slug)}&_fields=id,title,content,modified");
var post = posts?.FirstOrDefault()
    ?? throw new InvalidOperationException($"No post found with slug '{slug}'.");

// 2. HTML → blocks → SSML chunks
var title = HtmlEntity.DeEntitize(post.Title.Rendered).Trim();
var blocks = ArticleParser.Parse(post.Content.Rendered);
var chunks = SsmlBuilder.Build(title, blocks, voice);

var ssmlPath = Path.GetFullPath($"{slug}.ssml.txt");
await File.WriteAllTextAsync(ssmlPath, string.Join("\n\n<!-- ===== next chunk ===== -->\n\n", chunks));

Console.WriteLine($"Post {post.Id} '{title}': {blocks.Count} blocks → {chunks.Count} chunk(s), {chunks.Sum(c => c.Length)} SSML characters");
Console.WriteLine($"SSML written to {ssmlPath}");

if (dryRun)
{
    Console.WriteLine("Dry run: no synthesis, no cost.");
    return 0;
}

// 3. One Entra ID credential for both Speech and Storage — no keys anywhere.
//    Explicit per environment: Managed Identity in Azure (App Service/Functions set WEBSITE_SITE_NAME),
//    Azure CLI login on a dev machine. Avoids DefaultAzureCredential probing IMDS from a laptop.
TokenCredential credential = Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME") is not null
    ? new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned)
    : new AzureCliCredential();

// 4. Skip synthesis when the stored audio was built from identical SSML
var store = new AudioStore(new Uri(blobEndpoint), credential);
var hash = AudioStore.ComputeHash(chunks);

if (!force && await store.FindCurrentAsync(slug, hash) is { } existing)
{
    Console.WriteLine($"Unchanged since last synthesis, skipping (no cost): {existing}");
    return 0;
}

var token = await credential.GetTokenAsync(
    new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]), default);

var speechConfig = SpeechConfig.FromAuthorizationToken($"aad#{resourceId}#{token.Token}", region);
speechConfig.SetSpeechSynthesisOutputFormat(SpeechSynthesisOutputFormat.Audio24Khz96KBitRateMonoMp3);

// 5. Synthesize each chunk and concatenate the MP3 frames (same format → valid stream)
using var synthesizer = new SpeechSynthesizer(speechConfig, null);
using var audio = new MemoryStream();
var total = TimeSpan.Zero;

for (var i = 0; i < chunks.Count; i++)
{
    using var result = await synthesizer.SpeakSsmlAsync(chunks[i]);

    if (result.Reason == ResultReason.Canceled)
    {
        var details = SpeechSynthesisCancellationDetails.FromResult(result);
        Console.Error.WriteLine($"Chunk {i + 1} canceled: {details.Reason} | {details.ErrorCode} | {details.ErrorDetails}");
        return 1;
    }

    await audio.WriteAsync(result.AudioData);
    total += result.AudioDuration;
    Console.WriteLine($"  chunk {i + 1}/{chunks.Count}: {result.AudioDuration:mm\\:ss}");
}

// 6. Upload to Blob Storage with the SSML hash as metadata
var url = await store.UploadAsync(slug, hash, audio);
Console.WriteLine($"Uploaded ({total:mm\\:ss}): {url}");
return 0;

internal sealed record WpPost(int Id, WpRendered Title, WpRendered Content, DateTime Modified);
internal sealed record WpRendered(string Rendered);