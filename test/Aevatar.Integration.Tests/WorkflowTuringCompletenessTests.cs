using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Core;
using Aevatar.Workflow.Core.Modules;
using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Core.Validation;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Integration.Tests;

[Trait("Category", "Integration")]
[Trait("Feature", "WorkflowTuringCompleteness")]
public sealed class WorkflowTuringCompletenessTests
{
    [Fact]
    public async Task IncDecJzProgram_ShouldTransferCounterValueInClosedWorldMode()
    {
        var workflow = BuildCounterTransferWorkflow();
        WorkflowValidator.Validate(workflow).Should().BeEmpty();

        var completed = await ExecuteClosedWorldWorkflowAsync(workflow, maxTransitions: 256);

        completed.Success.Should().BeTrue();
        completed.Output.Should().Be("2");
    }

    [Fact]
    public async Task TwoCounterProgram_ShouldComputeAdditionInClosedWorldMode()
    {
        var workflow = BuildCounterAdditionWorkflow();
        WorkflowValidator.Validate(workflow).Should().BeEmpty();

        var completed = await ExecuteClosedWorldWorkflowAsync(workflow, maxTransitions: 512);

        completed.Success.Should().BeTrue();
        completed.Output.Should().Be("5");
    }

    [Fact]
    public async Task NonHaltingProgram_ShouldExceedTransitionBudget()
    {
        var workflow = BuildNonHaltingWorkflow();
        WorkflowValidator.Validate(workflow).Should().BeEmpty();

        Func<Task> run = async () => await ExecuteClosedWorldWorkflowAsync(workflow, maxTransitions: 64);
        await run.Should().ThrowAsync<TimeoutException>();
    }

