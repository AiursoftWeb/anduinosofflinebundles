namespace Aiursoft.AnduinOS.OfflineBundles.Core.Services;

public interface IBundleSigner
{
    Task<byte[]> GetPublicKeyAsync(Guid keyId, CancellationToken token);
    Task SignAsync(Guid keyId, string inputPath, string outputPath, bool clearSign, CancellationToken token);
}
