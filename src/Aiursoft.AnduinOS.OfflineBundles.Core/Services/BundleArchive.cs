using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Aiursoft.AnduinOS.OfflineBundles.Core.Models;

namespace Aiursoft.AnduinOS.OfflineBundles.Core.Services;

public static class BundleArchive
{
    private const long MaximumPayload = 64L * 1024 * 1024 * 1024;
    private static readonly string[] RepositoryMetadata = ["repository/Packages", "repository/Release", "repository/InRelease", "repository/Release.gpg"];

    public static async Task WriteAsync(string directory, string destination, BundleManifest manifest, CancellationToken token)
    {
        await using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        await using var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true);
        foreach (var name in new[] { "manifest.json", "manifest.asc" }.Concat(manifest.Files.Keys))
        {
            await using var input = File.OpenRead(Path.Combine(directory, name));
            var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
            {
                DataStream = input,
                ModificationTime = manifest.CreatedUtc,
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead
            };
            await tar.WriteEntryAsync(entry, token);
        }
    }

    public static async Task<VerificationResult> VerifyAsync(string file, string trustedKeyring,
        string? extractionDirectory = null, CancellationToken token = default)
    {
        var metadata = Path.Combine(Path.GetTempPath(), "offline-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(metadata);
        if (extractionDirectory != null)
        {
            if (Directory.Exists(extractionDirectory) && Directory.EnumerateFileSystemEntries(extractionDirectory).Any())
            {
                throw new InvalidDataException("Extraction destination must be empty.");
            }
            Directory.CreateDirectory(extractionDirectory);
        }
        try
        {
            await using var input = File.OpenRead(file);
            await using var gzip = new GZipStream(input, CompressionMode.Decompress);
            await using var reader = new TarReader(gzip, leaveOpen: true);
            var manifestText = await ReadHeaderAsync(reader, "manifest.json", 4 * 1024 * 1024, token);
            var signature = await ReadHeaderAsync(reader, "manifest.asc", 256 * 1024, token);
            var manifestPath = Path.Combine(metadata, "manifest.json");
            var signaturePath = Path.Combine(metadata, "manifest.asc");
            await File.WriteAllBytesAsync(manifestPath, manifestText, token);
            await File.WriteAllBytesAsync(signaturePath, signature, token);
            var fingerprint = await BundleSigning.VerifyAsync(manifestPath, signaturePath, trustedKeyring, token);
            var manifest = BundleJson.Read<BundleManifest>(new UTF8Encoding(false, true).GetString(manifestText));
            ValidateManifest(manifest);
            if (extractionDirectory != null &&
                new DriveInfo(Path.GetFullPath(extractionDirectory)).AvailableFreeSpace < manifest.Files.Values.Sum(f => f.Size))
            {
                throw new IOException("Insufficient space for the unpacked bundle. No payload was extracted.");
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            long total = 0;
            TarEntry? entry;
            while ((entry = await reader.GetNextEntryAsync(cancellationToken: token)) != null)
            {
                ValidateEntry(entry);
                if (!seen.Add(entry.Name) || !manifest.Files.TryGetValue(entry.Name, out var expected) || expected.Size != entry.Length)
                {
                    throw new InvalidDataException($"Unexpected, duplicate or wrong-size payload: {entry.Name}");
                }
                total = checked(total + entry.Length);
                if (total > MaximumPayload)
                {
                    throw new InvalidDataException("Bundle exceeds the supported unpacked size.");
                }
                await using var output = extractionDirectory == null ? null : CreatePayload(extractionDirectory, entry.Name);
                await using var metadataOutput = RepositoryMetadata.Contains(entry.Name) ? CreatePayload(metadata, entry.Name) : null;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[128 * 1024];
                long remaining = entry.Length;
                while (remaining > 0)
                {
                    var read = await entry.DataStream!.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), token);
                    if (read == 0)
                    {
                        throw new InvalidDataException("Truncated payload.");
                    }
                    hash.AppendData(buffer, 0, read);
                    if (output != null)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), token);
                    }
                    if (metadataOutput != null)
                    {
                        await metadataOutput.WriteAsync(buffer.AsMemory(0, read), token);
                    }
                    remaining -= read;
                }
                if (!string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), expected.Sha256, StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"Payload checksum mismatch: {entry.Name}");
                }
            }
            if (!seen.SetEquals(manifest.Files.Keys))
            {
                throw new InvalidDataException("The bundle is missing declared payload files.");
            }
            // Consume the gzip trailer (including its CRC) and reject hidden extra tar content.
            var trailer = new byte[8192];
            int trailing;
            long padding = 0;
            while ((trailing = await gzip.ReadAsync(trailer, token)) != 0)
            {
                padding += trailing;
                if (padding > 10240 || trailer.AsSpan(0, trailing).ContainsAnyExcept((byte)0))
                {
                    throw new InvalidDataException("Unexpected content after the tar archive.");
                }
            }
            await ValidateRepositoryAsync(metadata, manifest, fingerprint, trustedKeyring, token);
            if (manifest.Bootstrap != null)
            {
                var updater = manifest.Packages.Single(p => p.Identity.Name == manifest.Bootstrap.Package);
                if (!await AptWorkspace.CompareAsync(updater.Identity.Version, "ge", manifest.Bootstrap.MinimumVersion, token))
                {
                    throw new InvalidDataException("The bundled updater cannot meet its declared minimum version.");
                }
            }
            return new VerificationResult(manifest, fingerprint, total);
        }
        finally
        {
            Directory.Delete(metadata, recursive: true);
        }
    }

    public static void ValidateManifest(BundleManifest manifest)
    {
        if (manifest.FormatVersion != 1 || manifest.Product != "AnduinOS" ||
            manifest.Architecture is not ("amd64" or "arm64") ||
            manifest.RequiredFeatures.Any(f => f is not ("apt-repository-v1" or "sequential-apt-plans-v1" or "updater-bootstrap-v1")) ||
            !manifest.RequiredFeatures.Contains("apt-repository-v1") || !manifest.RequiredFeatures.Contains("sequential-apt-plans-v1") ||
            manifest.Files.Count > 100000 || manifest.Packages.Count == 0 || manifest.Baselines.Count == 0)
        {
            throw new InvalidDataException("Unsupported or incomplete bundle manifest.");
        }
        if (manifest.BatchId.Length != 64 || manifest.BatchId.Any(c => !char.IsAsciiHexDigitLower(c)) ||
            manifest.Suite.Length == 0 || manifest.Suite.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') ||
            manifest.Files.Values.Any(f => f.Size < 0 || f.Size > MaximumPayload) ||
            manifest.Files.Values.Sum(f => f.Size) > MaximumPayload)
        {
            throw new InvalidDataException("Invalid bundle identity or unpacked size.");
        }
        BundleBuilder.CheckOperatingSystem(new Dictionary<string, string>
        {
            ["ID"] = "anduinos", ["VERSION_ID"] = manifest.TargetVersion, ["UBUNTU_CODENAME"] = manifest.Suite
        }, manifest.ReleaseLine, manifest.Suite);
        ProfileReader.ValidateBootstrap(manifest.Bootstrap);
        if (manifest.RequiredFeatures.Contains("updater-bootstrap-v1") != (manifest.Bootstrap != null) ||
            manifest.RequiredInstallerFeatures.Count > 32 ||
            manifest.RequiredInstallerFeatures.Any(f => f.Length is 0 or > 128 || f.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) ||
            manifest.RequiredInstallerFeatures.Distinct().Count() != manifest.RequiredInstallerFeatures.Count ||
            manifest.RequiredInstallerFeatures.Count != 0 && manifest.Bootstrap == null)
        {
            throw new InvalidDataException("Invalid bootstrap capability declaration.");
        }
        // Installer capabilities are separate from the stable authenticated envelope.
        // Unknown system-plan semantics may be read, but never executed by this version.
        if (manifest.RequiredInstallerFeatures.Count == 0)
        {
            ProfileReader.ValidateStages(manifest.Stages, manifest.AllowedRemovals);
        }
        else if (manifest.Stages.Count is 0 or > 16)
        {
            throw new InvalidDataException("Invalid bounded future installer plan.");
        }
        ProfileReader.ValidatePackage(manifest.VersionPackage);
        ProfileReader.ValidatePath(manifest.VersionFile);
        var packageKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var package in manifest.Packages)
        {
            ProfileReader.ValidatePackage(package.Identity.Name);
            if (package.Identity.Architecture != manifest.Architecture && package.Identity.Architecture != "all" ||
                !packageKeys.Add(package.Identity.Key) || !package.Path.StartsWith("repository/pool/", StringComparison.Ordinal) ||
                !manifest.Files.TryGetValue(package.Path, out var digest) || digest != package.Content)
            {
                throw new InvalidDataException("Invalid or conflicting package manifest entry.");
            }
        }
        if (manifest.Bootstrap != null && !manifest.Packages.Any(p => p.Identity.Name == manifest.Bootstrap.Package))
        {
            throw new InvalidDataException("The bootstrap updater is missing from the bundle.");
        }
        foreach (var target in manifest.RequiredInstalledTargets)
        {
            if (!manifest.Packages.Any(p => p.Identity == target))
            {
                throw new InvalidDataException("A required target is not present in the bundle.");
            }
        }
        foreach (var (path, digest) in manifest.Files)
        {
            ProfileReader.ValidatePath(path);
            if (!path.StartsWith("repository/", StringComparison.Ordinal) || digest.Size < 0 || digest.Size > MaximumPayload ||
                digest.Sha256.Length != 64 || digest.Sha256.Any(c => !char.IsAsciiHexDigitLower(c)))
            {
                throw new InvalidDataException("Invalid manifest file entry.");
            }
        }
        if (!manifest.Packages.Any(p => p.Identity.Name == manifest.VersionPackage) ||
            manifest.RequiredInstalledTargets.Count != manifest.Packages.Count ||
            manifest.RequiredInstalledTargets.Distinct().Count() != manifest.RequiredInstalledTargets.Count ||
            !manifest.Files.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(manifest.Packages.Select(p => p.Path).Concat(RepositoryMetadata)))
        {
            throw new InvalidDataException("Repository files and required targets must match the package inventory.");
        }
        foreach (var required in RepositoryMetadata)
        {
            var limit = required == "repository/Packages" ? 32L * 1024 * 1024 : 1024 * 1024;
            if (!manifest.Files.TryGetValue(required, out var digest) || digest.Size > limit)
            {
                throw new InvalidDataException($"Missing repository metadata: {required}");
            }
        }
    }

    private static async Task ValidateRepositoryAsync(string root, BundleManifest manifest, string signer,
        string keyring, CancellationToken token)
    {
        var release = Path.Combine(root, "repository/Release");
        var clear = Path.Combine(root, "verified-release");
        if (await BundleSigning.VerifyAsync(release, release + ".gpg", keyring, token) != signer ||
            await BundleSigning.VerifyClearSignedAsync(Path.Combine(root, "repository/InRelease"), clear, keyring, token) != signer ||
            await BundleJson.HashAsync(clear, token) != await BundleJson.HashAsync(release, token))
        {
            throw new InvalidDataException("Repository signatures and Release content must agree with the bundle signer.");
        }
        var releaseRecords = DebianControl.Read(await File.ReadAllTextAsync(release, token));
        var expectedIndex = manifest.Files["repository/Packages"];
        var hashLine = $"{expectedIndex.Sha256} {expectedIndex.Size.ToString(System.Globalization.CultureInfo.InvariantCulture)} Packages";
        if (releaseRecords.Count != 1 || releaseRecords[0].GetValueOrDefault("Suite") != manifest.Suite ||
            releaseRecords[0].GetValueOrDefault("Codename") != manifest.Suite ||
            releaseRecords[0].GetValueOrDefault("Architectures") != manifest.Architecture + " all" ||
            releaseRecords[0].GetValueOrDefault("SHA256")?.Trim() != hashLine)
        {
            throw new InvalidDataException("Release metadata does not match the signed manifest.");
        }
        var inventory = manifest.Packages.ToDictionary(p => p.Identity.Key, StringComparer.Ordinal);
        foreach (var record in DebianControl.Read(await File.ReadAllTextAsync(Path.Combine(root, "repository/Packages"), token)))
        {
            var identity = DebianControl.Identity(record);
            if (!inventory.Remove(identity.Key, out var package) || package.Identity != identity ||
                record.GetValueOrDefault("Filename") != package.Path["repository/".Length..] ||
                record.GetValueOrDefault("SHA256") != package.Content.Sha256 ||
                record.GetValueOrDefault("Size") != package.Content.Size.ToString(System.Globalization.CultureInfo.InvariantCulture))
            {
                throw new InvalidDataException("Package index does not match the signed package inventory.");
            }
        }
        if (inventory.Count != 0)
        {
            throw new InvalidDataException("Some declared packages are missing from the repository index.");
        }
    }

    private static async Task<byte[]> ReadHeaderAsync(TarReader reader, string name, int limit, CancellationToken token)
    {
        var entry = await reader.GetNextEntryAsync(cancellationToken: token)
            ?? throw new InvalidDataException($"Missing {name}.");
        ValidateEntry(entry);
        if (entry.Name != name || entry.Length > limit || entry.Length == 0)
        {
            throw new InvalidDataException($"Expected bounded {name} at the start of the archive.");
        }
        var bytes = new byte[entry.Length];
        await entry.DataStream!.ReadExactlyAsync(bytes, token);
        return bytes;
    }

    private static void ValidateEntry(TarEntry entry)
    {
        ProfileReader.ValidatePath(entry.Name);
        if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) ||
            entry.LinkName.Length != 0 || entry.Length > MaximumPayload)
        {
            throw new InvalidDataException($"Only bounded regular archive files are accepted: {entry.Name}");
        }
    }

    private static FileStream CreatePayload(string root, string name)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    }
}