    private static WorkflowDefinition BuildCounterTransferWorkflow() =>
        new()
        {
            Name = "counter_transfer",
            Configuration = new WorkflowRuntimeConfiguration
            {
                ClosedWorldMode = true,
            },
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "init_c1",
                    Type = "assign",
                    Parameters = new Dictionary<string, string>
                    {
                        ["target"] = "c1",
                        ["value"] = "2",
                    },
                },
                new StepDefinition
                {
                    Id = "init_c2",
                    Type = "assign",
                    Parameters = new Dictionary<string, string>
                    {
                        ["target"] = "c2",
                        ["value"] = "0",
                    },
                },
                new StepDefinition
                {
                    Id = "check_c1",
                    Type = "conditional",
                    Parameters = new Dictionary<string, string>
                    {
                        ["condition"] = "${eq(variables.c1, '0')}",
                    },
                    Branches = new Dictionary<string, string>
                    {
                        ["true"] = "halt",
                        ["false"] = "dec_c1",
                    },
                },
                new StepDefinition
                {
                    Id = "dec_c1",
                    Type = "assign",
                    Next = "inc_c2",
                    Parameters = new Dictionary<string, string>
                    {
                        ["target"] = "c1",
                        ["value"] = "${sub(variables.c1, 1)}",
                    },
                },
                new StepDefinition
                {
                    Id = "inc_c2",
                    Type = "assign",
                    Next = "check_c1",
                    Parameters = new Dictionary<string, string>
                    {
                        ["target"] = "c2",
                        ["value"] = "${add(variables.c2, 1)}",
                    },
                },
                new StepDefinition
                {
                    Id = "halt",
                    Type = "assign",
                    Parameters = new Dictionary<string, string>
                    {
                        ["target"] = "result",
                        ["value"] = "${variables.c2}",
                    },
                },
            ],
        };

    private static WorkflowDefinition BuildCounterAdditionWorkflow() =>
        new()
        {
            Name = "counter_addition",
            Configuration = new WorkflowRuntimeConfiguration
            {
                ClosedWorldMode = true,
            },
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "init_a",
                    Type = "assign",
                    Parameters = new Dictionary<string, string>
                    {
                        ["target"] = "a",
                        ["value"] = "2",
                    },
                },
                new StepDefinition
                {
                    Id = "init_b",
                    Type = "assign",
                    Parameters = new Dictionary<string, string>
                    {
                        ["target"] = "b",
                        ["value"] = "3",
                    },
                },
                new StepDefinition
                {
                    Id = "check_b",
                    Type = "conditional",
                    Parameters = new Dictionary<string, string>
                    {
                        ["condition"] = "${eq(variables.b, '0')}",
                    },
                    Branches = new Dictionary<string, string>
                    {
                        ["true"] = "halt",
                        ["false"] = "inc_a",
                    },
                },
                new StepDefinition
                {
                    Id = "inc_a",
                    Type = "assign",
                    Next = "dec_b",
                    Parameters = new Dictionary<string, string>
                    {
                        ["target"] = "a",
                        ["value"] = "${add(variables.a, 1)}",
                    },
                },
                new StepDefinition
                {
                    Id = "dec_b",
                    Type = "assign",
                    Next = "check_b",
                    Parameters = new Dictionary<string, string>
                    {
                        ["target"] = "b",
                        ["value"] = "${sub(variables.b, 1)}",
                    },
                },
                new StepDefinition
                {
                    Id = "halt",
                    Type = "assign",
                    Parameters = new Dictionary<string, string>
                    {
                        ["target"] = "result",
                        ["value"] = "${variables.a}",
                    },
                },
            ],
        };

    private static WorkflowDefinition BuildNonHaltingWorkflow() =>
        new()
        {
            Name = "non_halting",
            Configuration = new WorkflowRuntimeConfiguration
            {
                ClosedWorldMode = true,
            },
            Roles = [],
            Steps =
            [
                new StepDefinition
                {
                    Id = "loop",
                    Type = "conditional",
                    Parameters = new Dictionary<string, string>
                    {
                        ["condition"] = "false",
                    },
                    Branches = new Dictionary<string, string>
                    {
                        ["true"] = "halt",
                        ["false"] = "loop",
                    },
                },
                new StepDefinition
                {
                    Id = "halt",
                    Type = "assign",
                    Parameters = new Dictionary<string, string>
                    {
                        ["target"] = "result",
                        ["value"] = "done",
                    },
                },
            ],
        };

    private static async Task<WorkflowCompletedEvent> ExecuteClosedWorldWorkflowAsync(
        WorkflowDefinition workflow,
        int maxTransitions)
    {
        var loop = new WorkflowLoopModule();
        loop.SetWorkflow(workflow);

        var modules = new Dictionary<string, IEventModule>(StringComparer.OrdinalIgnoreCase)
        {
            ["assign"] = new AssignModule(),
            ["conditional"] = new ConditionalModule(),
            ["switch"] = new SwitchModule(),
            ["transform"] = new TransformModule(),
            ["while"] = new WhileModule(),
        };

        var queue = new Queue<IMessage>();
        var workflowRunAgent = new TestWorkflowRunAgent("workflow-turing-proof-agent", "proof-run");
        queue.Enqueue(new StartWorkflowEvent
        {
            RunId = "proof-run",
            Input = "seed",
        });

        var transitions = 0;
        while (queue.Count > 0 && transitions < maxTransitions)
        {
            transitions++;
            var message = queue.Dequeue();
            if (message is not StartWorkflowEvent && message is not StepCompletedEvent)
                continue;

            var loopCtx = CreateContext(workflowRunAgent);
            await loop.HandleAsync(Envelope(message), loopCtx, CancellationToken.None);
            foreach (var (evt, _) in loopCtx.Published)
            {
                switch (evt)
                {
                    case WorkflowCompletedEvent completed:
                        return completed;
                    case StepCompletedEvent completedStep:
                        queue.Enqueue(completedStep);
                        break;
                    case StepRequestEvent request:
                    {
                        var completedStep = await ExecuteStepAsync(request, modules, workflowRunAgent);
                        queue.Enqueue(completedStep);
                        break;
                    }
                }
            }
        }

        throw new TimeoutException($"Workflow did not complete within transition budget ({maxTransitions}).");
    }

    private static async Task<StepCompletedEvent> ExecuteStepAsync(
        StepRequestEvent request,
        IReadOnlyDictionary<string, IEventModule> modules,
        TestWorkflowRunAgent workflowRunAgent)
    {
        if (!modules.TryGetValue(request.StepType, out var module))
            throw new InvalidOperationException($"No closed-world executor for step type '{request.StepType}'.");

        var ctx = CreateContext(workflowRunAgent);
        await module.HandleAsync(Envelope(request), ctx, CancellationToken.None);

        return ctx.Published.Select(x => x.evt).OfType<StepCompletedEvent>().Single();
    }

    private static EventEnvelope Envelope(IMessage evt)
    {
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            PublisherId = "workflow-turing-test",
            Direction = EventDirection.Self,
        };
    }

    private static TestEventHandlerContext CreateContext(TestWorkflowRunAgent workflowRunAgent)
    {
        return new TestEventHandlerContext(
            new ServiceCollection().BuildServiceProvider(),
            workflowRunAgent,
            NullLogger.Instance);
    }

}
