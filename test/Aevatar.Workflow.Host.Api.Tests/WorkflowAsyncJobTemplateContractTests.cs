using Aevatar.Workflow.Core.Primitives;
using Aevatar.Workflow.Core.Validation;
using FluentAssertions;

namespace Aevatar.Workflow.Host.Api.Tests;

public sealed class WorkflowAsyncJobTemplateContractTests
{
    private static readonly WorkflowParser Parser = new();

    [Fact]
    public void FirecrawlSubmitTemplate_ShouldHandoffDurablePollFactsToSelfReschedule()
    {
        var workflow = ParseTemplate("firecrawl_agent_async_submit.yaml");

        WorkflowValidator.Validate(workflow).Should().BeEmpty();
        workflow.Name.Should().Be("firecrawl_agent_async_submit");

        var submit = workflow.Steps.Should().Contain(step => step.Id == "submit_crawl").Subject;
        submit.Type.Should().Be("tool_call");
        submit.IdempotencyKey.Should().Be("${input.idempotency_key}");
        submit.Parameters["tool"].Should().Be("firecrawl_crawl_submit");
        submit.Parameters["arguments"].Should().Contain("\"idempotency_key\":\"${json(input.idempotency_key)}\"");

        var schedule = workflow.Steps.Should().Contain(step => step.Id == "ensure_poll_schedule").Subject;
        schedule.Type.Should().Be("self_reschedule");
        schedule.Parameters.Should().Contain("schedule_id", "firecrawl:${steps.submit_crawl.json.job_id}");
        schedule.Parameters.Should().Contain("cron_expression", "*/5 * * * *");
        schedule.Parameters.Should().Contain("timezone", "UTC");
        schedule.Parameters.Should().Contain("workflow_name", "firecrawl_agent_async_poll");
        schedule.Parameters.Should().ContainKey("prompt");
        schedule.Parameters["prompt"].Should().Contain("\"job_id\":\"${json(steps.submit_crawl.json.job_id)}\"");
        schedule.Parameters["prompt"].Should().Contain("\"idempotency_key\":\"${json(input.idempotency_key)}\"");
        schedule.Parameters["prompt"].Should().Contain("\"schedule_id\":\"firecrawl:${steps.submit_crawl.json.job_id}\"");
        schedule.Parameters["prompt"].Should().Contain("\"scope_id\":\"${json(input.scope_id)}\"");
        schedule.Parameters["prompt"].Should().Contain("\"attempt\":\"0\"");
        schedule.Parameters["prompt"].Should().Contain("\"max_attempts\":\"288\"");
        schedule.Parameters["prompt"].Should().Contain("\"deadline_utc\":\"${json(input.deadline_utc)}\"");
    }

    [Fact]
    public void FirecrawlPollTemplate_ShouldPollOnceAndDisableSameScheduleOnTerminalBranches()
    {
        var workflow = ParseTemplate("firecrawl_agent_async_poll.yaml");

        WorkflowValidator.Validate(workflow).Should().BeEmpty();
        workflow.Name.Should().Be("firecrawl_agent_async_poll");

        var poll = workflow.Steps.Should().Contain(step => step.Id == "poll_job").Subject;
        poll.Type.Should().Be("tool_call");
        poll.IdempotencyKey.Should().Be("${input.idempotency_key}");
        poll.Parameters.Should().Contain("tool", "firecrawl_crawl_status");
        poll.Parameters["arguments"].Should().Contain("\"job_id\":\"${json(input.job_id)}\"");

        var route = workflow.Steps.Should().Contain(step => step.Id == "route_status").Subject;
        route.Type.Should().Be("switch");
        route.Parameters.Should().Contain("on", "${steps.poll_job.json.status}");
        route.Branches.Should().Contain("completed", "stop_completed_schedule");
        route.Branches.Should().Contain("failed", "stop_failed_schedule");
        route.Branches.Should().Contain("cancelled", "stop_cancelled_schedule");
        route.Branches.Should().Contain("_default", "mark_pending");

        foreach (var terminalStepId in new[]
                 {
                     "stop_completed_schedule",
                     "stop_failed_schedule",
                     "stop_cancelled_schedule",
                 })
        {
            var terminalCleanup = workflow.Steps.Should().Contain(step => step.Id == terminalStepId).Subject;
            terminalCleanup.Type.Should().Be("self_reschedule");
            terminalCleanup.Parameters.Should().Contain("schedule_id", "${input.schedule_id}");
            terminalCleanup.Parameters.Should().Contain("enabled", "false");
            terminalCleanup.Parameters.Should().Contain("workflow_name", "firecrawl_agent_async_poll");
            terminalCleanup.Parameters.Should().Contain("prompt", "$input");
        }

        workflow.Steps.Should().Contain(step => step.Id == "mark_pending")
            .Which.Parameters["value"].Should().Contain("\"status\":\"pending\"");
    }

    [Theory]
    [InlineData("firecrawl_agent_async_submit.yaml")]
    [InlineData("firecrawl_agent_async_poll.yaml")]
    public void FirecrawlAsyncTemplates_ShouldNotUseCoreAsyncJobPrimitivesOrBusinessHeaders(string fileName)
    {
        var yaml = ReadTemplate(fileName);
        var workflow = Parser.Parse(yaml);

        workflow.Steps
            .Where(step => step.Type == "await_job" || step.Type == "async_job")
            .Should()
            .BeEmpty();
        yaml.Should().NotContain("type: await_job");
        yaml.Should().NotContain("type: async_job");
        yaml.Should().NotContain("header.job_id");
        yaml.Should().NotContain("header.idempotency_key");
        yaml.Should().NotContain("header.schedule_id");
        yaml.Should().NotContain("metadata");
        yaml.Should().NotContain("Metadata");
    }

    private static WorkflowDefinition ParseTemplate(string fileName) =>
        Parser.Parse(ReadTemplate(fileName));

    private static string ReadTemplate(string fileName) =>
        File.ReadAllText(Path.Combine(FindRepositoryRoot(), "workflows", fileName));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "aevatar.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}
