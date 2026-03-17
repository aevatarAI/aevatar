using Aevatar.Foundation.Abstractions;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Infrastructure.Activation;
using Aevatar.GAgentService.Governance.Infrastructure.Admission;
using Aevatar.GAgentService.Governance.Core.GAgents;
using Aevatar.GAgentService.Infrastructure.Activation;
using Aevatar.GAgentService.Tests.TestSupport;
using FluentAssertions;

namespace Aevatar.GAgentService.Tests.Infrastructure;

public sealed class GovernanceInfrastructureTests
{
    [Fact]
    public async Task DefaultActivationAdmissionEvaluator_ShouldFlagMissingPoliciesAndBindings()
    {
        var evaluator = new DefaultActivationAdmissionEvaluator();
        var request = new ActivationAdmissionRequest
        {
            CapabilityView = new ActivationCapabilityView
            {
                Identity = GAgentServiceTestKit.CreateIdentity(),
                RevisionId = "r1",
                MissingPolicyIds = { "policy-missing" },
                Bindings =
                {
                    new ServiceBindingSpec
                    {
                        Identity = GAgentServiceTestKit.CreateIdentity(),
                        BindingId = "binding-a",
                        BindingKind = ServiceBindingKind.Service,
                        ServiceRef = new BoundServiceRef
                        {
                            Identity = GAgentServiceTestKit.CreateIdentity(serviceId: "dependency"),
                            EndpointId = "run",
                        },
                    },
                },
                Policies =
                {
                    new ServicePolicySpec
                    {
                        Identity = GAgentServiceTestKit.CreateIdentity(),
                        PolicyId = "policy-a",
                        ActivationRequiredBindingIds = { "binding-missing" },
                    },
                },
            },
        };

        var decision = await evaluator.EvaluateAsync(request);

        decision.Allowed.Should().BeFalse();
        decision.Violations.Select(x => x.Code).Should().BeEquivalentTo(["missing_policy", "missing_binding"]);
    }

    [Fact]
    public async Task DefaultActivationAdmissionEvaluator_ShouldHonorCancellationAndAllowSatisfiedRequests()
    {
        var evaluator = new DefaultActivationAdmissionEvaluator();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var canceled = () => evaluator.EvaluateAsync(new ActivationAdmissionRequest
        {
            CapabilityView = new ActivationCapabilityView(),
        }, cts.Token);
        await canceled.Should().ThrowAsync<OperationCanceledException>();

        var allowed = await evaluator.EvaluateAsync(new ActivationAdmissionRequest
        {
            CapabilityView = new ActivationCapabilityView
            {
                Bindings =
                {
                    new ServiceBindingSpec
                    {
                        Identity = GAgentServiceTestKit.CreateIdentity(),
                        BindingId = "binding-a",
                        BindingKind = ServiceBindingKind.Secret,
                        SecretRef = new BoundSecretRef
                        {
                            SecretName = "secret",
                        },
                    },
                },
                Policies =
                {
                    new ServicePolicySpec
                    {
                        Identity = GAgentServiceTestKit.CreateIdentity(),
                        PolicyId = "policy-a",
                        ActivationRequiredBindingIds = { "binding-a" },
                    },
                },
            },
        });

        allowed.Allowed.Should().BeTrue();
        allowed.Violations.Should().BeEmpty();
    }

    [Fact]
    public async Task DefaultInvokeAdmissionEvaluator_ShouldEnforceExposurePolicyAndCallerRules()
    {
        var evaluator = new DefaultInvokeAdmissionEvaluator();
        var request = new InvokeAdmissionRequest
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            ServiceKey = "tenant/app/default/svc",
            EndpointId = "invoke",
            Endpoint = new ServiceEndpointExposureSpec
            {
                EndpointId = "invoke",
                ExposureKind = ServiceEndpointExposureKind.Disabled,
            },
            MissingPolicyIds = { "policy-missing" },
            HasActiveDeployment = false,
            Caller = new ServiceInvocationCaller
            {
                ServiceKey = "tenant/app/default/caller",
            },
            Policies =
            {
                new ServicePolicySpec
                {
                    Identity = GAgentServiceTestKit.CreateIdentity(),
                    PolicyId = "policy-a",
                    InvokeRequiresActiveDeployment = true,
                    InvokeAllowedCallerServiceKeys = { "tenant/app/default/allowed" },
                },
            },
        };

        var denied = await evaluator.EvaluateAsync(request);

        denied.Allowed.Should().BeFalse();
        denied.Violations.Select(x => x.Code).Should().BeEquivalentTo([
            "missing_policy",
            "endpoint_disabled",
            "inactive_deployment",
            "caller_not_allowed",
        ]);

