using System.Formats.Tar;
using System.IO.Compression;
using Aiursoft.AnduinOS.OfflineBundles.Core.Models;
using Aiursoft.AnduinOS.OfflineBundles.Core.Services;

namespace Aiursoft.AnduinOS.OfflineBundles.Tests;

[TestClass]
public class BundleWorkflowTests
{
    private BundleFixture _fixture = null!;

    [TestInitialize]
    public async Task Initialize()
    {
        _fixture = new BundleFixture();
        await _fixture.InitializeAsync();
    }

    [TestCleanup]
    public void Cleanup() => _fixture.Dispose();

    [TestMethod]
    public async Task OneBundlePlansOldIntermediateAndCurrentWithoutHostMutation()
    {
        var hostStatus = await BundleJson.HashAsync("/var/lib/dpkg/status");
        var result = await BuildAsync();
        var verified = await BundleArchive.VerifyAsync(result.File, _fixture.Keyring);
        Assert.AreEqual("2.0.5", verified.Manifest.TargetVersion);
        Assert.HasCount(3, verified.Manifest.Packages);
        foreach (var baseline in _fixture.Profile.Baselines)
        {
            var before = await BundleJson.HashAsync(baseline.StatusFile);
            var preview = await BundlePreview.PreviewAsync(result.File, _fixture.Keyring, baseline.StatusFile, baseline.OsReleaseFile);
            Assert.AreEqual("update-available", preview.State);
            Assert.IsEmpty(preview.Blockers);
            Assert.AreEqual(before, await BundleJson.HashAsync(baseline.StatusFile));
        }
        var current = _fixture.CreateBaseline("current", "2.0.5", "2.0.5-1", "5");
        var repeated = await BundlePreview.PreviewAsync(result.File, _fixture.Keyring, current.StatusFile, current.OsReleaseFile);
        Assert.AreEqual("already-installed", repeated.State, BundleJson.Write(repeated));
        Assert.AreEqual(hostStatus, await BundleJson.HashAsync("/var/lib/dpkg/status"));
    }

    [TestMethod]
    public async Task OldVimConstraintCannotMasqueradeAsCompletedUpdate()
    {
        var result = await BuildAsync();
        var baseline = _fixture.CreateBaseline("user-vim", "2.0.0", "2.0.0-1", "1",
            BundleFixture.Status("vim", "1", "vim-common (= 1)"));
        var preview = await BundlePreview.PreviewAsync(result.File, _fixture.Keyring, baseline.StatusFile, baseline.OsReleaseFile);
        Assert.AreEqual("blocked", preview.State);
        Assert.IsTrue(preview.Blockers.Any(b => b.Contains("vim", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task UserAppsOutsideTheUpdateArePreserved()
    {
        var result = await BuildAsync();
        var baseline = _fixture.CreateBaseline("docker", "2.0.3", "2.0.3-1", "3", BundleFixture.Status("docker-ce", "29.9.3"));
        var preview = await BundlePreview.PreviewAsync(result.File, _fixture.Keyring, baseline.StatusFile, baseline.OsReleaseFile);
        Assert.AreEqual("update-available", preview.State);
        Assert.IsFalse(preview.Stages.SelectMany(s => s.Changes).Any(c => c.Name == "docker-ce"));
    }

    [TestMethod]
    public async Task HoldAndMissingDependenciesAreBlocked()
    {
        var result = await BuildAsync();
        var baseline = _fixture.CreateBaseline("held", "2.0.0", "2.0.0-1", "1", hold: true);
        var preview = await BundlePreview.PreviewAsync(result.File, _fixture.Keyring, baseline.StatusFile, baseline.OsReleaseFile);
        Assert.AreEqual("blocked", preview.State);
        await _fixture.AddPackageAsync("anduinos-core-system", "2.0.6-1", "missing-library (>= 1)", osVersion: "2.0.6");
        await _fixture.PublishRepositoryAsync();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await BuildAsync());
    }

    [TestMethod]
    public async Task RebuildingDoesNotReplaceAnExistingBatch()
    {
        var first = await BuildAsync();
        var second = await BuildAsync();
        Assert.AreEqual(first.BatchId, second.BatchId);
        Assert.AreEqual(first.Content, second.Content);
    }

    [TestMethod]
    public async Task SameOsVersionWithNewPackageUpdatesIsANewBatch()
    {
        var first = await BuildAsync();
        await _fixture.AddPackageAsync("base-library", "6");
        await _fixture.PublishRepositoryAsync();
        var second = await BuildAsync();
        Assert.AreEqual(first.TargetVersion, second.TargetVersion);
        Assert.AreNotEqual(first.BatchId, second.BatchId);
        var current = _fixture.CreateBaseline("patched", "2.0.5", "2.0.5-1", "5");
        var preview = await BundlePreview.PreviewAsync(second.File, _fixture.Keyring, current.StatusFile, current.OsReleaseFile);
        Assert.AreEqual("update-available", preview.State);
    }

    [TestMethod]
    public async Task InvalidSignatureAndCorruptPayloadAreRejected()
    {
        var result = await BuildAsync();
        var signature = await RewriteAsync(result.File, "bad-signature.atgz", "manifest.asc", bytes =>
        {
            bytes[bytes.Length / 2] ^= 1;
            return bytes;
        });
        await Assert.ThrowsAsync<Exception>(() => BundleArchive.VerifyAsync(signature, _fixture.Keyring));
        var verified = await BundleArchive.VerifyAsync(result.File, _fixture.Keyring);
        var corrupt = await RewriteAsync(result.File, "corrupt.atgz", verified.Manifest.Packages[0].Path,
            bytes => { bytes[^1] ^= 1; return bytes; });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => BundleArchive.VerifyAsync(corrupt, _fixture.Keyring));
    }

    [TestMethod]
    public async Task MissingPayloadAndDuplicateEntryAreRejected()
    {
        var result = await BuildAsync();
        var verified = await BundleArchive.VerifyAsync(result.File, _fixture.Keyring);
        var missing = await RewriteAsync(result.File, "missing.atgz", verified.Manifest.Packages[0].Path, _ => null);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => BundleArchive.VerifyAsync(missing, _fixture.Keyring));
        var duplicate = await RewriteAsync(result.File, "duplicate.atgz", "manifest.asc", bytes => bytes, duplicate: true);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => BundleArchive.VerifyAsync(duplicate, _fixture.Keyring));
    }

