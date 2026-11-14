using System.CommandLine;
using System.Runtime.InteropServices;
using Aiursoft.AnduinOS.OfflineBundles.Core.Models;
using Aiursoft.AnduinOS.OfflineBundles.Core.Services;
using Amazon.Runtime;
using Amazon.S3;

namespace Aiursoft.AnduinOS.OfflineBundles.Handlers;

public sealed class AutomateHandler : BundleCommandHandler
{
    private static readonly Option<string> SettingsOption = new("--settings") { Required = true, Description = "Automation settings JSON (no Home endpoint required)." };
    private static readonly Option<bool> OnceOption = new("--once") { Description = "Process due jobs once and exit; otherwise keep running." };
    protected override string Name => "automate";
    protected override string Description => "Periodically build, verify and publish cumulative updates and their catalog to R2.";
    protected override IEnumerable<Option> Options => [SettingsOption, OnceOption];

    protected override async Task<int> ExecuteAsync(ParseResult result, CancellationToken token)
    {
        var settings = BundleJson.Read<AutomationSettings>(await File.ReadAllTextAsync(result.GetValue(SettingsOption)!, token));
        AutomationWorker.Validate(settings);
        var access = Environment.GetEnvironmentVariable("ANDUINOS_R2_ACCESS_KEY");
        var secret = Environment.GetEnvironmentVariable("ANDUINOS_R2_SECRET_KEY");
        if (string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(secret))
            throw new ArgumentException("Set ANDUINOS_R2_ACCESS_KEY and ANDUINOS_R2_SECRET_KEY.");
        using var s3 = new AmazonS3Client(new BasicAWSCredentials(access, secret), new AmazonS3Config
        {
            ServiceURL = settings.ServiceUrl, ForcePathStyle = true, AuthenticationRegion = "auto",
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
        });
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var termination = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            // The signal registration is disposed before its captured cancellation source.
            // ReSharper disable once AccessToDisposedClosure
            stopping.Cancel();
        });
        var worker = new AutomationWorker(settings, new R2CatalogStore(s3, settings.Bucket),
            message => Console.Error.WriteLine($"{DateTimeOffset.UtcNow:O} {message}"));
        return await worker.RunAsync(result.GetValue(OnceOption), stopping.Token);
    }
}
