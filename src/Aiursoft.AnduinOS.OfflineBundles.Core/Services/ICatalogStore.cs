using Aiursoft.AnduinOS.OfflineBundles.Core.Models;

namespace Aiursoft.AnduinOS.OfflineBundles.Core.Services;

public interface ICatalogStore : IBundlePublicationStore
{
    Task<CatalogSnapshot> ReadAsync(CancellationToken token);
    // Returns false on a conditional-write conflict. Never overwrite an unseen revision.
    Task<bool> WriteAsync(ReleaseCatalog catalog, string? expectedETag, CancellationToken token);
    Task DeleteAsync(UpdatePublication publication, CancellationToken token);
}
