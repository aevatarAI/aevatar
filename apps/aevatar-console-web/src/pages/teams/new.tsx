import { BuildOutlined, RocketOutlined, TeamOutlined } from '@ant-design/icons';
import { Button, Input, Space, Typography, message } from 'antd';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import React from 'react';
import { loadRestorableAuthSession } from '@/shared/auth/session';
import { history } from '@/shared/navigation/history';
import { buildTeamDetailHref, buildTeamsHref } from '@/shared/navigation/teamRoutes';
import { studioApi } from '@/shared/studio/api';
import { buildStudioRoute } from '@/shared/studio/navigation';
import { AevatarPanel } from '@/shared/ui/aevatarPageShells';
import ConsoleMetricCard from '@/shared/ui/ConsoleMetricCard';
import ConsoleMenuPageShell from '@/shared/ui/ConsoleMenuPageShell';
import ScopeQueryCard from '../scopes/components/ScopeQueryCard';
import { resolveStudioScopeContext } from '../scopes/components/resolvedScope';
import {
  buildScopeHref,
  normalizeScopeDraft,
  readScopeQueryDraft,
  type ScopeQueryDraft,
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

const secondaryActionButtonStyle: React.CSSProperties = {
  borderRadius: 10,
  fontSize: 14,
  fontWeight: 500,
  height: 44,
  paddingInline: 18,
};

const stageChipStyle: React.CSSProperties = {
  alignItems: 'center',
  background: '#f6f0ff',
  borderRadius: 20,
  color: '#6c5ce7',
  display: 'inline-flex',
  fontSize: 12,
  fontWeight: 500,
  padding: '6px 12px',
};

function trimOptional(value: string | null | undefined): string {
  return value?.trim() ?? '';
}

function readCreateTeamDraftFromLocation(): {
  readonly teamName: string;
  readonly entryName: string;
  readonly teamDraftWorkflowId: string;
  readonly teamDraftWorkflowName: string;
} {
  if (typeof window === 'undefined') {
    return {
      teamName: '',
      entryName: '',
      teamDraftWorkflowId: '',
      teamDraftWorkflowName: '',
    };
  }

  const params = new URLSearchParams(window.location.search);
  return {
    teamName: params.get('teamName')?.trim() ?? '',
    entryName: params.get('entryName')?.trim() ?? '',
    teamDraftWorkflowId: params.get('teamDraftWorkflowId')?.trim() ?? '',
    teamDraftWorkflowName: params.get('teamDraftWorkflowName')?.trim() ?? '',
  };
}

const TeamCreatePage: React.FC = () => {
  const queryClient = useQueryClient();
  const initialDraft = React.useMemo(readCreateTeamDraftFromLocation, []);
  const [draft, setDraft] = React.useState<ScopeQueryDraft>(() =>
    readScopeQueryDraft(),
  );
  const [activeDraft, setActiveDraft] = React.useState<ScopeQueryDraft>(() =>
    readScopeQueryDraft(),
  );
  const [teamName, setTeamName] = React.useState(initialDraft.teamName);
  const [teamDescription, setTeamDescription] = React.useState('');
  const [entryName] = React.useState(initialDraft.entryName);
  const [teamDraftWorkflowId, setTeamDraftWorkflowId] = React.useState(
    initialDraft.teamDraftWorkflowId,
  );
  const [teamDraftWorkflowName, setTeamDraftWorkflowName] = React.useState(
    initialDraft.teamDraftWorkflowName,
  );
  const [isCreatingTeam, setIsCreatingTeam] = React.useState(false);
  const [isDeletingDraft, setIsDeletingDraft] = React.useState(false);
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
    setDraft((currentDraft) =>
      currentDraft.scopeId.trim()
        ? currentDraft
        : { scopeId: resolvedScope.scopeId },
    );
    setActiveDraft((currentDraft) =>
      currentDraft.scopeId.trim()
        ? currentDraft
        : { scopeId: resolvedScope.scopeId },
    );
  }, [resolvedScope?.scopeId]);
  const scopeId = activeDraft.scopeId.trim();
  const legacyCreateParams = React.useMemo(
    () => ({
      entryName: initialDraft.entryName || undefined,
      teamDraftWorkflowId: teamDraftWorkflowId.trim() || undefined,
      teamDraftWorkflowName: teamDraftWorkflowName.trim() || undefined,
      teamName: teamName.trim() || undefined,
    }),
    [
      initialDraft.entryName,
      teamDraftWorkflowId,
      teamDraftWorkflowName,
      teamName,
    ],
  );
  React.useEffect(() => {
    const nextPath = buildScopeHref(
      '/teams/new',
      activeDraft,
      legacyCreateParams,
    );
    const currentPath =
      typeof window === 'undefined'
        ? ''
        : `${window.location.pathname}${window.location.search}`;
    if (nextPath !== currentPath) {
      history.replace(nextPath);
    }
  }, [activeDraft, legacyCreateParams]);
  const resolvedDraftWorkflowId = teamDraftWorkflowId.trim();
  const resolvedDraftWorkflowName =
    teamDraftWorkflowName.trim() || resolvedDraftWorkflowId;
  const hasSavedDraft = Boolean(resolvedDraftWorkflowId);
  const canCreateTeam = Boolean(scopeId && teamName.trim());
  const canOpenBuilder = Boolean(scopeId);
  const openBuilder = () =>
    history.push(
      buildStudioRoute({
        scopeId,
        focus: resolvedDraftWorkflowId
          ? `workflow:${resolvedDraftWorkflowId}`
          : undefined,
        tab: 'studio',
      }),
    );
  const openBehaviors = () =>
    history.push(
      buildStudioRoute({
        scopeId,
        tab: 'workflows',
      }),
    );
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
      await queryClient.invalidateQueries({
        queryKey: ['teams', 'roster', team.scopeId],
      });
      void message.success('已创建 Team。');
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
          : '创建 Team 失败。';
      void message.error(errorMessage);
    } finally {
      setIsCreatingTeam(false);
    }
  };
  const handleDeleteDraft = async () => {
    if (!resolvedDraftWorkflowId || isDeletingDraft) {
      return;
    }

    setIsDeletingDraft(true);
    try {
      await studioApi.deleteWorkflow(resolvedDraftWorkflowId);
      setTeamDraftWorkflowId('');
      setTeamDraftWorkflowName('');
      history.replace(
        buildScopeHref(
          '/teams/new',
          { scopeId },
          {
            teamName: teamName.trim() || undefined,
            entryName: entryName.trim() || undefined,
          },
        ),
      );
      void message.success('已删除当前团队草稿。');
    } catch (error) {
      const errorMessage =
        error instanceof Error && error.message.trim()
          ? error.message
          : '删除草稿失败。';
      void message.error(errorMessage);
    } finally {
      setIsDeletingDraft(false);
    }
  };

  return (
    <ConsoleMenuPageShell
      breadcrumb="Aevatar / Teams"
      extra={
        <Space wrap>
          <Button
            disabled={!canCreateTeam}
            loading={isCreatingTeam}
            onClick={() => void handleCreateTeam()}
            style={primaryActionButtonStyle}
          >
            Create Team
          </Button>
          <Button
            disabled={!canOpenBuilder}
            onClick={openBuilder}
            style={secondaryActionButtonStyle}
          >
            Continue in Studio
          </Button>
        </Space>
      }
      title="Create Team"
    >
      <div
        style={{
          display: 'grid',
          gap: 16,
          gridTemplateColumns: 'repeat(4, minmax(0, 1fr))',
          marginBottom: 20,
        }}
      >
        <ConsoleMetricCard label="数据源" tone="green" value="StudioTeam" />
        <ConsoleMetricCard label="工作空间" value={scopeId || '待选择'} />
        <ConsoleMetricCard label="创建后" value="Team detail" />
        <ConsoleMetricCard label="成员归属" value="后续分配" />
      </div>

      <AevatarPanel layoutMode="document" padding={20} title="工作空间上下文">
        <ScopeQueryCard
          activeScopeId={scopeId}
          draft={draft}
          loadLabel="使用这个工作空间"
          onChange={setDraft}
          onLoad={() => {
            const nextDraft = normalizeScopeDraft(draft);
            setDraft(nextDraft);
            setActiveDraft(nextDraft);
          }}
          onReset={() => {
            const nextDraft = normalizeScopeDraft({
              scopeId: resolvedScope?.scopeId ?? '',
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
          }}
          resetDisabled={
            normalizeScopeDraft(draft).scopeId ===
              (resolvedScope?.scopeId?.trim() ?? '') &&
            scopeId === (resolvedScope?.scopeId?.trim() ?? '')
          }
          resolvedScopeId={resolvedScope?.scopeId}
          resolvedScopeSource={resolvedScope?.scopeSource}
        />
      </AevatarPanel>

      <AevatarPanel
        layoutMode="document"
        padding={20}
        title="Team authority"
      >
        <div
          style={{
            alignItems: 'center',
            display: 'grid',
            gap: 20,
            gridTemplateColumns: 'minmax(0, 1fr) auto',
          }}
        >
          <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
            <Typography.Title
              level={3}
              style={{
                color: '#1d2129',
                fontSize: 28,
                fontWeight: 600,
                lineHeight: 1.2,
                margin: 0,
              }}
            >
              Create real Team roster entry
            </Typography.Title>
            <div
              style={{
                display: 'flex',
                flexWrap: 'wrap',
                gap: 8,
              }}
            >
              {['真实 Team API', '工作空间内归属', '成员后续分配', '运行态只作辅助'].map((item) => (
                <span key={item} style={stageChipStyle}>
                  {item}
                </span>
              ))}
            </div>
            <div
              style={{
                display: 'grid',
                gap: 12,
                gridTemplateColumns: 'repeat(auto-fit, minmax(240px, 1fr))',
                maxWidth: 720,
              }}
            >
              <div style={{ display: 'grid', gap: 8 }}>
                <Typography.Text strong>Team name</Typography.Text>
                <Input
                  aria-label="Team name"
                  placeholder="例如：订单助手团队"
                  value={teamName}
                  onChange={(event) => setTeamName(event.target.value)}
                />
              </div>
              <div style={{ display: 'grid', gap: 8 }}>
                <Typography.Text strong>Description</Typography.Text>
                <Input
                  aria-label="Team description"
                  placeholder="这个 Team 负责什么"
                  value={teamDescription}
                  onChange={(event) => setTeamDescription(event.target.value)}
                />
              </div>
              <Typography.Text
                type="secondary"
                style={{ gridColumn: '1 / -1', lineHeight: 1.6 }}
              >
                This page now creates a backend StudioTeam record. Members can be
                assigned later; the Teams homepage will use this roster entry as
                the primary team truth.
                {hasSavedDraft
                  ? ' The old saved draft below remains available for Studio recovery.'
                  : ''}
              </Typography.Text>
            </div>
            <Space wrap size={[8, 8]}>
              <Button
                icon={<TeamOutlined />}
                disabled={!canCreateTeam}
                loading={isCreatingTeam}
                onClick={() => void handleCreateTeam()}
                style={primaryActionButtonStyle}
              >
                Create Team
              </Button>
              <Button
                icon={<BuildOutlined />}
                disabled={!canOpenBuilder}
                onClick={openBuilder}
                style={secondaryActionButtonStyle}
              >
                Continue in Studio
              </Button>
              <Button
                icon={<RocketOutlined />}
                onClick={openBehaviors}
                style={secondaryActionButtonStyle}
              >
                View Behaviors
              </Button>
              <Button
                onClick={() => history.push(buildTeamsHref())}
                style={secondaryActionButtonStyle}
              >
                Back to My Teams
              </Button>
            </Space>
          </div>
          <div
            style={{
              alignItems: 'flex-end',
              display: 'flex',
              justifyContent: 'flex-end',
            }}
          >
            <Typography.Text
              style={{
                color: '#8c8c8c',
                fontSize: 12,
                fontWeight: 500,
              }}
            >
              {teamName.trim()
                ? `Team label: ${teamName.trim()}`
                : 'Create a Team before assigning members'}
            </Typography.Text>
          </div>
        </div>
      </AevatarPanel>

      {hasSavedDraft ? (
        <AevatarPanel
          layoutMode="document"
          padding={20}
          title="Saved Draft"
        >
          <div
            style={{
              display: 'grid',
              gap: 12,
            }}
          >
            <Typography.Text strong>已保存草稿</Typography.Text>
            <Typography.Text>{resolvedDraftWorkflowName}</Typography.Text>
            <Typography.Text type="secondary" style={{ lineHeight: 1.6 }}>
              This workflow draft is linked from an old Create Team flow. Continue
              in Studio to edit the initial member draft.
            </Typography.Text>
            <Space wrap size={[8, 8]}>
              <Button
                icon={<BuildOutlined />}
                disabled={isDeletingDraft}
                onClick={openBuilder}
                style={primaryActionButtonStyle}
              >
                Continue Draft
              </Button>
              <Button
                loading={isDeletingDraft}
                onClick={() => void handleDeleteDraft()}
                style={secondaryActionButtonStyle}
              >
                Delete Draft
              </Button>
            </Space>
            <Typography.Text type="secondary" style={{ lineHeight: 1.6 }}>
              Delete Draft removes the linked workflow draft. Legacy labels stay
              in the URL so old links remain understandable.
            </Typography.Text>
            {entryName.trim() ? (
              <Typography.Text type="secondary">
                Legacy initial member label: {entryName.trim()}
              </Typography.Text>
            ) : null}
          </div>
        </AevatarPanel>
      ) : null}
    </ConsoleMenuPageShell>
  );
};

export default TeamCreatePage;
