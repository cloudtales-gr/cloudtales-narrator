using Azure.Core;
using CloudTales.Narrator.Core;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);
var config = builder.Configuration;

// App settings use "__" as separator (Speech__ResourceId) and are read here as "Speech:ResourceId".
// Site-specific settings have no defaults: a missing one stops the host at startup, not mid-run.
// Article source: a static site's JSON feed (Site__FeedUrl) wins over the WordPress REST API (WordPress__BaseUrl)
var feedUrl = config["Site:FeedUrl"];
var wordPressBaseUrl = config["WordPress:BaseUrl"];
if (string.IsNullOrEmpty(feedUrl) && string.IsNullOrEmpty(wordPressBaseUrl))
    throw new InvalidOperationException("Missing app setting: set 'Site__FeedUrl' (static site feed) or 'WordPress__BaseUrl'.");
_ = Required(config, "Audio:AllowedHosts"); // read by GetAudio on each request
var ssml = new SsmlOptions(
    Voice: config["Speech:Voice"] ?? "en-US-Andrew:DragonHDLatestNeural",
    SiteName: Required(config, "Narrator:SiteName"),
    SpokenAddress: Required(config, "Narrator:SpokenAddress"));

builder.Services.AddSingleton<TokenCredential>(_ => NarratorCredential.Create());

if (!string.IsNullOrEmpty(feedUrl))
{
    builder.Services.AddHttpClient<IArticleSource, HugoFeedClient>(http =>
    {
        http.BaseAddress = new Uri(feedUrl);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CloudTales-Narrator/1.0");
    });
}
else
{
    builder.Services.AddHttpClient<IArticleSource, WordPressClient>(http =>
    {
        http.BaseAddress = new Uri(wordPressBaseUrl!);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WordPress-Narrator/1.0");
    });
}

builder.Services.AddSingleton(sp => new AudioStore(
    new Uri(Required(config, "Storage:BlobEndpoint")),
    sp.GetRequiredService<TokenCredential>()));

builder.Services.AddSingleton(sp => new SpeechNarrator(
    sp.GetRequiredService<TokenCredential>(),
    Required(config, "Speech:ResourceId"),
    Required(config, "Speech:Region")));

builder.Services.AddSingleton(sp => new NarrationPipeline(
    sp.GetRequiredService<AudioStore>(),
    sp.GetRequiredService<SpeechNarrator>(),
    ssml));

builder.Build().Run();

static string Required(IConfiguration config, string key) =>
    config[key] is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"Missing app setting '{key.Replace(":", "__")}'.");
