using System.Text.RegularExpressions;
using Aiursoft.AnduinOS.OfflineBundles.Core.Models;

namespace Aiursoft.AnduinOS.OfflineBundles.Core.Services;

public static partial class ProfileReader
{
    [GeneratedRegex("^[a-z0-9][a-z0-9+.-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageName();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Token();

    public static BuildProfile Read(string path)
    {
        path = Path.GetFullPath(path);
        var profile = BundleJson.Read<BuildProfile>(File.ReadAllText(path));
        Validate(profile);
        var directory = Path.GetDirectoryName(path)!;
        return profile with
        {
            Sources = profile.Sources.Select(source => source with
            {
                Keyring = ExistingFile(source.Keyring, directory)
            }).ToList(),
            Baselines = profile.Baselines.Select(baseline => baseline with
            {
                StatusFile = ExistingFile(baseline.StatusFile, directory),
                OsReleaseFile = ExistingFile(baseline.OsReleaseFile, directory)
            }).ToList()
        };
    }

    public static void Validate(BuildProfile profile)
    {
        if (profile.FormatVersion != 1 || !Token().IsMatch(profile.ReleaseLine) ||
            !Token().IsMatch(profile.Suite) || profile.Architecture is not ("amd64" or "arm64"))
        {
            throw new InvalidDataException("Unsupported profile version, release line, suite or architecture.");
        }
        ValidatePackage(profile.VersionPackage);
        ValidatePath(profile.VersionFile);
        ValidateStages(profile.Stages, profile.AllowedRemovals);
        if (profile.Sources.Count == 0 || profile.Baselines.Count == 0 ||
            profile.Baselines.Select(b => b.Name).Distinct(StringComparer.Ordinal).Count() != profile.Baselines.Count)
        {
            throw new InvalidDataException("Sources and uniquely named baselines are required.");
        }
        foreach (var baseline in profile.Baselines)
        {
            if (!Token().IsMatch(baseline.Name))
            {
                throw new InvalidDataException("Invalid baseline name.");
            }
        }
        foreach (var source in profile.Sources)
        {
            if (!Uri.TryCreate(source.Uri, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("file" or "https" or "http") || uri.UserInfo.Length != 0 ||
                source.Uri.Any(char.IsWhiteSpace) || source.Suites.Count == 0 ||
                source.Suites.Any(s => s != "./" && !Token().IsMatch(s)) ||
                source.Components.Any(c => !Token().IsMatch(c)) ||
                (source.Suites.Contains("./") && (source.Suites.Count != 1 || source.Components.Count != 0)))
            {
                throw new InvalidDataException("Invalid source. Use signed HTTP(S) repositories or a signed flat file repository.");
            }
        }
    }

    public static void ValidateStages(List<UpdateStage> stages, List<string> removals)
    {
        if (stages.Count == 0 || stages.Count > 16 || stages[^1].Operation != "dist-upgrade")
        {
            throw new InvalidDataException("The bounded update plan must end with dist-upgrade.");
        }
        foreach (var stage in stages)
        {
            if (stage.Operation is not ("install" or "dist-upgrade") ||
                stage.Operation == "install" && stage.Packages.Count == 0 ||
                stage.Operation == "dist-upgrade" && stage.Packages.Count != 0)
            {
                throw new InvalidDataException("Only named install stages and dist-upgrade are supported.");
            }
            foreach (var package in stage.Packages)
            {
                ValidatePackage(package);
            }
        }
        foreach (var package in removals)
        {
            ValidatePackage(package);
        }
    }

    public static void ValidatePackage(string name)
    {
        if (!PackageName().IsMatch(name))
        {
            throw new InvalidDataException($"Invalid package name: {name}");
        }
    }

    public static void ValidatePath(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 512 || name.Contains('\\') || name.Contains(':') ||
            name.Any(char.IsControl) || name.Split('/').Any(p => p is "" or "." or "..") || Path.IsPathRooted(name))
        {
            throw new InvalidDataException($"Unsafe archive path: {name}");
        }
    }

    private static string ExistingFile(string path, string root)
    {
        var result = Path.GetFullPath(path, root);
        if (!File.Exists(result) || result.Any(c => c is '\r' or '\n'))
        {
            throw new FileNotFoundException("Profile input file does not exist or has an invalid path.", result);
        }
        return result;
    }
}
