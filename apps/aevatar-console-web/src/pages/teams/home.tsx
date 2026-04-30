import { PlusOutlined } from "@ant-design/icons";
import { useQuery } from "@tanstack/react-query";
import { Alert, Button, Empty, Space, Tag, Typography } from "antd";
import React from "react";
import { formatCompactDateTime } from "@/shared/datetime/dateTime";
import { history } from "@/shared/navigation/history";
import {
  buildTeamCreateHref,
  buildTeamDetailHref,
} from "@/shared/navigation/teamRoutes";
import { studioApi } from "@/shared/studio/api";
import {
  formatStudioTeamLifecycleStage,
  type StudioMemberSummary,
  type StudioTeamSummary,
} from "@/shared/studio/models";
import { AevatarPanel } from "@/shared/ui/aevatarPageShells";
import { AevatarCompactText } from "@/shared/ui/compactText";
import ConsoleMetricCard from "@/shared/ui/ConsoleMetricCard";
import ConsoleMenuPageShell from "@/shared/ui/ConsoleMenuPageShell";
import { describeError } from "@/shared/ui/errorText";
import ScopeQueryCard from "../scopes/components/ScopeQueryCard";
import { resolveStudioScopeContext } from "../scopes/components/resolvedScope";
import {
  buildScopeHref,
  normalizeScopeDraft,
  readScopeQueryDraft,
  type ScopeQueryDraft,
} from "../scopes/components/scopeQuery";

function trimOptional(value: string | null | undefined): string {
  return value?.trim() ?? "";
}

function readTimestamp(value: string | null | undefined): number {
  const parsed = Date.parse(value || "");
  return Number.isFinite(parsed) ? parsed : 0;
}

function sortTeams(left: StudioTeamSummary, right: StudioTeamSummary): number {
  const updatedDelta = readTimestamp(right.updatedAt) - readTimestamp(left.updatedAt);
  if (updatedDelta !== 0) {
    return updatedDelta;
  }

  return left.displayName.localeCompare(right.displayName);
}

function formatMemberPreview(members: readonly StudioMemberSummary[]): string {
  if (members.length === 0) {
    return "No members assigned yet";
  }

  const labels = members
    .slice(0, 3)
    .map((member) => trimOptional(member.displayName) || member.memberId);
  return members.length > 3
    ? `${labels.join(" · ")} +${members.length - 3}`
    : labels.join(" · ");
}

const metricGridStyle: React.CSSProperties = {
  display: "grid",
  gap: 16,
  gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
};

const cardGridStyle: React.CSSProperties = {
  display: "grid",
  gap: 16,
  gridTemplateColumns: "repeat(auto-fit, minmax(280px, 1fr))",
};

const teamCardStyle: React.CSSProperties = {
  background: "#ffffff",
  border: "1px solid #e8edf5",
  borderRadius: 18,
  boxShadow: "0 12px 32px rgba(15, 23, 42, 0.05)",
  display: "grid",
  gap: 16,
  minWidth: 0,
  padding: 20,
};

const detailLabelStyle: React.CSSProperties = {
  color: "#8c8c8c",
  fontSize: 12,
  fontWeight: 600,
  textTransform: "uppercase",
};

const detailValueStyle: React.CSSProperties = {
  color: "#1d2129",
  fontSize: 14,
  lineHeight: 1.5,
};

