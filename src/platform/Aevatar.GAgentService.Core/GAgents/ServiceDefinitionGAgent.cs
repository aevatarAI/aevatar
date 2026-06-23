using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Core.Models;
using Aevatar.GAgentService.Core.Ports;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Core.GAgents;

[GAgent("gagent.service.definition")]
public sealed class ServiceDefinitionGAgent : GAgentBase<ServiceDefinitionState>
{
    private const string RegistrationRetryCallbackId = "service-definition-registration-retry";

    private readonly IActorDispatchPort _dispatchPort;
    private readonly INyxIdServiceRegistrationPort _registrationPort;
    private readonly INyxIdRegistrationTokenAccessor _tokenAccessor;
    private readonly ServiceExternalExposureRetrySettings _retrySettings;

    public ServiceDefinitionGAgent(
        IActorDispatchPort dispatchPort,
        INyxIdServiceRegistrationPort? registrationPort = null,
        INyxIdRegistrationTokenAccessor? tokenAccessor = null,
        ServiceExternalExposureRetrySettings? retrySettings = null)
    {
        _dispatchPort = dispatchPort ?? throw new ArgumentNullException(nameof(dispatchPort));
        _registrationPort = registrationPort ?? NullNyxIdServiceRegistrationPort.Instance;
        _tokenAccessor = tokenAccessor ?? NullNyxIdRegistrationTokenAccessor.Instance;
        _retrySettings = retrySettings ?? ServiceExternalExposureRetrySettings.Default;
        InitializeId();
    }

