using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace CloudTales.Narrator.Core;

/// <summary>
/// Stores narration MP3s in Blob Storage, one blob per post slug, tagged with a hash of the SSML
/// they were built from. Same hash → same audio → no need to pay for synthesis again.
/// </summary>
public sealed class AudioStore(Uri blobEndpoint, TokenCredential credential, string containerName = "audio")
{
    private const string HashKey = "ssmlhash";

    private readonly BlobContainerClient _container =
        new BlobServiceClient(blobEndpoint, credential).GetBlobContainerClient(containerName);

    /// <summary>
    /// Hashes the SSML chunks. The SSML includes the voice name, so changing the voice
    /// also invalidates the cached audio.
    /// </summary>
    public static string ComputeHash(IEnumerable<string> ssmlChunks) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u0000', ssmlChunks))));

    /// <summary>Returns the blob URL if stored audio exists for this exact SSML; otherwise null.</summary>
    public async Task<Uri?> FindCurrentAsync(string slug, string hash, CancellationToken ct = default)
    {
        var blob = _container.GetBlobClient(BlobName(slug));
        try
        {
            var props = await blob.GetPropertiesAsync(cancellationToken: ct);
            return props.Value.Metadata.TryGetValue(HashKey, out var stored) && stored == hash
                ? blob.Uri
                : null;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    /// <summary>Uploads (overwrites) the MP3 for a slug and records the SSML hash.</summary>
    public async Task<Uri> UploadAsync(string slug, string hash, Stream audio, CancellationToken ct = default)
    {
        var blob = _container.GetBlobClient(BlobName(slug));
        audio.Position = 0;

        await blob.UploadAsync(audio, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders
            {
                ContentType = "audio/mpeg",
                CacheControl = "public, max-age=3600"
            },
            Metadata = new Dictionary<string, string> { [HashKey] = hash }
        }, ct);

        return blob.Uri;
    }

    private static string BlobName(string slug) => $"{slug}.mp3";
}
