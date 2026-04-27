import { BuildOutlined, RocketOutlined } from '@ant-design/icons';
import { Button, Input, Space, Typography, message } from 'antd';
import React from 'react';
import { history } from '@/shared/navigation/history';
import { buildTeamCreateHref, buildTeamsHref } from '@/shared/navigation/teamRoutes';
import { studioApi } from '@/shared/studio/api';
import { buildStudioRoute } from '@/shared/studio/navigation';
import { AevatarPanel } from '@/shared/ui/aevatarPageShells';
import ConsoleMetricCard from '@/shared/ui/ConsoleMetricCard';
import ConsoleMenuPageShell from '@/shared/ui/ConsoleMenuPageShell';

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
  const initialDraft = React.useMemo(readCreateTeamDraftFromLocation, []);
  const [teamName, setTeamName] = React.useState(initialDraft.teamName);
  const [entryName, setEntryName] = React.useState(initialDraft.entryName);
  const [teamDraftWorkflowId, setTeamDraftWorkflowId] = React.useState(
    initialDraft.teamDraftWorkflowId,
  );
  const [teamDraftWorkflowName, setTeamDraftWorkflowName] = React.useState(
    initialDraft.teamDraftWorkflowName,
  );
  const [isDeletingDraft, setIsDeletingDraft] = React.useState(false);
  const resolvedEntryName = entryName.trim() || teamName.trim();
  const resolvedDraftWorkflowId = teamDraftWorkflowId.trim();
  const resolvedDraftWorkflowName =
    teamDraftWorkflowName.trim() || resolvedDraftWorkflowId;
  const hasSavedDraft = Boolean(resolvedDraftWorkflowId);
  const canOpenBuilder = Boolean(teamName.trim());
  const openBuilder = () =>
    history.push(
      buildStudioRoute({
        teamMode: 'create',
        teamName: teamName.trim() || undefined,
        entryName: resolvedEntryName || undefined,
        teamDraftWorkflowId: resolvedDraftWorkflowId || undefined,
        teamDraftWorkflowName: resolvedDraftWorkflowName || undefined,
        focus: resolvedDraftWorkflowId
          ? `workflow:${resolvedDraftWorkflowId}`
          : undefined,
        tab: 'studio',
      }),
    );
  const openBehaviors = () =>
    history.push(
      buildStudioRoute({
        tab: 'workflows',
      }),
    );
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
        buildTeamCreateHref({
          teamName: teamName.trim() || undefined,
          entryName: entryName.trim() || undefined,
        }),
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
        <Button
          disabled={!canOpenBuilder}
          onClick={openBuilder}
          style={primaryActionButtonStyle}
        >
          Continue in Studio
        </Button>
      }
      title="Saved Draft Recovery"
    >
      <div
        style={{
          display: 'grid',
          gap: 16,
          gridTemplateColumns: 'repeat(4, minmax(0, 1fr))',
          marginBottom: 20,
        }}
      >
        <ConsoleMetricCard label="用途" tone="purple" value="旧链接恢复" />
        <ConsoleMetricCard label="恢复对象" value="初始 member 草稿" />
        <ConsoleMetricCard label="继续位置" value="Studio" />
        <ConsoleMetricCard label="新增后端事实" tone="green" value="0" />
      </div>

      <AevatarPanel
        layoutMode="document"
        padding={20}
        title="Continue initial member draft"
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
              Saved draft recovery
            </Typography.Title>
            <div
              style={{
                display: 'flex',
                flexWrap: 'wrap',
                gap: 8,
              }}
            >
              {['旧链接兼容', '草稿恢复', '显式进入 Studio', '不创建团队事实'].map((item) => (
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
                <Typography.Text strong>Legacy team label</Typography.Text>
                <Input
                  aria-label="Legacy team label"
                  placeholder="例如：订单助手团队"
                  value={teamName}
                  onChange={(event) => setTeamName(event.target.value)}
                />
              </div>
              <div style={{ display: 'grid', gap: 8 }}>
                <Typography.Text strong>Initial member label</Typography.Text>
                <Input
                  aria-label="Initial member label"
                  placeholder="默认复用团队名称"
                  value={entryName}
                  onChange={(event) => setEntryName(event.target.value)}
                />
              </div>
              <Typography.Text
                type="secondary"
                style={{ gridColumn: '1 / -1', lineHeight: 1.6 }}
              >
                This compatibility page preserves old Create Team links and saved
                draft recovery. New team creation now starts in Studio by creating
                the first member.
                {hasSavedDraft
                  ? ' Continue in Studio to edit the linked initial member draft.'
                  : ''}
              </Typography.Text>
            </div>
            <Space wrap size={[8, 8]}>
              <Button
                icon={<BuildOutlined />}
                disabled={!canOpenBuilder}
                onClick={openBuilder}
                style={primaryActionButtonStyle}
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
                ? `Legacy label: ${teamName.trim()}`
                : 'Use this page only for old links or saved drafts'}
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
          </div>
        </AevatarPanel>
      ) : null}
    </ConsoleMenuPageShell>
  );
};

export default TeamCreatePage;
