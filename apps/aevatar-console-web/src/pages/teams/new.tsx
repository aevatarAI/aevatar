import { TeamOutlined } from '@ant-design/icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Alert, Button, Input, Space, Typography } from 'antd';
import React from 'react';
import { loadRestorableAuthSession } from '@/shared/auth/session';
import { t } from '@/shared/i18n/messages';
import { history } from '@/shared/navigation/history';
import {
  buildTeamCreateHref,
  buildTeamDetailHref,
  buildTeamsHref,
} from '@/shared/navigation/teamRoutes';
import { studioApi } from '@/shared/studio/api';
import {
  type AevatarBreadcrumbItem,
  AevatarPanel,
} from '@/shared/ui/aevatarPageShells';
import ConsoleMenuPageShell from '@/shared/ui/ConsoleMenuPageShell';
import { useConsoleToast } from '@/shared/ui/ConsoleToast';
import { describeError } from '@/shared/ui/errorText';
import { resolveStudioScopeContext } from '../scopes/components/resolvedScope';
import { readScopeQueryDraft } from '../scopes/components/scopeQuery';
import { rememberPendingTeamRosterSummary } from './pendingTeamRoster';

const primaryActionButtonStyle: React.CSSProperties = {
  borderRadius: 10,
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
  const toast = useConsoleToast();
  const initialDraft = React.useMemo(readCreateTeamDraftFromLocation, []);
  const [routeScopeId, setRouteScopeId] = React.useState(() =>
    readScopeQueryDraft().scopeId.trim(),
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
    () =>
      resolveStudioScopeContext(authSessionQuery.data) ?? locallyResolvedScope,
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
    const nextPath = buildTeamCreateHref({
      scopeId,
      teamName: routeParams.teamName,
    });
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
      t(
        'pages.teams.new.the.login.status.is',
        'The login status is temporarily unavailable, please refresh and try again.',
      ),
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
      toast.success(t('pages.teams.new.team.created', 'team created.'));
      history.push(
        buildTeamDetailHref({
          scopeId: team.scopeId,
          teamId: team.teamId,
        }),
      );
    } catch {
      toast.error(
        t('pages.teams.new.failed.to.create.team', 'Failed to create team.'),
      );
    } finally {
      setIsCreatingTeam(false);
    }
  };
  const teamsHref = scopeId
    ? buildTeamDetailHref({ scopeId })
    : buildTeamsHref();
  const breadcrumbItems: AevatarBreadcrumbItem[] = [
    {
      href: teamsHref,
      onClick: (event) => {
        event.preventDefault();
        history.push(teamsHref);
      },
      title: t('pages.teams.new.teamsBreadcrumb', 'Teams'),
    },
    {
      current: true,
      title: t('pages.teams.new.create.team.2', 'Create Team'),
    },
  ];

  return (
    <ConsoleMenuPageShell
      breadcrumbItems={breadcrumbItems}
      extra={
        <Space wrap>
          <Button
            disabled={!canCreateTeam}
            loading={isCreatingTeam}
            onClick={() => void handleCreateTeam()}
            style={primaryActionButtonStyle}
            type="primary"
          >
            {t('pages.teams.new.create.team', 'Create Team')}
          </Button>
        </Space>
      }
      title={t('pages.teams.new.create.team.2', 'Create Team')}
    >
      {authSessionIssue ? (
        <Alert
          description={
            resolvedScope?.scopeId
              ? t(
                  'pages.teams.new.has.continued.creating.team',
                  '{value1} has continued creating team using local login information.',
                  { value1: authSessionIssue },
                )
              : authSessionIssue
          }
          showIcon
          style={{ marginBottom: 20 }}
          title={
            resolvedScope?.scopeId
              ? t(
                  'pages.teams.new.the.current.login.status',
                  'The current login status verification failed, local login information has been used',
                )
              : t(
                  'pages.teams.new.current.login.status.verification',
                  'Current login status verification failed',
                )
          }
          type="warning"
        />
      ) : null}

      {!scopeId ? (
        <Alert
          showIcon
          style={{ marginBottom: 20 }}
          title={t(
            'pages.teams.new.the.current.login.status.2',
            'The current login status has not resolved the available team scope, please refresh and try again.',
          )}
          type="info"
        />
      ) : null}

      <AevatarPanel
        layoutMode="document"
        padding={20}
        title={t('pages.teams.new.team.information', 'team information')}
      >
        <div
          style={{
            display: 'flex',
            flexDirection: 'column',
            gap: 18,
            maxWidth: 760,
            minWidth: 0,
            width: '100%',
          }}
        >
          <div style={{ display: 'grid', gap: 8 }}>
            <Typography.Text strong>
              {t('pages.teams.new.team.name', 'Team name')}
            </Typography.Text>
            <Input
              aria-label={t('pages.teams.new.team.name.2', 'Team name')}
              placeholder={t(
                'pages.teams.new.for.example.order.assistant',
                'For example: Order Assistant team',
              )}
              value={teamName}
              onChange={(event) => setTeamName(event.target.value)}
            />
          </div>
          <div style={{ display: 'grid', gap: 8 }}>
            <Typography.Text strong>
              {t('pages.teams.new.description', 'Description')}
            </Typography.Text>
            <Input
              aria-label={t(
                'pages.teams.new.team.description',
                'Team description',
              )}
              placeholder={t(
                'pages.teams.new.what.is.this.team',
                'What is this team responsible for?',
              )}
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
              type="primary"
            >
              {t('pages.teams.new.create.team.3', 'Create Team')}
            </Button>
            <Button onClick={() => history.push(teamsHref)}>
              {t('pages.teams.new.back.to.my.teams', 'Back to My Teams')}
            </Button>
          </Space>
        </div>
      </AevatarPanel>
    </ConsoleMenuPageShell>
  );
};

export default TeamCreatePage;
