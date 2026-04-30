import { ArrowLeftOutlined } from "@ant-design/icons";
import { useQuery } from "@tanstack/react-query";
import { Alert, Button, Input, Space, Typography, message } from "antd";
import React from "react";
import { history } from "@/shared/navigation/history";
import {
  buildTeamCreateHref,
  buildTeamDetailHref,
} from "@/shared/navigation/teamRoutes";
import { studioApi } from "@/shared/studio/api";
import { AevatarPanel } from "@/shared/ui/aevatarPageShells";
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

function readLegacyTeamName(): string {
  if (typeof window === "undefined") {
    return "";
  }

  return trimOptional(new URLSearchParams(window.location.search).get("teamName"));
}

function hasLegacyDraftParams(): boolean {
  if (typeof window === "undefined") {
    return false;
  }

  const params = new URLSearchParams(window.location.search);
  return Boolean(
    trimOptional(params.get("entryName")) ||
      trimOptional(params.get("teamDraftWorkflowId")) ||
      trimOptional(params.get("teamDraftWorkflowName")),
  );
}

const TeamCreatePage: React.FC = () => {
  const [draft, setDraft] = React.useState<ScopeQueryDraft>(() => readScopeQueryDraft());
  const [activeDraft, setActiveDraft] = React.useState<ScopeQueryDraft>(() =>
    normalizeScopeDraft({ scopeId: "" }),
  );
  const [displayName, setDisplayName] = React.useState(() => readLegacyTeamName());
  const [description, setDescription] = React.useState("");
  const [isSubmitting, setIsSubmitting] = React.useState(false);
  const [showScopePicker, setShowScopePicker] = React.useState(false);
  const legacyDraftDetected = React.useMemo(hasLegacyDraftParams, []);

  const authSessionQuery = useQuery({
    queryKey: ["teams", "create", "auth-session"],
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
    history.replace(
      buildTeamCreateHref({
        scopeId: activeDraft.scopeId,
      }),
    );
  }, [activeDraft.scopeId]);

  const scopeId = activeDraft.scopeId.trim();
  const authIssue = authSessionQuery.isError
    ? describeError(authSessionQuery.error, "登录状态暂时不可用，请刷新后重试。")
    : "";
  const createDisabled = !scopeId || !displayName.trim() || isSubmitting;

  const handleCreate = async () => {
    if (createDisabled) {
      return;
    }

    setIsSubmitting(true);
    try {
      const created = await studioApi.createTeam({
        scopeId,
        displayName: displayName.trim(),
        description: trimOptional(description) || null,
      });
      void message.success("团队已创建。");
      history.push(
        buildTeamDetailHref({
          scopeId: created.scopeId,
          teamId: created.teamId,
        }),
      );
    } catch (error) {
      void message.error(
        describeError(error, "创建团队失败，请稍后再试。"),
      );
    } finally {
      setIsSubmitting(false);
    }
  };

  return (
    <ConsoleMenuPageShell
      breadcrumb="Aevatar / Teams"
      description="Create a real team record in the current scope first, then manage members and assignment from the team detail page."
      extra={
        <Space wrap>
          <Button
            icon={<ArrowLeftOutlined />}
            onClick={() =>
              history.push(
                buildScopeHref("/teams", {
                  scopeId,
                }),
              )
            }
            style={{ borderRadius: 12, height: 40, paddingInline: 18 }}
          >
            Back to My Teams
          </Button>
        </Space>
      }
      title="Create Team"
    >
      <div style={{ display: "grid", gap: 20 }}>
        {authIssue ? <Alert message={authIssue} showIcon type="warning" /> : null}
        {legacyDraftDetected ? (
          <Alert
            message="Legacy Create Team query params detected. This page now creates a real team record; initial member drafts should be continued in Studio separately."
            showIcon
            type="info"
          />
        ) : null}

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
              loadLabel="Use scope"
              onChange={setDraft}
              onLoad={() => {
                const nextDraft = normalizeScopeDraft(draft);
                setDraft(nextDraft);
                setActiveDraft(nextDraft);
                setShowScopePicker(false);
                history.replace(
                  buildTeamCreateHref({
                    scopeId: nextDraft.scopeId,
                  }),
                );
              }}
              onReset={() => {
                const nextDraft = normalizeScopeDraft({
                  scopeId: resolvedScope?.scopeId ?? "",
                });
                setDraft(nextDraft);
                setActiveDraft(nextDraft);
                history.replace(
                  buildTeamCreateHref({
                    scopeId: nextDraft.scopeId,
                  }),
                );
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
                history.replace(
                  buildTeamCreateHref({
                    scopeId: nextDraft.scopeId,
                  }),
                );
              }}
              resolvedScopeId={resolvedScope?.scopeId ?? null}
              resolvedScopeSource={resolvedScope?.scopeSource ?? null}
            />
          </AevatarPanel>
        )}

        <AevatarPanel
          extra={
            scopeId ? (
              <Button onClick={() => setShowScopePicker((current) => !current)}>
                {showScopePicker ? "Hide scope" : "Change scope"}
              </Button>
            ) : null
          }
          layoutMode="document"
          padding={24}
          title="Team Identity"
        >
          <div
            style={{
              display: "grid",
              gap: 16,
              gridTemplateColumns: "repeat(auto-fit, minmax(280px, 1fr))",
            }}
          >
            <div style={{ display: "grid", gap: 8 }}>
              <Typography.Text strong>Scope ID</Typography.Text>
              <Input disabled value={scopeId} placeholder="Load a scope first" />
            </div>
            <div style={{ display: "grid", gap: 8 }}>
              <Typography.Text strong>Display Name</Typography.Text>
              <Input
                aria-label="Display Name"
                placeholder="Orders Assistant Team"
                value={displayName}
                onChange={(event) => setDisplayName(event.target.value)}
              />
            </div>
            <div style={{ display: "grid", gap: 8, gridColumn: "1 / -1" }}>
              <Typography.Text strong>Description</Typography.Text>
              <Input.TextArea
                aria-label="Description"
                autoSize={{ minRows: 4, maxRows: 8 }}
                placeholder="Describe what this team owns, how members collaborate, or which business workflow it supports."
                value={description}
                onChange={(event) => setDescription(event.target.value)}
              />
            </div>
          </div>
          <Space style={{ marginTop: 16 }}>
            <Button
              disabled={createDisabled}
              loading={isSubmitting}
              onClick={handleCreate}
              type="primary"
            >
              Create Team
            </Button>
          </Space>
          <Typography.Paragraph
            style={{ color: "#8c8c8c", margin: "16px 0 0" }}
          >
            The backend only needs a scope, display name, and optional
            description. Member creation and member assignment happen after the
            team record exists.
          </Typography.Paragraph>
        </AevatarPanel>
      </div>
    </ConsoleMenuPageShell>
  );
};

export default TeamCreatePage;
