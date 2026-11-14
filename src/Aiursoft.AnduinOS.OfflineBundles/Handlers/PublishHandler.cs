using System.CommandLine;
using Aiursoft.AnduinOS.OfflineBundles.Core.Models;
using Aiursoft.AnduinOS.OfflineBundles.Core.Services;
using Amazon.Runtime;
using Amazon.S3;

namespace Aiursoft.AnduinOS.OfflineBundles.Handlers;

public sealed class PublishHandler : BundleCommandHandler
{
    private static readonly Option<string> SettingsOption = new("--settings") { Required = true, Description = "Publication JSON: service_url, bucket and home_url." };
    private static readonly Option<string> SummaryOption = new("--summary-file") { Required = true, Description = "UTF-8 plain-text release summary." };
    protected override string Name => "publish";
    protected override string Description => "Verify, upload and register an installation-qualified bundle. Does not schedule or delete releases.";
    protected override IEnumerable<Option> Options => [FileOption, KeyringOption, SettingsOption, SummaryOption, JsonOption];

    protected override async Task<int> ExecuteAsync(ParseResult result, CancellationToken token)
    {
        var settings = BundleJson.Read<PublicationSettings>(await File.ReadAllTextAsync(result.GetValue(SettingsOption)!, token));
        foreach (var address in new[] { settings.ServiceUrl, settings.HomeUrl })
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                throw new ArgumentException("Publication endpoints must be HTTPS URLs without credentials, query or fragment.");
        if (string.IsNullOrWhiteSpace(settings.Bucket)) throw new ArgumentException("A bucket is required.");
        var access = RequiredEnvironment("ANDUINOS_R2_ACCESS_KEY");
        var secret = RequiredEnvironment("ANDUINOS_R2_SECRET_KEY");
        var publisherToken = RequiredEnvironment("ANDUINOS_HOME_PUBLISHER_TOKEN");
        using var s3 = new AmazonS3Client(new BasicAWSCredentials(access, secret), new AmazonS3Config
        {
            ServiceURL = settings.ServiceUrl, ForcePathStyle = true, AuthenticationRegion = "auto",
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
        });
        using var handler = new HttpClientHandler();
        handler.AllowAutoRedirect = false;
        using var http = new HttpClient(handler);
        http.BaseAddress = new Uri(settings.HomeUrl.TrimEnd('/') + "/");
        http.Timeout = TimeSpan.FromMinutes(2);
        try
        {
            var publisher = new BundlePublisher(new R2BundleStore(s3, settings.Bucket), http);
            var published = await publisher.PublishAsync(result.GetValue(FileOption)!, result.GetValue(KeyringOption)!,
                (await File.ReadAllTextAsync(result.GetValue(SummaryOption)!, token)).Trim(), publisherToken, token);
            Console.WriteLine(result.GetValue(JsonOption) ? BundleJson.Write(published) : $"Published AnduinOS {published.TargetVersion} ({published.Architecture}). Batch: {published.BatchId}");
            return 0;
        }
        catch (Exception error) when (error is AmazonS3Exception or HttpRequestException)
        {
            await Console.Error.WriteLineAsync("Publication could not be confirmed. Retry the same batch; existing releases will not be overwritten.");
            return 1;
        }
    }

    private static string RequiredEnvironment(string name) =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))
            ? Environment.GetEnvironmentVariable(name)!
            : throw new ArgumentException($"Set {name} in the publishing environment.");
}
