using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.Contracts;
using Aevatar.Studio.Application.Studio.Services;
using FluentAssertions;

namespace Aevatar.Studio.Tests;

public sealed class StudioTeamServiceTests
{
    private const string ScopeId = "scope-1";
    private const string TeamId = "t-1";

    [Fact]
    public async Task CreateAsync_ShouldValidateAndDelegate()
    {
        var commandPort = new RecordingCommandPort();
        var queryPort = new InMemoryQueryPort(NewSummary());
        var service = new StudioTeamService(commandPort, queryPort);

        var result = await service.CreateAsync(
            ScopeId,
            new CreateStudioTeamRequest(DisplayName: "Alpha"));

        result.Should().NotBeNull();
        result.TeamId.Should().Be(TeamId);
        commandPort.CreateCalls.Should().Be(1);
    }

    [Fact]
    public async Task CreateAsync_ShouldRejectEmptyDisplayName()
    {
        var service = new StudioTeamService(new RecordingCommandPort(), new InMemoryQueryPort(null));

        var act = () => service.CreateAsync(
            ScopeId,
            new CreateStudioTeamRequest(DisplayName: "  "));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*displayName is required*");
    }

    [Fact]
    public async Task GetAsync_ShouldThrowNotFound_WhenTeamMissing()
    {
        var queryPort = new InMemoryQueryPort(summary: null);
        var service = new StudioTeamService(new RecordingCommandPort(), queryPort);

        var act = () => service.GetAsync(ScopeId, "missing-team");

        await act.Should().ThrowAsync<StudioTeamNotFoundException>();
    }

    [Fact]
    public async Task GetAsync_ShouldReturnSummary_WhenTeamExists()
    {
        var summary = NewSummary();
        var service = new StudioTeamService(new RecordingCommandPort(), new InMemoryQueryPort(summary));

        var result = await service.GetAsync(ScopeId, TeamId);

        result.Should().Be(summary);
    }

    [Fact]
    public async Task UpdateAsync_ShouldRejectEmptyDisplayName()
    {
        var summary = NewSummary();
        var service = new StudioTeamService(new RecordingCommandPort(), new InMemoryQueryPort(summary));

        var act = () => service.UpdateAsync(
            ScopeId, TeamId,
            new UpdateStudioTeamRequest(DisplayName: PatchValue<string>.Of("  ")));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*displayName must not be empty*");
    }

    [Fact]
    public async Task UpdateAsync_ShouldRejectDisplayNameOverCap()
    {
        var summary = NewSummary();
        var service = new StudioTeamService(new RecordingCommandPort(), new InMemoryQueryPort(summary));

        var act = () => service.UpdateAsync(
            ScopeId, TeamId,
            new UpdateStudioTeamRequest(
                DisplayName: PatchValue<string>.Of(
                    new string('a', StudioTeamInputLimits.MaxDisplayNameLength + 1))));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*displayName must be at most*");
    }

    [Fact]
    public async Task UpdateAsync_ShouldRejectDescriptionOverCap()
    {
        var summary = NewSummary();
        var service = new StudioTeamService(new RecordingCommandPort(), new InMemoryQueryPort(summary));

        var act = () => service.UpdateAsync(
            ScopeId, TeamId,
            new UpdateStudioTeamRequest(
                Description: PatchValue<string>.Of(
                    new string('a', StudioTeamInputLimits.MaxDescriptionLength + 1))));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*description must be at most*");
    }

    [Fact]
    public async Task UpdateAsync_ShouldDelegateAndReRead()
    {
        var commandPort = new RecordingCommandPort();
        var summary = NewSummary();
        var service = new StudioTeamService(commandPort, new InMemoryQueryPort(summary));

        var result = await service.UpdateAsync(
            ScopeId, TeamId,
            new UpdateStudioTeamRequest(DisplayName: PatchValue<string>.Of("Beta")));

        commandPort.UpdateCalls.Should().Be(1);
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task ArchiveAsync_ShouldDelegateAndReRead()
    {
        var commandPort = new RecordingCommandPort();
        var summary = NewSummary();
        var service = new StudioTeamService(commandPort, new InMemoryQueryPort(summary));

        var result = await service.ArchiveAsync(ScopeId, TeamId);

        commandPort.ArchiveCalls.Should().Be(1);
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task ListAsync_ShouldDelegate()
    {
        var summary = NewSummary();
        var queryPort = new InMemoryQueryPort(summary);
        var service = new StudioTeamService(new RecordingCommandPort(), queryPort);

        var result = await service.ListAsync(ScopeId);

        result.Should().NotBeNull();
        result.Teams.Should().ContainSingle();
    }

    private static StudioTeamSummaryResponse NewSummary() =>
        new(
            TeamId: TeamId,
            ScopeId: ScopeId,
            DisplayName: "Alpha",
            Description: "desc",
            LifecycleStage: TeamLifecycleStageNames.Active,
            MemberCount: 0,
            CreatedAt: DateTimeOffset.UtcNow.AddDays(-1),
            UpdatedAt: DateTimeOffset.UtcNow);

    private sealed class InMemoryQueryPort : IStudioTeamQueryPort
    {
        private readonly StudioTeamSummaryResponse? _summary;

        public InMemoryQueryPort(StudioTeamSummaryResponse? summary) => _summary = summary;

        public Task<StudioTeamRosterResponse> ListAsync(
            string scopeId, StudioTeamRosterPageRequest? page = null, CancellationToken ct = default) =>
            Task.FromResult(new StudioTeamRosterResponse(scopeId, _summary == null ? [] : [_summary]));

        public Task<StudioTeamSummaryResponse?> GetAsync(
            string scopeId, string teamId, CancellationToken ct = default) =>
            Task.FromResult(_summary);
    }

    private sealed class RecordingCommandPort : IStudioTeamCommandPort
    {
        public int CreateCalls { get; private set; }
        public int UpdateCalls { get; private set; }
        public int ArchiveCalls { get; private set; }

        public Task<StudioTeamSummaryResponse> CreateAsync(
            string scopeId, CreateStudioTeamRequest request, CancellationToken ct = default)
        {
            CreateCalls++;
            return Task.FromResult(new StudioTeamSummaryResponse(
                TeamId: TeamId,
                ScopeId: scopeId,
                DisplayName: request.DisplayName ?? string.Empty,
                Description: request.Description ?? string.Empty,
                LifecycleStage: TeamLifecycleStageNames.Active,
                MemberCount: 0,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow));
        }

        public Task UpdateAsync(
            string scopeId, string teamId, UpdateStudioTeamRequest request, CancellationToken ct = default)
        {
            UpdateCalls++;
            return Task.CompletedTask;
        }

        public Task ArchiveAsync(
            string scopeId, string teamId, CancellationToken ct = default)
        {
            ArchiveCalls++;
            return Task.CompletedTask;
        }
    }
}