    [EventHandler]
    public async Task HandleCreateAsync(CreateServiceDefinitionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateSpec(command.Spec);
        var currentSpec = State.Spec?.Clone();
        if (currentSpec?.Identity != null && !string.IsNullOrWhiteSpace(currentSpec.Identity.ServiceId))
            throw new InvalidOperationException($"Service definition '{ServiceKeys.Build(command.Spec.Identity)}' already exists.");

        await PersistDomainEventAsync(new ServiceDefinitionCreatedEvent
        {
            Spec = command.Spec.Clone(),
        });
        await DispatchInvocationCatalogObservationAsync(CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleUpdateAsync(UpdateServiceDefinitionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateSpec(command.Spec);
        EnsureExistingIdentity(command.Spec.Identity);
        await PersistDomainEventAsync(new ServiceDefinitionUpdatedEvent
        {
            Spec = command.Spec.Clone(),
        });
        await DispatchInvocationCatalogObservationAsync(CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleReconcileExternalExposureAsync(ReconcileExternalExposureCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureExistingIdentity(command.Identity);
        EnsureRegistrationIntent(command.OpenapiUrl, command.DesiredSpecHash);

        var exposure = State.Spec.ExternalExposure;
        var desiredHash = NormalizeRequired(command.DesiredSpecHash, nameof(command.DesiredSpecHash));
        var requestedCredentialKid = command.CredentialKid?.Trim() ?? string.Empty;
        if (exposure != null &&
            exposure.Status == ServiceRegistrationStatus.Registered &&
            exposure.ExposureDesired &&
            string.Equals(exposure.DesiredSpecHash, desiredHash, StringComparison.Ordinal) &&
            string.Equals(exposure.RegisteredSpecHash, desiredHash, StringComparison.Ordinal) &&
            (string.IsNullOrWhiteSpace(requestedCredentialKid) ||
             string.Equals(exposure.CredentialKid, requestedCredentialKid, StringComparison.Ordinal)) &&
            !string.IsNullOrWhiteSpace(exposure.NyxidServiceId))
        {
            return;
        }

        var nextAttempt = (exposure?.Attempt ?? 0) + 1;
        if (nextAttempt > _retrySettings.MaxAttempts)
            nextAttempt = 1;

        await PersistDomainEventAsync(new ServiceRegistrationRequestedEvent
        {
            Identity = command.Identity.Clone(),
            OpenapiUrl = command.OpenapiUrl.Trim(),
            DesiredSpecHash = desiredHash,
            CredentialKid = string.IsNullOrWhiteSpace(requestedCredentialKid)
                ? exposure?.CredentialKid ?? string.Empty
                : requestedCredentialKid,
            Attempt = nextAttempt,
            RequestedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        await DispatchInvocationCatalogObservationAsync(CancellationToken.None);

        await DispatchSelfAsync(new RunRegistrationAttemptCommand
        {
            Identity = command.Identity.Clone(),
            ExpectedAttempt = nextAttempt,
            DesiredSpecHash = desiredHash,
            OpenapiUrl = command.OpenapiUrl.Trim(),
        }, CancellationToken.None);
    }

    [EventHandler]
    public async Task HandleRetireExternalExposureAsync(RetireExternalExposureCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureExistingIdentity(command.Identity);

        var exposure = State.Spec.ExternalExposure;
        if (exposure == null ||
            (exposure.Status == ServiceRegistrationStatus.Retired && !exposure.ExposureDesired))
        {
            return;
        }

        var nyxidServiceId = exposure.NyxidServiceId ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(nyxidServiceId))
        {
            var token = await _tokenAccessor.GetTokenAsync(command.Identity, CancellationToken.None);
            if (token != null && !string.IsNullOrWhiteSpace(token.OwnerAccessToken))
            {
                await _registrationPort.RetireAsync(
                    new NyxIdServiceRetirementRequest(token.OwnerAccessToken, nyxidServiceId),
                    CancellationToken.None);
            }
        }

        await PersistDomainEventAsync(new ServiceRegistrationRetiredEvent
        {
            Identity = command.Identity.Clone(),
            NyxidServiceId = nyxidServiceId,
            NyxidSlug = exposure.NyxidSlug,
            DesiredSpecHash = string.IsNullOrWhiteSpace(command.DesiredSpecHash)
                ? exposure.DesiredSpecHash
                : command.DesiredSpecHash.Trim(),
            Attempt = exposure.Attempt,
            RetiredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        await DispatchInvocationCatalogObservationAsync(CancellationToken.None);
    }

    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleRegistrationRetryDueAsync(RegistrationRetryDueCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!MatchesActiveFailedRegistrationAttempt(command.ExpectedAttempt, command.DesiredSpecHash))
            return;

        var nextAttempt = command.ExpectedAttempt + 1;
        await PersistDomainEventAsync(new ServiceRegistrationRequestedEvent
        {
            Identity = command.Identity.Clone(),
            OpenapiUrl = command.OpenapiUrl ?? string.Empty,
            DesiredSpecHash = command.DesiredSpecHash?.Trim() ?? string.Empty,
            CredentialKid = State.Spec.ExternalExposure?.CredentialKid ?? string.Empty,
            Attempt = nextAttempt,
            RequestedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        await DispatchInvocationCatalogObservationAsync(CancellationToken.None);

        await DispatchSelfAsync(new RunRegistrationAttemptCommand
        {
            Identity = command.Identity.Clone(),
            ExpectedAttempt = nextAttempt,
            DesiredSpecHash = command.DesiredSpecHash ?? string.Empty,
            OpenapiUrl = command.OpenapiUrl ?? string.Empty,
        }, CancellationToken.None);
    }

    [EventHandler(AllowSelfHandling = true)]
    public async Task HandleRunRegistrationAttemptAsync(RunRegistrationAttemptCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureExistingIdentity(command.Identity);
        if (!MatchesActiveRegistrationAttempt(command.ExpectedAttempt, command.DesiredSpecHash))
            return;

        await PersistDomainEventAsync(new ServiceRegistrationAttemptStartedEvent
        {
            Identity = command.Identity.Clone(),
            DesiredSpecHash = command.DesiredSpecHash.Trim(),
            Attempt = command.ExpectedAttempt,
            StartedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        await DispatchInvocationCatalogObservationAsync(CancellationToken.None);

        var token = await _tokenAccessor.GetTokenAsync(command.Identity, CancellationToken.None);
        if (token == null || string.IsNullOrWhiteSpace(token.OwnerAccessToken))
        {
            await PersistRegistrationFailureAsync(
                command,
                new NyxIdRegistrationFailure(
                    NyxIdRegistrationFailureKind.MissingToken,
                    "missing_registration_token",
                    Retryable: true),
                State.Spec.ExternalExposure?.CredentialKid ?? string.Empty,
                scheduleRetry: true);
            return;
        }

        var request = new NyxIdServiceRegistrationRequest(
            State.Spec.Identity.Clone(),
            State.Spec.DisplayName ?? string.Empty,
            command.OpenapiUrl ?? string.Empty,
            command.DesiredSpecHash.Trim(),
            token.OwnerAccessToken,
            token.ServiceCredential,
            string.IsNullOrWhiteSpace(token.CredentialKid)
                ? State.Spec.ExternalExposure?.CredentialKid ?? string.Empty
                : token.CredentialKid.Trim(),
            State.Spec.ExternalExposure?.NyxidServiceId,
            State.Spec.ExternalExposure?.NyxidSlug);

        var result = string.IsNullOrWhiteSpace(request.ExistingNyxIdServiceId)
            ? await _registrationPort.RegisterAsync(request, CancellationToken.None)
            : await _registrationPort.UpdateAsync(request, CancellationToken.None);

        if (!result.Succeeded && result.AlreadyExists)
        {
            var recovered = await TryRecoverExistingRegistrationAsync(request, command);
            if (recovered)
                return;
        }

        if (!result.Succeeded)
        {
            await PersistRegistrationFailureAsync(
                command,
                result.Failure ?? new NyxIdRegistrationFailure(NyxIdRegistrationFailureKind.Adapter, "registration_failed", true),
                request.CredentialKid,
                scheduleRetry: result.Failure?.Retryable != false);
            return;
        }

        await PersistRegistrationSuccessAsync(
            command,
            result.NyxIdServiceId ?? request.ExistingNyxIdServiceId ?? string.Empty,
            result.NyxIdSlug ?? request.ExistingNyxIdSlug ?? string.Empty,
            result.RegisteredSpecHash ?? request.DesiredSpecHash,
            request.CredentialKid);
    }

    [EventHandler]
    public async Task HandleSetDefaultServingRevisionAsync(SetDefaultServingRevisionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureExistingIdentity(command.Identity);
        if (string.IsNullOrWhiteSpace(command.RevisionId))
            throw new InvalidOperationException("revision_id is required.");

        await PersistDomainEventAsync(new DefaultServingRevisionChangedEvent
        {
            Identity = command.Identity.Clone(),
            RevisionId = command.RevisionId,
        });
        await DispatchInvocationCatalogObservationAsync(CancellationToken.None);
    }

    protected override ServiceDefinitionState TransitionState(ServiceDefinitionState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ServiceDefinitionCreatedEvent>(ApplyCreated)
            .On<ServiceDefinitionUpdatedEvent>(ApplyUpdated)
            .On<ServiceExternalExposureUpdatedEvent>(ApplyLegacyExternalExposureUpdated)
            .On<ServiceRegistrationRequestedEvent>(ApplyRegistrationRequested)
            .On<ServiceRegistrationAttemptStartedEvent>(ApplyRegistrationAttemptStarted)
            .On<ServiceRegistrationSucceededEvent>(ApplyRegistrationSucceeded)
            .On<ServiceRegistrationFailedEvent>(ApplyRegistrationFailed)
            .On<ServiceRegistrationRetiredEvent>(ApplyRegistrationRetired)
            .On<DefaultServingRevisionChangedEvent>(ApplyDefaultServingRevisionChanged)
            .OrCurrent();

    private async Task<bool> TryRecoverExistingRegistrationAsync(
        NyxIdServiceRegistrationRequest request,
        RunRegistrationAttemptCommand command)
    {
        if (string.IsNullOrWhiteSpace(request.ExistingNyxIdServiceId))
            return false;

        var lookup = await _registrationPort.GetAsync(
            new NyxIdServiceLookupRequest(request.AccessToken, request.ExistingNyxIdServiceId),
            CancellationToken.None);
        if (!lookup.Found || lookup.Failure != null)
            return false;

        await PersistRegistrationSuccessAsync(
            command,
            lookup.NyxIdServiceId ?? request.ExistingNyxIdServiceId,
            lookup.NyxIdSlug ?? request.ExistingNyxIdSlug ?? string.Empty,
            lookup.RegisteredSpecHash ?? request.DesiredSpecHash,
            request.CredentialKid);
        return true;
    }

    private async Task PersistRegistrationSuccessAsync(
        RunRegistrationAttemptCommand command,
        string nyxIdServiceId,
        string nyxIdSlug,
        string registeredSpecHash,
        string credentialKid)
    {
        await PersistDomainEventAsync(new ServiceRegistrationSucceededEvent
        {
            Identity = command.Identity.Clone(),
            NyxidServiceId = nyxIdServiceId,
            NyxidSlug = nyxIdSlug,
            DesiredSpecHash = command.DesiredSpecHash.Trim(),
            RegisteredSpecHash = string.IsNullOrWhiteSpace(registeredSpecHash)
                ? command.DesiredSpecHash.Trim()
                : registeredSpecHash.Trim(),
            CredentialKid = credentialKid ?? string.Empty,
            Attempt = command.ExpectedAttempt,
            RegisteredAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        await DispatchInvocationCatalogObservationAsync(CancellationToken.None);
    }

    private async Task PersistRegistrationFailureAsync(
        RunRegistrationAttemptCommand command,
        NyxIdRegistrationFailure failure,
        string credentialKid,
        bool scheduleRetry)
    {
        var retryable = scheduleRetry && command.ExpectedAttempt < _retrySettings.MaxAttempts;
        var exhausted = scheduleRetry && !retryable;
        var now = DateTimeOffset.UtcNow;
        var nextAttemptAt = retryable
            ? DateTimeOffset.UtcNow + ComputeRetryDelay(command.ExpectedAttempt)
            : DateTimeOffset.UtcNow;
        await PersistDomainEventAsync(new ServiceRegistrationFailedEvent
        {
            Identity = command.Identity.Clone(),
            DesiredSpecHash = command.DesiredSpecHash.Trim(),
            LastError = exhausted
                ? NormalizeRetryExhaustedFailure(failure)
                : NormalizeFailure(failure),
            Attempt = command.ExpectedAttempt,
            FailedAt = Timestamp.FromDateTimeOffset(now),
            NextAttemptAt = retryable ? Timestamp.FromDateTimeOffset(nextAttemptAt) : null,
            CredentialKid = credentialKid ?? string.Empty,
        });
        await DispatchInvocationCatalogObservationAsync(CancellationToken.None);

        if (retryable)
            await ScheduleRegistrationRetryAsync(command, nextAttemptAt, CancellationToken.None);
    }

    private async Task ScheduleRegistrationRetryAsync(
        RunRegistrationAttemptCommand command,
        DateTimeOffset nextAttemptAt,
        CancellationToken ct)
    {
        var dueTime = nextAttemptAt - DateTimeOffset.UtcNow;
        if (dueTime <= TimeSpan.Zero)
            dueTime = TimeSpan.FromMilliseconds(1);

        await ScheduleSelfDurableTimeoutAsync(
            RegistrationRetryCallbackId,
            dueTime,
            new RegistrationRetryDueCommand
            {
                Identity = command.Identity.Clone(),
                ExpectedAttempt = command.ExpectedAttempt,
                DesiredSpecHash = command.DesiredSpecHash.Trim(),
                OpenapiUrl = command.OpenapiUrl ?? string.Empty,
            },
            ct: ct);
    }

    private Task DispatchSelfAsync(IMessage command, CancellationToken ct)
    {
        var envelope = CreateEnvelope(Id, command);
        envelope.Route = EnvelopeRouteSemantics.CreateTopologyPublication(Id, TopologyAudience.Self);
        return _dispatchPort.DispatchAsync(Id, envelope, ct);
    }

    private bool MatchesActiveRegistrationAttempt(int expectedAttempt, string? desiredSpecHash)
    {
        var exposure = State.Spec?.ExternalExposure;
        return exposure != null &&
               exposure.ExposureDesired &&
               exposure.Attempt == expectedAttempt &&
               string.Equals(exposure.DesiredSpecHash, desiredSpecHash?.Trim() ?? string.Empty, StringComparison.Ordinal) &&
               exposure.Status is ServiceRegistrationStatus.Pending or ServiceRegistrationStatus.Failed;
    }

    private bool MatchesActiveFailedRegistrationAttempt(int expectedAttempt, string? desiredSpecHash)
    {
        var exposure = State.Spec?.ExternalExposure;
        return exposure != null &&
               exposure.ExposureDesired &&
               exposure.Attempt == expectedAttempt &&
               string.Equals(exposure.DesiredSpecHash, desiredSpecHash?.Trim() ?? string.Empty, StringComparison.Ordinal) &&
               exposure.Status == ServiceRegistrationStatus.Failed;
    }

    private static ServiceDefinitionState ApplyCreated(ServiceDefinitionState state, ServiceDefinitionCreatedEvent evt)
    {
        var next = state.Clone();
        next.Spec = evt.Spec?.Clone() ?? new ServiceDefinitionSpec();
        next.Spec.ExternalExposure = ExtractExternalExposureIntent(evt.Spec?.ExternalExposure);
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Spec?.Identity, "created");
        return next;
    }

    private static ServiceDefinitionState ApplyUpdated(ServiceDefinitionState state, ServiceDefinitionUpdatedEvent evt)
    {
        var next = state.Clone();
        var spec = evt.Spec?.Clone() ?? new ServiceDefinitionSpec();
        spec.ExternalExposure = state.Spec?.ExternalExposure?.Clone();
        if (evt.Spec?.ExternalExposure?.ExposureDesired == true)
        {
            spec.ExternalExposure ??= new ExternalExposure();
            spec.ExternalExposure.ExposureDesired = true;
        }

        next.Spec = spec;
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Spec?.Identity, "updated");
        return next;
    }

    private static ServiceDefinitionState ApplyLegacyExternalExposureUpdated(
        ServiceDefinitionState state,
        ServiceExternalExposureUpdatedEvent evt)
    {
        var next = state.Clone();
        EnsureSpec(next);
        next.Spec.ExternalExposure = evt.ExternalExposure?.Clone() ?? new ExternalExposure();
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Identity, "external-exposure-updated");
        return next;
    }

    private static ServiceDefinitionState ApplyRegistrationRequested(
        ServiceDefinitionState state,
        ServiceRegistrationRequestedEvent evt)
    {
        var next = state.Clone();
        EnsureSpec(next);
        var current = next.Spec.ExternalExposure?.Clone() ?? new ExternalExposure();
        current.Status = ServiceRegistrationStatus.Pending;
        current.DesiredSpecHash = evt.DesiredSpecHash ?? string.Empty;
        current.LastError = string.Empty;
        current.Attempt = evt.Attempt;
        current.NextAttemptAt = null;
        current.CredentialKid = evt.CredentialKid ?? string.Empty;
        current.ExposureDesired = true;
        next.Spec.ExternalExposure = current;
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Identity, $"registration-requested:{evt.Attempt}");
        return next;
    }

    private static ServiceDefinitionState ApplyRegistrationAttemptStarted(
        ServiceDefinitionState state,
        ServiceRegistrationAttemptStartedEvent evt)
    {
        var next = state.Clone();
        EnsureSpec(next);
        var current = next.Spec.ExternalExposure?.Clone() ?? new ExternalExposure();
        current.Status = ServiceRegistrationStatus.Registering;
        current.DesiredSpecHash = evt.DesiredSpecHash ?? string.Empty;
        current.Attempt = evt.Attempt;
        current.NextAttemptAt = null;
        current.ExposureDesired = true;
        next.Spec.ExternalExposure = current;
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Identity, $"registration-started:{evt.Attempt}");
        return next;
    }

    private static ServiceDefinitionState ApplyRegistrationSucceeded(
        ServiceDefinitionState state,
        ServiceRegistrationSucceededEvent evt)
    {
        var next = state.Clone();
        EnsureSpec(next);
        var current = next.Spec.ExternalExposure?.Clone() ?? new ExternalExposure();
        current.Status = ServiceRegistrationStatus.Registered;
        current.NyxidServiceId = evt.NyxidServiceId ?? string.Empty;
        current.NyxidSlug = evt.NyxidSlug ?? string.Empty;
        current.DesiredSpecHash = evt.DesiredSpecHash ?? string.Empty;
        current.RegisteredSpecHash = evt.RegisteredSpecHash ?? string.Empty;
        current.LastError = string.Empty;
        current.Attempt = evt.Attempt;
        current.NextAttemptAt = null;
        current.CredentialKid = evt.CredentialKid ?? string.Empty;
        current.ExposureDesired = true;
        current.RegisteredAt = evt.RegisteredAt?.Clone();
        next.Spec.ExternalExposure = current;
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Identity, $"registration-succeeded:{evt.Attempt}");
        return next;
    }

    private static ServiceDefinitionState ApplyRegistrationFailed(
        ServiceDefinitionState state,
        ServiceRegistrationFailedEvent evt)
    {
        var next = state.Clone();
        EnsureSpec(next);
        var current = next.Spec.ExternalExposure?.Clone() ?? new ExternalExposure();
        current.Status = ServiceRegistrationStatus.Failed;
        current.DesiredSpecHash = evt.DesiredSpecHash ?? string.Empty;
        current.LastError = evt.LastError ?? string.Empty;
        current.Attempt = evt.Attempt;
        current.NextAttemptAt = evt.NextAttemptAt?.Clone();
        current.CredentialKid = evt.CredentialKid ?? string.Empty;
        current.ExposureDesired = true;
        next.Spec.ExternalExposure = current;
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Identity, $"registration-failed:{evt.Attempt}");
        return next;
    }

    private static ServiceDefinitionState ApplyRegistrationRetired(
        ServiceDefinitionState state,
        ServiceRegistrationRetiredEvent evt)
    {
        var next = state.Clone();
        EnsureSpec(next);
        var current = next.Spec.ExternalExposure?.Clone() ?? new ExternalExposure();
        current.Status = ServiceRegistrationStatus.Retired;
        current.NyxidServiceId = evt.NyxidServiceId ?? string.Empty;
        current.NyxidSlug = evt.NyxidSlug ?? string.Empty;
        current.DesiredSpecHash = evt.DesiredSpecHash ?? string.Empty;
        current.LastError = string.Empty;
        current.Attempt = evt.Attempt;
        current.NextAttemptAt = null;
        current.ExposureDesired = false;
        next.Spec.ExternalExposure = current;
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Identity, $"registration-retired:{evt.Attempt}");
        return next;
    }

    private static ServiceDefinitionState ApplyDefaultServingRevisionChanged(
        ServiceDefinitionState state,
        DefaultServingRevisionChangedEvent evt)
    {
        var next = state.Clone();
        next.DefaultServingRevisionId = evt.RevisionId ?? string.Empty;
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = BuildEventId(evt.Identity, $"default-serving:{evt.RevisionId}");
        return next;
    }

    private static void ValidateSpec(ServiceDefinitionSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Identity == null)
            throw new InvalidOperationException("service identity is required.");
        _ = ServiceKeys.Build(spec.Identity);
        if (spec.Endpoints.Count == 0)
            throw new InvalidOperationException("service endpoints are required.");
    }

    private void EnsureExistingIdentity(ServiceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var requested = ServiceKeys.Build(identity);
        var currentIdentity = State.Spec?.Identity?.Clone();
        var existing = currentIdentity == null ? string.Empty : ServiceKeys.Build(currentIdentity);
        if (existing.Length == 0)
            throw new InvalidOperationException($"Service definition '{requested}' does not exist.");
        if (!string.Equals(existing, requested, StringComparison.Ordinal))
            throw new InvalidOperationException($"Service definition actor '{Id}' is bound to '{existing}', but got '{requested}'.");
    }

    private static void EnsureRegistrationIntent(string? openApiUrl, string? desiredSpecHash)
    {
        _ = NormalizeRequired(openApiUrl, nameof(openApiUrl));
        _ = NormalizeRequired(desiredSpecHash, nameof(desiredSpecHash));
    }

    private static string NormalizeRequired(string? value, string name)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException($"{name} is required.");

        return normalized;
    }

    private static string NormalizeFailure(NyxIdRegistrationFailure failure)
    {
        var reason = failure.Reason?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(reason))
            reason = failure.Kind.ToString();

        return $"{failure.Kind}:{reason}";
    }

    private static string NormalizeRetryExhaustedFailure(NyxIdRegistrationFailure failure) =>
        $"retry_exhausted:{NormalizeFailure(failure)}";

    private TimeSpan ComputeRetryDelay(int attempt) =>
        _retrySettings.ComputeDelay(attempt);

    private static void EnsureSpec(ServiceDefinitionState state)
    {
        state.Spec ??= new ServiceDefinitionSpec();
    }

    private static ExternalExposure? ExtractExternalExposureIntent(ExternalExposure? externalExposure) =>
        externalExposure?.ExposureDesired == true
            ? new ExternalExposure { ExposureDesired = true }
            : null;

    private static string BuildEventId(ServiceIdentity? identity, string suffix)
    {
        var serviceKey = identity == null ? "unbound" : ServiceKeys.Build(identity);
        return $"{serviceKey}:{suffix}";
    }

    private Task DispatchInvocationCatalogObservationAsync(CancellationToken ct)
    {
        var spec = State.Spec;
        var identity = spec?.Identity;
        if (identity == null || string.IsNullOrWhiteSpace(identity.ServiceId))
            return Task.CompletedTask;

        var actorId = ServiceActorIds.InvocationCatalog(identity);
        return _dispatchPort.DispatchAsync(
            actorId,
            CreateEnvelope(
                actorId,
                new ObserveServiceInvocationCatalogCommand
                {
                    Identity = identity.Clone(),
                    ServiceEndpoints = { spec!.Endpoints.Select(ToDescriptor) },
                    SourceCatalogVersion = State.LastAppliedEventVersion,
                    ObservedAt = Timestamp.FromDateTime(DateTime.UtcNow),
                }),
            ct);
    }

    private static ServiceEndpointDescriptor ToDescriptor(ServiceEndpointSpec endpoint) =>
        new()
        {
            EndpointId = endpoint.EndpointId ?? string.Empty,
            DisplayName = endpoint.DisplayName ?? string.Empty,
            Kind = endpoint.Kind,
            RequestTypeUrl = endpoint.RequestTypeUrl ?? string.Empty,
            ResponseTypeUrl = endpoint.ResponseTypeUrl ?? string.Empty,
            Description = endpoint.Description ?? string.Empty,
        };

    private static EventEnvelope CreateEnvelope(string actorId, IMessage payload) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateDirect("gagent-service.definition", actorId),
            Propagation = new EnvelopePropagation(),
        };

    private sealed class NullNyxIdServiceRegistrationPort : INyxIdServiceRegistrationPort
    {
        public static NullNyxIdServiceRegistrationPort Instance { get; } = new();

        public Task<NyxIdServiceRegistrationResult> RegisterAsync(
            NyxIdServiceRegistrationRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(NyxIdServiceRegistrationResult.Failed(
                new NyxIdRegistrationFailure(NyxIdRegistrationFailureKind.Adapter, "registration_port_unavailable", true)));

        public Task<NyxIdServiceRegistrationResult> UpdateAsync(
            NyxIdServiceRegistrationRequest request,
            CancellationToken ct = default) =>
            RegisterAsync(request, ct);

        public Task<NyxIdServiceLookupResult> GetAsync(
            NyxIdServiceLookupRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(NyxIdServiceLookupResult.Failed(
                new NyxIdRegistrationFailure(NyxIdRegistrationFailureKind.Adapter, "registration_port_unavailable", true)));

        public Task<NyxIdServiceRetirementResult> RetireAsync(
            NyxIdServiceRetirementRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(NyxIdServiceRetirementResult.Failed(
                new NyxIdRegistrationFailure(NyxIdRegistrationFailureKind.Adapter, "registration_port_unavailable", true)));
    }

    private sealed class NullNyxIdRegistrationTokenAccessor : INyxIdRegistrationTokenAccessor
    {
        public static NullNyxIdRegistrationTokenAccessor Instance { get; } = new();

        public Task<NyxIdRegistrationToken?> GetTokenAsync(
            ServiceIdentity identity,
            CancellationToken ct = default) =>
            Task.FromResult<NyxIdRegistrationToken?>(null);
    }
}
