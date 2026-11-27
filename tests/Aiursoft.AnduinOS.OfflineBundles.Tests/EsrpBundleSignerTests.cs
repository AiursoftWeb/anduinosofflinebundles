using System.Net;
using System.Net.Sockets;
using System.Text;
using Aiursoft.AiurProtocol;
using Aiursoft.AiurProtocol.Exceptions;
using Aiursoft.AiurProtocol.Models;
using Aiursoft.AnduinOS.OfflineBundles.Core.Models;
using Aiursoft.AnduinOS.OfflineBundles.Core.Services;
using Aiursoft.EsrpSign.SDK.Models;
using Newtonsoft.Json;

namespace Aiursoft.AnduinOS.OfflineBundles.Tests;

[TestClass]
public class EsrpBundleSignerTests
{
    private const string TestApiKey = "esrp-test-api-key-secret-value";
    private const string TestPassphrase = "esrp-test-passphrase-secret-value";

    [TestMethod]
    public async Task BuildsAndVerifiesRealBundleThroughEsrpHttpApi()
    {
        using var fixture = new BundleFixture();
        await fixture.InitializeAsync();
        await using var server = new LoopbackEsrpServer(fixture, TestApiKey, TestPassphrase);
        using var signer = new EsrpBundleSigner(server.Endpoint, TestApiKey, TestPassphrase,
            initialRetryDelay: TimeSpan.FromMilliseconds(10));
        var signing = new SigningOptions(server.Endpoint, fixture.KeyId, fixture.Keyring);

        await BundleSigning.ValidateIdentityAsync(signing, signer, await BundleJson.HashAsync(fixture.Keyring), default);
        var built = await new BundleBuilder(signer).BuildAsync(fixture.Profile, fixture.Output, signing);
        var verified = await BundleArchive.VerifyAsync(built.File, fixture.Keyring);

        Assert.AreEqual("2.0.5", verified.Manifest.TargetVersion);
        Assert.AreEqual(1, server.PublicKeyCalls);
        Assert.AreEqual(3, server.SignCalls);
        CollectionAssert.AreEquivalent(
            new[] { SignatureFormat.ClearSigned, SignatureFormat.DetachedArmored, SignatureFormat.DetachedArmored },
            server.RequestedFormats);
    }

    [TestMethod]
    public async Task RetriesTransientServerFailureAndSucceeds()
    {
        using var fixture = new BundleFixture();
        await fixture.InitializeAsync();
        await using var server = new LoopbackEsrpServer(fixture, TestApiKey, TestPassphrase);
        server.TransientFailuresRemaining = 2;
        using var signer = new EsrpBundleSigner(server.Endpoint, TestApiKey, TestPassphrase,
            maxAttempts: 4, initialRetryDelay: TimeSpan.FromMilliseconds(10));

        var input = Path.Combine(fixture.Root, "payload.txt");
        var output = Path.Combine(fixture.Root, "payload.asc");
        await File.WriteAllTextAsync(input, "offline update payload\n");

        await signer.SignAsync(fixture.KeyId, input, output, clearSign: false, default);
        var fingerprint = await BundleSigning.VerifyAsync(input, output, fixture.Keyring, default);

        Assert.IsFalse(string.IsNullOrWhiteSpace(fingerprint));
        Assert.AreEqual(3, server.SignCalls);
    }

