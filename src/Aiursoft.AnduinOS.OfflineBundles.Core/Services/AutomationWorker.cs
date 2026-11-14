using System.Text.RegularExpressions;
using Aiursoft.AnduinOS.OfflineBundles.Core.Models;
using Amazon.S3;
using Amazon.Runtime;

namespace Aiursoft.AnduinOS.OfflineBundles.Core.Services;

public sealed class AutomationWorker(AutomationSettings settings, ICatalogStore store,
    Action<string> log, TimeProvider? time = null,
    Func<BuildProfile, string, SigningOptions, CancellationToken, Task<BuildResult>>? build = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly CatalogPublisher _publisher = new(store, time);
    private readonly Func<BuildProfile, string, SigningOptions, CancellationToken, Task<BuildResult>> _build =
        build ?? new BundleBuilder(log).BuildAsync;

    public async Task<int> RunAsync(bool once, CancellationToken token)
    {
        Validate(settings);
        var root = Path.GetFullPath(settings.StateDirectory);
        Directory.CreateDirectory(root);
        // Held for the entire lifetime, including waits. All invocations must share this local volume.
        await using var runLock = new FileStream(Path.Combine(root, "worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var stateFile = Path.Combine(root, "schedule.json");
        var state = File.Exists(stateFile) ? BundleJson.Read<AutomationState>(await File.ReadAllTextAsync(stateFile, token)) : new AutomationState();
        do
        {
            var failed = false;
            foreach (var job in settings.Jobs)
            {
                var schedule = state.Jobs.GetValueOrDefault(job.Id) ?? new JobSchedule(DateTimeOffset.MinValue);
                if (schedule.NextAttemptUtc > _time.GetUtcNow()) continue;
                var started = _time.GetUtcNow();
                var output = Path.Combine(root, job.Id);
                Directory.CreateDirectory(output);
                try
                {
                    var profile = ProfileReader.Read(job.Profile, job.Inputs);
                    if (schedule.PendingFile == null)
                    {
                        CleanInterruptedBuilds(output);
                        log($"{job.Id}: building the latest available target.");
                        var result = await _build(profile, output, settings.Signing, token);
                        if (Path.GetDirectoryName(Path.GetFullPath(result.File)) != output)
                            throw new InvalidDataException("Build output is outside the job directory.");
                        schedule = schedule with { PendingFile = Path.GetFileName(result.File) };
                        state.Jobs[job.Id] = schedule;
                        await SaveAsync(stateFile, state, token);
                    }
                    if (Path.GetFileName(schedule.PendingFile) != schedule.PendingFile || !schedule.PendingFile.EndsWith(".atgz", StringComparison.Ordinal))
                        throw new InvalidDataException("Invalid pending archive path.");
                    var file = Path.Combine(output, schedule.PendingFile);
                    // Resume the same bytes after an upload/catalog failure, before asking APT for a newer batch.
                    var release = await _publisher.PublishAsync(file, settings.Signing.TrustedKeyring, job.Summary, token);
                    log($"{job.Id}: published {release.Update.TargetVersion}, batch {release.Update.BatchId}.");
                    state.Jobs[job.Id] = new JobSchedule(started.AddHours(settings.IntervalHours));
                    await SaveAsync(stateFile, state, token);
                    await PruneLocalAsync(output, token);
                }
                catch (Exception error) when (!token.IsCancellationRequested && IsJobFailure(error))
                {
                    failed = true;
                    // Do not print connection strings, credentials, signed URLs or request bodies.
                    log($"{job.Id}: failed ({error.GetType().Name}); retry in {settings.RetryMinutes} minutes. Previous releases remain available.");
                    state.Jobs[job.Id] = schedule with { NextAttemptUtc = _time.GetUtcNow().AddMinutes(settings.RetryMinutes) };
                    await SaveAsync(stateFile, state, token);
                }
            }
            try { await _publisher.CleanupAsync(token); }
            catch (Exception error) when (!token.IsCancellationRequested && IsJobFailure(error))
            {
                failed = true;
                log($"Retention cleanup failed ({error.GetType().Name}); it will be retried. No active release was selected for deletion.");
            }
            if (once) return failed ? 1 : 0;
            var next = settings.Jobs.Min(j => state.Jobs.GetValueOrDefault(j.Id)?.NextAttemptUtc ?? _time.GetUtcNow());
            var delay = next - _time.GetUtcNow();
            // Also revisit tombstones at least hourly, without rebuilding successful groups early.
            if (delay > TimeSpan.FromHours(1)) delay = TimeSpan.FromHours(1);
            if (delay < TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
            await Task.Delay(delay, _time, token);
        } while (true);
    }

    private static bool IsJobFailure(Exception error) => error is IOException or InvalidOperationException or ArgumentException or
        UnauthorizedAccessException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception or AmazonS3Exception or AmazonClientException or HttpRequestException or OperationCanceledException;

    private static async Task SaveAsync(string file, AutomationState state, CancellationToken token)
    {
        var temporary = file + ".tmp";
        await File.WriteAllTextAsync(temporary, BundleJson.Write(state), token);
        File.Move(temporary, file, overwrite: true);
    }

    private async Task PruneLocalAsync(string output, CancellationToken token)
    {
        var catalog = (await store.ReadAsync(token)).Catalog;
        CatalogPublisher.Validate(catalog);
        var keep = catalog.Releases.Select(x => x.Update.BatchId[..16]).ToHashSet(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(output, "AnduinOS-*.atgz"))
        {
            var match = Regex.Match(Path.GetFileName(file), "-([a-f0-9]{16})\\.atgz$");
            if (!match.Success || keep.Contains(match.Groups[1].Value)) continue;
            File.Delete(file);
            File.Delete(file + ".build.json");
            if (Directory.Exists(file + ".inputs")) Directory.Delete(file + ".inputs", recursive: true);
        }
    }

    private static void CleanInterruptedBuilds(string output)
    {
        // The job owns this directory and the lifetime lock excludes another builder using it.
        foreach (var directory in Directory.EnumerateDirectories(output, ".build-*"))
            if (Regex.IsMatch(Path.GetFileName(directory), "\\A\\.build-[a-f0-9]{32}\\z")) Directory.Delete(directory, recursive: true);
        foreach (var directory in Directory.EnumerateDirectories(output, "AnduinOS-*.atgz.inputs"))
        {
            var archive = directory[..^7];
            if (File.Exists(archive)) continue;
            Directory.Delete(directory, recursive: true);
            File.Delete(archive + ".build.json");
        }
    }

    public static void Validate(AutomationSettings value)
    {
        if (!Uri.TryCreate(value.ServiceUrl, UriKind.Absolute, out var url) || url.Scheme != "https" ||
            url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0 || string.IsNullOrWhiteSpace(value.Bucket))
            throw new ArgumentException("Configure an HTTPS R2 endpoint without credentials, query or fragment, and a bucket.");
        if (!Path.IsPathFullyQualified(value.StateDirectory) || value.IntervalHours < 1 || value.RetryMinutes < 1 ||
            value.Jobs.Count == 0 || value.Jobs.Select(x => x.Id).Distinct().Count() != value.Jobs.Count)
            throw new ArgumentException("Provide an absolute state directory, positive intervals and uniquely named jobs.");
        foreach (var job in value.Jobs)
            if (!Regex.IsMatch(job.Id, "\\A[a-z0-9][a-z0-9-]{0,63}\\z") ||
                !Path.IsPathFullyQualified(job.Profile) || !Path.IsPathFullyQualified(job.Inputs) ||
                string.IsNullOrWhiteSpace(job.Summary) || job.Summary.Length > 2000)
                throw new ArgumentException("Jobs require a safe ID, absolute profile/input paths and a short summary.");
        if (!Path.IsPathFullyQualified(value.Signing.Home) || !Path.IsPathFullyQualified(value.Signing.TrustedKeyring) || string.IsNullOrWhiteSpace(value.Signing.Key))
            throw new ArgumentException("Configure absolute signing-home/keyring paths and a signing key.");
    }
}
