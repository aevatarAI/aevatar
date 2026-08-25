using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Hosting.NyxId;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Studio.Tests;

public sealed class NyxIdWorkflowDeliveryConnectionInventoryPortTests
{
    [Fact]
    public async Task ListAsync_ShouldReadPublishedKeysRouteAndPreserveEligibilityFacts()
    {
        const string response = """
            {
              "keys": [
                {
                  "id": "user-service-personal",
                  "slug": "customer-lark-bot-7",
                  "label": "Personal Lark",
                  "catalog_service_name": "Lark Bot",
                  "is_active": true,
                  "status": "active",
                  "credential_source": { "type": "personal" },
                  "catalog_service_id": "catalog-lark-bot",
                  "catalog_service_slug": "api-lark-bot",
                  "connected": true
                },
                {
                  "id": "user-service-organization",
                  "slug": "api-lark-team",
                  "label": null,
                  "catalog_service_name": "Lark Team",
                  "is_active": false,
                  "status": "refresh_failed",
                  "node_id": "node-alpha",
                  "node_status": "offline",
                  "credential_source": {
                    "type": "org",
                    "org_id": "org-alpha",
                    "org_name": "Alpha",
                    "avatar_url": null,
                    "role": "viewer",
                    "allowed": false
                  },
                  "connected": false
                }
              ]
            }
            """;
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json(response),
        });
        var port = CreatePort(handler);

        var result = await port.ListAsync("  caller-bearer  ", CancellationToken.None);

        result.Should().Equal(
            new NyxIdUserServiceInventoryItem(
                "user-service-personal",
                "customer-lark-bot-7",
                "api-lark-bot",
                "Personal Lark",
                IsActive: true,
                NyxIdInventoryCredentialSourceKind.Personal,
                Allowed: true,
                NyxIdInventoryCredentialStatus.Active,
                NodeId: null,
                NyxIdInventoryNodeStatus.NotBound,
                Connected: true),
            new NyxIdUserServiceInventoryItem(
                "user-service-organization",
                "api-lark-team",
                CatalogServiceSlug: null,
                Label: null,
                IsActive: false,
                NyxIdInventoryCredentialSourceKind.Organization,
                Allowed: false,
                NyxIdInventoryCredentialStatus.RefreshFailed,
                NodeId: "node-alpha",
                NyxIdInventoryNodeStatus.Offline,
                Connected: false));
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Method.Should().Be(HttpMethod.Get);
        handler.Requests[0].Path.Should().Be("/api/v1/keys");
        handler.Requests[0].Authorization.Should().NotBeNull();
        handler.Requests[0].Authorization!.Scheme.Should().Be("Bearer");
        handler.Requests[0].Authorization!.Parameter.Should().Be("caller-bearer");
    }

    [Fact]
    public async Task ListAsync_WhenNyxIdRejectsBearer_ShouldMapAuthenticationFailure()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = Json("""{"error":"unauthorized","error_code":1001}"""),
        });
        var port = CreatePort(handler);

        var act = () => port.ListAsync("caller-bearer", CancellationToken.None);

        var exception = await act.Should().ThrowAsync<NyxIdUserServiceInventoryException>();
        exception.Which.Kind.Should().Be(
            NyxIdUserServiceInventoryFailureKind.AuthenticationRejected);
        exception.Which.Message.Should().NotContain("1001");
    }

    [Fact]
    public async Task ListAsync_WhenPublishedResponseIsMalformed_ShouldMapResponseInvalid()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json("""{"keys":[{"id":"missing-required-fields"}]}"""),
        });
        var port = CreatePort(handler);

        var act = () => port.ListAsync("caller-bearer", CancellationToken.None);

        var exception = await act.Should().ThrowAsync<NyxIdUserServiceInventoryException>();
        exception.Which.Kind.Should().Be(NyxIdUserServiceInventoryFailureKind.ResponseInvalid);
    }

    [Fact]
    public async Task ListAsync_WhenClientCannotBeCreated_ShouldMapUnavailableWithoutLeakingDetail()
    {
        var port = new NyxIdWorkflowDeliveryConnectionInventoryPort(
            new ThrowingHttpClientFactory("sensitive client factory detail"),
            Configuration(),
            NullLogger<NyxIdWorkflowDeliveryConnectionInventoryPort>.Instance);

        var act = () => port.ListAsync("caller-bearer", CancellationToken.None);

        var exception = await act.Should().ThrowAsync<NyxIdUserServiceInventoryException>();
        exception.Which.Kind.Should().Be(NyxIdUserServiceInventoryFailureKind.Unavailable);
        exception.Which.Message.Should().NotContain("sensitive client factory detail");
        exception.Which.InnerException.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task ListAsync_WhenChunkedBodyExceedsLimit_ShouldFailClosed()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new GeneratedReadStream(
                NyxIdWorkflowDeliveryConnectionInventoryPort.MaxResponseBodyBytes + 1)),
        });
        var port = CreatePort(handler);

        var act = () => port.ListAsync("caller-bearer", CancellationToken.None);

        var exception = await act.Should().ThrowAsync<NyxIdUserServiceInventoryException>();
        exception.Which.Kind.Should().Be(NyxIdUserServiceInventoryFailureKind.Unavailable);
        exception.Which.Message.Should().Contain(
            NyxIdWorkflowDeliveryConnectionInventoryPort.MaxResponseBodyBytes.ToString());
    }

    [Fact]
    public async Task ListAsync_WhenOperationBudgetExpires_ShouldMapUnavailable()
    {
        var handler = new CancellationAwareHandler();
        var port = CreatePort(handler, TimeSpan.FromMilliseconds(20));

        var act = () => port.ListAsync("caller-bearer", CancellationToken.None);

        var exception = await act.Should().ThrowAsync<NyxIdUserServiceInventoryException>();
        exception.Which.Kind.Should().Be(NyxIdUserServiceInventoryFailureKind.Unavailable);
        exception.Which.Message.Should().Contain("timed out");
        handler.CancellationObserved.Should().BeTrue();
    }

    [Fact]
    public void InventoryTransportLimits_ShouldRemainBounded()
    {
        NyxIdWorkflowDeliveryConnectionInventoryPort.MaxResponseBodyBytes.Should().Be(4 * 1024 * 1024);
        NyxIdWorkflowDeliveryConnectionInventoryPort.SourceTimeout.Should().Be(TimeSpan.FromSeconds(15));
    }

    private static StringContent Json(string body) =>
        new(body, Encoding.UTF8, "application/json");

    private static NyxIdWorkflowDeliveryConnectionInventoryPort CreatePort(
        HttpMessageHandler handler,
        TimeSpan? sourceTimeout = null) =>
        new(
            new StubHttpClientFactory(handler),
            Configuration(),
            NullLogger<NyxIdWorkflowDeliveryConnectionInventoryPort>.Instance,
            sourceTimeout ?? NyxIdWorkflowDeliveryConnectionInventoryPort.SourceTimeout);

    private static IConfiguration Configuration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aevatar:NyxId:ApiBaseUrl"] = "https://nyxid.example",
            })
            .Build();

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class ThrowingHttpClientFactory(string detail) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException(detail);
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri?.AbsolutePath ?? string.Empty,
                request.Headers.Authorization));
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class CancellationAwareHandler : HttpMessageHandler
    {
        public bool CancellationObserved { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<HttpResponseMessage>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() =>
            {
                CancellationObserved = true;
                completion.TrySetCanceled(cancellationToken);
            });
            return completion.Task;
        }
    }

    private sealed class GeneratedReadStream(long length) : Stream
    {
        private long _remaining = length;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var bytesRead = (int)Math.Min(count, _remaining);
            buffer.AsSpan(offset, bytesRead).Fill((byte)'x');
            _remaining -= bytesRead;
            return bytesRead;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytesRead = (int)Math.Min(buffer.Length, _remaining);
            buffer.Span[..bytesRead].Fill((byte)'x');
            _remaining -= bytesRead;
            return ValueTask.FromResult(bytesRead);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        string Path,
        AuthenticationHeaderValue? Authorization);
}
