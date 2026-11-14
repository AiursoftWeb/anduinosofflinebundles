using System.Net.Http.Headers;
using System.Net.Http.Json;
using Aiursoft.AnduinOS.OfflineBundles.Core.Models;

namespace Aiursoft.AnduinOS.OfflineBundles.Core.Services;

public sealed class BundlePublisher(IBundlePublicationStore store, HttpClient home)
{
    public async Task<UpdatePublication> PublishAsync(string file, string keyring, string summary,
        string publisherToken, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(summary) || summary.Length > 2000)
            throw new ArgumentException("Provide an update summary of 1-2000 characters.");
        if (string.IsNullOrWhiteSpace(publisherToken)) throw new ArgumentException("A Home publisher token is required.");
        var publication = await DescribeAsync(file, keyring, summary, token);
        await store.UploadAsync(file, publication, token);
        await store.VerifyAsync(publication, token);
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/offline-updates");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", publisherToken);
        request.Content = JsonContent.Create(publication);
        using var response = await home.SendAsync(request, token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Home rejected publication (HTTP {(int)response.StatusCode}). The uploaded object is retained; retry the same batch.");
        return publication;
    }
    public static async Task<UpdatePublication> DescribeAsync(string file, string keyring, string summary, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(summary) || summary.Length > 2000)
            throw new ArgumentException("Provide an update summary of 1-2000 characters.");
        // Integrity is necessary, but installation qualification remains a release prerequisite.
        var digest = await BundleJson.HashAsync(file, token);
        var verified = await BundleArchive.VerifyAsync(file, keyring, token: token);
        var manifest = verified.Manifest;
        if (digest != await BundleJson.HashAsync(file, token))
            throw new IOException("The archive changed during verification.");
        var publication = new UpdatePublication(manifest.BatchId, manifest.ReleaseLine, manifest.Suite,
            manifest.Architecture, manifest.TargetVersion,
            manifest.Baselines.Select(b => Version.Parse(b.SystemVersion)).Min()!.ToString(),
            digest, verified.SigningFingerprint, new FileInfo(file).Length, manifest.CreatedUtc.UtcDateTime, summary);
        return publication;
    }

}
