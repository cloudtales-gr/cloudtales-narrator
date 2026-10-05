using Azure.Core;
using CloudTales.Narrator.Core;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);
var config = builder.Configuration;

// App settings use "__" as separator (Speech__ResourceId) → read here as "Speech:ResourceId"
builder.Services.AddSingleton<TokenCredential>(_ => NarratorCredential.Create());

builder.Services.AddHttpClient<WordPressClient>(http =>
{
    http.BaseAddress = new Uri(config["WordPress:BaseUrl"] ?? "https://cloudtales.gr/");
    http.DefaultRequestHeaders.UserAgent.ParseAdd("CloudTales-Narrator/0.3");
});

builder.Services.AddSingleton(sp => new AudioStore(
    new Uri(Required(config, "Storage:BlobEndpoint")),
    sp.GetRequiredService<TokenCredential>()));

builder.Services.AddSingleton(sp => new SpeechNarrator(
    sp.GetRequiredService<TokenCredential>(),
    Required(config, "Speech:ResourceId"),
    config["Speech:Region"] ?? "westeurope"));

builder.Services.AddSingleton(sp => new NarrationPipeline(
    sp.GetRequiredService<AudioStore>(),
    sp.GetRequiredService<SpeechNarrator>(),
    config["Speech:Voice"] ?? "en-US-Andrew:DragonHDLatestNeural"));

builder.Build().Run();

static string Required(IConfiguration config, string key) =>
    config[key] ?? throw new InvalidOperationException($"Missing app setting '{key.Replace(":", "__")}'.");
