using System.Text.RegularExpressions;
using Aiursoft.AnduinOS.OfflineBundles.Core.Models;

namespace Aiursoft.AnduinOS.OfflineBundles.Core.Services;

public sealed class CatalogPublisher(ICatalogStore store, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    public static readonly TimeSpan DeletionGrace = TimeSpan.FromHours(24);

    public async Task<CatalogRelease> PublishAsync(string file, string keyring, string summary, CancellationToken token)
    {
        var update = await BundlePublisher.DescribeAsync(file, keyring, summary, token);
        var snapshot = await store.ReadAsync(token);
        Validate(snapshot.Catalog);
        var existing = FindExisting(snapshot.Catalog, update);
        if (existing != null)
        {
            await store.VerifyAsync(existing.Update, token);
            return existing;
        }
        CheckTarget(snapshot.Catalog, update);
        await store.UploadAsync(file, update, token);
        await store.VerifyAsync(update, token);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            // Read again after the slow upload; another group may have been published meanwhile.
            snapshot = await store.ReadAsync(token);
            Validate(snapshot.Catalog);
            existing = FindExisting(snapshot.Catalog, update);
            if (existing != null) return existing;
            CheckTarget(snapshot.Catalog, update);
            var release = new CatalogRelease(update, _time.GetUtcNow());
            var releases = snapshot.Catalog.Releases.Append(release).ToList();
            var keep = releases.GroupBy(x => Group(x.Update)).SelectMany(g => g
                .OrderByDescending(x => Version.Parse(x.Update.TargetVersion))
                .ThenByDescending(x => x.Update.CreatedUtc).ThenBy(x => x.Update.BatchId, StringComparer.Ordinal).Take(3)).ToList();
            var retired = snapshot.Catalog.Retired.Concat(releases.Except(keep)
                .Select(x => new RetiredRelease(x, _time.GetUtcNow() + DeletionGrace))).ToList();
            if (await store.WriteAsync(new ReleaseCatalog { Releases = keep, Retired = retired }, snapshot.ETag, token))
                return release;
        }
        throw new IOException("Catalog changed repeatedly. Retry the same batch; the previous catalog is preserved.");
    }

    // Run only in the same single-writer worker as publication. Do not use multiple replicas.
    public async Task CleanupAsync(CancellationToken token)
    {
        var snapshot = await store.ReadAsync(token);
        Validate(snapshot.Catalog);
        var expired = snapshot.Catalog.Retired.Where(x => x.DeleteAfterUtc <= _time.GetUtcNow()).ToList();
        if (expired.Count == 0) return;
        foreach (var retired in expired) await store.DeleteAsync(retired.Release.Update, token);
        // If deletion or this write fails, the tombstone stays and deletion can safely be retried.
        if (!await store.WriteAsync(snapshot.Catalog with { Retired = snapshot.Catalog.Retired.Except(expired).ToList() }, snapshot.ETag, token))
            throw new IOException("Catalog changed during cleanup. Check that only one worker is running.");
    }

    private static CatalogRelease? FindExisting(ReleaseCatalog catalog, UpdatePublication update)
    {
        var existing = catalog.Releases.SingleOrDefault(x => x.Update.BatchId == update.BatchId);
        if (existing != null && (Group(existing.Update) != Group(update) || existing.Update.TargetVersion != update.TargetVersion ||
                                 existing.Update.MinimumVersion != update.MinimumVersion || existing.Update.SigningFingerprint != update.SigningFingerprint))
            throw new InvalidDataException("A batch already exists with different release metadata.");
        if (catalog.Retired.Any(x => x.Release.Update.BatchId == update.BatchId))
            throw new InvalidDataException("This batch has already been retired.");
        // A rebuild of identical inputs can have a new archive timestamp/signature. Keep the original verified object.
        return existing;
    }

    private static void CheckTarget(ReleaseCatalog catalog, UpdatePublication update)
    {
        if (catalog.Releases.Any(x => Group(x.Update) == Group(update) &&
            (Version.Parse(x.Update.TargetVersion) > Version.Parse(update.TargetVersion) ||
             x.Update.CreatedUtc > update.CreatedUtc)))
            throw new InvalidDataException("An older update cannot replace a newer published target.");
    }

    public static string Group(UpdatePublication update) => $"{update.Suite}/{update.ReleaseLine}/{update.Architecture}";

    public static void Validate(ReleaseCatalog catalog)
    {
        if (catalog.SchemaVersion != 1) throw new InvalidDataException("Unsupported catalog schema.");
        var all = catalog.Releases.Concat(catalog.Retired.Select(x => x.Release)).ToList();
        if (all.Select(x => x.Update.BatchId).Distinct().Count() != all.Count ||
            catalog.Releases.GroupBy(x => Group(x.Update)).Any(x => x.Count() > 3))
            throw new InvalidDataException("Duplicate batches or more than three active releases in a group.");
        foreach (var entry in all)
        {
            var u = entry.Update;
            if (!Regex.IsMatch(u.BatchId, "\\A[a-f0-9]{64}\\z") || !Regex.IsMatch(u.Sha256, "\\A[a-f0-9]{64}\\z") ||
                !Regex.IsMatch(u.Suite, "\\A[a-z][a-z0-9-]*\\z") || u.Architecture is not ("amd64" or "arm64") ||
                !Regex.IsMatch(u.ReleaseLine, "\\A[0-9]+\\.[0-9]+\\z") ||
                !Version.TryParse(u.TargetVersion, out var target) || !Version.TryParse(u.MinimumVersion, out var minimum) ||
                target.ToString(2) != u.ReleaseLine || minimum.ToString(2) != u.ReleaseLine || minimum > target || u.Size <= 0 ||
                string.IsNullOrWhiteSpace(u.Summary) || u.Summary.Length > 2000)
                throw new InvalidDataException("Invalid catalog release metadata.");
        }
    }
}
