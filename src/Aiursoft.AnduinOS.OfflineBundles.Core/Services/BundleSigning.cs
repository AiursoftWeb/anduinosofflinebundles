using Aiursoft.AnduinOS.OfflineBundles.Core.Models;

namespace Aiursoft.AnduinOS.OfflineBundles.Core.Services;

public static class BundleSigning
{
    public static void ValidateEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
        {
            throw new ArgumentException("Configure an HTTPS ESRP Sign endpoint without credentials, query or fragment.",
                nameof(endpoint));
        }
    }

    public static async Task ValidateIdentityAsync(SigningOptions options, IBundleSigner signer,
        string? expectedSha256, CancellationToken token)
    {
        ValidateEndpoint(options.Endpoint);
        if (options.KeyId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty ESRP Sign key ID is required.", nameof(options));
        }

        if (!File.Exists(options.TrustedKeyring))
        {
            throw new FileNotFoundException("An external trusted keyring is required.", options.TrustedKeyring);
        }

        if (expectedSha256 != null && !string.Equals(expectedSha256,
                await BundleJson.HashAsync(options.TrustedKeyring, token), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The trusted keyring does not match its configured SHA-256.");
        }

        var localKey = await File.ReadAllBytesAsync(options.TrustedKeyring, token);
        var remoteKey = await signer.GetPublicKeyAsync(options.KeyId, token);
        if (!remoteKey.AsSpan().SequenceEqual(localKey))
        {
            throw new InvalidDataException("The ESRP Sign public key does not match the configured trusted keyring.");
        }
    }

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
