import {
  InfoCircleOutlined,
  TeamOutlined,
  WarningOutlined,
} from '@ant-design/icons';
import { Button, Input, Space, Typography, message, theme } from 'antd';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import React from 'react';
import { loadRestorableAuthSession } from '@/shared/auth/session';
import { useTranslation } from '@/shared/i18n/localization';
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

type TeamCreateNoticeTone = 'info' | 'warning';

type TeamCreateNotice = {
  readonly description?: string;
  readonly key: string;
  readonly title: string;
  readonly tone: TeamCreateNoticeTone;
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

const TeamCreateNoticeRail: React.FC<{
  readonly notices: readonly TeamCreateNotice[];
}> = ({ notices }) => {
  const { token } = theme.useToken();
  const { t } = useTranslation();

  if (notices.length === 0) {
    return null;
  }

  const toneStyle: Record<TeamCreateNoticeTone, React.CSSProperties> = {
    info: {
      background: 'rgba(24, 144, 255, 0.06)',
      borderColor: 'rgba(24, 144, 255, 0.18)',
      color: token.colorInfo,
    },
    warning: {
      background: 'rgba(250, 173, 20, 0.08)',
      borderColor: 'rgba(250, 173, 20, 0.24)',
      color: token.colorWarning,
    },
  };

  return (
    <section
      aria-label={t('team.create.status.aria')}
      role="status"
      style={{
        display: 'flex',
        flexDirection: 'column',
        gap: 8,
        marginBottom: 20,
      }}
    >
      {notices.map((notice) => {
        const isWarning = notice.tone === 'warning';
        return (
          <div
            key={notice.key}
            style={{
              ...toneStyle[notice.tone],
              alignItems: 'flex-start',
              border: '1px solid',
              borderRadius: 8,
              display: 'grid',
              gap: 10,
              gridTemplateColumns: '16px minmax(0, 1fr)',
              padding: '10px 12px',
            }}
          >
            {isWarning ? <WarningOutlined /> : <InfoCircleOutlined />}
            <div
              style={{
                display: 'flex',
                flexDirection: 'column',
                gap: 2,
                minWidth: 0,
              }}
            >
              <Typography.Text
                style={{
                  color: token.colorText,
                  fontSize: 13,
                  fontWeight: 600,
                  lineHeight: 1.4,
                }}
              >
                {notice.title}
              </Typography.Text>
              {notice.description ? (
                <Typography.Text
                  style={{
                    color: token.colorTextSecondary,
                    fontSize: 13,
                    lineHeight: 1.45,
                  }}
                >
                  {notice.description}
                </Typography.Text>
              ) : null}
            </div>
          </div>
        );
      })}
    </section>
  );
};

const TeamCreatePage: React.FC = () => {
  const queryClient = useQueryClient();
  const { t } = useTranslation();
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
  const serverResolvedScope = React.useMemo(
    () => resolveStudioScopeContext(authSessionQuery.data),
    [authSessionQuery.data],
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
  const createScopeId =
    serverResolvedScope?.scopeId?.trim() === scopeId ? scopeId : '';
  const authUnavailable = Boolean(
    authSessionIssue ||
      (authSessionQuery.isSuccess && !authSessionQuery.data?.authenticated),
  );
  const createNotices = React.useMemo(() => {
    const notices: TeamCreateNotice[] = [];

    if (authSessionIssue) {
      notices.push({
        description: authSessionIssue,
        key: 'auth-session-error',
        title: t('team.create.authFailed'),
        tone: 'warning',
      });
    } else if (authUnavailable) {
      notices.push({
        description: t('team.create.authUnavailableDescription'),
        key: 'auth-session-unavailable',
        title: t('team.create.authUnavailable'),
        tone: 'warning',
      });
    }

    if (!scopeId) {
      notices.push({
        key: 'missing-scope',
        title: t('team.create.missingScope'),
        tone: 'info',
      });
    }

    return notices;
  }, [authSessionIssue, authUnavailable, scopeId, t]);
  const canCreateTeam = Boolean(createScopeId && teamName.trim());
  const canEditTeamDraft = Boolean(createScopeId) && !authUnavailable && !isCreatingTeam;
  const createTeamButtonStyle = canCreateTeam
    ? primaryActionButtonStyle
    : undefined;
  const handleCreateTeam = async () => {
    if (!canCreateTeam || isCreatingTeam) {
      return;
    }

    setIsCreatingTeam(true);
    try {
      const team = await studioApi.createTeam({
        scopeId: createScopeId,
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
      void message.success(t('team.create.success'));
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
          : t('team.create.failed');
      void message.error(errorMessage);
    } finally {
      setIsCreatingTeam(false);
    }
  };
  return (
    <ConsoleMenuPageShell
      breadcrumb={t('team.create.breadcrumb')}
      extra={
        <Space wrap>
          <Button
            disabled={!canCreateTeam}
            loading={isCreatingTeam}
            onClick={() => void handleCreateTeam()}
            style={createTeamButtonStyle}
          >
            {t('team.create.action')}
          </Button>
        </Space>
      }
      title={t('team.create.title')}
    >
      <TeamCreateNoticeRail notices={createNotices} />

      <AevatarPanel
        layoutMode="document"
        padding={20}
        title={t('team.create.panelTitle')}
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
            <Typography.Text strong>{t('team.create.name')}</Typography.Text>
            <Input
              aria-label={t('team.create.nameAria')}
              disabled={!canEditTeamDraft}
              placeholder={t('team.create.namePlaceholder')}
              value={teamName}
              onChange={(event) => setTeamName(event.target.value)}
            />
          </div>
          <div style={{ display: 'grid', gap: 8 }}>
            <Typography.Text strong>{t('team.create.description')}</Typography.Text>
            <Input
              aria-label={t('team.create.descriptionAria')}
              disabled={!canEditTeamDraft}
              placeholder={t('team.create.descriptionPlaceholder')}
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
              style={createTeamButtonStyle}
            >
              {t('team.create.action')}
            </Button>
            <Button
              disabled={isCreatingTeam}
              onClick={() =>
                history.push(
                  scopeId ? buildScopeHref('/teams', { scopeId }) : buildTeamsHref(),
                )
              }
            >
              {t('team.create.back')}
            </Button>
          </Space>
          {authUnavailable ? (
            <Typography.Text type="secondary">
              {t('team.create.authRequiredHint')}
            </Typography.Text>
          ) : null}
        </div>
      </AevatarPanel>
    </ConsoleMenuPageShell>
  );
};

export default TeamCreatePage;
