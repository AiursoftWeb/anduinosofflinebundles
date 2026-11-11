using System.Text.Json.Serialization;

// ReSharper disable NotAccessedPositionalProperty.Global
// These records are JSON wire contracts; consumers read the serialized properties.

namespace Aiursoft.AnduinOS.OfflineBundles.Core.Models;

public sealed record BuildProfile
{
    public int FormatVersion { get; init; } = 1;
    public required string ReleaseLine { get; init; }
    public required string Suite { get; init; }
    public required string Architecture { get; init; }
    public string VersionPackage { get; init; } = "base-files";
    public string VersionFile { get; init; } = "usr/lib/os-release";
    public required List<SourceDefinition> Sources { get; init; }
    public required List<BaselineDefinition> Baselines { get; init; }
    public List<UpdateStage> Stages { get; init; } = [new() { Operation = "dist-upgrade" }];
    public List<string> AllowedRemovals { get; init; } = [];
}

public sealed record SourceDefinition
{
    public required string Uri { get; init; }
    public required List<string> Suites { get; init; }
    public List<string> Components { get; init; } = [];
    public required string Keyring { get; init; }
}

public sealed record BaselineDefinition
{
    public required string Name { get; init; }
    public required string StatusFile { get; init; }
    public required string OsReleaseFile { get; init; }
}

public sealed record UpdateStage
{
    public required string Operation { get; init; }
    public List<string> Packages { get; init; } = [];
}

public sealed record PackageIdentity(string Name, string Version, string Architecture)
{
    [JsonIgnore] public string Key => $"{Name}:{Architecture}";
    [JsonIgnore] public string AptSpecification => $"{Name}:{Architecture}={Version}";
}

public sealed record PackageChange(string Action, string Name, string? Before, string? After, string Architecture);
public sealed record StagePlan(UpdateStage Stage, List<PackageChange> Changes);
public sealed record BaselinePlan(string Name, string SystemVersion, string StatusSha256, List<StagePlan> Stages);
public sealed record FileDigest(long Size, string Sha256);
public sealed record BundlePackage(PackageIdentity Identity, string Path, FileDigest Content);

public sealed record BundleManifest
{
    public int FormatVersion { get; init; } = 1;
    public string Product { get; init; } = "AnduinOS";
    public required string ReleaseLine { get; init; }
    public required string Suite { get; init; }
    public required string Architecture { get; init; }
    public required string TargetVersion { get; init; }
    public required string BatchId { get; init; }
    public required DateTimeOffset CreatedUtc { get; init; }
    public List<string> RequiredFeatures { get; init; } = ["apt-repository-v1", "sequential-apt-plans-v1"];
    public required string VersionPackage { get; init; }
    public required string VersionFile { get; init; }
    public required List<UpdateStage> Stages { get; init; }
    public required List<string> AllowedRemovals { get; init; }
    public required List<BaselinePlan> Baselines { get; init; }
    public required List<BundlePackage> Packages { get; init; }
    public required List<PackageIdentity> RequiredInstalledTargets { get; init; }
    public required SortedDictionary<string, FileDigest> SourceIndexes { get; init; }
    public required SortedDictionary<string, FileDigest> Files { get; init; }
}

public sealed record SigningOptions(string Home, string Key, string TrustedKeyring);
public sealed record BuildResult(string File, FileDigest Content, string BatchId, string TargetVersion, int PackageCount);
public sealed record VerificationResult(BundleManifest Manifest, string SigningFingerprint, long UnpackedBytes);
public sealed record PreviewResult(string CurrentVersion, string TargetVersion, string BatchId,
    string State, List<StagePlan> Stages, long ArchiveBytes, long UnpackedBytes,
    long AdditionalStagingBytes, string Snapshot, List<string> Blockers);
