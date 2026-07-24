using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Identity.Abstractions;

namespace Aevatar.GAgents.Channel.Identity;

internal sealed class ManagedCodexCredentialReadinessObservationPort
    : IManagedCodexCredentialReadinessObservationPort
{
    internal const string ProjectionKind = "managed-codex-credential-readiness";

    private readonly IProjectionScopeActivationService<ManagedCodexCredentialReadinessRuntimeLease>
        _activationService;
    private readonly IProjectionScopeReleaseService<ManagedCodexCredentialReadinessRuntimeLease>
        _releaseService;
    private readonly IProjectionSessionEventHub<ManagedCodexCredentialSnapshot> _eventHub;

    public ManagedCodexCredentialReadinessObservationPort(
        IProjectionScopeActivationService<ManagedCodexCredentialReadinessRuntimeLease> activationService,
        IProjectionScopeReleaseService<ManagedCodexCredentialReadinessRuntimeLease> releaseService,
        IProjectionSessionEventHub<ManagedCodexCredentialSnapshot> eventHub)
    {
        _activationService = activationService ?? throw new ArgumentNullException(nameof(activationService));
        _releaseService = releaseService ?? throw new ArgumentNullException(nameof(releaseService));
        _eventHub = eventHub ?? throw new ArgumentNullException(nameof(eventHub));
    }

    public async Task<IManagedCodexCredentialReadinessObservationLease> BindAsync(
        ExternalSubjectRef owner,
        CancellationToken ct = default)
    {
        var actorId = ManagedCodexCredentialActorIdentity.From(owner);
        var sessionId = Guid.NewGuid().ToString("N");
        var runtimeLease = await _activationService.EnsureAsync(
            new ProjectionScopeStartRequest
            {
                RootActorId = actorId,
                ProjectionKind = ProjectionKind,
                Mode = ProjectionRuntimeMode.SessionObservation,
                SessionId = sessionId,
            },
            ct);
        var snapshots = new SnapshotBuffer(
            System.Threading.Channels.Channel.CreateBounded<ManagedCodexCredentialSnapshot>(
                new BoundedChannelOptions(16)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.Wait,
                    AllowSynchronousContinuations = false,
                }));

        try
        {
            var subscription = await _eventHub.SubscribeAsync(
                actorId,
                sessionId,
                snapshots.WriteAsync,
                ct);
            return new ObservationLease(runtimeLease, subscription, snapshots, _releaseService);
        }
        catch
        {
            snapshots.BeginTeardown();
            await _releaseService.ReleaseIfIdleAsync(runtimeLease, CancellationToken.None);
            throw;
        }
    }

    private sealed class SnapshotBuffer(Channel<ManagedCodexCredentialSnapshot> snapshots)
    {
        private int _teardownStarted;

        public ChannelReader<ManagedCodexCredentialSnapshot> Reader => snapshots.Reader;

        public async ValueTask WriteAsync(ManagedCodexCredentialSnapshot snapshot)
        {
            try
            {
                await snapshots.Writer.WriteAsync(snapshot.Clone());
            }
            catch (ChannelClosedException) when (Volatile.Read(ref _teardownStarted) != 0)
            {
            }
        }

        public void BeginTeardown()
        {
            Volatile.Write(ref _teardownStarted, 1);
            snapshots.Writer.TryComplete();
        }
    }

    private sealed class ObservationLease : IManagedCodexCredentialReadinessObservationLease
    {
        private readonly ManagedCodexCredentialReadinessRuntimeLease _runtimeLease;
        private readonly IAsyncDisposable _subscription;
        private readonly SnapshotBuffer _snapshots;
        private readonly IProjectionScopeReleaseService<ManagedCodexCredentialReadinessRuntimeLease>
            _releaseService;
        private readonly Lazy<Task> _disposal;

        public ObservationLease(
            ManagedCodexCredentialReadinessRuntimeLease runtimeLease,
            IAsyncDisposable subscription,
            SnapshotBuffer snapshots,
            IProjectionScopeReleaseService<ManagedCodexCredentialReadinessRuntimeLease> releaseService)
        {
            _runtimeLease = runtimeLease;
            _subscription = subscription;
            _snapshots = snapshots;
            _releaseService = releaseService;
            _disposal = new Lazy<Task>(
                DisposeCoreAsync,
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public async IAsyncEnumerable<ManagedCodexCredentialSnapshot> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var snapshot in _snapshots.Reader.ReadAllAsync(ct))
                yield return snapshot.Clone();
        }

        public ValueTask DisposeAsync() => new(_disposal.Value);

        private async Task DisposeCoreAsync()
        {
            _snapshots.BeginTeardown();
            try
            {
                await _subscription.DisposeAsync();
            }
            finally
            {
                await _releaseService.ReleaseIfIdleAsync(_runtimeLease, CancellationToken.None);
            }
        }
    }
}
