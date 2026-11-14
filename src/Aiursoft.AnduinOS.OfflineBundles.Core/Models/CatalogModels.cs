using System.Text.Json.Serialization;

// ReSharper disable NotAccessedPositionalProperty.Global
namespace Aiursoft.AnduinOS.OfflineBundles.Core.Models;

public sealed record CatalogRelease(UpdatePublication Update, DateTimeOffset PublishedUtc)
{
    public string ObjectKey => Update.ObjectKey;
}

public sealed record RetiredRelease(CatalogRelease Release, DateTimeOffset DeleteAfterUtc);

public sealed record ReleaseCatalog
{
    [JsonRequired] public int SchemaVersion { get; init; } = 1;
    public List<CatalogRelease> Releases { get; init; } = [];
    public List<RetiredRelease> Retired { get; init; } = [];
}

public sealed record CatalogSnapshot(ReleaseCatalog Catalog, string? ETag);