    [TestMethod]
    public async Task SignedUnknownCapabilityAndPathTraversalAreRejected()
    {
        var result = await BuildAsync();
        var directory = Path.Combine(_fixture.Root, "resign");
        var original = await BundleArchive.VerifyAsync(result.File, _fixture.Keyring, directory);
        var manifest = original.Manifest with { RequiredFeatures = ["apt-repository-v1", "unknown-required-capability"] };
        var path = Path.Combine(directory, "manifest.json");
        await File.WriteAllTextAsync(path, BundleJson.Write(manifest));
        await BundleSigning.SignAsync(path, Path.Combine(directory, "manifest.asc"), _fixture.Signing, false, default);
        var output = Path.Combine(_fixture.Root, "future.atgz");
        await BundleArchive.WriteAsync(directory, output, manifest, default);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => BundleArchive.VerifyAsync(output, _fixture.Keyring));
        var traversal = await RewriteAsync(result.File, "traversal.atgz", "manifest.asc", bytes => bytes, extraPath: "../escape");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => BundleArchive.VerifyAsync(traversal, _fixture.Keyring));
        Assert.IsFalse(File.Exists(Path.Combine(_fixture.Root, "escape")));
    }

    [TestMethod]
    public async Task WrongSuiteAndForeignArchitectureAreRejectedBeforeUpdating()
    {
        var result = await BuildAsync();
        var baseline = _fixture.CreateBaseline("wrong", "2.0.0", "2.0.0-1", "1");
        await File.AppendAllTextAsync(baseline.StatusFile, BundleFixture.Status("other-arch", "1").Replace("Architecture: all", "Architecture: arm64"));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => BundlePreview.PreviewAsync(result.File, _fixture.Keyring, baseline.StatusFile, baseline.OsReleaseFile));
        await File.WriteAllTextAsync(baseline.OsReleaseFile, "ID=anduinos\nVERSION_ID=2.0.0\nUBUNTU_CODENAME=stonking\n");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => BundlePreview.PreviewAsync(result.File, _fixture.Keyring, baseline.StatusFile, baseline.OsReleaseFile));
    }

    private Task<BuildResult> BuildAsync() => new BundleBuilder().BuildAsync(_fixture.Profile, _fixture.Output, _fixture.Signing);

    [TestMethod]
    public async Task OlderOsBundleDoesNotTouchANewerSystem()
    {
        var result = await BuildAsync();
        var baseline = _fixture.CreateBaseline("future-os", "2.0.6", "2.0.6-1", "6");
        var preview = await BundlePreview.PreviewAsync(result.File, _fixture.Keyring, baseline.StatusFile, baseline.OsReleaseFile);
        Assert.AreEqual("blocked", preview.State);
        Assert.IsEmpty(preview.Stages);
        Assert.IsTrue(preview.Blockers.Any(b => b.Contains("downgrade refused", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task UnexpectedRemovalIsBlockedAndNewerUserPackagesAreNotDowngraded()
    {
        await _fixture.AddPackageAsync("replacement", "1", conflicts: "user-app");
        await _fixture.PublishRepositoryAsync();
        var profile = _fixture.Profile with
        {
            Stages = [new UpdateStage { Operation = "install", Packages = ["replacement"] }, new UpdateStage { Operation = "dist-upgrade" }]
        };
        var result = await new BundleBuilder().BuildAsync(profile, _fixture.Output, _fixture.Signing);
        var blocked = _fixture.CreateBaseline("conflict", "2.0.0", "2.0.0-1", "1", BundleFixture.Status("user-app", "1"));
        var preview = await BundlePreview.PreviewAsync(result.File, _fixture.Keyring, blocked.StatusFile, blocked.OsReleaseFile);
        Assert.AreEqual("blocked", preview.State);
        Assert.IsTrue(preview.Blockers.Any(b => b.Contains("Unapproved package removal: user-app", StringComparison.Ordinal)));
        var newer = _fixture.CreateBaseline("newer", "2.0.0", "2.0.0-1", "6");
        var preserved = await BundlePreview.PreviewAsync(result.File, _fixture.Keyring, newer.StatusFile, newer.OsReleaseFile);
        Assert.AreEqual("update-available", preserved.State);
        Assert.IsFalse(preserved.Stages.SelectMany(s => s.Changes).Any(c => c.Name == "base-library"));
    }

    [TestMethod]
    public async Task AuthenticatedButContradictoryIndexIsRejected()
    {
        var result = await BuildAsync();
        var directory = Path.Combine(_fixture.Root, "contradictory");
        var original = await BundleArchive.VerifyAsync(result.File, _fixture.Keyring, directory);
        var packages = original.Manifest.Packages.ToList();
        packages[0] = packages[0] with { Identity = packages[0].Identity with { Version = "99" } };
        var manifest = original.Manifest with { Packages = packages, RequiredInstalledTargets = packages.Select(p => p.Identity).ToList() };
        var file = await ResignAsync(directory, manifest, "contradictory.atgz");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => BundleArchive.VerifyAsync(file, _fixture.Keyring));
    }

    [TestMethod]
    public async Task RepositorySignatureIsCheckedEvenWithAValidOuterSignature()
    {
        var result = await BuildAsync();
        var directory = Path.Combine(_fixture.Root, "repository-signature");
        var original = await BundleArchive.VerifyAsync(result.File, _fixture.Keyring, directory);
        var signature = Path.Combine(directory, "repository/Release.gpg");
        await File.WriteAllTextAsync(signature, "invalid repository signature");
        var files = new SortedDictionary<string, FileDigest>(original.Manifest.Files, StringComparer.Ordinal)
        {
            ["repository/Release.gpg"] = new(new FileInfo(signature).Length, await BundleJson.HashAsync(signature))
        };
        var file = await ResignAsync(directory, original.Manifest with { Files = files }, "repository-signature.atgz");
        await Assert.ThrowsAsync<Exception>(() => BundleArchive.VerifyAsync(file, _fixture.Keyring));
    }

    [TestMethod]
    public async Task BootstrapIncludesUpdaterDependenciesAndRemainsAnAuthenticatedBoundary()
    {
        await _fixture.AddPackageAsync("anduinos-offline-updater", "2.1.0-2", "bootstrap-library (>= 2)");
        await _fixture.AddPackageAsync("bootstrap-library", "2");
        await _fixture.PublishRepositoryAsync();
        var plain = await BuildAsync();
        var profile = _fixture.Profile with { Bootstrap = new UpdaterBootstrap { MinimumVersion = "2.1.0-2" } };
        var result = await new BundleBuilder().BuildAsync(profile, _fixture.Output, _fixture.Signing);
        var root = Path.Combine(_fixture.Root, "bootstrap-extracted");
        var verified = await BundleArchive.VerifyAsync(result.File, _fixture.Keyring, root);
        Assert.AreNotEqual(plain.BatchId, result.BatchId);
        Assert.Contains("updater-bootstrap-v1", verified.Manifest.RequiredFeatures);
        Assert.IsTrue(verified.Manifest.Packages.Any(p => p.Identity.Name == "bootstrap-library"));
        foreach (var baseline in profile.Baselines)
        {
            var preview = await BundlePreview.PreviewAsync(result.File, _fixture.Keyring, baseline.StatusFile, baseline.OsReleaseFile);
            Assert.IsEmpty(preview.Blockers);
            Assert.IsTrue(preview.Stages[0].Changes.Any(c => c.Name == "anduinos-offline-updater"));
        }
        var future = verified.Manifest with { RequiredInstallerFeatures = ["future-installer-v2"] };
        var futureFile = await ResignAsync(root, future, "future-installer.atgz");
        await BundleArchive.VerifyAsync(futureFile, _fixture.Keyring);
        var b = profile.Baselines[0];
        var rejected = await BundlePreview.PreviewAsync(futureFile, _fixture.Keyring, b.StatusFile, b.OsReleaseFile);
        Assert.AreEqual("blocked", rejected.State);
        Assert.IsEmpty(rejected.Stages);
        var invalid = verified.Manifest with { Bootstrap = new UpdaterBootstrap { MinimumVersion = "99" } };
        var invalidFile = await ResignAsync(root, invalid, "too-old-updater.atgz");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => BundleArchive.VerifyAsync(invalidFile, _fixture.Keyring));
        Assert.ThrowsExactly<InvalidDataException>(() => BundleArchive.ValidateManifest(verified.Manifest with
        {
            Bootstrap = new UpdaterBootstrap { Package = "arbitrary-command", MinimumVersion = "1" }
        }));
        // Explicit opt-in exports real builder output for cross-language acceptance,
        // never keys or artifacts to the source tree or normal CI output.
        if (Environment.GetEnvironmentVariable("OFFLINE_CONTRACT_EXPORT") is { Length: > 0 } export)
        {
            Directory.CreateDirectory(export);
            foreach (var (file, name) in new[] { (plain.File, "legacy.atgz"), (result.File, "bootstrap.atgz"),
                         (futureFile, "future.atgz"), (invalidFile, "invalid-minimum.atgz"), (_fixture.Keyring, "trusted.gpg"),
                         (b.StatusFile, "baseline.status"), (b.OsReleaseFile, "baseline.os-release") })
            {
                File.Copy(file, Path.Combine(export, name), overwrite: true);
            }
        }
    }

    [TestMethod]
    public async Task BootstrapCannotUseTheSystemPlansRemovalExceptions()
    {
        await _fixture.AddPackageAsync("anduinos-offline-updater", "2.1.0-2", conflicts: "user-editor");
        await _fixture.PublishRepositoryAsync();
        var baseline = _fixture.CreateBaseline("user-editor", "2.0.0", "2.0.0-1", "1",
            BundleFixture.Status("user-editor", "1"));
        var profile = _fixture.Profile with
        {
            Bootstrap = new UpdaterBootstrap { MinimumVersion = "2.1.0-2" },
            Baselines = [baseline], AllowedRemovals = ["user-editor"]
        };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            new BundleBuilder().BuildAsync(profile, _fixture.Output, _fixture.Signing));
    }

    private async Task<string> ResignAsync(string directory, BundleManifest manifest, string name)
    {
        var path = Path.Combine(directory, "manifest.json");
        await File.WriteAllTextAsync(path, BundleJson.Write(manifest));
        await BundleSigning.SignAsync(path, Path.Combine(directory, "manifest.asc"), _fixture.Signing, false, default);
        var output = Path.Combine(_fixture.Root, name);
        await BundleArchive.WriteAsync(directory, output, manifest, default);
        return output;
    }

    private async Task<string> RewriteAsync(string input, string name, string changedPath, Func<byte[], byte[]?> change,
        bool duplicate = false, string? extraPath = null)
    {
        var output = Path.Combine(_fixture.Root, name);
        await using var source = File.OpenRead(input);
        await using var unzip = new GZipStream(source, CompressionMode.Decompress);
        await using var reader = new TarReader(unzip);
        await using var destination = File.Create(output);
        await using var zip = new GZipStream(destination, CompressionLevel.Fastest);
        await using var writer = new TarWriter(zip);
        TarEntry? entry;
        while ((entry = await reader.GetNextEntryAsync()) != null)
        {
            using var memory = new MemoryStream();
            await entry.DataStream!.CopyToAsync(memory);
            var bytes = entry.Name == changedPath ? change(memory.ToArray()) : memory.ToArray();
            if (bytes == null) continue;
            await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, entry.Name) { DataStream = new MemoryStream(bytes) });
            if (duplicate && entry.Name == changedPath)
            {
                await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, entry.Name) { DataStream = new MemoryStream(bytes) });
            }
        }
        if (extraPath != null)
        {
            await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, extraPath) { DataStream = new MemoryStream([1]) });
        }
        return output;
    }
}
