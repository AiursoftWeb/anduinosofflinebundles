using System.Net;
using System.Security.Cryptography;
using Aiursoft.AnduinOS.OfflineBundles.Core.Models;
using Aiursoft.AnduinOS.OfflineBundles.Core.Services;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace Aiursoft.AnduinOS.OfflineBundles.Tests;

[TestClass]
public class R2PublicationTests
{
    [TestMethod]
    public async Task MultipartUploadPreservesBytesAndRepeatedPublicationDoesNotUploadAgain()
    {
        var directory = Directory.CreateTempSubdirectory("offline-publication-");
        try
        {
            var file = Path.Combine(directory.FullName, "update.atgz");
            await using (var output = File.Create(file))
            {
                output.SetLength(64 * 1024 * 1024 + 17);
                output.Position = output.Length - 1;
                output.WriteByte(42);
            }
            var publication = await Publication(file);
            using var client = new StorageClient(file);
            var store = new R2BundleStore(client, "updates");
            await store.UploadAsync(file, publication, default);
            Assert.IsTrue(client.Completed);
            Assert.IsFalse(client.Aborted);
            CollectionAssert.AreEqual(new long[] { 64 * 1024 * 1024, 17 }, client.PartLengths);
            Assert.AreEqual(publication.Sha256, client.UploadedHash);
            await store.VerifyAsync(publication, default);
            await store.UploadAsync(file, publication, default);
            Assert.HasCount(2, client.PartLengths);
            Assert.AreEqual("application/octet-stream", client.ContentType);
        }
        finally { directory.Delete(recursive: true); }
    }

    [TestMethod]
    public async Task InterruptedUploadAbortsWithoutCompletingAnObject()
    {
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(file, [1, 2, 3]);
            using var client = new StorageClient(file) { FailUpload = true };
            var publication = await Publication(file);
            await Assert.ThrowsExactlyAsync<IOException>(() =>
                new R2BundleStore(client, "updates").UploadAsync(file, publication, default));
            Assert.IsTrue(client.Aborted);
            Assert.IsFalse(client.Completed);
        }
        finally { File.Delete(file); }
    }

    [TestMethod]
    public async Task WrongBytesOrConflictingObjectMetadataAreRejected()
    {
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(file, [1, 2, 3]);
            var publication = await Publication(file);
            using var client = new StorageClient(file);
            var store = new R2BundleStore(client, "updates");
            await store.UploadAsync(file, publication, default);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.UploadAsync(file, publication with { Size = 9 }, default));
            await File.WriteAllBytesAsync(file, [3, 2, 1]);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.VerifyAsync(publication, default));
        }
        finally { File.Delete(file); }
    }

    private static async Task<UpdatePublication> Publication(string file) => new(
        new string('a', 64), "2.0", "resolute", "amd64", "2.0.5", "2.0.0",
        await BundleJson.HashAsync(file), new string('B', 40), new FileInfo(file).Length, DateTime.UtcNow, "Security updates.");

    private sealed class StorageClient(string file) : AmazonS3Client(new BasicAWSCredentials("test", "test"),
        new AmazonS3Config { ServiceURL = "https://storage.example", ForcePathStyle = true, AuthenticationRegion = "auto" })
    {
        public readonly List<long> PartLengths = [];
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private MetadataCollection _metadata = new();
        public bool Completed { get; private set; }
        public bool Aborted { get; private set; }
        public bool FailUpload { get; init; }
        public string? UploadedHash { get; private set; }
        public string? ContentType { get; private set; }

        public override Task<GetObjectMetadataResponse> GetObjectMetadataAsync(GetObjectMetadataRequest request, CancellationToken cancellationToken = default)
        {
            if (!Completed) throw new AmazonS3Exception("Not found") { StatusCode = HttpStatusCode.NotFound };
            var response = new GetObjectMetadataResponse { ContentLength = new FileInfo(file).Length };
            foreach (var key in _metadata.Keys) response.Metadata.Add(key, _metadata[key]);
            return Task.FromResult(response);
        }
        public override Task<InitiateMultipartUploadResponse> InitiateMultipartUploadAsync(InitiateMultipartUploadRequest request, CancellationToken cancellationToken = default)
        {
            _metadata = request.Metadata;
            ContentType = request.ContentType;
            return Task.FromResult(new InitiateMultipartUploadResponse { UploadId = "test-upload" });
        }
        public override async Task<UploadPartResponse> UploadPartAsync(UploadPartRequest request, CancellationToken cancellationToken = default)
        {
            if (FailUpload) throw new IOException("Interrupted upload");
            Assert.AreEqual(true, request.DisablePayloadSigning);
            Assert.AreEqual(true, request.DisableDefaultChecksumValidation);
            Assert.AreEqual(PartLengths.Count + 1, request.PartNumber);
            PartLengths.Add(request.InputStream.Length);
            var buffer = new byte[8192];
            int count;
            while ((count = await request.InputStream.ReadAsync(buffer, cancellationToken)) != 0) _hash.AppendData(buffer, 0, count);
            return new UploadPartResponse { ETag = "part-" + request.PartNumber };
        }
        public override Task<CompleteMultipartUploadResponse> CompleteMultipartUploadAsync(CompleteMultipartUploadRequest request, CancellationToken cancellationToken = default)
        {
            Assert.HasCount(PartLengths.Count, request.PartETags);
            Completed = true;
            UploadedHash = Convert.ToHexStringLower(_hash.GetHashAndReset());
            return Task.FromResult(new CompleteMultipartUploadResponse());
        }
        public override Task<AbortMultipartUploadResponse> AbortMultipartUploadAsync(AbortMultipartUploadRequest request, CancellationToken cancellationToken = default)
        {
            Aborted = true;
            return Task.FromResult(new AbortMultipartUploadResponse());
        }
        public override Task<GetObjectResponse> GetObjectAsync(GetObjectRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GetObjectResponse { ContentLength = new FileInfo(file).Length, ResponseStream = File.OpenRead(file) });
        protected override void Dispose(bool disposing)
        {
            if (disposing) _hash.Dispose();
            base.Dispose(disposing);
        }
    }
}