    [TestMethod]
    public async Task DoesNotRetryWrongKeyAndDoesNotLeakSecrets()
    {
        using var fixture = new BundleFixture();
        await fixture.InitializeAsync();
        await using var server = new LoopbackEsrpServer(fixture, TestApiKey, TestPassphrase);
        const string wrongPassphrase = "wrong-passphrase-never-logged-1234";
        using var signer = new EsrpBundleSigner(server.Endpoint, TestApiKey, wrongPassphrase,
            maxAttempts: 4, initialRetryDelay: TimeSpan.FromMilliseconds(10));

        var input = Path.Combine(fixture.Root, "payload.txt");
        var output = Path.Combine(fixture.Root, "payload.asc");
        await File.WriteAllTextAsync(input, "offline update payload\n");

        var error = await Assert.ThrowsExactlyAsync<AiurUnexpectedServerResponseException>(() =>
            signer.SignAsync(fixture.KeyId, input, output, clearSign: false, default));

        Assert.AreEqual(Code.WrongKey, error.Response.Code);
        Assert.AreEqual(1, server.SignCalls);
        Assert.IsFalse(error.ToString().Contains(TestApiKey, StringComparison.Ordinal));
        Assert.IsFalse(error.ToString().Contains(wrongPassphrase, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task RejectsMismatchedRemotePublicKeyAtStartup()
    {
        using var fixture = new BundleFixture();
        await fixture.InitializeAsync();
        await using var server = new LoopbackEsrpServer(fixture, TestApiKey, TestPassphrase);
        server.OverridePublicKey = [0x99, 0x01, 0x02, 0x03];
        using var signer = new EsrpBundleSigner(server.Endpoint, TestApiKey, TestPassphrase,
            initialRetryDelay: TimeSpan.FromMilliseconds(10));
        var signing = new SigningOptions(server.Endpoint, fixture.KeyId, fixture.Keyring);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            BundleSigning.ValidateIdentityAsync(signing, signer, null, default));
    }

    private sealed class LoopbackEsrpServer : IAsyncDisposable
    {
        private readonly BundleFixture _fixture;
        private readonly string _expectedApiKey;
        private readonly string _expectedPassphrase;
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public string Endpoint { get; }
        public int PublicKeyCalls { get; private set; }
        public int SignCalls { get; private set; }
        public int TransientFailuresRemaining { get; set; }
        public byte[]? OverridePublicKey { get; set; }
        public List<SignatureFormat> RequestedFormats { get; } = [];

        public LoopbackEsrpServer(BundleFixture fixture, string expectedApiKey, string expectedPassphrase)
        {
            _fixture = fixture;
            _expectedApiKey = expectedApiKey;
            _expectedPassphrase = expectedPassphrase;
            var port = AllocateLoopbackPort();
            Endpoint = $"http://127.0.0.1:{port}";
            _listener = new HttpListener();
            _listener.Prefixes.Add(Endpoint + "/");
            _listener.Start();
            _loop = Task.Run(() => ServeAsync(_cts.Token));
        }

        private async Task ServeAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().WaitAsync(token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                await HandleAsync(context, token);
            }
        }

        private async Task HandleAsync(HttpListenerContext context, CancellationToken token)
        {
            var path = context.Request.Url!.AbsolutePath;
            var keyPrefix = $"/api/v1/signing-keys/{_fixture.KeyId:D}";

            if (context.Request.HttpMethod == "GET" && path == keyPrefix + "/public-key")
            {
                PublicKeyCalls++;
                var keyBytes = OverridePublicKey ?? await File.ReadAllBytesAsync(_fixture.Keyring, token);
                await WriteJsonAsync(context.Response, HttpStatusCode.OK, new PublicKeyResponse
                {
                    Code = Code.ResultShown,
                    Message = "Public key.",
                    PublicKey = keyBytes
                });
                return;
            }

            if (context.Request.HttpMethod == "POST" && path == keyPrefix + "/sign-file")
            {
                SignCalls++;
                if (TransientFailuresRemaining > 0)
                {
                    TransientFailuresRemaining--;
                    context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                    context.Response.ContentType = "text/plain";
                    await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("Temporary outage"), token);
                    context.Response.Close();
                    return;
                }

                if (context.Request.Headers["Authorization"] != $"Bearer {_expectedApiKey}")
                {
                    await WriteJsonAsync(context.Response, HttpStatusCode.Unauthorized, new AiurResponse
                    {
                        Code = Code.Unauthorized,
                        Message = "Unauthorized."
                    });
                    return;
                }

                var formatParam = context.Request.QueryString["format"];
                var format = string.IsNullOrEmpty(formatParam)
                    ? SignatureFormat.DetachedBinary
                    : Enum.Parse<SignatureFormat>(formatParam, ignoreCase: true);
                RequestedFormats.Add(format);

                var (passphrase, payload) = await ReadMultipartMixedAsync(context.Request, token);
                if (!string.Equals(passphrase, _expectedPassphrase, StringComparison.Ordinal))
                {
                    await WriteJsonAsync(context.Response, HttpStatusCode.BadRequest, new AiurResponse
                    {
                        Code = Code.WrongKey,
                        Message = "The key passphrase is incorrect."
                    });
                    return;
                }

                var inputPath = Path.Combine(_fixture.Root, $"esrp-in-{Guid.NewGuid():N}");
                var outputPath = Path.Combine(_fixture.Root, $"esrp-out-{Guid.NewGuid():N}");
                try
                {
                    await File.WriteAllBytesAsync(inputPath, payload, token);
                    var clearSign = format == SignatureFormat.ClearSigned;
                    await _fixture.SignWithGpgAsync(inputPath, outputPath, clearSign, token);
                    var signature = await File.ReadAllBytesAsync(outputPath, token);
                    await WriteJsonAsync(context.Response, HttpStatusCode.OK, new SignResponse
                    {
                        Code = Code.JobDone,
                        Message = "Data signed.",
                        Signature = signature
                    });
                }
                finally
                {
                    if (File.Exists(inputPath)) File.Delete(inputPath);
                    if (File.Exists(outputPath)) File.Delete(outputPath);
                }

                return;
            }

            await WriteJsonAsync(context.Response, HttpStatusCode.NotFound, new AiurResponse
            {
                Code = Code.NotFound,
                Message = "Not found."
            });
        }

        private static async Task<(string Passphrase, byte[] Payload)> ReadMultipartMixedAsync(
            HttpListenerRequest request, CancellationToken token)
        {
            var contentType = request.ContentType ?? throw new InvalidDataException("Missing Content-Type.");
            var marker = "boundary=";
            var boundaryIndex = contentType.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (boundaryIndex < 0) throw new InvalidDataException("Missing boundary.");
            var boundary = contentType[(boundaryIndex + marker.Length)..].Trim().Trim('"');
            var semicolon = boundary.IndexOf(';');
            if (semicolon >= 0) boundary = boundary[..semicolon].Trim().Trim('"');

            using var buffer = new MemoryStream();
            await request.InputStream.CopyToAsync(buffer, token);
            var body = buffer.ToArray();
            var delimiter = Encoding.ASCII.GetBytes("--" + boundary);
            var parts = SplitParts(body, delimiter);
            if (parts.Count != 2) throw new InvalidDataException($"Expected 2 parts, got {parts.Count}.");

            var passphrase = Encoding.UTF8.GetString(ExtractPartBody(parts[0]));
            var payload = ExtractPartBody(parts[1]);
            return (passphrase, payload);
        }

        private static List<byte[]> SplitParts(byte[] body, byte[] delimiter)
        {
            var positions = new List<int>();
            var span = body.AsSpan();
            var offset = 0;
            while (offset < body.Length)
            {
                var found = span[offset..].IndexOf(delimiter);
                if (found < 0) break;
                positions.Add(offset + found);
                offset += found + delimiter.Length;
            }

            var parts = new List<byte[]>();
            for (var i = 0; i < positions.Count - 1; i++)
            {
                var start = positions[i] + delimiter.Length;
                if (start + 1 < body.Length && body[start] == '\r' && body[start + 1] == '\n') start += 2;
                var end = positions[i + 1];
                if (end >= 2 && body[end - 2] == '\r' && body[end - 1] == '\n') end -= 2;
                parts.Add(body[start..end]);
            }

            return parts;
        }

        private static byte[] ExtractPartBody(byte[] part)
        {
            var separator = "\r\n\r\n"u8;
            var index = part.AsSpan().IndexOf(separator);
            if (index < 0) throw new InvalidDataException("Missing multipart header separator.");
            return part[(index + separator.Length)..];
        }

        private static async Task WriteJsonAsync(HttpListenerResponse response, HttpStatusCode status, object payload)
        {
            response.StatusCode = (int)status;
            response.ContentType = "application/json; charset=utf-8";
            var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(payload, ProtocolSettings.JsonSettings));
            await response.OutputStream.WriteAsync(bytes);
            response.Close();
        }

        private static int AllocateLoopbackPort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            _listener.Close();
            try
            {
                await _loop;
            }
            catch
            {
                // Shutdown.
            }

            _cts.Dispose();
        }
    }
}
