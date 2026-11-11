using Aiursoft.AnduinOS.OfflineBundles.Core.Models;

namespace Aiursoft.AnduinOS.OfflineBundles.Core.Services;

public static class BundlePreview
{
    public static async Task<PreviewResult> PreviewAsync(string file, string keyring, string statusFile,
        string osReleaseFile, CancellationToken token = default)
    {
        var root = Path.Combine(Path.GetTempPath(), "offline-preview-" + Guid.NewGuid().ToString("N"));
        try
        {
            var verified = await BundleArchive.VerifyAsync(file, keyring, root, token);
            return await PreviewExtractedAsync(verified.Manifest, root, keyring, statusFile, osReleaseFile,
                new FileInfo(file).Length, token);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    public static async Task<PreviewResult> PreviewExtractedAsync(BundleManifest manifest, string root,
        string keyring, string statusFile, string osReleaseFile, long archiveBytes, CancellationToken token)
    {
        var os = DebianControl.ReadOsRelease(await File.ReadAllTextAsync(osReleaseFile, token));
        BundleBuilder.CheckOperatingSystem(os, manifest.ReleaseLine, manifest.Suite);
        var stages = new List<StagePlan>();
        var blockers = new List<string>();
        var work = Path.Combine(Path.GetTempPath(), "offline-plan-" + Guid.NewGuid().ToString("N"));
        try
        {
            var apt = new AptWorkspace(work, manifest.Architecture);
            await apt.SetSourcesAsync([new SourceDefinition
            {
                Uri = new Uri(Path.GetFullPath(Path.Combine(root, "repository")) + Path.DirectorySeparatorChar).AbsoluteUri,
                Suites = ["./"], Keyring = keyring
            }], token);
            await apt.SetStatusAsync(await File.ReadAllTextAsync(statusFile, token), token);
            try
            {
                if (await AptWorkspace.CompareAsync(os["VERSION_ID"], "gt", manifest.TargetVersion, token))
                {
                    throw new InvalidDataException("This bundle targets an older OS version; downgrade refused.");
                }
                if (manifest.RequiredInstallerFeatures.Count != 0)
                {
                    throw new InvalidDataException("This build tool cannot simulate the required installer capabilities: " +
                        string.Join(", ", manifest.RequiredInstallerFeatures));
                }
                foreach (var (stage, removals) in BundleBuilder.WithBootstrap(manifest.Stages, manifest.Bootstrap, manifest.AllowedRemovals))
                {
                    var changes = await apt.SimulateAsync(stage, removals, token);
                    stages.Add(new StagePlan(stage, changes));
                    await apt.AdvanceAsync(changes, token);
                }
                var finalPackages = DebianControl.Read(await File.ReadAllTextAsync(apt.StatusPath, token));
                foreach (var target in manifest.RequiredInstalledTargets)
                {
                    var installed = finalPackages.FirstOrDefault(p => p.GetValueOrDefault("Package") == target.Name &&
                        p.GetValueOrDefault("Architecture") == target.Architecture &&
                        p.GetValueOrDefault("Status")?.EndsWith(" ok installed", StringComparison.Ordinal) == true);
                    if (installed != null && await AptWorkspace.CompareAsync(installed["Version"], "lt", target.Version, token))
                    {
                        blockers.Add($"Required installed target not satisfied: {target.Name} {installed["Version"]} < {target.Version}.");
                    }
                }
                if (!finalPackages.Any(p => p.GetValueOrDefault("Package") == manifest.VersionPackage &&
                    p.GetValueOrDefault("Status")?.EndsWith(" ok installed", StringComparison.Ordinal) == true))
                {
                    blockers.Add("The operating-system version package is not installed.");
                }
            }
            catch (Exception error) when (error is InvalidDataException or InvalidOperationException)
            {
                blockers.Add(error.Message);
            }
            var changed = stages.Any(stage => stage.Changes.Count != 0);
            if (!changed && os["VERSION_ID"] != manifest.TargetVersion)
            {
                blockers.Add("The OS version does not match the target despite an empty APT plan.");
            }
            var unpacked = manifest.Files.Values.Sum(f => f.Size);
            return new PreviewResult(os["VERSION_ID"], manifest.TargetVersion, manifest.BatchId,
                blockers.Count != 0 ? "blocked" : changed ? "update-available" : "already-installed",
                stages, archiveBytes, unpacked, unpacked,
                "Not probed by the build tool; the installer must detect local snapshot capabilities.", blockers);
        }
        finally
        {
            if (Directory.Exists(work))
            {
                Directory.Delete(work, recursive: true);
            }
        }
    }
}
