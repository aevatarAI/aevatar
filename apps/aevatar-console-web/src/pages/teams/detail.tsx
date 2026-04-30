import { useQuery, useQueryClient } from "@tanstack/react-query";
import {
  Alert,
  Button,
  Empty,
  Input,
  Space,
  Tag,
  Typography,
  message,
} from "antd";
import React from "react";
import { formatCompactDateTime } from "@/shared/datetime/dateTime";
import {
  history,
  subscribeToLocationChanges,
} from "@/shared/navigation/history";
import {
  buildTeamDetailHref,
  readTeamDetailRouteState,
} from "@/shared/navigation/teamRoutes";
import { studioApi } from "@/shared/studio/api";
import {
  formatStudioMemberLifecycleStage,
  formatStudioTeamLifecycleStage,
  type StudioMemberImplementationKind,
  type StudioMemberSummary,
} from "@/shared/studio/models";
import { buildStudioRoute } from "@/shared/studio/navigation";
import { AevatarPanel } from "@/shared/ui/aevatarPageShells";
import { AevatarCompactText } from "@/shared/ui/compactText";
import ConsoleMetricCard from "@/shared/ui/ConsoleMetricCard";
import ConsoleMenuPageShell from "@/shared/ui/ConsoleMenuPageShell";
import { describeError } from "@/shared/ui/errorText";
import { buildScopeHref } from "../scopes/components/scopeQuery";

function trimOptional(value: string | null | undefined): string {
  return value?.trim() ?? "";
}

function readRouteState() {
  if (typeof window === "undefined") {
    return readTeamDetailRouteState("", "");
  }

  return readTeamDetailRouteState(window.location.search, window.location.pathname);
}

function formatMemberImplementation(value: StudioMemberImplementationKind): string {
  switch (value) {
    case "workflow":
      return "Workflow";
    case "script":
      return "Script";
    case "gagent":
      return "GAgent";
    default:
      return "Unknown";
  }
}

function sortMembers(left: StudioMemberSummary, right: StudioMemberSummary): number {
  const rightUpdatedAt = Date.parse(right.updatedAt);
  const leftUpdatedAt = Date.parse(left.updatedAt);
  if (Number.isFinite(rightUpdatedAt) && Number.isFinite(leftUpdatedAt)) {
    if (rightUpdatedAt !== leftUpdatedAt) {
      return rightUpdatedAt - leftUpdatedAt;
    }
  }

  return left.displayName.localeCompare(right.displayName);
}

const detailGridStyle: React.CSSProperties = {
  display: "grid",
  gap: 16,
  gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
};

const formGridStyle: React.CSSProperties = {
  display: "grid",
  gap: 16,
  gridTemplateColumns: "repeat(auto-fit, minmax(260px, 1fr))",
};

const fieldStyle: React.CSSProperties = {
  display: "grid",
  gap: 8,
};

const listItemStyle: React.CSSProperties = {
  background: "#ffffff",
  border: "1px solid #eef2f7",
  borderRadius: 16,
  display: "grid",
  gap: 14,
  padding: 18,
};

const selectStyle: React.CSSProperties = {
  background: "#ffffff",
  border: "1px solid #d9d9d9",
  borderRadius: 8,
  minHeight: 40,
  padding: "0 12px",
  width: "100%",
};

