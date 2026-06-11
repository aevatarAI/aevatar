using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;

namespace Aevatar.Tests.Shared;

internal static class SharedOrleansPortAllocator
{
    private const int MaxStartAttempts = 3;
    private static readonly SemaphoreSlim HostStartupGate = new(1, 1);

    public static Task<IHost> StartHostAsync(
        Func<ReservedOrleansPorts, IHost> buildHost,
        TimeSpan? startupTimeout = null) =>
        StartHostAsync(buildHost, startupTimeout, CancellationToken.None);

    public static async Task<IHost> StartHostAsync(
        Func<ReservedOrleansPorts, IHost> buildHost,
        TimeSpan? startupTimeout,
        CancellationToken cancellationToken)
    {
        // Refactor (iter84/cluster-084):
        // Old: each Orleans integration test reserved ephemeral ports, released them,
        // then raced other tests before the silo actually bound those endpoints.
        // New: serialize host startup, keep candidate ports reserved while building
        // host options, release immediately before StartAsync, and retry only on bind failures.
        Exception? lastFailure = null;

        for (var attempt = 1; attempt <= MaxStartAttempts; attempt++)
        {
            await HostStartupGate.WaitAsync(cancellationToken);
            IHost? host = null;

            try
            {
                using var ports = ReservedOrleansPorts.Reserve();
                host = buildHost(ports);
                ports.Release();

                var startTask = host.StartAsync(cancellationToken);
                if (startupTimeout is { } timeout)
                {
                    await startTask.WaitAsync(timeout, cancellationToken);
                }
                else
                {
                    await startTask;
                }

                return host;
            }
            catch (Exception ex) when (IsPortBindFailure(ex) && attempt < MaxStartAttempts)
            {
                lastFailure = ex;
                host?.Dispose();
            }
            catch
            {
                host?.Dispose();
                throw;
            }
            finally
            {
                HostStartupGate.Release();
            }
        }

        throw new InvalidOperationException("Failed to start Orleans test host with reserved ports.", lastFailure);
    }

    private static bool IsPortBindFailure(Exception exception) =>
        exception is SocketException
        || exception.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("Only one usage of each socket address", StringComparison.OrdinalIgnoreCase)
        || exception.InnerException is not null && IsPortBindFailure(exception.InnerException)
        || exception is AggregateException aggregate && aggregate.InnerExceptions.Any(IsPortBindFailure);

    public sealed class ReservedOrleansPorts : IDisposable
    {
        private TcpListener? _siloListener;
        private TcpListener? _gatewayListener;

        private ReservedOrleansPorts(TcpListener siloListener, TcpListener gatewayListener)
        {
            _siloListener = siloListener;
            _gatewayListener = gatewayListener;
            SiloPort = GetPort(siloListener);
            GatewayPort = GetPort(gatewayListener);
        }

        public int SiloPort { get; }

        public int GatewayPort { get; }

        public static ReservedOrleansPorts Reserve()
        {
            var siloListener = StartLoopbackListener();

            try
            {
                var gatewayListener = StartLoopbackListener();
                return new ReservedOrleansPorts(siloListener, gatewayListener);
            }
            catch
            {
                siloListener.Stop();
                throw;
            }
        }

        public void Release()
        {
            _gatewayListener?.Stop();
            _gatewayListener = null;
            _siloListener?.Stop();
            _siloListener = null;
        }

        public void Dispose() => Release();

        private static TcpListener StartLoopbackListener()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0)
            {
                ExclusiveAddressUse = true,
            };
            listener.Start();
            return listener;
        }

        private static int GetPort(TcpListener listener) =>
            ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
