using System.Net;
using System.Security.Cryptography;
using Aiursoft.AnduinOS.OfflineBundles.Core.Models;
using Amazon.S3;
using Amazon.S3.Model;

namespace Aiursoft.AnduinOS.OfflineBundles.Core.Services;

public sealed class R2BundleStore(IAmazonS3 client, string bucket) : IBundlePublicationStore
{
    private const int PartSize = 64 * 1024 * 1024;

    public async Task UploadAsync(string file, UpdatePublication publication, CancellationToken token)
    {
        try
        {
            var existing = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
                { BucketName = bucket, Key = publication.ObjectKey }, token);
            if (existing.ContentLength != publication.Size ||
                existing.Metadata["x-amz-meta-sha256"] != publication.Sha256 ||
                existing.Metadata["x-amz-meta-batch-id"] != publication.BatchId)
                throw new InvalidDataException("The immutable object already exists with different metadata.");
            return;
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound) { }

        var initiate = new InitiateMultipartUploadRequest
            { BucketName = bucket, Key = publication.ObjectKey, ContentType = "application/octet-stream" };
        initiate.Metadata.Add("sha256", publication.Sha256);
        initiate.Metadata.Add("batch-id", publication.BatchId);
        var upload = await client.InitiateMultipartUploadAsync(initiate, token);
        var completed = false;
        try
        {
            await using var input = File.OpenRead(file);
            var buffer = new byte[PartSize];
            var parts = new List<PartETag>();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            while (true)
            {
                var count = 0;
                while (count < buffer.Length)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(count), token);
                    if (read == 0) break;
                    count += read;
                }
                if (count == 0) break;
                hash.AppendData(buffer, 0, count);
                total += count;
                using var part = new MemoryStream(buffer, 0, count, writable: false);
                var response = await client.UploadPartAsync(new UploadPartRequest
                {
                    BucketName = bucket, Key = publication.ObjectKey, UploadId = upload.UploadId,
                    PartNumber = parts.Count + 1, PartSize = count, InputStream = part,
                    DisablePayloadSigning = true, DisableDefaultChecksumValidation = true
                }, token);
                parts.Add(new PartETag(parts.Count + 1, response.ETag));
            }
            if (total != publication.Size || Convert.ToHexStringLower(hash.GetHashAndReset()) != publication.Sha256)
                throw new IOException("The archive changed during upload; no publication was made.");
            await client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = bucket, Key = publication.ObjectKey, UploadId = upload.UploadId, PartETags = parts
            }, token);
            completed = true;
        }
        finally
        {
            if (!completed)
            {
                // Best effort: interrupted uploads can also be expired by the bucket's multipart lifecycle policy.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try
                {
                    await client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
                        { BucketName = bucket, Key = publication.ObjectKey, UploadId = upload.UploadId }, cleanup.Token);
                }
                catch (Exception error) when (error is AmazonS3Exception or HttpRequestException or OperationCanceledException) { }
            }
        }
    }

    public async Task VerifyAsync(UpdatePublication publication, CancellationToken token)
    {
        using var response = await client.GetObjectAsync(new GetObjectRequest
            { BucketName = bucket, Key = publication.ObjectKey }, token);
        if (response.ContentLength != publication.Size)
            throw new InvalidDataException("Uploaded object size does not match the verified archive.");
        var digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(response.ResponseStream, token));
        if (digest != publication.Sha256)
            throw new InvalidDataException("Uploaded object SHA256 does not match the verified archive.");
    }
}
