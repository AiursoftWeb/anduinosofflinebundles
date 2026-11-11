using System.CommandLine;
using Aiursoft.AnduinOS.OfflineBundles.Core.Services;

namespace Aiursoft.AnduinOS.OfflineBundles.Handlers;

public sealed class VerifyHandler : BundleCommandHandler
{
    protected override string Name => "verify";
    protected override string Description => "Verify an archive's signed manifest and every declared payload file.";
    protected override IEnumerable<Option> Options => [FileOption, KeyringOption, JsonOption];

    protected override async Task<int> ExecuteAsync(ParseResult result, CancellationToken token)
    {
        var verified = await BundleArchive.VerifyAsync(result.GetValue(FileOption)!, result.GetValue(KeyringOption)!, token: token);
        Console.WriteLine(result.GetValue(JsonOption) ? BundleJson.Write(verified) :
            $"Verified {verified.Manifest.Packages.Count} packages. Signer: {verified.SigningFingerprint}\n" +
            "This checks archive integrity; it does not certify a completed system installation.");
        return 0;
    }
}
