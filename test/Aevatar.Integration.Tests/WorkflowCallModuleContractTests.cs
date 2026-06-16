using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Core.Modules;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Integration.Tests;

public sealed class WorkflowCallModuleContractTests : WorkflowCoreModuleTestBase
{
        [Fact]
        public async Task WorkflowCallModule_ShouldPublishFailureWhenMissingWorkflow_AndEmitInvocationRequestWhenPresent()
        {
            var module = new WorkflowCallModule();
            var ctx = CreateContext();

            await module.HandleAsync(
                Envelope(new StepRequestEvent
                {
                    StepId = "wf-1",
                    StepType = "workflow_call",
                    Input = "payload",
                }),
                ctx,
                CancellationToken.None);

            var failure = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
            failure.Success.Should().BeFalse();
            failure.Error.Should().Contain("missing workflow parameter");

            ctx.Published.Clear();

            await module.HandleAsync(
                Envelope(new StepRequestEvent
                {
                    StepId = "wf-2",
                    StepType = "workflow_call",
                    Input = "payload-2",
                    Parameters =
                    {
                        ["workflow"] = "sub_flow",
                        ["lifecycle"] = "singleton",
                    },
                }),
                ctx,
                CancellationToken.None);

            var invocation = ctx.Published.Select(x => x.evt).OfType<SubWorkflowInvokeRequestedEvent>().Single();
            invocation.WorkflowName.Should().Be("sub_flow");
            invocation.Input.Should().Be("payload-2");
            invocation.ParentStepId.Should().Be("wf-2");
            invocation.ParentRunId.Should().Be("default");
            invocation.Lifecycle.Should().Be("singleton");
            Regex.IsMatch(invocation.InvocationId, "^default:workflow_call:wf-2:[0-9a-f]{32}$")
                .Should().BeTrue("workflow_call invocation id should follow canonical format");

            ctx.Published.Clear();

            await module.HandleAsync(
                Envelope(new StepRequestEvent
                {
                    StepId = "wf-invalid-lifecycle",
                    StepType = "workflow_call",
                    Input = "payload-invalid",
                    Parameters =
                    {
                        ["workflow"] = "sub_flow",
                        ["lifecycle"] = "isolate",
                    },
                }),
                ctx,
                CancellationToken.None);

            var invalidLifecycleFailure = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
            invalidLifecycleFailure.Success.Should().BeFalse();
            invalidLifecycleFailure.Error.Should().Contain("lifecycle must be singleton/transient/scope");
            ctx.Published.Select(x => x.evt).OfType<SubWorkflowInvokeRequestedEvent>().Should().BeEmpty();

            ctx.Published.Clear();

            await module.HandleAsync(
                Envelope(new StepRequestEvent
                {
                    StepId = "",
                    StepType = "workflow_call",
                    Input = "payload-3",
                    Parameters =
                    {
                        ["workflow"] = "sub_flow",
                    },
                }),
                ctx,
                CancellationToken.None);

            var emptyStepFailure = ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
            emptyStepFailure.Success.Should().BeFalse();
            emptyStepFailure.Error.Should().Contain("missing step_id");
            ctx.Published.Select(x => x.evt).OfType<SubWorkflowInvokeRequestedEvent>().Should().BeEmpty();
        }

        [Fact]
        public void WorkflowCallModule_ShouldNotKeepProcessLevelInvocationDictionaries()
        {
            var forbidden = new[]
            {
                typeof(Dictionary<,>),
                typeof(ConcurrentDictionary<,>),
                typeof(HashSet<>),
                typeof(Queue<>),
            };

            var fields = typeof(WorkflowCallModule).GetFields(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic);

            var violations = fields
                .Where(field => field.FieldType.IsGenericType)
                .Select(field => (field.Name, genericType: field.FieldType.GetGenericTypeDefinition()))
                .Where(x => forbidden.Contains(x.genericType))
                .Select(x => x.Name)
                .ToList();

            violations.Should().BeEmpty("workflow_call fact state must be persisted in WorkflowGAgent state");
        }
}
