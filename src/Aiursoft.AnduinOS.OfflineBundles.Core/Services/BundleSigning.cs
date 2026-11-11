using Aiursoft.AnduinOS.OfflineBundles.Core.Models;

namespace Aiursoft.AnduinOS.OfflineBundles.Core.Services;

public static class BundleSigning
{
    public static Task SignAsync(string input, string output, SigningOptions options, bool clearSign,
        CancellationToken token) => NativeCommand.RunAsync("gpg",
        ["--homedir", options.Home, "--batch", "--no-tty", "--yes", "--local-user", options.Key,
            "--armor", "--output", output, clearSign ? "--clearsign" : "--detach-sign", input],
        cancellationToken: token);

    public static Task<string> VerifyAsync(string input, string signature, string trustedKeyring, CancellationToken token) =>
        VerifySignatureAsync([signature, input], trustedKeyring, token);

    public static Task<string> VerifyClearSignedAsync(string input, string output, string trustedKeyring, CancellationToken token) =>
        VerifySignatureAsync(["--output", output, input], trustedKeyring, token);

    private static async Task<string> VerifySignatureAsync(string[] arguments, string trustedKeyring, CancellationToken token)
    {
        if (!File.Exists(trustedKeyring))
        {
            throw new FileNotFoundException("An external trusted keyring is required.", trustedKeyring);
        }
        var home = Path.Combine(Path.GetTempPath(), "offline-gpgv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            var result = await NativeCommand.RunAsync("gpgv",
                ["--homedir", home, "--keyring", Path.GetFullPath(trustedKeyring), "--status-fd", "1", .. arguments],
                cancellationToken: token);
            if (new[] { "EXPKEYSIG", "EXPSIG", "REVKEYSIG", "KEYEXPIRED", "SIGEXPIRED" }
                .Any(status => result.Output.Contains("[GNUPG:] " + status, StringComparison.Ordinal)))
            {
                throw new InvalidDataException("The signature uses expired or revoked trust material.");
            }
            var valid = result.Output.Split('\n').SingleOrDefault(line => line.StartsWith("[GNUPG:] VALIDSIG ", StringComparison.Ordinal));
            return valid?.Split(' ', StringSplitOptions.RemoveEmptyEntries)[2]
                ?? throw new InvalidDataException("No valid signature was reported.");
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }
}
