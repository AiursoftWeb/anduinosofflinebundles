using System.Net;
using System.Net.Http.Json;
using Aiursoft.AnduinOS.OfflineBundles.Core.Models;
using Aiursoft.AnduinOS.OfflineBundles.Core.Services;

namespace Aiursoft.AnduinOS.OfflineBundles.Tests;

[TestClass]
public class PublicationTests
{
    [TestMethod]
    public async Task VerifiedUploadPrecedesRegistrationAndFailedRegistrationCanBeRetried()
    {
        using var fixture = new BundleFixture();
        await fixture.InitializeAsync();
        var build = await new BundleBuilder(fixture.Signer).BuildAsync(fixture.Profile, fixture.Output, fixture.Signing);
        var store = new PublicationStore();
        using var handler = new RegistrationHandler(store);
        using var http = new HttpClient(handler);
        http.BaseAddress = new Uri("https://home.example/");
        var publisher = new BundlePublisher(store, http);
        handler.Status = HttpStatusCode.ServiceUnavailable;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            publisher.PublishAsync(build.File, fixture.Keyring, "Security updates.", "test-publisher", default));
        Assert.IsNotNull(handler.Publication);
        var first = handler.Publication;
        handler.Status = HttpStatusCode.OK;
        var result = await publisher.PublishAsync(build.File, fixture.Keyring, "Security updates.", "test-publisher", default);
        Assert.AreEqual(first, result);
        Assert.AreEqual("2.0.5", result.TargetVersion);
        Assert.AreEqual("2.0.0", result.MinimumVersion);
        Assert.AreEqual(await BundleJson.HashAsync(build.File), result.Sha256);
        Assert.AreEqual(new FileInfo(build.File).Length, result.Size);
        Assert.AreEqual(2, handler.Calls);
        Assert.AreEqual(1, store.Objects.Count);
        CollectionAssert.AreEqual(new[] { "upload", "verify", "register", "upload", "verify", "register" }, store.Events);
    }

    [TestMethod]
    public async Task FailedStorageVerificationCannotAdvertiseAnUpdate()
    {
        using var fixture = new BundleFixture();
        await fixture.InitializeAsync();
        var build = await new BundleBuilder(fixture.Signer).BuildAsync(fixture.Profile, fixture.Output, fixture.Signing);
        var store = new PublicationStore { FailVerification = true };
        using var handler = new RegistrationHandler(store);
        using var http = new HttpClient(handler);
        http.BaseAddress = new Uri("https://home.example/");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new BundlePublisher(store, http)
            .PublishAsync(build.File, fixture.Keyring, "Security updates.", "test-publisher", default));
        Assert.AreEqual(0, handler.Calls);
        Assert.AreEqual(1, store.Objects.Count);
    }

    [TestMethod]
    public async Task InvalidArchiveCannotReachStorageOrWebsite()
    {
        using var fixture = new BundleFixture();
        await fixture.InitializeAsync();
        var file = Path.Combine(fixture.Root, "invalid.atgz");
        await File.WriteAllTextAsync(file, "not a signed archive");
        var store = new PublicationStore();
        using var handler = new RegistrationHandler(store);
        using var http = new HttpClient(handler);
        http.BaseAddress = new Uri("https://home.example/");
        await Assert.ThrowsAsync<Exception>(() => new BundlePublisher(store, http)
            .PublishAsync(file, fixture.Keyring, "Security updates.", "test-publisher", default));
        Assert.IsEmpty(store.Events);
        Assert.AreEqual(0, handler.Calls);
    }

    private sealed class PublicationStore : IBundlePublicationStore
    {
        public readonly List<string> Events = [];
        public readonly Dictionary<string, byte[]> Objects = [];
        public bool FailVerification { get; init; }
        public async Task UploadAsync(string file, UpdatePublication publication, CancellationToken token)
        {
            Events.Add("upload");
            Objects.TryAdd(publication.ObjectKey, await File.ReadAllBytesAsync(file, token));
        }
        public Task VerifyAsync(UpdatePublication publication, CancellationToken token)
        {
            Events.Add("verify");
            if (FailVerification) throw new InvalidDataException("Simulated corrupt upload.");
            Assert.IsTrue(Objects.ContainsKey(publication.ObjectKey));
            return Task.CompletedTask;
        }
    }

    private sealed class RegistrationHandler(PublicationStore store) : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.Created;
        public UpdatePublication? Publication { get; private set; }
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("https://home.example/api/offline-updates", request.RequestUri!.AbsoluteUri);
            Assert.AreEqual("Bearer test-publisher", request.Headers.Authorization!.ToString());
            Assert.AreEqual("verify", store.Events[^1]);
            store.Events.Add("register");
            Calls++;
            Publication = await request.Content!.ReadFromJsonAsync<UpdatePublication>(token);
            return new HttpResponseMessage(Status);
        }
    }
}
