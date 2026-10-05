using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;

namespace CloudTales.Narrator.Core;

/// <summary>
/// Stores narration MP3s in a private Blob container, one blob per post slug, tagged with a hash of the
/// SSML they were built from. Playback goes through short-lived user delegation SAS links (Entra ID
/// signed, no account keys).
/// </summary>
public sealed class AudioStore
{
    private const string HashKey = "ssmlhash";

    private readonly BlobServiceClient _service;
    private readonly BlobContainerClient _container;
    private readonly SemaphoreSlim _keyLock = new(1, 1);
    private UserDelegationKey? _delegationKey;

    public AudioStore(Uri blobEndpoint, TokenCredential credential, string containerName = "audio")
    {
        _service = new BlobServiceClient(blobEndpoint, credential);
        _container = _service.GetBlobContainerClient(containerName);
    }

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
                CacheControl = "private, max-age=3600"
            },
            Metadata = new Dictionary<string, string> { [HashKey] = hash }
        }, ct);

        return blob.Uri;
    }

    /// <summary>
    /// Returns a read-only, HTTPS-only link to the MP3 valid for <paramref name="lifetime"/>,
    /// or null if no audio exists for the slug.
    /// </summary>
    public async Task<Uri?> GetReadUriAsync(string slug, TimeSpan lifetime, CancellationToken ct = default)
    {
        var blob = _container.GetBlobClient(BlobName(slug));
        if (!(await blob.ExistsAsync(ct)).Value) return null;

        var now = DateTimeOffset.UtcNow;
        var sas = new BlobSasBuilder
        {
            BlobContainerName = _container.Name,
            BlobName = blob.Name,
            Resource = "b",
            StartsOn = now.AddMinutes(-5), // tolerate clock skew
            ExpiresOn = now.Add(lifetime),
            Protocol = SasProtocol.Https
        };
        sas.SetPermissions(BlobSasPermissions.Read);

        var key = await GetDelegationKeyAsync(ct);
        return new BlobUriBuilder(blob.Uri)
        {
            Sas = sas.ToSasQueryParameters(key, _service.AccountName)
        }.ToUri();
    }

    /// <summary>The delegation key is valid for a day; reuse it instead of requesting one per link.</summary>
    private async Task<UserDelegationKey> GetDelegationKeyAsync(CancellationToken ct)
    {
        if (IsFresh(_delegationKey)) return _delegationKey!;

        await _keyLock.WaitAsync(ct);
        try
        {
            if (IsFresh(_delegationKey)) return _delegationKey!;

            var now = DateTimeOffset.UtcNow;
            _delegationKey = (await _service.GetUserDelegationKeyAsync(now.AddMinutes(-5), now.AddDays(1), ct)).Value;
            return _delegationKey;
        }
        finally
        {
            _keyLock.Release();
        }
    }

    private static bool IsFresh(UserDelegationKey? key) =>
        key is not null && key.SignedExpiresOn > DateTimeOffset.UtcNow.AddHours(3);

    private static string BlobName(string slug) => $"{slug}.mp3";
}
