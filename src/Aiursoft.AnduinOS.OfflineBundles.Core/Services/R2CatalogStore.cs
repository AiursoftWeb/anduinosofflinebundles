using System.Net;
using System.Text;
using Aiursoft.AnduinOS.OfflineBundles.Core.Models;
using Amazon.S3;
using Amazon.S3.Model;

namespace Aiursoft.AnduinOS.OfflineBundles.Core.Services;

public sealed class R2CatalogStore(IAmazonS3 client, string bucket) : ICatalogStore
{
    public const string CatalogKey = "offline/catalog.json";
    private readonly R2BundleStore _bundles = new(client, bucket);

    public Task UploadAsync(string file, UpdatePublication publication, CancellationToken token) => _bundles.UploadAsync(file, publication, token);
    public Task VerifyAsync(UpdatePublication publication, CancellationToken token) => _bundles.VerifyAsync(publication, token);
    public async Task DeleteAsync(UpdatePublication publication, CancellationToken token) =>
        await client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = publication.ObjectKey }, token);

    public async Task<CatalogSnapshot> ReadAsync(CancellationToken token)
    {
        try
        {
            using var response = await client.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = CatalogKey }, token);
            if (response.ContentLength is < 1 or > 4 * 1024 * 1024 || string.IsNullOrWhiteSpace(response.ETag))
                throw new InvalidDataException("Invalid catalog length or missing ETag.");
            using var reader = new StreamReader(response.ResponseStream, Encoding.UTF8);
            var catalog = BundleJson.Read<ReleaseCatalog>(await reader.ReadToEndAsync(token));
            CatalogPublisher.Validate(catalog);
            return new CatalogSnapshot(catalog, response.ETag);
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            return new CatalogSnapshot(new ReleaseCatalog(), null);
        }
    }

    public async Task<bool> WriteAsync(ReleaseCatalog catalog, string? expectedETag, CancellationToken token)
    {
        CatalogPublisher.Validate(catalog);
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(BundleJson.Write(catalog)));
        try
        {
            await client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket, Key = CatalogKey, InputStream = body, ContentType = "application/json",
                Headers = { CacheControl = "no-cache" },
                IfMatch = expectedETag, IfNoneMatch = expectedETag == null ? "*" : null,
                DisablePayloadSigning = true, DisableDefaultChecksumValidation = true
            }, token);
            return true;
        }
        catch (AmazonS3Exception error) when (error.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
        {
            return false;
        }
    }
}
