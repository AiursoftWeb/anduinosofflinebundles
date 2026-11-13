using System.Text;
using System.Text.RegularExpressions;
using Aiursoft.AnduinOS.OfflineBundles.Core.Models;

namespace Aiursoft.AnduinOS.OfflineBundles.Core.Services;

/// <summary>Owns isolated APT configuration, indexes and a virtual dpkg status database. Never installs packages.</summary>
public sealed partial class AptWorkspace
{
    private readonly string _root;
    private readonly string _architecture;
    private readonly Dictionary<string, string?> _environment;
    private readonly Dictionary<(string Package, string Version), Dictionary<string, string>> _metadata = new();
    public string StatusPath => Path.Combine(_root, "state/status");
    public string ListsPath => Path.Combine(_root, "state/lists");

    public AptWorkspace(string root, string architecture)
    {
        _root = Path.GetFullPath(root);
        _architecture = architecture;
        foreach (var directory in new[] { "etc/parts", "etc/sources.list.d", "etc/preferences.d", "state/lists/partial", "cache/archives/partial", "log", "keys" })
        {
            Directory.CreateDirectory(Path.Combine(_root, directory));
        }
        File.WriteAllText(StatusPath, string.Empty);
        File.WriteAllText(Path.Combine(_root, "etc/preferences"), string.Empty);
        File.WriteAllText(Path.Combine(_root, "etc/empty.conf"), string.Empty);
        var configuration = Path.Combine(_root, "apt.conf");
        File.WriteAllText(configuration, $$"""
            Dir "{{Escape(_root)}}";
            Dir::Etc "{{Escape(Path.Combine(_root, "etc"))}}";
            Dir::Etc::parts "parts";
            Dir::Etc::main "empty.conf";
            Dir::Etc::sourcelist "sources.sources";
            Dir::Etc::sourceparts "sources.list.d";
            Dir::Etc::preferences "preferences";
            Dir::Etc::preferencesparts "preferences.d";
            Dir::State "{{Escape(Path.Combine(_root, "state"))}}";
            Dir::State::status "{{Escape(StatusPath)}}";
            Dir::Cache "{{Escape(Path.Combine(_root, "cache"))}}";
            Dir::Cache::pkgcache "";
            Dir::Cache::srcpkgcache "";
            Dir::Log "{{Escape(Path.Combine(_root, "log"))}}";
            #clear APT::Architectures;
            APT::Architecture "{{architecture}}";
            APT::Architectures { "{{architecture}}"; };
            APT::Sandbox::User "{{Escape(Environment.UserName)}}";
            APT::Get::List-Cleanup "false";
            Acquire::Languages "none";
            Acquire::Retries "1";
            Acquire::http::Timeout "60";
            Acquire::https::Timeout "60";
            Acquire::AllowInsecureRepositories "false";
            APT::Get::AllowUnauthenticated "false";
            """);
        _environment = new Dictionary<string, string?> { ["APT_CONFIG"] = configuration };
    }

    public async Task SetSourcesAsync(IEnumerable<SourceDefinition> sources, CancellationToken token,
        string? preferencesFile = null)
    {
        _metadata.Clear();
        await File.WriteAllTextAsync(Path.Combine(_root, "etc/preferences"),
            preferencesFile == null ? string.Empty : await File.ReadAllTextAsync(preferencesFile, token), token);
        var builder = new StringBuilder();
        var index = 0;
        foreach (var source in sources)
        {
            var key = Path.Combine(_root, $"keys/source-{index++}.gpg");
            File.Copy(source.Keyring, key, overwrite: true);
            builder.AppendLine("Types: deb");
            builder.AppendLine($"URIs: {source.Uri}");
            builder.AppendLine($"Suites: {string.Join(' ', source.Suites)}");
            if (source.Components.Count != 0)
            {
                builder.AppendLine($"Components: {string.Join(' ', source.Components)}");
            }
            builder.AppendLine($"Architectures: {_architecture}");
            builder.AppendLine($"Signed-By: {key}");
            builder.AppendLine();
        }
        await File.WriteAllTextAsync(Path.Combine(_root, "etc/sources.sources"), builder.ToString(), token);
        await AptAsync("apt-get", ["update", "--error-on=any"], token);
    }

    public async Task SetStatusAsync(string status, CancellationToken token)
    {
        var paragraphs = DebianControl.Read(status);
        foreach (var package in paragraphs.Where(p => p.ContainsKey("Status")))
        {
            var state = package["Status"].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (state.Length != 3 || state[1] != "ok" || state[2] is not ("installed" or "config-files" or "not-installed"))
            {
                throw new InvalidDataException("The baseline has an unfinished dpkg transaction.");
            }
            if (package.GetValueOrDefault("Architecture") is { } architecture && architecture != _architecture && architecture != "all")
            {
                throw new InvalidDataException("This preview does not support a foreign-architecture installed package.");
            }
        }
        await File.WriteAllTextAsync(StatusPath, status, token);
    }