        var allowed = await evaluator.EvaluateAsync(new InvokeAdmissionRequest
        {
            Identity = GAgentServiceTestKit.CreateIdentity(),
            ServiceKey = "tenant/app/default/svc",
            EndpointId = "invoke",
            Endpoint = new ServiceEndpointExposureSpec
            {
                EndpointId = "invoke",
                ExposureKind = ServiceEndpointExposureKind.Public,
            },
            HasActiveDeployment = true,
            Caller = new ServiceInvocationCaller
            {
                ServiceKey = "tenant/app/default/allowed",
            },
            Policies =
            {
                new ServicePolicySpec
                {
                    Identity = GAgentServiceTestKit.CreateIdentity(),
                    PolicyId = "policy-a",
                    InvokeRequiresActiveDeployment = true,
                    InvokeAllowedCallerServiceKeys = { "tenant/app/default/allowed" },
                },
            },
        });

        allowed.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task DefaultServiceGovernanceCommandTargetProvisioner_ShouldCreateAndReuseGovernanceActors()
    {
        var identity = GAgentServiceTestKit.CreateIdentity();
        var runtime = new RecordingActorRuntime();
        var provisioner = new DefaultServiceGovernanceCommandTargetProvisioner(runtime);

        var configurationTarget = await provisioner.EnsureConfigurationTargetAsync(identity);

        configurationTarget.Should().Be(ServiceActorIds.Configuration(identity));
        runtime.CreateCalls.Should().ContainSingle()
            .Which.Should().Be((typeof(ServiceConfigurationGAgent), ServiceActorIds.Configuration(identity)));

        runtime.MarkExisting(ServiceActorIds.Configuration(identity));
        runtime.CreateCalls.Clear();

        await provisioner.EnsureConfigurationTargetAsync(identity);

        runtime.CreateCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task DefaultServiceGovernanceCommandTargetProvisioner_ShouldValidateInputs()
    {
        var runtime = new RecordingActorRuntime();
        var provisioner = new DefaultServiceGovernanceCommandTargetProvisioner(runtime);
        var act = () => provisioner.EnsureConfigurationTargetAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ActorTargetProvisionerBase_ShouldValidateActorId_AndRejectMissingActorAfterExistenceCheck()
    {
        var runtime = new RecordingActorRuntime();
        var provisioner = new TestProvisioner(runtime);
        var blank = () => provisioner.EnsureAsync<TestStaticServiceAgent>(" ");

        await blank.Should().ThrowAsync<ArgumentException>();

        runtime.MarkExistingWithoutActor("actor-missing");
        var missing = () => provisioner.EnsureAsync<TestStaticServiceAgent>("actor-missing");

        await missing.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*was not found after existence check*");
    }

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        private readonly Dictionary<string, IActor> _actors = new(StringComparer.Ordinal);
        private readonly HashSet<string> _existingWithoutActor = new(StringComparer.Ordinal);

        public List<(System.Type actorType, string actorId)> CreateCalls { get; } = [];

        public void MarkExisting(string actorId)
        {
            _actors[actorId] = new RecordingActor(actorId);
        }

        public void MarkExistingWithoutActor(string actorId)
        {
            _existingWithoutActor.Add(actorId);
        }

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent =>
            CreateAsync(typeof(TAgent), id, ct);

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default)
        {
            var actorId = id ?? $"created:{agentType.Name}";
            CreateCalls.Add((agentType, actorId));
            var actor = new RecordingActor(actorId);
            _actors[actorId] = actor;
            return Task.FromResult<IActor>(actor);
        }

        public Task DestroyAsync(string id, CancellationToken ct = default)
        {
            _actors.Remove(id);
            return Task.CompletedTask;
        }

        public Task<IActor?> GetAsync(string id) =>
            Task.FromResult(_actors.TryGetValue(id, out var actor) ? actor : null);

        public Task<bool> ExistsAsync(string id) =>
            Task.FromResult(_actors.ContainsKey(id) || _existingWithoutActor.Contains(id));

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class TestProvisioner : ActorTargetProvisionerBase
    {
        public TestProvisioner(IActorRuntime runtime)
            : base(runtime)
        {
        }

        public Task<string> EnsureAsync<TAgent>(string actorId, CancellationToken ct = default)
            where TAgent : IAgent =>
            EnsureActorAsync<TAgent>(actorId, ct);
    }

    private sealed class RecordingActor : IActor
    {
        public RecordingActor(string id)
        {
            Id = id;
        }

        public string Id { get; }

        public IAgent Agent { get; } = new TestStaticServiceAgent();

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }
}
