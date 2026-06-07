import { TeamOutlined } from '@ant-design/icons';
import { Alert, Button, Form, Input, Space, message } from 'antd';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import React from 'react';
import { loadRestorableAuthSession } from '@/shared/auth/session';
import { history } from '@/shared/navigation/history';
import { buildTeamDetailHref, buildTeamsHref } from '@/shared/navigation/teamRoutes';
import { studioApi } from '@/shared/studio/api';
import { describeError } from '@/shared/ui/errorText';
import { AevatarPanel } from '@/shared/ui/aevatarPageShells';
import ConsoleMenuPageShell from '@/shared/ui/ConsoleMenuPageShell';
import { rememberPendingTeamRosterSummary } from './pendingTeamRoster';
import { resolveStudioScopeContext } from '../scopes/components/resolvedScope';
import {
  buildScopeHref,
  readScopeQueryDraft,
} from '../scopes/components/scopeQuery';
import { t } from "@/shared/i18n/messages";

const primaryActionButtonStyle: React.CSSProperties = {
  background: '#6c5ce7',
  borderColor: '#6c5ce7',
  borderRadius: 10,
  color: '#ffffff',
  fontSize: 14,
  fontWeight: 600,
  height: 44,
  paddingInline: 18,
};

type CreateTeamFormValues = {
  readonly description?: string;
  readonly teamName?: string;
};

function trimOptional(value: string | null | undefined): string {
  return value?.trim() ?? '';
}

