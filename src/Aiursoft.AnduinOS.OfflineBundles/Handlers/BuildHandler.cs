using System.CommandLine;
using Aiursoft.AnduinOS.OfflineBundles.Core.Models;
using Aiursoft.AnduinOS.OfflineBundles.Core.Services;

namespace Aiursoft.AnduinOS.OfflineBundles.Handlers;

public sealed class BuildHandler : BundleCommandHandler
{
    protected override string Name => "build";
    protected override string Description => "Build a signed cumulative bundle from authenticated APT sources and baseline package databases.";
    private static readonly Option<string> ProfileOption = new("--profile") { Required = true, Description = "Build profile JSON." };
    private static readonly Option<string?> InputsOption = new("--inputs") { Description = "Directory for baseline files and source keyrings; defaults to the profile directory." };
    private static readonly Option<string> OutputOption = new("--output") { Required = true, Description = "Output directory." };
    private static readonly Option<string> EsrpEndpointOption = new("--esrp-endpoint") { Required = true, Description = "ESRP Sign HTTPS endpoint." };
    private static readonly Option<Guid> EsrpKeyIdOption = new("--esrp-key-id") { Required = true, Description = "ESRP Sign company signing key ID." };
    protected override IEnumerable<Option> Options => [ProfileOption, InputsOption, OutputOption, EsrpEndpointOption, EsrpKeyIdOption, KeyringOption, JsonOption];

    protected override async Task<int> ExecuteAsync(ParseResult result, CancellationToken token)
    {
        var profile = ProfileReader.Read(result.GetValue(ProfileOption)!, result.GetValue(InputsOption));
        var endpoint = result.GetValue(EsrpEndpointOption)!;
        var keyId = result.GetValue(EsrpKeyIdOption);
        var apiKey = Environment.GetEnvironmentVariable("ESRPSIGN_API_KEY");
        var passphrase = Environment.GetEnvironmentVariable("ESRPSIGN_KEY_PASSPHRASE");
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(passphrase))
            throw new ArgumentException("Set ESRPSIGN_API_KEY and ESRPSIGN_KEY_PASSPHRASE.");
        var signing = new SigningOptions(endpoint, keyId, Path.GetFullPath(result.GetValue(KeyringOption)!));
        using var signer = new EsrpBundleSigner(endpoint, apiKey, passphrase);
        await BundleSigning.ValidateIdentityAsync(signing, signer, null, token);
        var builder = new BundleBuilder(signer, message => Console.Error.WriteLine(message));
        var built = await builder.BuildAsync(profile, result.GetValue(OutputOption)!, signing, token);
        Console.WriteLine(result.GetValue(JsonOption) ? BundleJson.Write(built) :
            $"Created: {built.File}\nTarget: {built.TargetVersion}\nPackages: {built.PackageCount}\nSHA256: {built.Content.Sha256}");
        return 0;
    }
}
