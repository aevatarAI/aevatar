import { TeamOutlined } from '@ant-design/icons';
import { Alert, Button, Input, Space, Typography, message } from 'antd';
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

function trimOptional(value: string | null | undefined): string {
  return value?.trim() ?? '';
}

function readCreateTeamDraftFromLocation(): {
  readonly teamName: string;
} {
  if (typeof window === 'undefined') {
    return {
      teamName: '',
    };
  }

  const params = new URLSearchParams(window.location.search);
  return {
    teamName: params.get('teamName')?.trim() ?? '',
  };
}

const TeamCreatePage: React.FC = () => {
  const queryClient = useQueryClient();
  const initialDraft = React.useMemo(readCreateTeamDraftFromLocation, []);
  const [routeScopeId, setRouteScopeId] = React.useState(
    () => readScopeQueryDraft().scopeId.trim(),
  );
  const [teamName, setTeamName] = React.useState(initialDraft.teamName);
  const [teamDescription, setTeamDescription] = React.useState('');
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
  const routeParams = React.useMemo(
    () => ({
      teamName: teamName.trim() || undefined,
    }),
    [teamName],
  );
  React.useEffect(() => {
    const nextPath = buildScopeHref(
      '/teams/new',
      { scopeId },
      routeParams,
    );
    const currentPath =
      typeof window === 'undefined'
        ? ''
        : `${window.location.pathname}${window.location.search}`;
    if (nextPath !== currentPath) {
      history.replace(nextPath);
    }
  }, [routeParams, scopeId]);
  const authSessionIssue = React.useMemo(() => {
    if (!authSessionQuery.isError) {
      return '';
    }

    return describeError(
      authSessionQuery.error,
      '登录状态暂时不可用，请刷新后重试。',
    );
  }, [authSessionQuery.error, authSessionQuery.isError]);
  const canCreateTeam = Boolean(scopeId && teamName.trim());
  const handleCreateTeam = async () => {
    if (!canCreateTeam || isCreatingTeam) {
      return;
    }

    setIsCreatingTeam(true);
    try {
      const team = await studioApi.createTeam({
        scopeId,
        displayName: teamName.trim(),
        description: teamDescription.trim() || undefined,
      });
      queryClient.setQueryData(
        ['teams', 'team-summary', team.scopeId, team.teamId],
        team,
      );
      rememberPendingTeamRosterSummary(team);
      await queryClient.invalidateQueries({
        queryKey: ['teams', 'roster', team.scopeId],
      });
      void message.success('已创建团队。');
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
          : '创建团队失败。';
      void message.error(errorMessage);
    } finally {
      setIsCreatingTeam(false);
    }
  };
  return (
    <ConsoleMenuPageShell
      breadcrumb="Aevatar / 团队"
      extra={
        <Space wrap>
          <Button
            disabled={!canCreateTeam}
            loading={isCreatingTeam}
            onClick={() => void handleCreateTeam()}
            style={primaryActionButtonStyle}
          >
            创建团队
          </Button>
        </Space>
      }
      title="创建团队"
    >
      {authSessionIssue ? (
        <Alert
          description={
            resolvedScope?.scopeId
              ? `${authSessionIssue} 已使用本地登录信息继续创建团队。`
              : authSessionIssue
          }
          showIcon
          style={{ marginBottom: 20 }}
          title={
            resolvedScope?.scopeId
              ? '当前登录态校验失败，已使用本地登录信息'
              : '当前登录态校验失败'
          }
          type="warning"
        />
      ) : null}

      {!scopeId ? (
        <Alert
          showIcon
          style={{ marginBottom: 20 }}
          title="当前登录状态还没有解析出可用的团队范围，请刷新后重试。"
          type="info"
        />
      ) : null}

      <AevatarPanel
        layoutMode="document"
        padding={20}
        title="团队信息"
      >
        <div
          style={{
            display: 'flex',
            flexDirection: 'column',
            gap: 18,
            maxWidth: 760,
          }}
        >
          <div style={{ display: 'grid', gap: 8 }}>
            <Typography.Text strong>团队名称</Typography.Text>
            <Input
              aria-label="团队名称"
              placeholder="例如：订单助手团队"
              value={teamName}
              onChange={(event) => setTeamName(event.target.value)}
            />
          </div>
          <div style={{ display: 'grid', gap: 8 }}>
            <Typography.Text strong>团队描述</Typography.Text>
            <Input
              aria-label="团队描述"
              placeholder="这个团队负责什么"
              value={teamDescription}
              onChange={(event) => setTeamDescription(event.target.value)}
            />
          </div>
          <Space wrap size={[8, 8]}>
            <Button
              icon={<TeamOutlined />}
              disabled={!canCreateTeam}
              loading={isCreatingTeam}
              onClick={() => void handleCreateTeam()}
              style={primaryActionButtonStyle}
            >
              创建团队
            </Button>
            <Button onClick={() => history.push(buildTeamsHref())}>
              返回我的团队
            </Button>
          </Space>
        </div>
      </AevatarPanel>
    </ConsoleMenuPageShell>
  );
};

export default TeamCreatePage;