const TeamsHomePage: React.FC = () => {
  const [draft, setDraft] = React.useState<ScopeQueryDraft>(() => readScopeQueryDraft());
  const [activeDraft, setActiveDraft] = React.useState<ScopeQueryDraft>(() =>
    normalizeScopeDraft({ scopeId: "" }),
  );
  const [showScopePicker, setShowScopePicker] = React.useState(false);

  const authSessionQuery = useQuery({
    queryKey: ["teams", "auth-session"],
    queryFn: () => studioApi.getAuthSession(),
    retry: false,
  });
  const resolvedScope = React.useMemo(
    () => resolveStudioScopeContext(authSessionQuery.data),
    [authSessionQuery.data],
  );

  React.useEffect(() => {
    if (!resolvedScope?.scopeId) {
      return;
    }

    const nextDraft = normalizeScopeDraft({ scopeId: resolvedScope.scopeId });
    setDraft(nextDraft);
    setActiveDraft(nextDraft);
  }, [resolvedScope?.scopeId]);

  React.useEffect(() => {
    history.replace(buildScopeHref("/teams", activeDraft));
  }, [activeDraft]);

  const scopeId = activeDraft.scopeId.trim();
  const teamsQuery = useQuery({
    enabled: scopeId.length > 0,
    queryKey: ["teams", "roster", scopeId],
    queryFn: () => studioApi.listTeams(scopeId),
    retry: false,
  });
  const membersQuery = useQuery({
    enabled: scopeId.length > 0,
    queryKey: ["teams", "members", scopeId],
    queryFn: () => studioApi.listMembers(scopeId),
    retry: false,
  });

  const teams = React.useMemo(
    () => [...(teamsQuery.data?.teams ?? [])].sort(sortTeams),
    [teamsQuery.data?.teams],
  );
  const membersByTeamId = React.useMemo(() => {
    const grouped = new Map<string, StudioMemberSummary[]>();
    for (const member of membersQuery.data?.members ?? []) {
      const teamId = trimOptional(member.teamId);
      if (!teamId) {
        continue;
      }

      const bucket = grouped.get(teamId);
      if (bucket) {
        bucket.push(member);
      } else {
        grouped.set(teamId, [member]);
      }
    }

    return grouped;
  }, [membersQuery.data?.members]);
  const unassignedMembers = React.useMemo(
    () =>
      (membersQuery.data?.members ?? []).filter(
        (member) => !trimOptional(member.teamId),
      ),
    [membersQuery.data?.members],
  );
  const archivedTeamCount = teams.filter(
    (team) => team.lifecycleStage === "archived",
  ).length;
  const activeTeamCount = teams.length - archivedTeamCount;
  const membersAssignedCount = (membersQuery.data?.members ?? []).filter((member) =>
    Boolean(trimOptional(member.teamId)),
  ).length;
  const queryIssues = [
    authSessionQuery.isError
      ? describeError(
          authSessionQuery.error,
          "登录状态暂时不可用，请刷新后重试。",
        )
      : "",
    teamsQuery.isError
      ? describeError(teamsQuery.error, "团队列表暂时不可用，请稍后再试。")
      : "",
    membersQuery.isError
      ? describeError(membersQuery.error, "成员列表暂时不可用，请稍后再试。")
      : "",
  ].filter(Boolean);

  return (
    <ConsoleMenuPageShell
      breadcrumb="Aevatar / Teams"
      description="Create real team records, inspect each team roster inside the current scope, and hand members off into Studio only when you need to build or bind."
      extra={
        <Space wrap>
          <Button
            onClick={() => setShowScopePicker((current) => !current)}
            style={{ borderRadius: 12, height: 40, paddingInline: 18 }}
          >
            {showScopePicker ? "Hide scope" : "Change scope"}
          </Button>
          <Button
            icon={<PlusOutlined />}
            onClick={() =>
              history.push(
                buildTeamCreateHref({
                  scopeId:
                    scopeId ||
                    readScopeQueryDraft().scopeId ||
                    resolvedScope?.scopeId,
                }),
              )
            }
            style={{ borderRadius: 12, height: 40, paddingInline: 18 }}
            type="primary"
          >
            Create Team
          </Button>
        </Space>
      }
      title="My Teams"
    >
      <div style={{ display: "grid", gap: 20 }}>
        {(showScopePicker || !scopeId) && (
          <AevatarPanel
            extra={
              showScopePicker && scopeId ? (
                <Button
                  onClick={() => {
                    setDraft(normalizeScopeDraft(activeDraft));
                    setShowScopePicker(false);
                  }}
                >
                  Cancel
                </Button>
              ) : null
            }
            layoutMode="document"
            padding={20}
            title="Scope Context"
          >
            <ScopeQueryCard
              activeScopeId={scopeId}
              draft={draft}
              loadLabel="Load team scope"
              onChange={setDraft}
              onLoad={() => {
                const nextDraft = normalizeScopeDraft(draft);
                setDraft(nextDraft);
                setActiveDraft(nextDraft);
                setShowScopePicker(false);
              }}
              onReset={() => {
                const nextDraft = normalizeScopeDraft({
                  scopeId: resolvedScope?.scopeId ?? "",
                });
                setDraft(nextDraft);
                setActiveDraft(nextDraft);
              }}
              onUseResolvedScope={() => {
                if (!resolvedScope?.scopeId) {
                  return;
                }

                const nextDraft = normalizeScopeDraft({
                  scopeId: resolvedScope.scopeId,
                });
                setDraft(nextDraft);
                setActiveDraft(nextDraft);
                setShowScopePicker(false);
              }}
              resolvedScopeId={resolvedScope?.scopeId ?? null}
              resolvedScopeSource={resolvedScope?.scopeSource ?? null}
            />
          </AevatarPanel>
        )}

        {queryIssues.map((issue) => (
          <Alert key={issue} message={issue} showIcon type="warning" />
        ))}

        <div style={metricGridStyle}>
          <ConsoleMetricCard label="Teams" value={String(teams.length)} />
          <ConsoleMetricCard
            label="Active Teams"
            tone="green"
            value={String(activeTeamCount)}
          />
          <ConsoleMetricCard
            label="Assigned Members"
            tone="purple"
            value={String(membersAssignedCount)}
          />
          <ConsoleMetricCard
            label="Unassigned Members"
            value={String(unassignedMembers.length)}
          />
        </div>

        {!scopeId ? (
          <AevatarPanel layoutMode="document" padding={24} title="Team Roster">
            <Empty
              description="Load a scope first so we can fetch teams from the backend."
              image={Empty.PRESENTED_IMAGE_SIMPLE}
            />
          </AevatarPanel>
        ) : teamsQuery.isLoading ? (
          <AevatarPanel layoutMode="document" padding={24} title="Team Roster">
            <Typography.Text>Loading teams...</Typography.Text>
          </AevatarPanel>
        ) : teams.length === 0 ? (
          <AevatarPanel layoutMode="document" padding={24} title="Team Roster">
            <Empty
              description="This scope does not have any teams yet."
              image={Empty.PRESENTED_IMAGE_SIMPLE}
            >
              <Button
                type="primary"
                onClick={() =>
                  history.push(
                    buildTeamCreateHref({
                      scopeId,
                    }),
                  )
                }
              >
                Create the first team
              </Button>
            </Empty>
          </AevatarPanel>
        ) : (
          <div style={cardGridStyle}>
            {teams.map((team) => {
              const teamMembers = membersByTeamId.get(team.teamId) ?? [];
              return (
                <div key={team.teamId} style={teamCardStyle}>
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
                        {team.displayName}
                      </Typography.Title>
                      <Typography.Paragraph
                        ellipsis={{ rows: 2 }}
                        style={{ color: "#8c8c8c", margin: "8px 0 0" }}
                      >
                        {team.description || "No team description yet."}
                      </Typography.Paragraph>
                    </div>
                    <Tag color={team.lifecycleStage === "archived" ? "default" : "blue"}>
                      {formatStudioTeamLifecycleStage(team.lifecycleStage)}
                    </Tag>
                  </div>

                  <div
                    style={{
                      display: "grid",
                      gap: 12,
                      gridTemplateColumns: "repeat(2, minmax(0, 1fr))",
                    }}
                  >
                    <div>
                      <div style={detailLabelStyle}>Team ID</div>
                      <div style={detailValueStyle}>
                        <AevatarCompactText copyable monospace value={team.teamId} />
                      </div>
                    </div>
                    <div>
                      <div style={detailLabelStyle}>Members</div>
                      <div style={detailValueStyle}>{String(team.memberCount)}</div>
                    </div>
                    <div>
                      <div style={detailLabelStyle}>Last Updated</div>
                      <div style={detailValueStyle}>
                        {formatCompactDateTime(team.updatedAt, "--")}
                      </div>
                    </div>
                    <div>
                      <div style={detailLabelStyle}>Member Preview</div>
                      <div style={detailValueStyle}>
                        {formatMemberPreview(teamMembers)}
                      </div>
                    </div>
                  </div>

                  <Space wrap>
                    <Button
                      type="primary"
                      onClick={() =>
                        history.push(
                          buildTeamDetailHref({
                            scopeId: team.scopeId,
                            teamId: team.teamId,
                          }),
                        )
                      }
                    >
                      Manage Team
                    </Button>
                    <Button
                      onClick={() =>
                        history.push(
                          buildTeamDetailHref({
                            scopeId: team.scopeId,
                            teamId: team.teamId,
                            tab: "members",
                          }),
                        )
                      }
                    >
                      View Members
                    </Button>
                  </Space>
                </div>
              );
            })}
          </div>
        )}
      </div>
    </ConsoleMenuPageShell>
  );
};

export default TeamsHomePage;
