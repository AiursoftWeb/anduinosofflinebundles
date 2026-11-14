using System.Text.Json.Serialization;

namespace Aiursoft.AnduinOS.OfflineBundles.Core.Models;

public sealed record PublicationSettings
{
    public required string ServiceUrl { get; init; }
    public required string Bucket { get; init; }
    public required string HomeUrl { get; init; }
}

public sealed record UpdatePublication(string BatchId, string ReleaseLine, string Suite, string Architecture,
    string TargetVersion, string MinimumVersion, string Sha256, string SigningFingerprint, long Size,
    DateTime CreatedUtc, string Summary)
{
    [JsonIgnore] public string ObjectKey => $"offline/{Suite}/{ReleaseLine}/{Architecture}/{BatchId}/{Sha256}.atgz";
}
