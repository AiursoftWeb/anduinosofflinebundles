using System.Formats.Tar;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Aiursoft.AnduinOS.OfflineBundles.Core.Models;

namespace Aiursoft.AnduinOS.OfflineBundles.Core.Services;

public sealed class BundleBuilder(Action<string>? progress = null)
{
    public async Task<BuildResult> BuildAsync(BuildProfile profile, string outputDirectory, SigningOptions signing,
        CancellationToken token = default)
    {
        ProfileReader.Validate(profile);
        outputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var work = Path.Combine(outputDirectory, ".build-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var inputs = Path.Combine(work, "inputs");
            Directory.CreateDirectory(inputs);
            var baselines = new List<BaselineDefinition>();
            foreach (var baseline in profile.Baselines)
            {
                var status = Path.Combine(inputs, baseline.Name + ".status");
                var os = Path.Combine(inputs, baseline.Name + ".os-release");
                File.Copy(baseline.StatusFile, status);
                File.Copy(baseline.OsReleaseFile, os);
                baselines.Add(baseline with { StatusFile = status, OsReleaseFile = os });
            }
            profile = profile with { Baselines = baselines };
            var apt = new AptWorkspace(Path.Combine(work, "apt"), profile.Architecture);
            progress?.Invoke("Verifying source indexes.");
            await apt.SetSourcesAsync(profile.Sources, token);
            var sourceIndexes = await InventoryAsync(apt.ListsPath, token);
            foreach (var name in sourceIndexes.Keys)
            {
                var target = Path.Combine(inputs, "source-indexes", name);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(apt.ListsPath, name), target);
            }
            for (var index = 0; index < profile.Sources.Count; index++)
            {
                File.Copy(profile.Sources[index].Keyring, Path.Combine(inputs, $"source-{index}.gpg"));
            }
            var recordedProfile = profile with
            {
                Baselines = profile.Baselines.Select(b => b with
                {
                    StatusFile = Path.GetFileName(b.StatusFile), OsReleaseFile = Path.GetFileName(b.OsReleaseFile)
                }).ToList(),
                Sources = profile.Sources.Select((s, i) => s with { Keyring = $"source-{i}.gpg" }).ToList()
            };
            await File.WriteAllTextAsync(Path.Combine(inputs, "profile.json"), BundleJson.Write(recordedProfile), token);
            var allPackages = new Dictionary<string, PackageIdentity>(StringComparer.Ordinal);
            var baselinePlans = new List<BaselinePlan>();
            foreach (var baseline in profile.Baselines)
            {
                progress?.Invoke($"Resolving baseline {baseline.Name}.");
                var os = DebianControl.ReadOsRelease(await File.ReadAllTextAsync(baseline.OsReleaseFile, token));
                CheckOperatingSystem(os, profile.ReleaseLine, profile.Suite);
                await apt.SetStatusAsync(await File.ReadAllTextAsync(baseline.StatusFile, token), token);
                var stages = new List<StagePlan>();
                foreach (var (stage, removals) in WithBootstrap(profile.Stages, profile.Bootstrap, profile.AllowedRemovals))
                {
                    var changes = await apt.SimulateAsync(stage, removals, token);
                    foreach (var change in changes.Where(c => c.Action == "install"))
                    {
                        AddPackage(allPackages, new PackageIdentity(change.Name, change.After!, change.Architecture));
                    }
                    stages.Add(new StagePlan(stage, changes));
                    await apt.AdvanceAsync(changes, token);
                }
                baselinePlans.Add(new BaselinePlan(baseline.Name, os["VERSION_ID"],
                    await BundleJson.HashAsync(baseline.StatusFile, token), stages));
            }
            // Resolve the version package against the frozen indexes, without inheriting a baseline's installed candidate.
            await apt.SetStatusAsync(string.Empty, token);
            var versionMetadata = await apt.CandidateAsync(profile.VersionPackage, token);
            var versionIdentity = DebianControl.Identity(versionMetadata);
            AddPackage(allPackages, versionIdentity);
            if (profile.Bootstrap != null)
            {
                var updater = DebianControl.Identity(await apt.CandidateAsync(profile.Bootstrap.Package, token));
                if (!await AptWorkspace.CompareAsync(updater.Version, "ge", profile.Bootstrap.MinimumVersion, token))
                {
                    throw new InvalidDataException("The source updater is older than the required bootstrap version.");
                }
                AddPackage(allPackages, updater);
            }
            var downloads = Path.Combine(work, "downloads");
            progress?.Invoke($"Downloading {allPackages.Count} packages from the verified indexes.");
            await apt.DownloadAsync(allPackages.Values.OrderBy(p => p.Key, StringComparer.Ordinal), downloads, token);
            var root = Path.Combine(work, "bundle");
            var repository = Path.Combine(root, "repository");
            var pool = Path.Combine(repository, "pool");
            Directory.CreateDirectory(pool);
            var packages = new List<BundlePackage>();
            string? versionDeb = null;
            foreach (var file in Directory.EnumerateFiles(downloads, "*.deb").Order(StringComparer.Ordinal))
            {
                var controlResult = await NativeCommand.RunAsync("dpkg-deb", ["--field", file], cancellationToken: token);
                var control = DebianControl.Read(controlResult.Output).Single();
                var identity = DebianControl.Identity(control);
                if (!allPackages.TryGetValue(identity.Key, out var expected) || expected != identity)
                {
                    throw new InvalidDataException("The downloaded package was not part of the resolved plan.");
                }
                var index = await apt.MetadataAsync(identity.Name, identity.Version, token);
                var digest = new FileDigest(new FileInfo(file).Length, await BundleJson.HashAsync(file, token));
                if (index.GetValueOrDefault("SHA256") != digest.Sha256 || index.GetValueOrDefault("Size") != digest.Size.ToString(CultureInfo.InvariantCulture))
                {
                    throw new InvalidDataException($"Package differs from its authenticated index: {identity.Name}");
                }
                var relative = $"repository/pool/{digest.Sha256}.deb";
                var destination = Path.Combine(root, relative);
                File.Move(file, destination);
                packages.Add(new BundlePackage(identity, relative, digest));
                if (identity == versionIdentity)
                {
                    versionDeb = destination;
                }
            }
            if (packages.Count != allPackages.Count || versionDeb == null)
            {
                throw new InvalidDataException("Some resolved packages were not downloaded.");
            }
            packages = packages.OrderBy(p => p.Identity.Key, StringComparer.Ordinal).ToList();
            var targetOs = await ReadVersionAsync(versionDeb, profile.VersionFile, work, token);
            CheckOperatingSystem(targetOs, profile.ReleaseLine, profile.Suite);
            var targetVersion = targetOs["VERSION_ID"];
            var created = DateTimeOffset.UtcNow;
            progress?.Invoke("Generating the signed local APT repository.");
            await WriteRepositoryAsync(repository, profile.Architecture, profile.Suite, created, signing, token);
            var files = await InventoryAsync(root, token);
            var targets = allPackages.Values.OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
            var identityText = BundleJson.Write(new
            {
                profile.ReleaseLine, profile.Suite, profile.Architecture, TargetVersion = targetVersion,
                profile.VersionPackage, profile.VersionFile, profile.Stages, profile.Bootstrap,
                AllowedRemovals = profile.AllowedRemovals.Order(StringComparer.Ordinal).ToArray(),
                Packages = packages,
                Baselines = baselinePlans
            });
            var batchId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identityText)));
            var manifest = new BundleManifest
            {
                ReleaseLine = profile.ReleaseLine, Suite = profile.Suite, Architecture = profile.Architecture,
                TargetVersion = targetVersion, BatchId = batchId, CreatedUtc = created,
                VersionPackage = profile.VersionPackage, VersionFile = profile.VersionFile,
                Stages = profile.Stages, AllowedRemovals = profile.AllowedRemovals, Bootstrap = profile.Bootstrap,
                RequiredFeatures = profile.Bootstrap == null
                    ? ["apt-repository-v1", "sequential-apt-plans-v1"]
                    : ["apt-repository-v1", "sequential-apt-plans-v1", "updater-bootstrap-v1"],
                Baselines = baselinePlans, Packages = packages, RequiredInstalledTargets = targets,
                SourceIndexes = sourceIndexes, Files = files
            };
            BundleArchive.ValidateManifest(manifest);
            await File.WriteAllTextAsync(Path.Combine(root, "manifest.json"), BundleJson.Write(manifest), token);
            await BundleSigning.SignAsync(Path.Combine(root, "manifest.json"), Path.Combine(root, "manifest.asc"), signing, false, token);
            var destinationFile = Path.Combine(outputDirectory, $"AnduinOS-{targetVersion}-{profile.Architecture}-{batchId[..16]}.atgz");
            if (File.Exists(destinationFile))
            {
                var previous = await BundleArchive.VerifyAsync(destinationFile, signing.TrustedKeyring, token: token);
                if (previous.Manifest.BatchId != batchId)
                {
                    throw new InvalidDataException("An output name collision was detected; existing output was preserved.");
                }
                progress?.Invoke("Reusing the already verified immutable bundle.");
            }
            else
            {
                var candidate = Path.Combine(work, "candidate.atgz");
                progress?.Invoke("Writing and verifying the cumulative archive.");
                await BundleArchive.WriteAsync(root, candidate, manifest, token);
                await BundleArchive.VerifyAsync(candidate, signing.TrustedKeyring, token: token);
                // Resolve every declared baseline using only the new local repository.
                // This catches missing dependencies and targets before the candidate becomes an output.
                foreach (var baseline in profile.Baselines)
                {
                    var preview = await BundlePreview.PreviewExtractedAsync(manifest, root, signing.TrustedKeyring,
                        baseline.StatusFile, baseline.OsReleaseFile, new FileInfo(candidate).Length, token);
                    if (preview.Blockers.Count != 0)
                    {
                        throw new InvalidDataException($"Local closure failed for {baseline.Name}: {string.Join("; ", preview.Blockers)}");
                    }
                }
                var nativeVersions = new SortedDictionary<string, string>(StringComparer.Ordinal);
                foreach (var tool in new[] { "apt-get", "apt-ftparchive", "dpkg-deb", "gpg", "gpgv" })
                {
                    var version = await NativeCommand.RunAsync(tool, ["--version"], cancellationToken: token);
                    nativeVersions[tool] = version.Output.Split('\n')[0];
                }
                var receipt = new
                {
                    Manifest = manifest,
                    Sources = profile.Sources.Select(s => new { s.Uri, s.Suites, s.Components }),
                    Builder = typeof(BundleBuilder).Assembly.GetName().Version?.ToString(),
                    Runtime = Environment.Version.ToString(),
                    NativeVersions = nativeVersions,
                    CapturedInputs = Path.GetFileName(destinationFile) + ".inputs",
                    Validation = "Signed archive, authenticated package hashes and local APT baseline plans; no installation performed."
                };
                Directory.Move(inputs, destinationFile + ".inputs");
                await File.WriteAllTextAsync(destinationFile + ".build.json", BundleJson.Write(receipt), token);
                File.Move(candidate, destinationFile);
            }
            return new BuildResult(destinationFile,
                new FileDigest(new FileInfo(destinationFile).Length, await BundleJson.HashAsync(destinationFile, token)),
                batchId, targetVersion, packages.Count);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    public static IEnumerable<(UpdateStage Stage, IEnumerable<string> Removals)> WithBootstrap(
        IEnumerable<UpdateStage> stages, UpdaterBootstrap? bootstrap, IReadOnlyCollection<string> allowedRemovals)
    {
        if (bootstrap != null)
        {
            // Bootstrap cannot borrow the later system migration's removal permission.
            yield return (new UpdateStage { Operation = "install", Packages = [bootstrap.Package] }, []);
        }
        foreach (var stage in stages)
        {
            yield return (stage, allowedRemovals);
        }
    }

    public static void CheckOperatingSystem(IReadOnlyDictionary<string, string> os, string line, string suite)
    {
        if (os.GetValueOrDefault("ID") != "anduinos" ||
            os.GetValueOrDefault("UBUNTU_CODENAME") != suite ||
            os.GetValueOrDefault("VERSION_ID") is not { } version ||
            !version.StartsWith(line + ".", StringComparison.Ordinal) ||
            version.Any(c => !char.IsAsciiDigit(c) && c != '.'))
        {
            throw new InvalidDataException("The operating system is outside the bundle's supported release line or suite.");
        }
    }

    private static void AddPackage(IDictionary<string, PackageIdentity> packages, PackageIdentity package)
    {
        if (packages.TryGetValue(package.Key, out var existing) && existing != package)
        {
            throw new InvalidDataException($"Different versions required for {package.Key}; review the migration plan.");
        }
        packages[package.Key] = package;
    }

    private static async Task<Dictionary<string, string>> ReadVersionAsync(string deb, string versionFile, string work, CancellationToken token)
    {
        var tarPath = Path.Combine(work, "version.tar");
        await using (var output = File.Create(tarPath))
        {
            await NativeCommand.RunAsync("dpkg-deb", ["--fsys-tarfile", deb], output: output, cancellationToken: token);
        }
        await using var input = File.OpenRead(tarPath);
        await using var tar = new TarReader(input);
        TarEntry? entry;
        while ((entry = await tar.GetNextEntryAsync(cancellationToken: token)) != null)
        {
            if (entry.Name.TrimStart('.', '/') != versionFile)
            {
                continue;
            }
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.Length > 65536)
            {
                throw new InvalidDataException("The version package must contain a bounded regular os-release file.");
            }
            using var reader = new StreamReader(entry.DataStream!);
            return DebianControl.ReadOsRelease(await reader.ReadToEndAsync(token));
        }
        throw new InvalidDataException($"The version package does not contain {versionFile}.");
    }

    private static async Task WriteRepositoryAsync(string repository, string architecture, string suite,
        DateTimeOffset created, SigningOptions signing, CancellationToken token)
    {
        await using (var output = File.Create(Path.Combine(repository, "Packages")))
        {
            await NativeCommand.RunAsync("apt-ftparchive", ["packages", "pool"], repository, output: output, cancellationToken: token);
        }
        var packages = Path.Combine(repository, "Packages");
        var release = $"Origin: AnduinOS Offline\nLabel: AnduinOS Offline\nSuite: {suite}\nCodename: {suite}\n" +
            $"Architectures: {architecture} all\nDate: {created.ToString("ddd, dd MMM yyyy HH:mm:ss +0000", CultureInfo.InvariantCulture)}\n" +
            "Description: Immutable cumulative offline update repository\nSHA256:\n" +
            $" {await BundleJson.HashAsync(packages, token)} {new FileInfo(packages).Length} Packages\n";
        var releasePath = Path.Combine(repository, "Release");
        await File.WriteAllTextAsync(releasePath, release, token);
        await BundleSigning.SignAsync(releasePath, Path.Combine(repository, "InRelease"), signing, true, token);
        await BundleSigning.SignAsync(releasePath, Path.Combine(repository, "Release.gpg"), signing, false, token);
    }

    private static async Task<SortedDictionary<string, FileDigest>> InventoryAsync(string root, CancellationToken token)
    {
        var files = new SortedDictionary<string, FileDigest>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (name == "lock" || name.StartsWith("partial/", StringComparison.Ordinal) || name.StartsWith("auxfiles/", StringComparison.Ordinal))
            {
                continue;
            }
            files.Add(name, new FileDigest(new FileInfo(file).Length, await BundleJson.HashAsync(file, token)));
        }
        return files;
    }
}
