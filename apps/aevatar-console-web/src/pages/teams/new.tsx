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
        draftMode: resolvedDraftWorkflowId ? undefined : 'new',
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
          Open Studio
        </Button>
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
        <ConsoleMetricCard label="入口" tone="purple" value="Studio" />
        <ConsoleMetricCard label="构建对象" value="行为 + 脚本" />
        <ConsoleMetricCard label="完成后" value="Team Details" />
        <ConsoleMetricCard label="新增后端流" tone="green" value="0" />
      </div>

      <AevatarPanel
        layoutMode="document"
        padding={20}
        title="Start Building"
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
              Studio
            </Typography.Title>
            <div
              style={{
                display: 'flex',
                flexWrap: 'wrap',
                gap: 8,
              }}
            >
              {['行为定义', '脚本行为', 'Agent 角色', '集成'].map((item) => (
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
                <Typography.Text strong>团队名称</Typography.Text>
                <Input
                  aria-label="团队名称"
                  placeholder="例如：订单助手团队"
                  value={teamName}
                  onChange={(event) => setTeamName(event.target.value)}
                />
              </div>
              <div style={{ display: 'grid', gap: 8 }}>
                <Typography.Text strong>入口名称</Typography.Text>
                <Input
                  aria-label="入口名称"
                  placeholder="默认复用团队名称"
                  value={entryName}
                  onChange={(event) => setEntryName(event.target.value)}
                />
              </div>
              <Typography.Text
                type="secondary"
                style={{ gridColumn: '1 / -1', lineHeight: 1.6 }}
              >
                团队名称会显示在创建流程中；入口名称会作为 Studio 新草稿的默认名称。
                如果入口名称留空，Studio 会自动复用团队名称。
                {hasSavedDraft
                  ? ' 这次创建流程已经有已保存草稿，重新进入 Studio 会继续编辑它。'
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
                Open Studio
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
                ? `已填写团队名称：${teamName.trim()}`
                : '先填写团队名称，再进入 Studio'}
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
              这份行为定义草稿已经和当前创建团队流程关联。再次进入 Studio 时，会继续编辑它。
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
              Delete Draft 会删除当前创建流程关联的行为草稿；团队名称和入口名称会保留在这个页面。
            </Typography.Text>
          </div>
        </AevatarPanel>
      ) : null}
    </ConsoleMenuPageShell>
  );
};

export default TeamCreatePage;
