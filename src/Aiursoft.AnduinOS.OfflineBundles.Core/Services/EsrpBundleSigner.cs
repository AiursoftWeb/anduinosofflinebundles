using System.Net;
using System.Text;
using Aiursoft.AiurProtocol.Exceptions;
using Aiursoft.EsrpSign.SDK;
using Aiursoft.EsrpSign.SDK.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Aiursoft.AnduinOS.OfflineBundles.Core.Services;

public sealed class EsrpBundleSigner : IBundleSigner, IDisposable
{
    private readonly ServiceProvider _services;
    private readonly string _apiKey;
    private readonly string _passphrase;
    private readonly TimeProvider _time;
    private readonly int _maxAttempts;
    private readonly TimeSpan _initialRetryDelay;

    public EsrpBundleSigner(string endpoint, string apiKey, string passphrase,
        TimeProvider? time = null, int maxAttempts = 4, TimeSpan? initialRetryDelay = null)
    {
        BundleSigning.ValidateEndpoint(endpoint);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("Set a non-empty ESRP Sign API key.", nameof(apiKey));
        }

        if (string.IsNullOrWhiteSpace(passphrase) || passphrase.Length < 12 ||
            Encoding.UTF8.GetByteCount(passphrase) > SignFileRequest.MaxPassphraseBytes)
        {
            throw new ArgumentException("Provide a key passphrase of 12 to 256 UTF-8 bytes.", nameof(passphrase));
        }

        if (maxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), "Retry attempts must be at least 1.");
        }

        _apiKey = apiKey;
        _passphrase = passphrase;
        _time = time ?? TimeProvider.System;
        _maxAttempts = maxAttempts;
        _initialRetryDelay = initialRetryDelay ?? TimeSpan.FromSeconds(1);

        var services = new ServiceCollection();
        services.AddEsrpSignSdk(endpoint.TrimEnd('/'));
        _services = services.BuildServiceProvider();
    }

    public async Task<byte[]> GetPublicKeyAsync(Guid keyId, CancellationToken token)
    {
        if (keyId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty ESRP Sign key ID is required.", nameof(keyId));
        }

        var response = await ExecuteWithRetryAsync(async ct =>
        {
            using var scope = _services.CreateScope();
            var client = scope.ServiceProvider.GetRequiredService<EsrpSignApiClient>();
            return await client.GetPublicKeyAsync(keyId).WaitAsync(ct);
        }, token);

        if (response.PublicKey.Length == 0)
        {
            throw new InvalidDataException("ESRP Sign returned an empty public key.");
        }

        return response.PublicKey;
    }

    public async Task SignAsync(Guid keyId, string inputPath, string outputPath, bool clearSign,
        CancellationToken token)
    {
        if (keyId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty ESRP Sign key ID is required.", nameof(keyId));
        }

        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("The file to sign does not exist.", inputPath);
        }

        var format = clearSign ? SignatureFormat.ClearSigned : SignatureFormat.DetachedArmored;
        var response = await ExecuteWithRetryAsync(async ct =>
        {
            using var scope = _services.CreateScope();
            var client = scope.ServiceProvider.GetRequiredService<EsrpSignApiClient>();
            return await client.SignFileAsync(_apiKey, keyId, _passphrase, inputPath, format, ct);
        }, token);

        if (response.Signature.Length == 0)
        {
            throw new InvalidDataException("ESRP Sign returned an empty signature.");
        }

        await File.WriteAllBytesAsync(outputPath, response.Signature, token);
    }

    private async Task<T> ExecuteWithRetryAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken token)
    {
        var delay = _initialRetryDelay;
        for (var attempt = 1; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                return await operation(token);
            }
            catch (Exception error) when (attempt < _maxAttempts && IsTransient(error, token))
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, _time, token);
                    delay = TimeSpan.FromTicks(delay.Ticks * 2);
                }
            }
        }
    }

    private static bool IsTransient(Exception error, CancellationToken callerToken) => error switch
    {
        HttpRequestException => true,
        OperationCanceledException => !callerToken.IsCancellationRequested,
        AiurHttpResponseException http => (int)http.StatusCode >= 500 ||
            http.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests,
        _ => false
    };

    public void Dispose() => _services.Dispose();
}
