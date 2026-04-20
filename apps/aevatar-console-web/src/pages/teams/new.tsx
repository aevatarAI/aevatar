import { BuildOutlined, RocketOutlined } from '@ant-design/icons';
import { Alert, Button, Input, Space, Typography } from 'antd';
import React from 'react';
import { formatCompactDateTime } from '@/shared/datetime/dateTime';
import { history } from '@/shared/navigation/history';
import { buildTeamsHref } from '@/shared/navigation/teamRoutes';
import {
  countTeamCreateDraftPointersOutsideScope,
  hasTeamCreateDraftPointer,
  loadTeamCreateDraftPointer,
  loadTeamCreateDraftPointers,
  saveTeamCreateDraftPointer,
  selectTeamCreateDraftPointer,
  type TeamCreateDraftPointer,
} from '@/shared/navigation/teamCreateDraftPointer';
import { buildStudioRoute } from '@/shared/studio/navigation';
import { AevatarPanel } from '@/shared/ui/aevatarPageShells';
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

const modeCardBaseStyle: React.CSSProperties = {
  background: '#ffffff',
  border: '1px solid #e5e7eb',
  borderRadius: 16,
  cursor: 'pointer',
  display: 'grid',
  gap: 8,
  padding: 18,
  textAlign: 'left',
};

const modeCardPreviewStyle: React.CSSProperties = {
  background: '#f8f9ff',
  border: '1px solid rgba(108, 92, 231, 0.12)',
  borderRadius: 12,
  display: 'grid',
  gap: 6,
  padding: 12,
};

type CreateTeamRouteDraft = {
  readonly scopeId: string;
  readonly scopeLabel: string;
  readonly teamName: string;
  readonly entryName: string;
  readonly teamDraftWorkflowId: string;
  readonly teamDraftWorkflowName: string;
  readonly sourceBehaviorDefinitionId: string;
  readonly sourceBehaviorDefinitionName: string;
};

function readCreateTeamDraftFromLocation(): CreateTeamRouteDraft {
  if (typeof window === 'undefined') {
    return {
      scopeId: '',
      scopeLabel: '',
      teamName: '',
      entryName: '',
      teamDraftWorkflowId: '',
      teamDraftWorkflowName: '',
      sourceBehaviorDefinitionId: '',
      sourceBehaviorDefinitionName: '',
    };
  }

  const params = new URLSearchParams(window.location.search);
  return {
    scopeId: params.get('scopeId')?.trim() ?? '',
    scopeLabel: params.get('scopeLabel')?.trim() ?? '',
    teamName: params.get('teamName')?.trim() ?? '',
    entryName: params.get('entryName')?.trim() ?? '',
    teamDraftWorkflowId: params.get('teamDraftWorkflowId')?.trim() ?? '',
    teamDraftWorkflowName: params.get('teamDraftWorkflowName')?.trim() ?? '',
    sourceBehaviorDefinitionId:
      params.get('sourceBehaviorDefinitionId')?.trim() ?? '',
    sourceBehaviorDefinitionName:
      params.get('sourceBehaviorDefinitionName')?.trim() ?? '',
  };
}

function toSavedDraftPointer(
  value: CreateTeamRouteDraft,
): TeamCreateDraftPointer {
  return {
    scopeId: value.scopeId.trim(),
    teamName: value.teamName.trim(),
    entryName: value.entryName.trim(),
    teamDraftWorkflowId: value.teamDraftWorkflowId.trim(),
    teamDraftWorkflowName: value.teamDraftWorkflowName.trim(),
    sourceBehaviorDefinitionId: value.sourceBehaviorDefinitionId.trim(),
    sourceBehaviorDefinitionName: value.sourceBehaviorDefinitionName.trim(),
    updatedAt: '',
  };
}

