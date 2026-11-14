using Aiursoft.AnduinOS.OfflineBundles.Core.Models;
using Aiursoft.AnduinOS.OfflineBundles.Core.Services;

namespace Aiursoft.AnduinOS.OfflineBundles.Tests;

[TestClass]
public class AutomationTests
{
    [TestMethod]
    public async Task FourBatchesRetainThreeAndDeleteOnlyAfterGrace()
    {
        using var fixture = new BundleFixture();
        await fixture.InitializeAsync();
        var clock = new ManualClock();
        var store = new CatalogStorage();
        var publisher = new CatalogPublisher(store, clock);
        CatalogRelease? first = null;
        for (var version = 5; version <= 8; version++)
        {
            if (version != 5)
            {
                await fixture.AddPackageAsync("anduinos-core-system", $"2.0.{version}-1", "base-library (>= 5)", osVersion: $"2.0.{version}");
                await fixture.PublishRepositoryAsync();
            }
            var built = await new BundleBuilder().BuildAsync(fixture.Profile, fixture.Output, fixture.Signing);
            var release = await publisher.PublishAsync(built.File, fixture.Keyring, "Cumulative updates.", default);
            first ??= release;
            Assert.AreEqual(version.ToString(), release.Update.TargetVersion.Split('.')[2]);
        }
        Assert.HasCount(3, store.Catalog.Releases);
        Assert.HasCount(1, store.Catalog.Retired);
        Assert.AreEqual(first!.ObjectKey, store.Catalog.Retired[0].Release.ObjectKey);
        await publisher.CleanupAsync(default);
        Assert.IsEmpty(store.Deleted);
        clock.Advance(TimeSpan.FromHours(25));
        store.FailDelete = true;
        await Assert.ThrowsExactlyAsync<IOException>(() => publisher.CleanupAsync(default));
        Assert.HasCount(1, store.Catalog.Retired);
        store.FailDelete = false;
        await publisher.CleanupAsync(default);
        CollectionAssert.AreEqual(new[] { first.ObjectKey }, store.Deleted);
        Assert.IsEmpty(store.Catalog.Retired);
        Assert.HasCount(3, store.Catalog.Releases);
        Assert.IsTrue(BundleJson.Write(store.Catalog).Contains("\"object_key\""));
    }

    [TestMethod]
    public async Task LostCatalogResponseAndRestartResumeSameArchiveWithoutRebuilding()
    {
        using var fixture = new BundleFixture();
        await fixture.InitializeAsync();
        var profilePath = Path.Combine(fixture.Root, "profile.json");
        await File.WriteAllTextAsync(profilePath, BundleJson.Write(fixture.Profile));
        var settings = new AutomationSettings
        {
            ServiceUrl = "https://storage.example", Bucket = "updates", StateDirectory = Path.Combine(fixture.Root, "worker"),
            Signing = fixture.Signing, Jobs = [new AutomationJob("resolute-amd64", profilePath, fixture.Root, "Cumulative updates.")]
        };
        var clock = new ManualClock();
        var store = new CatalogStorage { LoseWriteResponse = true };
        var builds = 0;
        async Task<BuildResult> Build(BuildProfile profile, string output, SigningOptions signing, CancellationToken token)
        {
            builds++;
            return await new BundleBuilder().BuildAsync(profile, output, signing, token);
        }
        var worker = new AutomationWorker(settings, store, _ => { }, clock, Build);
        Assert.AreEqual(1, await worker.RunAsync(true, default));
        Assert.HasCount(1, store.Catalog.Releases); // Remote write succeeded, local acknowledgement did not.
        Assert.AreEqual(1, builds);
        Assert.AreEqual(0, await new AutomationWorker(settings, store, _ => { }, clock, Build).RunAsync(true, default));
        Assert.AreEqual(1, builds); // Restart respects the retry schedule.
        clock.Advance(TimeSpan.FromMinutes(61));
        store.LoseWriteResponse = false;
        Assert.AreEqual(0, await new AutomationWorker(settings, store, _ => { }, clock, Build).RunAsync(true, default));
        Assert.AreEqual(1, builds);
        Assert.AreEqual(1, store.Uploads);
        Assert.HasCount(1, store.Catalog.Releases);
        var state = BundleJson.Read<AutomationState>(await File.ReadAllTextAsync(Path.Combine(settings.StateDirectory, "schedule.json")));
        Assert.IsNull(state.Jobs["resolute-amd64"].PendingFile);
        Assert.AreEqual(clock.GetUtcNow().AddHours(48), state.Jobs["resolute-amd64"].NextAttemptUtc);
    }

