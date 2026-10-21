using System.Text.Json;
using System.Xml.Linq;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace CloudTales.Narrator.Core;

/// <summary>Speech characters used in one calendar month (UTC).</summary>
public sealed record MonthlyUsage(string Month, long Characters, int Syntheses, bool Warned, bool Exhausted);

/// <summary>Which one-off email, if any, this decision should trigger.</summary>
public enum BudgetNotice { None, Warning, Exhausted }

/// <summary>Outcome of asking the meter for room for one synthesis.</summary>
public sealed record UsageDecision(bool Allowed, long Requested, long Budget, MonthlyUsage Usage, BudgetNotice Notice);

/// <summary>
/// Monthly cost guardrail for Azure Speech. Every synthesis reserves its billable characters first; when the
/// month's budget would be exceeded the synthesis is refused (no Speech call, no cost) until the 1st of the
/// next month. Usage lives in one small JSON blob per month (container "usage" of the audio storage account),
/// updated with ETag checks so parallel executions never lose a count. Budget 0 = no limit.
/// </summary>
public sealed class UsageMeter(BlobContainerClient container, long monthlyBudget, TimeProvider? clock = null)
{
    /// <summary>Share of the budget at which the warning email goes out (once a month).</summary>
    public const double WarningShare = 0.8;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private bool _containerReady;

    public long Budget => monthlyBudget;

    /// <summary>
    /// Characters Azure bills for these SSML chunks: the spoken text, not the markup.
    /// Whitespace between tags is counted too, so this errs slightly on the high side.
    /// </summary>
    public static long BillableCharacters(IEnumerable<string> ssmlChunks) =>
        ssmlChunks.Sum(chunk => (long)XDocument.Parse(chunk).Root!.Value.Length);

    /// <summary>Current month's usage (zero when nothing was synthesized yet).</summary>
    public async Task<MonthlyUsage> GetAsync(CancellationToken ct = default) => (await ReadAsync(ct)).Usage;

    /// <summary>
    /// Reserves <paramref name="characters"/> for one synthesis if the month's budget allows it.
    /// Reserved characters are not given back if the synthesis then fails: the chunks already spoken were billed.
    /// </summary>
    public async Task<UsageDecision> TryReserveAsync(long characters, CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            var (usage, etag) = await ReadAsync(ct);

            if (monthlyBudget > 0 && usage.Characters + characters > monthlyBudget)
            {
                if (usage.Exhausted)
                    return new(false, characters, monthlyBudget, usage, BudgetNotice.None);

                var exhausted = usage with { Exhausted = true };
                if (await TryWriteAsync(exhausted, etag, ct))
                    return new(false, characters, monthlyBudget, exhausted, BudgetNotice.Exhausted);
            }
            else
            {
                var next = usage with { Characters = usage.Characters + characters, Syntheses = usage.Syntheses + 1 };
                var warn = monthlyBudget > 0 && !usage.Warned && next.Characters >= monthlyBudget * WarningShare;
                if (warn) next = next with { Warned = true };

                if (await TryWriteAsync(next, etag, ct))
                    return new(true, characters, monthlyBudget, next, warn ? BudgetNotice.Warning : BudgetNotice.None);
            }

            // Someone else updated the blob between our read and write: read again
            if (attempt >= 8) throw new InvalidOperationException("Could not update the Speech usage counter (too much contention).");
            await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), ct);
        }
    }

    private string CurrentMonth => _clock.GetUtcNow().ToString("yyyy-MM");

    private async Task<(MonthlyUsage Usage, ETag? ETag)> ReadAsync(CancellationToken ct)
    {
        await EnsureContainerAsync(ct);
        var month = CurrentMonth;
        var blob = container.GetBlobClient($"{month}.json");
        try
        {
            var result = await blob.DownloadContentAsync(ct);
            var usage = result.Value.Content.ToObjectFromJson<MonthlyUsage>(Json) ?? new MonthlyUsage(month, 0, 0, false, false);
            return (usage, result.Value.Details.ETag);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return (new MonthlyUsage(month, 0, 0, false, false), null);
        }
    }

    private async Task<bool> TryWriteAsync(MonthlyUsage usage, ETag? etag, CancellationToken ct)
    {
        var blob = container.GetBlobClient($"{usage.Month}.json");
        var conditions = etag is { } e ? new BlobRequestConditions { IfMatch = e } : new BlobRequestConditions { IfNoneMatch = ETag.All };
        try
        {
            await blob.UploadAsync(BinaryData.FromObjectAsJson(usage, Json), new BlobUploadOptions
            {
                Conditions = conditions,
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" }
            }, ct);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            return false;
        }
    }

    private async Task EnsureContainerAsync(CancellationToken ct)
    {
        if (_containerReady) return;
        await container.CreateIfNotExistsAsync(cancellationToken: ct);
        _containerReady = true;
    }
}
