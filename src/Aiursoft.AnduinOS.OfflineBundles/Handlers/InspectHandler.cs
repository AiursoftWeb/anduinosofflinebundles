using System.CommandLine;
using Aiursoft.AnduinOS.OfflineBundles.Core.Services;

namespace Aiursoft.AnduinOS.OfflineBundles.Handlers;

public sealed class InspectHandler : BundleCommandHandler
{
    protected override string Name => "inspect";
    protected override string Description => "Inspect a verified bundle, optionally resolving a read-only update plan for an installed baseline.";
    private static readonly Option<string> StatusOption = new("--status") { Description = "A dpkg status snapshot to preview; no installation takes place." };
    private static readonly Option<string> OsReleaseOption = new("--os-release") { Description = "The matching os-release snapshot; required with --status." };
    protected override IEnumerable<Option> Options => [FileOption, KeyringOption, StatusOption, OsReleaseOption, JsonOption];

    protected override async Task<int> ExecuteAsync(ParseResult result, CancellationToken token)
    {
        var status = result.GetValue(StatusOption);
        var os = result.GetValue(OsReleaseOption);
        if ((status == null) != (os == null))
        {
            throw new ArgumentException("--status and --os-release must be supplied together.");
        }
        var file = result.GetValue(FileOption)!;
        var keyring = result.GetValue(KeyringOption)!;
        if (status != null)
        {
            var preview = await BundlePreview.PreviewAsync(file, keyring, status, os!, token);
            Console.WriteLine(result.GetValue(JsonOption) ? BundleJson.Write(preview) :
                $"{preview.CurrentVersion} -> {preview.TargetVersion}: {preview.State}\n" +
                $"Additional staging: {preview.AdditionalStagingBytes} bytes (excludes installation and snapshots).\n" +
                string.Join('\n', preview.Blockers));
            return preview.Blockers.Count == 0 ? 0 : 2;
        }
        var verified = await BundleArchive.VerifyAsync(file, keyring, token: token);
        Console.WriteLine(result.GetValue(JsonOption) ? BundleJson.Write(verified) :
            $"Verified: AnduinOS {verified.Manifest.TargetVersion} / {verified.Manifest.Architecture}\n" +
            $"Batch: {verified.Manifest.BatchId}\nPackages: {verified.Manifest.Packages.Count}\nUnpacked: {verified.UnpackedBytes} bytes");
        return 0;
    }
}