const TeamDetailPage: React.FC = () => {
  const queryClient = useQueryClient();
  const [routeState, setRouteState] = React.useState(readRouteState);
  const [teamDisplayName, setTeamDisplayName] = React.useState("");
  const [teamDescription, setTeamDescription] = React.useState("");
  const [newMemberName, setNewMemberName] = React.useState("");
  const [newMemberDescription, setNewMemberDescription] = React.useState("");
  const [newMemberId, setNewMemberId] = React.useState("");
  const [newMemberKind, setNewMemberKind] =
    React.useState<StudioMemberImplementationKind>("workflow");
  const [selectedExistingMemberId, setSelectedExistingMemberId] = React.useState("");
  const [busyAction, setBusyAction] = React.useState("");

  React.useEffect(() => {
    return subscribeToLocationChanges(() => {
      setRouteState(readRouteState());
    });
  }, []);

  const scopeId = trimOptional(routeState.scopeId);
  const routeTeamId = trimOptional(routeState.teamId);
  const routeMemberId = trimOptional(routeState.memberId);

  const teamsQuery = useQuery({
    enabled: scopeId.length > 0,
    queryKey: ["teams", "roster", scopeId],
    queryFn: () => studioApi.listTeams(scopeId),
    retry: false,
  });
  const teamSelection = React.useMemo(
    () => [...(teamsQuery.data?.teams ?? [])],
    [teamsQuery.data?.teams],
  );
  const implicitSingleTeamId =
    !routeTeamId && teamSelection.length === 1 ? teamSelection[0].teamId : "";
  const selectedTeamId = routeTeamId || implicitSingleTeamId;

  const legacyMemberQuery = useQuery({
    enabled: scopeId.length > 0 && !routeTeamId && routeMemberId.length > 0,
    queryKey: ["teams", "legacy-member", scopeId, routeMemberId],
    queryFn: () => studioApi.getMember(scopeId, routeMemberId),
    retry: false,
  });

  React.useEffect(() => {
    if (!scopeId || routeTeamId || routeMemberId.length === 0) {
      return;
    }

    const inferredTeamId = trimOptional(legacyMemberQuery.data?.summary.teamId);
    if (!inferredTeamId) {
      return;
    }

    history.replace(
      buildTeamDetailHref({
        memberId: routeState.memberId || undefined,
        runId: routeState.runId || undefined,
        scopeId,
        serviceId: routeState.serviceId || undefined,
        tab: routeState.tab,
        teamId: inferredTeamId,
        workflowId: routeState.workflowId || undefined,
      }),
    );
  }, [
    legacyMemberQuery.data?.summary.teamId,
    routeMemberId,
    routeState.memberId,
    routeState.runId,
    routeState.serviceId,
    routeState.tab,
    routeState.workflowId,
    routeTeamId,
    scopeId,
  ]);

  React.useEffect(() => {
    if (!scopeId || routeTeamId || routeMemberId || teamSelection.length !== 1) {
      return;
    }

    history.replace(
      buildTeamDetailHref({
        scopeId,
        tab: routeState.tab,
        teamId: teamSelection[0].teamId,
      }),
    );
  }, [
    routeMemberId,
    routeState.tab,
    routeTeamId,
    scopeId,
    teamSelection,
  ]);

  const teamQuery = useQuery({
    enabled: scopeId.length > 0 && selectedTeamId.length > 0,
    queryKey: ["teams", "detail", scopeId, selectedTeamId],
    queryFn: () => studioApi.getTeam(scopeId, selectedTeamId),
    retry: false,
  });
  const teamMembersQuery = useQuery({
    enabled: scopeId.length > 0 && selectedTeamId.length > 0,
    queryKey: ["teams", "detail-members", scopeId, selectedTeamId],
    queryFn: () => studioApi.listTeamMembers(scopeId, selectedTeamId),
    retry: false,
  });
  const allMembersQuery = useQuery({
    enabled: scopeId.length > 0 && selectedTeamId.length > 0,
    queryKey: ["teams", "all-members", scopeId],
    queryFn: () => studioApi.listMembers(scopeId),
    retry: false,
  });

  const team = teamQuery.data ?? null;
  const teamMembers = React.useMemo(
    () => [...(teamMembersQuery.data?.members ?? [])].sort(sortMembers),
    [teamMembersQuery.data?.members],
  );
  const assignableMembers = React.useMemo(
    () =>
      (allMembersQuery.data?.members ?? [])
        .filter((member) => member.memberId !== routeMemberId)
        .filter((member) => trimOptional(member.teamId) !== selectedTeamId)
        .sort(sortMembers),
    [allMembersQuery.data?.members, routeMemberId, selectedTeamId],
  );

  React.useEffect(() => {
    if (!team) {
      return;
    }

    setTeamDisplayName(team.displayName);
    setTeamDescription(team.description);
  }, [team]);

  const invalidateTeamQueries = React.useCallback(async () => {
    const invalidations: Array<Promise<unknown>> = [];
    if (scopeId) {
      invalidations.push(
        queryClient.invalidateQueries({ queryKey: ["teams", "roster", scopeId] }),
      );
      invalidations.push(
        queryClient.invalidateQueries({ queryKey: ["teams", "all-members", scopeId] }),
      );
    }
    if (scopeId && selectedTeamId) {
      invalidations.push(
        queryClient.invalidateQueries({
          queryKey: ["teams", "detail", scopeId, selectedTeamId],
        }),
      );
      invalidations.push(
        queryClient.invalidateQueries({
          queryKey: ["teams", "detail-members", scopeId, selectedTeamId],
        }),
      );
    }
    await Promise.all(invalidations);
  }, [queryClient, scopeId, selectedTeamId]);

  const handleSaveTeam = async () => {
    if (!scopeId || !selectedTeamId || !teamDisplayName.trim()) {
      return;
    }

    setBusyAction("save-team");
    try {
      const updated = await studioApi.updateTeam({
        scopeId,
        teamId: selectedTeamId,
        displayName: teamDisplayName.trim(),
        description: trimOptional(teamDescription) || null,
      });
      setTeamDisplayName(updated.displayName);
      setTeamDescription(updated.description);
      await invalidateTeamQueries();
      void message.success("团队信息已更新。");
    } catch (error) {
      void message.error(
        describeError(error, "更新团队信息失败，请稍后再试。"),
      );
    } finally {
      setBusyAction("");
    }
  };

  const handleArchiveTeam = async () => {
    if (!scopeId || !selectedTeamId) {
      return;
    }

    setBusyAction("archive-team");
    try {
      await studioApi.archiveTeam(scopeId, selectedTeamId);
      await invalidateTeamQueries();
      void message.success("团队已归档。");
    } catch (error) {
      void message.error(describeError(error, "归档团队失败，请稍后再试。"));
    } finally {
      setBusyAction("");
    }
  };

  const handleCreateMember = async () => {
    if (!scopeId || !selectedTeamId || !newMemberName.trim()) {
      return;
    }

    setBusyAction("create-member");
    try {
      await studioApi.createMember({
        scopeId,
        teamId: selectedTeamId,
        displayName: newMemberName.trim(),
        description: trimOptional(newMemberDescription) || null,
        implementationKind: newMemberKind,
        memberId: trimOptional(newMemberId) || null,
      });
      setNewMemberName("");
      setNewMemberDescription("");
      setNewMemberId("");
      setNewMemberKind("workflow");
      await invalidateTeamQueries();
      void message.success("成员已创建并加入团队。");
    } catch (error) {
      void message.error(
        describeError(error, "创建成员失败，请稍后再试。"),
      );
    } finally {
      setBusyAction("");
    }
  };

  const handleAssignExistingMember = async () => {
    if (!scopeId || !selectedTeamId || !selectedExistingMemberId.trim()) {
      return;
    }

    setBusyAction("assign-member");
    try {
      await studioApi.updateMemberTeam(scopeId, selectedExistingMemberId.trim(), selectedTeamId);
      setSelectedExistingMemberId("");
      await invalidateTeamQueries();
      void message.success("成员已加入当前团队。");
    } catch (error) {
      void message.error(
        describeError(error, "加入团队失败，请稍后再试。"),
      );
    } finally {
      setBusyAction("");
    }
  };

  const handleRemoveMember = async (memberId: string) => {
    if (!scopeId || !memberId) {
      return;
    }

    setBusyAction(`remove:${memberId}`);
    try {
      await studioApi.updateMemberTeam(scopeId, memberId, null);
      await invalidateTeamQueries();
      void message.success("成员已移出团队。");
    } catch (error) {
      void message.error(
        describeError(error, "移出团队失败，请稍后再试。"),
      );
    } finally {
      setBusyAction("");
    }
  };

  const issueMessages = [
    teamsQuery.isError
      ? describeError(teamsQuery.error, "团队列表暂时不可用，请稍后再试。")
      : "",
    teamQuery.isError
      ? describeError(teamQuery.error, "团队详情暂时不可用，请稍后再试。")
      : "",
    teamMembersQuery.isError
      ? describeError(teamMembersQuery.error, "团队成员暂时不可用，请稍后再试。")
      : "",
    allMembersQuery.isError
      ? describeError(allMembersQuery.error, "成员目录暂时不可用，请稍后再试。")
      : "",
    legacyMemberQuery.isError
      ? describeError(legacyMemberQuery.error, "旧成员深链暂时无法解析。")
      : "",
  ].filter(Boolean);

  const backToTeamsHref = buildScopeHref("/teams", { scopeId });
  const teamSummary = team ?? teamSelection.find((item) => item.teamId === selectedTeamId) ?? null;
  const teamName = teamSummary?.displayName || selectedTeamId || "Team Detail";
  const saveTeamDisabled =
    busyAction.length > 0 ||
    !teamSummary ||
    !teamDisplayName.trim() ||
    (teamDisplayName.trim() === teamSummary.displayName &&
      trimOptional(teamDescription) === trimOptional(teamSummary.description));

  return (
    <ConsoleMenuPageShell
      breadcrumb={`Aevatar / Teams / ${teamName}`}
      description="Manage the current team record, inspect who is assigned to it, and use the existing member-first backend endpoints to add or remove members."
      extra={
        <Space wrap>
          <Button
            onClick={() => history.push(backToTeamsHref)}
            style={{ borderRadius: 12, height: 40, paddingInline: 18 }}
          >
            Back to My Teams
          </Button>
          {selectedTeamId ? (
            <Button
              danger
              loading={busyAction === "archive-team"}
              onClick={handleArchiveTeam}
              style={{ borderRadius: 12, height: 40, paddingInline: 18 }}
            >
              Archive Team
            </Button>
          ) : null}
        </Space>
      }
      title={teamName}
    >
      <div style={{ display: "grid", gap: 20 }}>
        {issueMessages.map((issue) => (
          <Alert key={issue} message={issue} showIcon type="warning" />
        ))}

        {!scopeId ? (
          <AevatarPanel layoutMode="document" padding={24} title="Team Detail">
            <Empty
              description="Missing scope id in the route. Return to My Teams and reload a scope first."
              image={Empty.PRESENTED_IMAGE_SIMPLE}
            />
          </AevatarPanel>
        ) : null}

        {scopeId && !selectedTeamId && legacyMemberQuery.isLoading ? (
          <AevatarPanel layoutMode="document" padding={24} title="Resolving Team">
            <Typography.Text>
              Resolving the current team from member {routeMemberId}...
            </Typography.Text>
          </AevatarPanel>
        ) : null}

        {scopeId && !selectedTeamId && !legacyMemberQuery.isLoading && teamSelection.length > 1 ? (
          <AevatarPanel layoutMode="document" padding={24} title="Choose a Team">
            <div style={{ display: "grid", gap: 16 }}>
              <Typography.Paragraph style={{ margin: 0 }}>
                This route did not include a <code>teamId</code>. Pick one team in
                the current scope and we will reopen the canonical team detail
                route.
              </Typography.Paragraph>
              <div style={formGridStyle}>
                {teamSelection.map((entry) => (
                  <div key={entry.teamId} style={listItemStyle}>
                    <div>
                      <Typography.Title level={4} style={{ margin: 0 }}>
                        {entry.displayName}
                      </Typography.Title>
                      <Typography.Paragraph
                        ellipsis={{ rows: 2 }}
                        style={{ color: "#8c8c8c", margin: "8px 0 0" }}
                      >
                        {entry.description || "No team description yet."}
                      </Typography.Paragraph>
                    </div>
                    <Space wrap>
                      <Tag color={entry.lifecycleStage === "archived" ? "default" : "blue"}>
                        {formatStudioTeamLifecycleStage(entry.lifecycleStage)}
                      </Tag>
                      <Tag>{entry.memberCount} members</Tag>
                    </Space>
                    <Button
                      type="primary"
                      onClick={() =>
                        history.push(
                          buildTeamDetailHref({
                            memberId: routeState.memberId || undefined,
                            runId: routeState.runId || undefined,
                            scopeId,
                            serviceId: routeState.serviceId || undefined,
                            tab: routeState.tab,
                            teamId: entry.teamId,
                            workflowId: routeState.workflowId || undefined,
                          }),
                        )
                      }
                    >
                      Open Team
                    </Button>
                  </div>
                ))}
              </div>
            </div>
          </AevatarPanel>
        ) : null}

        {scopeId &&
        !selectedTeamId &&
        !legacyMemberQuery.isLoading &&
        teamSelection.length === 0 ? (
          <AevatarPanel layoutMode="document" padding={24} title="No Teams Yet">
            <Empty
              description="This scope does not have any teams yet."
              image={Empty.PRESENTED_IMAGE_SIMPLE}
            />
          </AevatarPanel>
        ) : null}

        {scopeId &&
        !selectedTeamId &&
        !legacyMemberQuery.isLoading &&
        teamSelection.length <= 1 &&
        routeMemberId &&
        !trimOptional(legacyMemberQuery.data?.summary.teamId) ? (
          <Alert
            message="This member is not assigned to a team yet, so the old deep link cannot resolve a team detail page."
            showIcon
            type="info"
          />
        ) : null}

        {scopeId && selectedTeamId ? (
          <>
            <div style={detailGridStyle}>
              <ConsoleMetricCard
                label="Members"
                value={String(teamSummary?.memberCount ?? teamMembers.length)}
              />
              <ConsoleMetricCard
                label="Lifecycle"
                tone="green"
                value={formatStudioTeamLifecycleStage(teamSummary?.lifecycleStage)}
              />
              <ConsoleMetricCard
                label="Created"
                value={formatCompactDateTime(teamSummary?.createdAt, "--")}
              />
              <ConsoleMetricCard
                label="Updated"
                tone="purple"
                value={formatCompactDateTime(teamSummary?.updatedAt, "--")}
              />
            </div>

            <AevatarPanel layoutMode="document" padding={24} title="Team Summary">
              <div style={detailGridStyle}>
                <div>
                  <Typography.Text type="secondary">Team ID</Typography.Text>
                  <div>
                    <AevatarCompactText copyable monospace value={selectedTeamId} />
                  </div>
                </div>
                <div>
                  <Typography.Text type="secondary">Scope ID</Typography.Text>
                  <div>
                    <AevatarCompactText copyable monospace value={scopeId} />
                  </div>
                </div>
                <div>
                  <Typography.Text type="secondary">Lifecycle</Typography.Text>
                  <div>
                    <Tag color={teamSummary?.lifecycleStage === "archived" ? "default" : "blue"}>
                      {formatStudioTeamLifecycleStage(teamSummary?.lifecycleStage)}
                    </Tag>
                  </div>
                </div>
                <div>
                  <Typography.Text type="secondary">Members</Typography.Text>
                  <div>{String(teamSummary?.memberCount ?? teamMembers.length)}</div>
                </div>
                <div>
                  <Typography.Text type="secondary">Created</Typography.Text>
                  <div>{formatCompactDateTime(teamSummary?.createdAt, "--")}</div>
                </div>
                <div>
                  <Typography.Text type="secondary">Updated</Typography.Text>
                  <div>{formatCompactDateTime(teamSummary?.updatedAt, "--")}</div>
                </div>
              </div>
              <Typography.Paragraph style={{ margin: "16px 0 0" }}>
                {teamSummary?.description || "No team description yet."}
              </Typography.Paragraph>
            </AevatarPanel>

            <AevatarPanel layoutMode="document" padding={24} title="Team Members">
              {teamMembersQuery.isLoading ? (
                <Typography.Text>Loading team members...</Typography.Text>
              ) : teamMembers.length === 0 ? (
                <Empty
                  description="This team does not have any members yet."
                  image={Empty.PRESENTED_IMAGE_SIMPLE}
                />
              ) : (
                <div style={{ display: "grid", gap: 16 }}>
                  {teamMembers.map((member) => (
                    <div key={member.memberId} style={listItemStyle}>
                      <div
                        style={{
                          alignItems: "flex-start",
                          display: "flex",
                          gap: 12,
                          justifyContent: "space-between",
                        }}
                      >
                        <div style={{ minWidth: 0 }}>
                          <Typography.Title level={4} style={{ margin: 0 }}>
                            {member.displayName}
                          </Typography.Title>
                          <Typography.Paragraph
                            ellipsis={{ rows: 2 }}
                            style={{ color: "#8c8c8c", margin: "8px 0 0" }}
                          >
                            {member.description || "No member description yet."}
                          </Typography.Paragraph>
                        </div>
                        <Tag>{formatStudioMemberLifecycleStage(member.lifecycleStage)}</Tag>
                      </div>
                      <div style={detailGridStyle}>
                        <div>
                          <Typography.Text type="secondary">Member ID</Typography.Text>
                          <div>
                            <AevatarCompactText copyable monospace value={member.memberId} />
                          </div>
                        </div>
                        <div>
                          <Typography.Text type="secondary">Implementation</Typography.Text>
                          <div>{formatMemberImplementation(member.implementationKind)}</div>
                        </div>
                        <div>
                          <Typography.Text type="secondary">Published Service</Typography.Text>
                          <div>{trimOptional(member.publishedServiceId) || "--"}</div>
                        </div>
                        <div>
                          <Typography.Text type="secondary">Last Updated</Typography.Text>
                          <div>{formatCompactDateTime(member.updatedAt, "--")}</div>
                        </div>
                      </div>
                      <Space wrap>
                        <Button
                          type="primary"
                          onClick={() =>
                            history.push(
                              buildStudioRoute({
                                memberId: member.memberId,
                                scopeId,
                                tab: "studio",
                              }),
                            )
                          }
                        >
                          Open in Studio
                        </Button>
                        <Button
                          loading={busyAction === `remove:${member.memberId}`}
                          onClick={() => handleRemoveMember(member.memberId)}
                        >
                          Remove from Team
                        </Button>
                      </Space>
                    </div>
                  ))}
                </div>
              )}
            </AevatarPanel>

            <AevatarPanel layoutMode="document" padding={24} title="Create Member In This Team">
              <div style={formGridStyle}>
                <div style={fieldStyle}>
                  <Typography.Text strong>Display Name</Typography.Text>
                  <Input
                    aria-label="New Member Display Name"
                    placeholder="Support Planner"
                    value={newMemberName}
                    onChange={(event) => setNewMemberName(event.target.value)}
                  />
                </div>
                <div style={fieldStyle}>
                  <Typography.Text strong>Implementation Kind</Typography.Text>
                  <select
                    aria-label="New Member Implementation Kind"
                    style={selectStyle}
                    value={newMemberKind}
                    onChange={(event) =>
                      setNewMemberKind(event.target.value as StudioMemberImplementationKind)
                    }
                  >
                    <option value="workflow">Workflow</option>
                    <option value="script">Script</option>
                    <option value="gagent">GAgent</option>
                  </select>
                </div>
                <div style={fieldStyle}>
                  <Typography.Text strong>Custom Member ID</Typography.Text>
                  <Input
                    aria-label="New Member ID"
                    placeholder="optional-member-id"
                    value={newMemberId}
                    onChange={(event) => setNewMemberId(event.target.value)}
                  />
                </div>
                <div style={{ ...fieldStyle, gridColumn: "1 / -1" }}>
                  <Typography.Text strong>Description</Typography.Text>
                  <Input.TextArea
                    aria-label="New Member Description"
                    autoSize={{ minRows: 3, maxRows: 6 }}
                    placeholder="Describe what this member is responsible for inside the team."
                    value={newMemberDescription}
                    onChange={(event) => setNewMemberDescription(event.target.value)}
                  />
                </div>
              </div>
              <Space style={{ marginTop: 16 }}>
                <Button
                  loading={busyAction === "create-member"}
                  onClick={handleCreateMember}
                  type="primary"
                >
                  Create Member
                </Button>
              </Space>
            </AevatarPanel>

            <AevatarPanel layoutMode="document" padding={24} title="Add Existing Member">
              <div style={formGridStyle}>
                <div style={{ ...fieldStyle, gridColumn: "1 / -1" }}>
                  <Typography.Text strong>Available Members</Typography.Text>
                  <select
                    aria-label="Existing Member Selector"
                    style={selectStyle}
                    value={selectedExistingMemberId}
                    onChange={(event) => setSelectedExistingMemberId(event.target.value)}
                  >
                    <option value="">Select a member</option>
                    {assignableMembers.map((member) => {
                      const currentTeamId = trimOptional(member.teamId);
                      const suffix = currentTeamId
                        ? `currently in ${currentTeamId}`
                        : "currently unassigned";
                      return (
                        <option key={member.memberId} value={member.memberId}>
                          {(trimOptional(member.displayName) || member.memberId) +
                            ` · ${member.memberId} · ${suffix}`}
                        </option>
                      );
                    })}
                  </select>
                </div>
              </div>
              <Space style={{ marginTop: 16 }}>
                <Button
                  disabled={!selectedExistingMemberId}
                  loading={busyAction === "assign-member"}
                  onClick={handleAssignExistingMember}
                  type="primary"
                >
                  Add Member
                </Button>
              </Space>
            </AevatarPanel>

            <AevatarPanel layoutMode="document" padding={24} title="Edit Team">
              <div style={formGridStyle}>
                <div style={fieldStyle}>
                  <Typography.Text strong>Display Name</Typography.Text>
                  <Input
                    aria-label="Team Display Name"
                    value={teamDisplayName}
                    onChange={(event) => setTeamDisplayName(event.target.value)}
                  />
                </div>
                <div style={{ ...fieldStyle, gridColumn: "1 / -1" }}>
                  <Typography.Text strong>Description</Typography.Text>
                  <Input.TextArea
                    aria-label="Team Description"
                    autoSize={{ minRows: 4, maxRows: 8 }}
                    value={teamDescription}
                    onChange={(event) => setTeamDescription(event.target.value)}
                  />
                </div>
              </div>
              <Space style={{ marginTop: 16 }}>
                <Button
                  disabled={saveTeamDisabled}
                  loading={busyAction === "save-team"}
                  onClick={handleSaveTeam}
                  type="primary"
                >
                  Save Team Changes
                </Button>
              </Space>
            </AevatarPanel>
          </>
        ) : null}
      </div>
    </ConsoleMenuPageShell>
  );
};

export default TeamDetailPage;
