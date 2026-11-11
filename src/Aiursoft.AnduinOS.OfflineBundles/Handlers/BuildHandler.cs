using System.CommandLine;
using Aiursoft.AnduinOS.OfflineBundles.Core.Models;
using Aiursoft.AnduinOS.OfflineBundles.Core.Services;

namespace Aiursoft.AnduinOS.OfflineBundles.Handlers;

public sealed class BuildHandler : BundleCommandHandler
{
    protected override string Name => "build";
    protected override string Description => "Build a signed cumulative bundle from authenticated APT sources and baseline package databases.";
    private static readonly Option<string> ProfileOption = new("--profile") { Required = true, Description = "Build profile JSON." };
    private static readonly Option<string> OutputOption = new("--output") { Required = true, Description = "Output directory." };
    private static readonly Option<string> HomeOption = new("--signing-home") { Required = true, Description = "Existing GnuPG home; never copied into the bundle." };
    private static readonly Option<string> KeyOption = new("--signing-key") { Required = true, Description = "Signing key fingerprint or identifier." };
    protected override IEnumerable<Option> Options => [ProfileOption, OutputOption, HomeOption, KeyOption, KeyringOption, JsonOption];

    protected override async Task<int> ExecuteAsync(ParseResult result, CancellationToken token)
    {
        var profile = ProfileReader.Read(result.GetValue(ProfileOption)!);
        var signing = new SigningOptions(Path.GetFullPath(result.GetValue(HomeOption)!), result.GetValue(KeyOption)!,
            Path.GetFullPath(result.GetValue(KeyringOption)!));
        var builder = new BundleBuilder(message => Console.Error.WriteLine(message));
        var built = await builder.BuildAsync(profile, result.GetValue(OutputOption)!, signing, token);
        Console.WriteLine(result.GetValue(JsonOption) ? BundleJson.Write(built) :
            $"Created: {built.File}\nTarget: {built.TargetVersion}\nPackages: {built.PackageCount}\nSHA256: {built.Content.Sha256}");
        return 0;
    }
}
