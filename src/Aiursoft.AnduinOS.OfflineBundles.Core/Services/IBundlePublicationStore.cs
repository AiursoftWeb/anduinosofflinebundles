using Aiursoft.AnduinOS.OfflineBundles.Core.Models;

namespace Aiursoft.AnduinOS.OfflineBundles.Core.Services;

public interface IBundlePublicationStore
{
    Task UploadAsync(string file, UpdatePublication publication, CancellationToken token);
    Task VerifyAsync(UpdatePublication publication, CancellationToken token);
}