    [TestMethod]
    public async Task FailedVerificationPreservesCatalogAndConditionalConflictIsRetried()
    {
        using var fixture = new BundleFixture();
        await fixture.InitializeAsync();
        var build = await new BundleBuilder().BuildAsync(fixture.Profile, fixture.Output, fixture.Signing);
        var store = new CatalogStorage { FailVerification = true };
        var publisher = new CatalogPublisher(store);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => publisher.PublishAsync(build.File, fixture.Keyring, "Updates.", default));
        Assert.IsEmpty(store.Catalog.Releases);
        store.FailVerification = false;
        store.Conflicts = 1;
        var release = await publisher.PublishAsync(build.File, fixture.Keyring, "Updates.", default);
        Assert.HasCount(1, store.Catalog.Releases);
        Assert.AreEqual(0, store.Conflicts);
        Assert.AreEqual(release, await publisher.PublishAsync(build.File, fixture.Keyring, "Updates.", default));
        Assert.HasCount(1, store.Catalog.Releases);
        Assert.AreEqual("verify", store.Events[^1]);
    }

    [TestMethod]
    public async Task OlderTargetIsRejectedBeforeUploadAndOtherGroupIsPreserved()
    {
        using var fixture = new BundleFixture();
        await fixture.InitializeAsync();
        var build = await new BundleBuilder().BuildAsync(fixture.Profile, fixture.Output, fixture.Signing);
        var update = await BundlePublisher.DescribeAsync(build.File, fixture.Keyring, "Updates.", default);
        var newer = new CatalogRelease(update with { BatchId = new string('b', 64), TargetVersion = "2.0.10" }, DateTimeOffset.UtcNow);
        var other = new CatalogRelease(update with { BatchId = new string('c', 64), Architecture = "arm64" }, DateTimeOffset.UtcNow);
        var store = new CatalogStorage { Catalog = new ReleaseCatalog { Releases = [newer, other] } };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new CatalogPublisher(store).PublishAsync(build.File, fixture.Keyring, "Updates.", default));
        Assert.AreEqual(0, store.Uploads);
        CollectionAssert.AreEqual(new[] { newer, other }, store.Catalog.Releases);
    }

    [TestMethod]
    public async Task AFailedJobDoesNotBlockAnotherGroupAndLifetimeLockPreventsOverlap()
    {
        using var fixture = new BundleFixture();
        await fixture.InitializeAsync();
        var profilePath = Path.Combine(fixture.Root, "profile.json");
        await File.WriteAllTextAsync(profilePath, BundleJson.Write(fixture.Profile));
        var settings = new AutomationSettings
        {
            ServiceUrl = "https://storage.example", Bucket = "updates", StateDirectory = Path.Combine(fixture.Root, "worker"), Signing = fixture.Signing,
            Jobs = [new AutomationJob("broken", Path.Combine(fixture.Root, "missing.json"), fixture.Root, "Updates."),
                new AutomationJob("working", profilePath, fixture.Root, "Updates.")]
        };
        var store = new CatalogStorage();
        var worker = new AutomationWorker(settings, store, _ => { });
        Directory.CreateDirectory(settings.StateDirectory);
        await using (new FileStream(Path.Combine(settings.StateDirectory, "worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsExactlyAsync<IOException>(() => worker.RunAsync(true, default));
        }
        Assert.AreEqual(1, await worker.RunAsync(true, default));
        Assert.HasCount(1, store.Catalog.Releases);
        var retrySettings = settings with { StateDirectory = Path.Combine(fixture.Root, "timeout-worker"), Jobs = [settings.Jobs[1]] };
        store.TimeoutVerification = true;
        Assert.AreEqual(1, await new AutomationWorker(retrySettings, store, _ => { }).RunAsync(true, default));
        var pending = BundleJson.Read<AutomationState>(await File.ReadAllTextAsync(Path.Combine(retrySettings.StateDirectory, "schedule.json")));
        Assert.IsNotNull(pending.Jobs["working"].PendingFile);
        var state = BundleJson.Read<AutomationState>(await File.ReadAllTextAsync(Path.Combine(settings.StateDirectory, "schedule.json")));
        Assert.IsTrue(state.Jobs["broken"].NextAttemptUtc < state.Jobs["working"].NextAttemptUtc);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class CatalogStorage : ICatalogStore
    {
        public ReleaseCatalog Catalog { get; set; } = new();
        public readonly List<string> Events = [];
        public readonly List<string> Deleted = [];
        public int Uploads { get; private set; }
        public bool FailVerification { get; set; }
        public bool TimeoutVerification { get; set; }
        public bool LoseWriteResponse { get; set; }
        public bool FailDelete { get; set; }
        public int Conflicts { get; set; }
        private int _revision;
        public Task<CatalogSnapshot> ReadAsync(CancellationToken token) => Task.FromResult(new CatalogSnapshot(Catalog, _revision.ToString()));
        public Task<bool> WriteAsync(ReleaseCatalog catalog, string? expectedETag, CancellationToken token)
        {
            Events.Add("catalog");
            if (Conflicts > 0) { Conflicts--; _revision++; return Task.FromResult(false); }
            Assert.AreEqual(expectedETag, _revision.ToString());
            Catalog = catalog;
            _revision++;
            if (LoseWriteResponse) throw new IOException("Acknowledgement lost.");
            return Task.FromResult(true);
        }
        public Task UploadAsync(string file, UpdatePublication publication, CancellationToken token)
        {
            Events.Add("upload"); Uploads++; return Task.CompletedTask;
        }
        public Task VerifyAsync(UpdatePublication publication, CancellationToken token)
        {
            Events.Add("verify");
            if (TimeoutVerification) throw new TaskCanceledException("Storage request timed out.");
            if (FailVerification) throw new InvalidDataException("Corrupt object.");
            return Task.CompletedTask;
        }
        public Task DeleteAsync(UpdatePublication publication, CancellationToken token)
        {
            Assert.IsFalse(Catalog.Releases.Any(x => x.ObjectKey == publication.ObjectKey));
            if (FailDelete) throw new IOException("Delete unavailable.");
            Deleted.Add(publication.ObjectKey); return Task.CompletedTask;
        }
    }
}