    public async Task<List<PackageChange>> SimulateAsync(UpdateStage stage, IEnumerable<string> allowedRemovals, CancellationToken token)
    {
        var arguments = new List<string> { "--simulate", "--quiet=2", stage.Operation };
        arguments.AddRange(stage.Packages);
        var result = await AptAsync("apt-get", arguments, token);
        var changes = new List<PackageChange>();
        var approvedRemovals = allowedRemovals.ToHashSet(StringComparer.Ordinal);
        var installed = DebianControl.Read(await File.ReadAllTextAsync(StatusPath, token));
        foreach (var line in result.Output.Split('\n'))
        {
            var install = InstallationLine().Match(line);
            if (install.Success)
            {
                var name = install.Groups[1].Value.Split(':')[0];
                var version = install.Groups[3].Value;
                var metadata = await MetadataAsync(name, version, token);
                var before = install.Groups[2].Success ? install.Groups[2].Value : null;
                if (before != null && await CompareAsync(version, "lt", before, token))
                {
                    throw new InvalidDataException($"Downgrade refused: {name} {before} -> {version}.");
                }
                changes.Add(new PackageChange("install", name, before, version, metadata["Architecture"]));
            }
            else if (line.StartsWith("Remv ", StringComparison.Ordinal))
            {
                var name = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1].Split(':')[0];
                if (!approvedRemovals.Contains(name))
                {
                    throw new InvalidDataException($"Unapproved package removal: {name}.");
                }
                var old = installed.SingleOrDefault(p => p.GetValueOrDefault("Package") == name);
                changes.Add(new PackageChange("remove", name, old?.GetValueOrDefault("Version"), null,
                    old?.GetValueOrDefault("Architecture") ?? _architecture));
            }
            else if (line.StartsWith("Inst ", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Unrecognized APT plan line: {line}");
            }
        }
        return changes;
    }

    public async Task<Dictionary<string, string>> MetadataAsync(string package, string version, CancellationToken token)
    {
        if (_metadata.TryGetValue((package, version), out var cached))
        {
            return new Dictionary<string, string>(cached, StringComparer.OrdinalIgnoreCase);
        }
        var result = await AptAsync("apt-cache", ["show", $"{package}={version}"], token);
        var metadata = DebianControl.Read(result.Output).FirstOrDefault(p => p.GetValueOrDefault("Version") == version &&
            (p.GetValueOrDefault("Architecture") == _architecture || p.GetValueOrDefault("Architecture") == "all"))
            ?? throw new InvalidDataException($"No trusted index entry for {package}={version}.");
        // Only cache authenticated index entries, not status-only package descriptions.
        if (metadata.ContainsKey("SHA256"))
        {
            _metadata[(package, version)] = new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase);
        }
        return metadata;
    }

    public async Task<Dictionary<string, string>> CandidateAsync(string package, CancellationToken token)
    {
        var policy = await AptAsync("apt-cache", ["policy", package], token);
        var candidate = policy.Output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("Candidate: ", StringComparison.Ordinal));
        if (candidate == null || candidate.EndsWith("(none)", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"No candidate for {package}.");
        }
        return await MetadataAsync(package, candidate[11..], token);
    }

    public async Task DownloadAsync(IEnumerable<PackageIdentity> packages, string directory, CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        foreach (var batch in packages.Distinct().Chunk(50))
        {
            await NativeCommand.RunAsync("apt-get", new[] { "download" }.Concat(batch.Select(p => p.AptSpecification)),
                directory, _environment, cancellationToken: token);
        }
    }

    public async Task AdvanceAsync(IEnumerable<PackageChange> changes, CancellationToken token)
    {
        var paragraphs = DebianControl.Read(await File.ReadAllTextAsync(StatusPath, token));
        foreach (var change in changes)
        {
            paragraphs.RemoveAll(p => p.GetValueOrDefault("Package") == change.Name &&
                p.GetValueOrDefault("Architecture") == change.Architecture);
            if (change.Action == "install")
            {
                var paragraph = await MetadataAsync(change.Name, change.After!, token);
                foreach (var field in new[] { "Filename", "Size", "MD5sum", "SHA1", "SHA256", "SHA512", "Description-md5" })
                {
                    paragraph.Remove(field);
                }
                paragraph["Status"] = "install ok installed";
                paragraphs.Add(paragraph);
            }
        }
        await File.WriteAllTextAsync(StatusPath, DebianControl.Write(paragraphs), token);
    }

    public static async Task<bool> CompareAsync(string left, string operation, string right, CancellationToken token)
    {
        var result = await NativeCommand.RunAsync("dpkg", ["--compare-versions", left, operation, right],
            cancellationToken: token, requireSuccess: false);
        if (result.ExitCode > 1)
        {
            throw new InvalidDataException("Invalid Debian version comparison.");
        }
        return result.ExitCode == 0;
    }

    private Task<CommandResult> AptAsync(string command, IEnumerable<string> arguments, CancellationToken token) =>
        NativeCommand.RunAsync(command, arguments, _root, _environment, cancellationToken: token);

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    [GeneratedRegex(@"^Inst (\S+)(?: \[([^\]]+)\])? \((\S+) ", RegexOptions.CultureInvariant)]
    private static partial Regex InstallationLine();
}
