using Azure.Core;
using Microsoft.CognitiveServices.Speech;

namespace CloudTales.Narrator.Core;

/// <summary>Raised when the Speech service cancels a synthesis request.</summary>
public sealed class SpeechSynthesisException(string message) : Exception(message);

/// <summary>Synthesizes SSML chunks with Azure Speech (Entra ID auth) into a single MP3 stream.</summary>
public sealed class SpeechNarrator(TokenCredential credential, string resourceId, string region)
{
    private static readonly TokenRequestContext Scope = new(["https://cognitiveservices.azure.com/.default"]);

    /// <summary>
    /// Synthesizes each chunk in order and concatenates the MP3 frames (same format → valid stream).
    /// The caller owns the returned stream.
    /// </summary>
    public async Task<(MemoryStream Audio, TimeSpan Duration)> SynthesizeAsync(
        IReadOnlyList<string> ssmlChunks, CancellationToken ct = default)
    {
        // A fresh token per article: credentials cache it, and a long batch never outlives it
        var token = await credential.GetTokenAsync(Scope, ct);

        var config = SpeechConfig.FromAuthorizationToken($"aad#{resourceId}#{token.Token}", region);
        config.SetSpeechSynthesisOutputFormat(SpeechSynthesisOutputFormat.Audio24Khz96KBitRateMonoMp3);

        using var synthesizer = new SpeechSynthesizer(config, null);
        var audio = new MemoryStream();
        var total = TimeSpan.Zero;

        try
        {
            for (var i = 0; i < ssmlChunks.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                using var result = await synthesizer.SpeakSsmlAsync(ssmlChunks[i]);

                if (result.Reason == ResultReason.Canceled)
                {
                    var d = SpeechSynthesisCancellationDetails.FromResult(result);
                    throw new SpeechSynthesisException(
                        $"Chunk {i + 1}/{ssmlChunks.Count} canceled: {d.Reason} | {d.ErrorCode} | {d.ErrorDetails}");
                }

                await audio.WriteAsync(result.AudioData, ct);
                total += result.AudioDuration;
            }
        }
        catch
        {
            await audio.DisposeAsync();
            throw;
        }

        return (audio, total);
    }
}