const TeamCreatePage: React.FC = () => {
  const queryClient = useQueryClient();
  const [form] = Form.useForm<CreateTeamFormValues>();
  const [routeScopeId, setRouteScopeId] = React.useState(
    () => readScopeQueryDraft().scopeId.trim(),
  );
  const [isCreatingTeam, setIsCreatingTeam] = React.useState(false);
  const hasInitializedScopeFromResolvedSession = React.useRef(false);
  const authSessionQuery = useQuery({
    queryKey: ['scopes', 'auth-session'],
    queryFn: () => studioApi.getAuthSession(),
    retry: false,
  });
  const localScopeId = trimOptional(loadRestorableAuthSession()?.user.sub);
  const locallyResolvedScope = React.useMemo(() => {
    if (!localScopeId) {
      return null;
    }

    return {
      scopeId: localScopeId,
      scopeSource: 'local-session',
    };
  }, [localScopeId]);
  const resolvedScope = React.useMemo(
    () => resolveStudioScopeContext(authSessionQuery.data) ?? locallyResolvedScope,
    [authSessionQuery.data, locallyResolvedScope],
  );
  React.useEffect(() => {
    if (!resolvedScope?.scopeId) {
      return;
    }
    if (hasInitializedScopeFromResolvedSession.current) {
      return;
    }

    hasInitializedScopeFromResolvedSession.current = true;
    setRouteScopeId((currentScopeId) =>
      currentScopeId.trim() ? currentScopeId : resolvedScope.scopeId,
    );
  }, [resolvedScope?.scopeId]);
  const scopeId = routeScopeId || resolvedScope?.scopeId?.trim() || '';
  React.useEffect(() => {
    const nextPath = buildScopeHref('/teams/new', { scopeId });
    const currentPath =
      typeof window === 'undefined'
        ? ''
        : `${window.location.pathname}${window.location.search}`;
    if (nextPath !== currentPath) {
      history.replace(nextPath);
    }
  }, [scopeId]);
  const authSessionIssue = React.useMemo(() => {
    if (!authSessionQuery.isError) {
      return '';
    }

    return describeError(
      authSessionQuery.error,
      t("pages.teams.new.the.login.status.is", "The login status is temporarily unavailable, please refresh and try again."),
    );
  }, [authSessionQuery.error, authSessionQuery.isError]);
  const handleCreateTeam = async (values: CreateTeamFormValues) => {
    const displayName = trimOptional(values.teamName);
    if (!scopeId || !displayName || isCreatingTeam) {
      return;
    }

    setIsCreatingTeam(true);
    try {
      const team = await studioApi.createTeam({
        scopeId,
        displayName,
        description: trimOptional(values.description) || undefined,
      });
      queryClient.setQueryData(
        ['teams', 'team-summary', team.scopeId, team.teamId],
        team,
      );
      rememberPendingTeamRosterSummary(team);
      await queryClient.invalidateQueries({
        queryKey: ['teams', 'roster', team.scopeId],
      });
      void message.success(t("pages.teams.new.team.created", "team created."));
      history.push(
        buildTeamDetailHref({
          scopeId: team.scopeId,
          teamId: team.teamId,
        }),
      );
    } catch (error) {
      const errorMessage =
        error instanceof Error && error.message.trim()
          ? error.message
          : t("pages.teams.new.failed.to.create.team", "Failed to create team.");
      void message.error(errorMessage);
    } finally {
      setIsCreatingTeam(false);
    }
  };
  return (
    <ConsoleMenuPageShell
      breadcrumb={t("pages.teams.new.aevatar.teams", "Aevatar / Teams")}
      title={t("pages.teams.new.create.team.2", "Create Team")}
    >
      {authSessionIssue ? (
        <Alert
          description={
            resolvedScope?.scopeId
              ? t("pages.teams.new.has.continued.creating.team", "{value1} has continued creating team using local login information.", { value1: authSessionIssue })
              : authSessionIssue
          }
          showIcon
          style={{ marginBottom: 20 }}
          title={
            resolvedScope?.scopeId
              ? t("pages.teams.new.the.current.login.status", "The current login status verification failed, local login information has been used")
              : t("pages.teams.new.current.login.status.verification", "Current login status verification failed")
          }
          type="warning"
        />
      ) : null}

      {!scopeId ? (
        <Alert
          showIcon
          style={{ marginBottom: 20 }}
          title={t("pages.teams.new.the.current.login.status.2", "The current login status has not resolved the available team scope, please refresh and try again.")}
          type="info"
        />
      ) : null}

      <AevatarPanel
        layoutMode="document"
        padding={20}
        title={t("pages.teams.new.team.information", "team information")}
      >
        <Form<CreateTeamFormValues>
          form={form}
          layout="vertical"
          onFinish={(values) => void handleCreateTeam(values)}
          style={{
            display: 'flex',
            flexDirection: 'column',
            gap: 18,
            maxWidth: 760,
          }}
        >
          <Form.Item
            label={t("pages.teams.new.team.name", "Team name")}
            name="teamName"
            rules={[
              {
                required: true,
                transform: (value) => trimOptional(value),
                message: t("pages.teams.new.team.name.required", "Please enter a team name."),
              },
            ]}
          >
            <Input
              aria-label={t("pages.teams.new.team.name.2", "Team name")}
              placeholder={t("pages.teams.new.for.example.order.assistant", "For example: Order Assistant team")}
            />
          </Form.Item>
          <Form.Item
            label={t("pages.teams.new.description", "Description")}
            name="description"
          >
            <Input
              aria-label={t("pages.teams.new.team.description", "Team description")}
              placeholder={t("pages.teams.new.what.is.this.team", "What is this team responsible for?")}
            />
          </Form.Item>
          <Space wrap size={[8, 8]}>
            <Button
              icon={<TeamOutlined />}
              disabled={!scopeId}
              htmlType="submit"
              loading={isCreatingTeam}
              style={primaryActionButtonStyle}
              type="primary"
            >
              {t("pages.teams.new.create.team.3", "Create Team")}</Button>
            <Button onClick={() => history.push(buildTeamsHref())}>
              {t("pages.teams.new.back.to.my.teams", "Back to My Teams")}</Button>
          </Space>
        </Form>
      </AevatarPanel>
    </ConsoleMenuPageShell>
  );
};

export default TeamCreatePage;
