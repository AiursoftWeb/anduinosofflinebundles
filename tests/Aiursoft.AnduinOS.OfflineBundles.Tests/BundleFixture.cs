using System.Globalization;
using Aiursoft.AnduinOS.OfflineBundles.Core.Models;
using Aiursoft.AnduinOS.OfflineBundles.Core.Services;

namespace Aiursoft.AnduinOS.OfflineBundles.Tests;

internal sealed class BundleFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "offline-tests-" + Guid.NewGuid().ToString("N"));
    public string Repository => Path.Combine(Root, "repository");
    public string Keyring => Path.Combine(Root, "trusted.gpg");
    public SigningOptions Signing => new(Path.Combine(Root, "signing"), "offline-bundle-tests@example.invalid", Keyring);
    public string Output => Path.Combine(Root, "output");
    public BuildProfile Profile { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Repository);
        Directory.CreateDirectory(Signing.Home);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Signing.Home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        await NativeCommand.RunAsync("gpg", ["--homedir", Signing.Home, "--batch", "--pinentry-mode", "loopback",
            "--passphrase", "", "--quick-generate-key", Signing.Key, "ed25519", "sign", "1d"]);
        await NativeCommand.RunAsync("gpg", ["--homedir", Signing.Home, "--batch", "--output", Keyring, "--export"]);
        await AddPackageAsync("anduinos-core-system", "2.0.5-1", "base-library (>= 5)", osVersion: "2.0.5");
        await AddPackageAsync("base-library", "5");
        await AddPackageAsync("vim-common", "5");
        await PublishRepositoryAsync();
        Profile = new BuildProfile
        {
            ReleaseLine = "2.0", Suite = "resolute", Architecture = "amd64", VersionPackage = "anduinos-core-system",
            Sources = [new SourceDefinition { Uri = new Uri(Repository + "/").AbsoluteUri, Suites = ["./"], Keyring = Keyring }],
            Baselines = [CreateBaseline("original", "2.0.0", "2.0.0-1", "1"),
                CreateBaseline("intermediate", "2.0.3", "2.0.3-1", "3")]
        };
    }

    public BaselineDefinition CreateBaseline(string name, string version, string coreVersion, string libraryVersion,
        string extra = "", bool hold = false)
    {
        var status = Path.Combine(Root, name + ".status");
        File.WriteAllText(status, Status("anduinos-core-system", coreVersion,
                coreVersion == "2.0.5-1" ? "base-library (>= 5)" : "base-library (>= 1)") +
            Status("base-library", libraryVersion, hold: hold) + Status("vim-common", libraryVersion) + extra);
        var os = Path.Combine(Root, name + ".os-release");
        File.WriteAllText(os, OsRelease(version));
        return new BaselineDefinition { Name = name, StatusFile = status, OsReleaseFile = os };
    }

    public async Task AddPackageAsync(string name, string version, string? depends = null, string? conflicts = null, string? osVersion = null)
    {
        var directory = Path.Combine(Root, "deb-" + name + "-" + version);
        Directory.CreateDirectory(Path.Combine(directory, "DEBIAN"));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Path.Combine(directory, "DEBIAN"), UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        var control = $"Package: {name}\nVersion: {version}\nArchitecture: all\nMaintainer: Fixture <fixture@example.invalid>\nDescription: Offline bundle business test fixture\n";
        if (depends != null) control += $"Depends: {depends}\n";
        if (conflicts != null) control += $"Conflicts: {conflicts}\n";
        File.WriteAllText(Path.Combine(directory, "DEBIAN/control"), control);
        if (osVersion != null)
        {
            Directory.CreateDirectory(Path.Combine(directory, "usr/lib"));
            File.WriteAllText(Path.Combine(directory, "usr/lib/os-release"), OsRelease(osVersion));
        }
        await NativeCommand.RunAsync("dpkg-deb", ["--build", "--root-owner-group", directory,
            Path.Combine(Repository, $"{name}_{version}_all.deb")]);
    }

    public async Task PublishRepositoryAsync()
    {
        var packages = Path.Combine(Repository, "Packages");
        await using (var output = File.Create(packages))
        {
            await NativeCommand.RunAsync("apt-ftparchive", ["packages", "."], Repository, output: output);
        }
        var release = Path.Combine(Repository, "Release");
        File.WriteAllText(release, "Origin: Offline Fixture\nSuite: resolute\nCodename: resolute\nArchitectures: amd64 all\nDate: " +
            DateTimeOffset.UtcNow.ToString("ddd, dd MMM yyyy HH:mm:ss +0000", CultureInfo.InvariantCulture) +
            $"\nSHA256:\n {await BundleJson.HashAsync(packages)} {new FileInfo(packages).Length} Packages\n");
        await BundleSigning.SignAsync(release, Path.Combine(Repository, "InRelease"), Signing, true, default);
    }

    public static string Status(string name, string version, string? depends = null, bool hold = false) =>
        $"Package: {name}\nStatus: {(hold ? "hold" : "install")} ok installed\nArchitecture: all\nVersion: {version}\n" +
        (depends == null ? "" : $"Depends: {depends}\n") + "Description: Test package\n\n";

    private static string OsRelease(string version) => $"ID=anduinos\nVERSION_ID=\"{version}\"\nUBUNTU_CODENAME=resolute\n";

    public void Dispose()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}
