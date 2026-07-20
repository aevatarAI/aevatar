using System.Net;
using System.Reflection;
using System.Text;
using Aevatar.Foundation.Abstractions.Connectors;
using Aevatar.Foundation.Core.Connectors;
using FluentAssertions;

namespace Aevatar.Foundation.Core.Tests;

public sealed class OutboundHttpRequestExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_ShouldRejectPrivateIpLiteralBeforeSending()
    {
        var handler = new RecordingHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
            });
        var executor = new DefaultOutboundHttpRequestExecutor(
            new HttpClient(handler),
            new StaticDnsResolver(IPAddress.Parse("93.184.216.34")));

        var response = await executor.ExecuteAsync(new OutboundHttpRequest
        {
            Method = "GET",
            Url = "https://127.0.0.1/admin",
            TimeoutMs = 5000,
        });

        response.Success.Should().BeFalse();
        response.Error.Should().Contain("blocked destination");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldValidateRedirectTarget()
    {
        var handler = new RecordingHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsoluteUri == "https://api.example.com/start")
            {
                return new HttpResponseMessage(HttpStatusCode.Redirect)
                {
                    Headers = { Location = new Uri("https://169.254.169.254/latest/meta-data") },
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}"""),
            };
        });
        var resolver = new StaticDnsResolver(
            ("api.example.com", IPAddress.Parse("93.184.216.34")),
            ("169.254.169.254", IPAddress.Parse("169.254.169.254")));
        var executor = new DefaultOutboundHttpRequestExecutor(new HttpClient(handler), resolver);

        var response = await executor.ExecuteAsync(new OutboundHttpRequest
        {
            Method = "GET",
            Url = "https://api.example.com/start",
            TimeoutMs = 5000,
            MaxRedirects = 3,
        });

        response.Success.Should().BeFalse();
        response.Error.Should().Contain("blocked destination");
        handler.Requests.Should().ContainSingle()
            .Which.RequestUri!.AbsoluteUri.Should().Be("https://api.example.com/start");
    }

    [Fact]
    public void DefaultExecutor_ShouldValidateDnsAtSocketConnectionBoundary()
    {
        var executor = new DefaultOutboundHttpRequestExecutor();

        var client = typeof(DefaultOutboundHttpRequestExecutor)
            .GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(executor)
            .Should()
            .BeOfType<HttpClient>()
            .Subject;
        var handler = typeof(HttpMessageInvoker)
            .GetField("_handler", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(client)
            .Should()
            .BeOfType<SocketsHttpHandler>()
            .Subject;

        handler.ConnectCallback.Should().NotBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldFailWhenResponseExceedsLimit()
    {
        var handler = new RecordingHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("abcdef", Encoding.UTF8, "text/plain"),
            });
        var executor = new DefaultOutboundHttpRequestExecutor(
            new HttpClient(handler),
            new StaticDnsResolver(IPAddress.Parse("93.184.216.34")));

        var response = await executor.ExecuteAsync(new OutboundHttpRequest
        {
            Method = "GET",
            Url = "https://api.example.com/data",
            TimeoutMs = 5000,
            MaxResponseBytes = 5,
        });

        response.Success.Should().BeFalse();
        response.Error.Should().Contain("response exceeded 5 bytes");
        response.Output.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnStatusAndBodyForNonSuccess()
    {
        var handler = new RecordingHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("down", Encoding.UTF8, "text/plain"),
                ReasonPhrase = "Service Unavailable",
            });
        var executor = new DefaultOutboundHttpRequestExecutor(
            new HttpClient(handler),
            new StaticDnsResolver(IPAddress.Parse("93.184.216.34")));

        var response = await executor.ExecuteAsync(new OutboundHttpRequest
        {
            Method = "POST",
            Url = "https://api.example.com/data",
            Body = "{}",
            ContentType = "application/json",
            TimeoutMs = 5000,
        });

        response.Success.Should().BeFalse();
        response.Error.Should().Contain("503 Service Unavailable");
        response.Output.Should().Be("down");
        response.Metadata["connector.http.status_code"].Should().Be("503");
        handler.Requests.Should().ContainSingle();
    }

    private sealed class RecordingHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            Requests.Add(request);
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class StaticDnsResolver : IOutboundHttpDnsResolver
    {
        private readonly Dictionary<string, IPAddress[]> _addresses;

        public StaticDnsResolver(params IPAddress[] addresses)
        {
            _addresses = new Dictionary<string, IPAddress[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["*"] = addresses,
            };
        }

        public StaticDnsResolver(params (string Host, IPAddress Address)[] addresses)
        {
            _addresses = addresses
                .GroupBy(entry => entry.Host, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(entry => entry.Address).ToArray(),
                    StringComparer.OrdinalIgnoreCase);
        }

        public ValueTask<IReadOnlyList<IPAddress>> GetHostAddressesAsync(
            string host,
            CancellationToken ct = default)
        {
            _ = ct;
            return ValueTask.FromResult<IReadOnlyList<IPAddress>>(
                _addresses.TryGetValue(host, out var addresses) ||
                _addresses.TryGetValue("*", out addresses)
                    ? addresses
                    : []);
        }
    }
}