function resolveInitialSavedDraft(
  routeDraft: CreateTeamRouteDraft,
): TeamCreateDraftPointer {
  const persistedDraft = loadTeamCreateDraftPointer(routeDraft.scopeId);
  if (hasTeamCreateDraftPointer(persistedDraft)) {
    return persistedDraft;
  }

  return toSavedDraftPointer(routeDraft);
}

function resolveInitialSavedDrafts(
  routeDraft: CreateTeamRouteDraft,
): TeamCreateDraftPointer[] {
  const persistedDrafts = loadTeamCreateDraftPointers(routeDraft.scopeId);
  if (persistedDrafts.length > 0) {
    return persistedDrafts;
  }

  const legacyRouteDraft = toSavedDraftPointer(routeDraft);
  return hasTeamCreateDraftPointer(legacyRouteDraft) ? [legacyRouteDraft] : [];
}

const TeamCreatePage: React.FC = () => {
  const initialRouteDraft = React.useMemo(readCreateTeamDraftFromLocation, []);
  const initialSavedDrafts = React.useMemo(
    () => resolveInitialSavedDrafts(initialRouteDraft),
    [initialRouteDraft],
  );
  const initialSavedDraft = React.useMemo(
    () => resolveInitialSavedDraft(initialRouteDraft),
    [initialRouteDraft],
  );
  const [savedDraftPointers, setSavedDraftPointers] = React.useState(
    initialSavedDrafts,
  );
  const [selectedSavedDraftWorkflowId, setSelectedSavedDraftWorkflowId] = React.useState(
    initialSavedDraft.teamDraftWorkflowId,
  );
  const selectedSavedDraft = React.useMemo(
    () =>
      savedDraftPointers.find(
        (item) => item.teamDraftWorkflowId === selectedSavedDraftWorkflowId,
      ) || savedDraftPointers[0] || initialSavedDraft,
    [initialSavedDraft, savedDraftPointers, selectedSavedDraftWorkflowId],
  );
  const savedDraftWorkflowId = selectedSavedDraft.teamDraftWorkflowId.trim();
  const savedDraftWorkflowName =
    selectedSavedDraft.teamDraftWorkflowName.trim() || savedDraftWorkflowId;
  const savedDraftTeamName = selectedSavedDraft.teamName.trim();
  const savedDraftEntryName =
    selectedSavedDraft.entryName.trim() || selectedSavedDraft.teamName.trim();
  const savedDraftUpdatedAt = selectedSavedDraft.updatedAt.trim();
  const savedDraftPrimaryName =
    savedDraftTeamName || savedDraftEntryName || savedDraftWorkflowName || '未命名团队';
  const savedDraftHasDistinctEntry = Boolean(savedDraftEntryName)
    && savedDraftEntryName !== savedDraftPrimaryName;
  const hasSavedDraft = savedDraftPointers.length > 0;
  const hiddenCrossScopeDraftCount = React.useMemo(
    () => countTeamCreateDraftPointersOutsideScope(initialRouteDraft.scopeId),
    [initialRouteDraft.scopeId, savedDraftPointers],
  );
  const [mode, setMode] = React.useState<'' | 'new' | 'resume'>(
    hasSavedDraft ? '' : 'new',
  );
  const [teamName, setTeamName] = React.useState(initialRouteDraft.teamName);
  const [entryName, setEntryName] = React.useState(initialRouteDraft.entryName);
  const resolvedEntryName = entryName.trim() || teamName.trim();
  const openMode = hasSavedDraft ? mode : 'new';
  React.useEffect(() => {
    if (
      hasTeamCreateDraftPointer(loadTeamCreateDraftPointer(initialRouteDraft.scopeId)) ||
      !hasTeamCreateDraftPointer(initialRouteDraft)
    ) {
      return;
    }

    saveTeamCreateDraftPointer(toSavedDraftPointer(initialRouteDraft));
    setSavedDraftPointers(loadTeamCreateDraftPointers(initialRouteDraft.scopeId));
    setSelectedSavedDraftWorkflowId(
      loadTeamCreateDraftPointer(initialRouteDraft.scopeId).teamDraftWorkflowId,
    );
  }, [initialRouteDraft]);
  const selectSavedDraft = (workflowId: string) => {
    const nextSelectedDraft = selectTeamCreateDraftPointer(
      workflowId,
      initialRouteDraft.scopeId,
    );
    setSavedDraftPointers(loadTeamCreateDraftPointers(initialRouteDraft.scopeId));
    setSelectedSavedDraftWorkflowId(nextSelectedDraft.teamDraftWorkflowId);
  };
  const canOpenBuilder =
    openMode === ''
      ? false
      : openMode === 'resume'
        ? hasSavedDraft
        : Boolean(teamName.trim());
  const primaryActionLabel =
    openMode === 'resume'
      ? 'Continue Draft'
      : openMode === 'new'
        ? 'Create in Studio'
        : 'Choose a Path First';
  const openBuilder = () =>
    history.push(
      buildStudioRoute({
        scopeId: initialRouteDraft.scopeId || undefined,
        scopeLabel: initialRouteDraft.scopeLabel || undefined,
        teamMode: 'create',
        teamName:
          openMode === 'resume'
            ? savedDraftTeamName || undefined
            : teamName.trim() || undefined,
        entryName:
          openMode === 'resume'
            ? savedDraftEntryName || undefined
            : resolvedEntryName || undefined,
        teamDraftWorkflowId:
          openMode === 'resume' ? savedDraftWorkflowId || undefined : undefined,
        teamDraftWorkflowName:
          openMode === 'resume' ? savedDraftWorkflowName || undefined : undefined,
        sourceBehaviorDefinitionId:
          openMode === 'resume'
            ? selectedSavedDraft.sourceBehaviorDefinitionId || undefined
            : undefined,
        sourceBehaviorDefinitionName:
          openMode === 'resume'
            ? selectedSavedDraft.sourceBehaviorDefinitionName || undefined
            : undefined,
        workflowId: openMode === 'resume' ? savedDraftWorkflowId || undefined : undefined,
        draftMode: openMode === 'resume' ? undefined : 'new',
        tab: 'studio',
      }),
    );
  const openBehaviors = () =>
    history.push(
      buildStudioRoute({
        scopeId: initialRouteDraft.scopeId || undefined,
        scopeLabel: initialRouteDraft.scopeLabel || undefined,
        tab: 'workflows',
      }),
    );

  return (
    <ConsoleMenuPageShell
      breadcrumb="Aevatar / Teams"
      title="Create Team"
    >
      <AevatarPanel
        layoutMode="document"
        padding={20}
        title="Choose a Path"
      >
        <div
          style={{
            display: 'grid',
            gap: 18,
          }}
        >
          <div style={{ display: 'grid', gap: 10, maxWidth: 860 }}>
            <Typography.Title
              level={3}
              style={{
                color: '#1d2129',
                fontSize: 30,
                fontWeight: 600,
                lineHeight: 1.2,
                margin: 0,
              }}
            >
              {hasSavedDraft ? 'Choose What You Want To Do' : 'Create a New Team'}
            </Typography.Title>
            <Typography.Text
              type="secondary"
              style={{
                fontSize: 15,
                lineHeight: 1.7,
              }}
            >
              {hasSavedDraft
                ? 'You already have a saved draft. Choose whether to resume it, or start creating a different team from a fresh Studio draft.'
                : 'Define the team name and entry name first, then open Studio to build the entry workflow.'}
            </Typography.Text>
          </div>

          {!hasSavedDraft && hiddenCrossScopeDraftCount > 0 ? (
            <Alert
              showIcon
              type="info"
              message="当前 Scope 下没有 saved draft"
              description={`另有 ${hiddenCrossScopeDraftCount} 份草稿属于其他 Scope，因此这里不会显示。切回对应 Scope 后再恢复它们。`}
            />
          ) : null}

          {hasSavedDraft ? (
            <div
              style={{
                display: 'grid',
                gap: 12,
                gridTemplateColumns: 'repeat(auto-fit, minmax(240px, 1fr))',
                maxWidth: 920,
              }}
            >
              <button
                type="button"
                aria-pressed={openMode === 'new'}
                onClick={() => setMode('new')}
                style={{
                  ...modeCardBaseStyle,
                  ...(openMode === 'new'
                    ? {
                        border: '1px solid #6c5ce7',
                        boxShadow: '0 0 0 2px rgba(108, 92, 231, 0.12)',
                      }
                    : null),
                }}
              >
                <Typography.Text strong style={{ fontSize: 18 }}>
                  Create New Team
                </Typography.Text>
                <Typography.Text type="secondary" style={{ lineHeight: 1.6 }}>
                  Start a fresh team flow. The saved draft will stay available, but this action will not reuse it.
                </Typography.Text>
              </button>

              <button
                type="button"
                aria-pressed={openMode === 'resume'}
                onClick={() => setMode('resume')}
                style={{
                  ...modeCardBaseStyle,
                  ...(openMode === 'resume'
                    ? {
                        border: '1px solid #6c5ce7',
                        boxShadow: '0 0 0 2px rgba(108, 92, 231, 0.12)',
                      }
                    : null),
                }}
              >
                <Typography.Text strong style={{ fontSize: 18 }}>
                  Resume Saved Draft
                </Typography.Text>
                <Typography.Text type="secondary" style={{ lineHeight: 1.6 }}>
                  Continue the unfinished draft you saved earlier instead of creating a different team.
                </Typography.Text>
                {savedDraftPointers.length > 1 ? (
                  <Typography.Text type="secondary" style={{ lineHeight: 1.6 }}>
                    {savedDraftPointers.length} drafts available. Resume will use the one you select below.
                  </Typography.Text>
                ) : null}
                <div style={modeCardPreviewStyle}>
                  <div style={{ display: 'grid', gap: 2 }}>
                    <Typography.Text type="secondary">Team</Typography.Text>
                    <Typography.Text strong>
                      {savedDraftPrimaryName}
                    </Typography.Text>
                  </div>
                  {savedDraftHasDistinctEntry ? (
                    <div style={{ display: 'grid', gap: 2 }}>
                      <Typography.Text type="secondary">Entry</Typography.Text>
                      <Typography.Text strong>
                        {savedDraftEntryName}
                      </Typography.Text>
                    </div>
                  ) : null}
                  {savedDraftUpdatedAt ? (
                    <div style={{ display: 'grid', gap: 2 }}>
                      <Typography.Text type="secondary">Last Saved</Typography.Text>
                      <Typography.Text strong>
                        {formatCompactDateTime(savedDraftUpdatedAt, 'n/a')}
                      </Typography.Text>
                    </div>
                  ) : null}
                </div>
              </button>
            </div>
          ) : null}

          {openMode === 'new' ? (
            <div
              style={{
                display: 'grid',
                gap: 12,
                gridTemplateColumns: 'repeat(auto-fit, minmax(240px, 1fr))',
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
                  ? ' 当前页面上的 saved draft 不会自动绑定到这次新建流程。'
                  : ''}
              </Typography.Text>
            </div>
          ) : null}

          {openMode === 'resume' ? (
            <div
              style={{
                background: '#faf7ff',
                border: '1px solid rgba(108, 92, 231, 0.14)',
                borderRadius: 18,
                display: 'grid',
                gap: 14,
                maxWidth: 760,
                padding: 18,
              }}
            >
              <div style={{ display: 'grid', gap: 6 }}>
                <Typography.Text strong style={{ fontSize: 18 }}>
                  {savedDraftPointers.length > 1 ? 'Saved Drafts' : 'Saved Draft'}
                </Typography.Text>
                <Typography.Text type="secondary" style={{ lineHeight: 1.6 }}>
                  {savedDraftPointers.length > 1
                    ? 'Choose which draft to resume. Creating a new team should not overwrite the drafts you already saved.'
                    : 'This draft is treated as a separate recovery path. It should not be mixed with creating another team from a new form.'}
                </Typography.Text>
              </div>
              <div
                style={{
                  display: 'grid',
                  gap: 12,
                  gridTemplateColumns: 'repeat(auto-fit, minmax(220px, 1fr))',
                }}
              >
                {savedDraftPointers.map((draftPointer) => {
                  const draftWorkflowId = draftPointer.teamDraftWorkflowId.trim();
                  const draftWorkflowName =
                    draftPointer.teamDraftWorkflowName.trim() || draftWorkflowId;
                  const draftPrimaryName =
                    draftPointer.teamName.trim()
                    || draftPointer.entryName.trim()
                    || draftWorkflowName
                    || '未命名团队';
                  const draftEntryName =
                    draftPointer.entryName.trim() || draftPointer.teamName.trim();
                  const draftHasDistinctEntry = Boolean(draftEntryName)
                    && draftEntryName !== draftPrimaryName;
                  const isSelected =
                    draftWorkflowId === selectedSavedDraftWorkflowId ||
                    (!selectedSavedDraftWorkflowId && draftPointer === savedDraftPointers[0]);

                  return (
                    <button
                      key={draftWorkflowId}
                      type="button"
                      aria-pressed={isSelected}
                      onClick={() => selectSavedDraft(draftWorkflowId)}
                      style={{
                        ...modeCardPreviewStyle,
                        cursor: 'pointer',
                        textAlign: 'left',
                        ...(isSelected
                          ? {
                              border: '1px solid #6c5ce7',
                              boxShadow: '0 0 0 2px rgba(108, 92, 231, 0.12)',
                            }
                          : null),
                      }}
                    >
                      <div style={{ display: 'grid', gap: 2 }}>
                        <Typography.Text type="secondary">Team</Typography.Text>
                        <Typography.Text strong>
                          {draftPrimaryName}
                        </Typography.Text>
                      </div>
                      {draftHasDistinctEntry ? (
                        <div style={{ display: 'grid', gap: 2 }}>
                          <Typography.Text type="secondary">Entry</Typography.Text>
                          <Typography.Text strong>
                            {draftEntryName}
                          </Typography.Text>
                        </div>
                      ) : null}
                      {draftPointer.updatedAt ? (
                        <div style={{ display: 'grid', gap: 2 }}>
                          <Typography.Text type="secondary">Last Saved</Typography.Text>
                          <Typography.Text strong>
                            {formatCompactDateTime(draftPointer.updatedAt, 'n/a')}
                          </Typography.Text>
                        </div>
                      ) : null}
                    </button>
                  );
                })}
              </div>
              <Typography.Text type="secondary" style={{ lineHeight: 1.6 }}>
                Delete Draft 需要后端删除 workflow 接口，当前前端先不提供假删除。
              </Typography.Text>
            </div>
          ) : null}

          <Space wrap size={[8, 8]}>
            <Button
              icon={<BuildOutlined />}
              disabled={!canOpenBuilder}
              onClick={openBuilder}
              style={primaryActionButtonStyle}
            >
              {primaryActionLabel}
            </Button>
            {openMode === 'resume' ? (
              <Button
                disabled
                style={secondaryActionButtonStyle}
              >
                Delete Draft
              </Button>
            ) : null}
            <Button
              icon={<RocketOutlined />}
              onClick={openBehaviors}
              style={secondaryActionButtonStyle}
            >
              View Workflows
            </Button>
            <Button
              onClick={() =>
                history.push(
                  buildTeamsHref({
                    scopeId: initialRouteDraft.scopeId || undefined,
                  }),
                )
              }
              style={secondaryActionButtonStyle}
            >
              Back to My Teams
            </Button>
          </Space>
        </div>
      </AevatarPanel>
    </ConsoleMenuPageShell>
  );
};

export default TeamCreatePage;
