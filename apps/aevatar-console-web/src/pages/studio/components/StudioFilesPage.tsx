import {
  ApiOutlined,
  AppstoreOutlined,
  BarsOutlined,
  CodeOutlined,
  DownOutlined,
  FileOutlined,
  FolderOpenOutlined,
  MessageOutlined,
  RightOutlined,
  SearchOutlined,
  SettingOutlined,
  TeamOutlined,
} from '@ant-design/icons';
import { useQuery } from '@tanstack/react-query';
import { Alert, Button, Col, Input, Row, Space, Typography } from 'antd';
import React from 'react';
import { chatHistoryApi } from '@/pages/chat/chatHistoryApi';
import type { ConversationMeta } from '@/pages/chat/chatTypes';
import ExplorerDetailPane from '@/pages/studio/explorer/ExplorerDetailPane';
import ExplorerTree from '@/pages/studio/explorer/ExplorerTree';
import { useExplorerStore } from '@/pages/studio/explorer/useExplorerStore';
import type {
  StudioConnectorCatalog,
  StudioRoleCatalog,
  StudioSettings,
  StudioWorkflowSummary,
  StudioWorkspaceSettings,
} from '@/shared/studio/models';
import { scriptsApi } from '@/shared/studio/scriptsApi';
import {
  cardStackStyle,
  embeddedPanelStyle,
  stretchColumnStyle,
} from '@/shared/ui/proComponents';
import StudioFilesDetailPane from './StudioFilesDetailPane';

type QueryState<T> = {
  readonly isLoading: boolean;
  readonly isError: boolean;
  readonly error: unknown;
  readonly data: T | undefined;
};

type StudioFileKey =
  | 'settings.json'
  | 'role-catalog'
  | 'connector-catalog'
  | `chat-history:${string}`
  | `workflow:${string}`
  | `script:${string}`;

type StaticFileDescriptor = {
  readonly file: 'settings.json' | 'role-catalog' | 'connector-catalog';
  readonly icon: React.ReactNode;
  readonly label: string;
};

type FilesViewMode = 'curated' | 'explorer';
type PendingExplorerAction =
  | { kind: 'select-file'; nextKey: string }
  | { kind: 'switch-view'; nextMode: FilesViewMode };

type StudioFilesPageProps = {
  readonly workflows: QueryState<StudioWorkflowSummary[]>;
  readonly workspaceSettings: QueryState<StudioWorkspaceSettings>;
  readonly roles: QueryState<StudioRoleCatalog>;
  readonly connectors: QueryState<StudioConnectorCatalog>;
  readonly settings: QueryState<StudioSettings>;
  readonly scopeId: string;
  readonly workflowStorageMode: string;
  readonly scriptsEnabled: boolean;
  readonly onOpenWorkflowInStudio: (workflowId: string) => void;
  readonly onOpenScriptInStudio: (scriptId: string) => void;
  readonly showHeader?: boolean;
};

const staticFiles: readonly StaticFileDescriptor[] = [
  {
    file: 'settings.json',
    icon: <SettingOutlined />,
    label: 'settings.json',
  },
  {
    file: 'role-catalog',
    icon: <TeamOutlined />,
    label: 'Role Catalog',
  },
  {
    file: 'connector-catalog',
    icon: <ApiOutlined />,
    label: 'Connector Catalog',
  },
];

const pageRowStyle: React.CSSProperties = {
  flex: '1 1 0',
  minHeight: 0,
  overflow: 'hidden',
};

const panelShellStyle: React.CSSProperties = {
  ...embeddedPanelStyle,
  display: 'flex',
  flex: '1 1 0',
  flexDirection: 'column',
  gap: 16,
  minHeight: 0,
  overflow: 'hidden',
  width: '100%',
};

const filesColumnStyle: React.CSSProperties = {
  ...stretchColumnStyle,
  flex: '1 1 0',
  height: '100%',
  minHeight: 0,
  minWidth: 0,
  overflow: 'hidden',
};

const treeScrollStyle: React.CSSProperties = {
  display: 'flex',
  flex: 1,
  flexDirection: 'column',
  gap: 4,
  height: 0,
  minHeight: 0,
  overflowX: 'hidden',
  overflowY: 'auto',
  paddingRight: 4,
};

const treeButtonStyle: React.CSSProperties = {
  alignItems: 'center',
  background: 'transparent',
  border: 'none',
  borderRadius: 10,
  cursor: 'pointer',
  display: 'flex',
  gap: 10,
  justifyContent: 'space-between',
  padding: '10px 12px',
  textAlign: 'left',
  width: '100%',
};

const treeButtonActiveStyle: React.CSSProperties = {
  background: 'rgba(22, 119, 255, 0.08)',
};

const treeMetaStyle: React.CSSProperties = {
  color: 'var(--ant-color-text-secondary)',
  fontSize: 12,
};

const treeChildButtonStyle: React.CSSProperties = {
  ...treeButtonStyle,
  paddingLeft: 40,
};

const treeDividerStyle: React.CSSProperties = {
  borderTop: '1px solid var(--ant-color-border-secondary)',
  margin: '8px 12px',
};

const viewToggleWrapStyle: React.CSSProperties = {
  display: 'flex',
  justifyContent: 'flex-start',
};

function matchesSearch(values: Array<string | null | undefined>, search: string): boolean {
  const keyword = search.trim().toLowerCase();
  if (!keyword) {
    return true;
  }

  return values.some((value) => String(value || '').toLowerCase().includes(keyword));
}

function fileKeyIsVisible(
  fileKey: StudioFileKey,
  visibleWorkflowIds: readonly string[],
  visibleScriptIds: readonly string[],
  visibleChatHistoryIds: readonly string[],
  visibleStaticFiles: readonly string[],
): boolean {
  if (fileKey.startsWith('workflow:')) {
    return visibleWorkflowIds.includes(fileKey.slice('workflow:'.length));
  }

  if (fileKey.startsWith('script:')) {
    return visibleScriptIds.includes(fileKey.slice('script:'.length));
  }

  if (fileKey.startsWith('chat-history:')) {
    return visibleChatHistoryIds.includes(fileKey.slice('chat-history:'.length));
  }

  return visibleStaticFiles.includes(fileKey);
}

const TreeRow: React.FC<{
  active: boolean;
  count?: React.ReactNode;
  icon: React.ReactNode;
  indent?: boolean;
  label: string;
  meta?: string;
  onClick?: () => void;
}> = ({ active, count, icon, indent = false, label, meta, onClick }) => (
  <button
    type="button"
    onClick={onClick}
    style={{
      ...(indent ? treeChildButtonStyle : treeButtonStyle),
      ...(active ? treeButtonActiveStyle : null),
    }}
  >
    <span style={{ alignItems: 'center', display: 'flex', gap: 10, minWidth: 0 }}>
      <span aria-hidden="true">{icon}</span>
      <span style={{ minWidth: 0 }}>
        <Typography.Text ellipsis>{label}</Typography.Text>
        {meta ? (
          <div>
            <Typography.Text style={treeMetaStyle}>{meta}</Typography.Text>
          </div>
        ) : null}
      </span>
    </span>
    {count !== undefined ? <Typography.Text style={treeMetaStyle}>{count}</Typography.Text> : null}
  </button>
);

const FolderToggle: React.FC<{
  count?: React.ReactNode;
  label: string;
  open: boolean;
  onToggle: () => void;
}> = ({ count, label, open, onToggle }) => (
  <button type="button" onClick={onToggle} style={treeButtonStyle}>
    <span style={{ alignItems: 'center', display: 'flex', gap: 10 }}>
      {open ? <DownOutlined /> : <RightOutlined />}
      <FolderOpenOutlined />
      <Typography.Text>{label}</Typography.Text>
    </span>
    {count !== undefined ? <Typography.Text style={treeMetaStyle}>{count}</Typography.Text> : null}
  </button>
);

const StudioFilesPage: React.FC<StudioFilesPageProps> = ({
  workflows,
  workspaceSettings,
  roles,
  connectors,
  settings,
  scopeId,
  workflowStorageMode,
  scriptsEnabled,
  onOpenWorkflowInStudio,
  onOpenScriptInStudio,
  showHeader = true,
}) => {
  const [selectedFile, setSelectedFile] = React.useState<StudioFileKey>('settings.json');
  const [viewMode, setViewMode] = React.useState<FilesViewMode>('curated');
  const [explorerDirty, setExplorerDirty] = React.useState(false);
  const [pendingExplorerAction, setPendingExplorerAction] =
    React.useState<PendingExplorerAction | null>(null);
  const [search, setSearch] = React.useState('');
  const [workflowsOpen, setWorkflowsOpen] = React.useState(true);
  const [scriptsOpen, setScriptsOpen] = React.useState(true);
  const [chatHistoriesOpen, setChatHistoriesOpen] = React.useState(false);
  const explorer = useExplorerStore(scopeId);

  const scripts = useQuery({
    queryKey: ['studio-files-scripts', scopeId],
    enabled: scriptsEnabled && Boolean(scopeId),
    queryFn: () => scriptsApi.listScripts(scopeId, true),
  });
  const chatConversations = useQuery({
    queryKey: ['studio-files-chat-histories', scopeId],
    enabled: Boolean(scopeId),
    queryFn: () => chatHistoryApi.listConversationMetas(scopeId),
  });

  const filteredWorkflows = React.useMemo(
    () =>
      [...(workflows.data ?? [])]
        .filter((workflow) =>
          matchesSearch(
            [
              workflow.name,
              workflow.description,
              workflow.fileName,
              workflow.directoryLabel,
              workflow.filePath,
            ],
            search,
          ),
        )
        .sort((left, right) => left.name.localeCompare(right.name)),
    [search, workflows.data],
  );
  const filteredScripts = React.useMemo(
    () =>
      [...(scripts.data ?? [])]
        .filter((detail) =>
          matchesSearch(
            [
              detail.script?.scriptId,
              detail.script?.activeRevision,
              detail.script?.catalogActorId,
              detail.source?.definitionActorId,
            ],
            search,
          ),
        )
        .sort((left, right) =>
          String(left.script?.scriptId || '').localeCompare(String(right.script?.scriptId || '')),
        ),
    [scripts.data, search],
  );
  const visibleStaticFiles = React.useMemo(
    () =>
      staticFiles
        .filter((item) => matchesSearch([item.label], search))
        .map((item) => item.file),
    [search],
  );
  const filteredChatConversations = React.useMemo(
    () =>
      [...(chatConversations.data ?? [])]
        .filter((conversation) =>
          matchesSearch(
            [
              conversation.id,
              conversation.actorId,
              conversation.commandId,
              conversation.runId,
              conversation.title,
              conversation.serviceId,
              conversation.serviceKind,
            ],
            search,
          ),
        )
        .sort((left, right) => right.updatedAt.localeCompare(left.updatedAt)),
    [chatConversations.data, search],
  );
  const visibleExplorerKeys = React.useMemo(
    () =>
      explorer.manifest
        .filter((entry) =>
          matchesSearch([entry.key, entry.name, entry.type], search),
        )
        .map((entry) => entry.key),
    [explorer.manifest, search],
  );

  React.useEffect(() => {
    const visibleWorkflowIds = filteredWorkflows.map((workflow) => workflow.workflowId);
    const visibleScriptIds = filteredScripts.map((detail) => detail.script?.scriptId || '');
    const visibleChatHistoryIds = filteredChatConversations.map((conversation) => conversation.id);
    if (
      fileKeyIsVisible(
        selectedFile,
        visibleWorkflowIds,
        visibleScriptIds,
        visibleChatHistoryIds,
        visibleStaticFiles,
      )
    ) {
      return;
    }

    if (visibleStaticFiles.includes('settings.json')) {
      setSelectedFile('settings.json');
      return;
    }

    if (visibleWorkflowIds[0]) {
      setSelectedFile(`workflow:${visibleWorkflowIds[0]}`);
      return;
    }

    if (visibleScriptIds[0]) {
      setSelectedFile(`script:${visibleScriptIds[0]}`);
      return;
    }

    if (visibleChatHistoryIds[0]) {
      setSelectedFile(`chat-history:${visibleChatHistoryIds[0]}`);
    }
  }, [
    filteredChatConversations,
    filteredScripts,
    filteredWorkflows,
    selectedFile,
    visibleStaticFiles,
  ]);

  React.useEffect(() => {
    if (viewMode !== 'explorer') {
      return;
    }

    if (visibleExplorerKeys.length === 0) {
      if (!explorerDirty) {
        explorer.setSelectedKey(null);
      }
      return;
    }

    if (!explorer.selectedKey) {
      explorer.setSelectedKey(visibleExplorerKeys[0] ?? null);
      return;
    }

    if (!visibleExplorerKeys.includes(explorer.selectedKey) && !explorerDirty) {
      explorer.setSelectedKey(visibleExplorerKeys[0] ?? null);
    }
  }, [
    explorer.selectedKey,
    explorer.setSelectedKey,
    explorerDirty,
    viewMode,
    visibleExplorerKeys,
  ]);

  React.useEffect(() => {
    if (!explorerDirty) {
      setPendingExplorerAction(null);
    }
  }, [explorerDirty]);

  const handleViewModeChange = React.useCallback(
    (nextMode: FilesViewMode) => {
      if (nextMode === viewMode) {
        return;
      }

      if (viewMode === 'explorer' && explorerDirty) {
        setPendingExplorerAction({ kind: 'switch-view', nextMode });
        return;
      }

      setPendingExplorerAction(null);
      setViewMode(nextMode);
      if (nextMode !== 'explorer') {
        setExplorerDirty(false);
      }
    },
    [explorerDirty, viewMode],
  );

  const handleExplorerSelect = React.useCallback(
    (key: string) => {
      if (key === explorer.selectedKey) {
        return;
      }

      if (explorerDirty) {
        setPendingExplorerAction({ kind: 'select-file', nextKey: key });
        return;
      }

      setPendingExplorerAction(null);
      explorer.setSelectedKey(key);
      setExplorerDirty(false);
    },
    [explorer.selectedKey, explorer.setSelectedKey, explorerDirty],
  );

  const handleDiscardExplorerChanges = React.useCallback(() => {
    if (!pendingExplorerAction) {
      return;
    }

    if (pendingExplorerAction.kind === 'switch-view') {
      setViewMode(pendingExplorerAction.nextMode);
      if (pendingExplorerAction.nextMode !== 'explorer') {
        setExplorerDirty(false);
      }
    } else {
      explorer.setSelectedKey(pendingExplorerAction.nextKey);
      setExplorerDirty(false);
    }

    setPendingExplorerAction(null);
  }, [explorer, pendingExplorerAction]);

  const handleKeepEditingExplorer = React.useCallback(() => {
    setPendingExplorerAction(null);
  }, []);

  const rootLabel = viewMode === 'explorer' ? scopeId || 'chrono-storage' : scopeId || 'workspace';

  return (
    <Row gutter={[16, 16]} align="stretch" style={pageRowStyle}>
      <Col xs={24} xl={8} xxl={7} style={filesColumnStyle}>
        <section style={panelShellStyle}>
          {showHeader ? (
            <div style={cardStackStyle}>
              <Typography.Title level={4} style={{ margin: 0 }}>
                Files
              </Typography.Title>
              <Typography.Text type="secondary">
                Use the guided Studio view for common assets, or switch to raw storage
                when you need to inspect the underlying files directly.
              </Typography.Text>
            </div>
          ) : null}

          <div style={viewToggleWrapStyle}>
            <Space.Compact>
              <Button
                type={viewMode === 'curated' ? 'primary' : 'default'}
                icon={<AppstoreOutlined />}
                onClick={() => handleViewModeChange('curated')}
              >
                Studio View
              </Button>
              <Button
                type={viewMode === 'explorer' ? 'primary' : 'default'}
                icon={<BarsOutlined />}
                onClick={() => handleViewModeChange('explorer')}
              >
                Storage Explorer
              </Button>
            </Space.Compact>
          </div>

          <Input
            allowClear
            prefix={<SearchOutlined />}
            aria-label="Search files"
            placeholder={viewMode === 'explorer' ? 'Search explorer' : 'Search files'}
            value={search}
            onChange={(event) => setSearch(event.target.value)}
          />

          {pendingExplorerAction ? (
            <Alert
              type="warning"
              showIcon
              message="You have unsaved Explorer changes"
              description={
                pendingExplorerAction.kind === 'switch-view'
                  ? 'Discard the current edits before leaving Storage Explorer.'
                  : 'Discard the current edits before opening another storage file.'
              }
              action={
                <Space wrap size={[8, 8]}>
                  <Button size="small" onClick={handleKeepEditingExplorer}>
                    Keep editing
                  </Button>
                  <Button danger size="small" onClick={handleDiscardExplorerChanges}>
                    Discard changes
                  </Button>
                </Space>
              }
            />
          ) : null}

          {viewMode === 'explorer' ? (
            <ExplorerTree
              manifest={explorer.manifest}
              onSelect={handleExplorerSelect}
              scopeId={scopeId}
              search={search}
              selectedKey={explorer.selectedKey}
            />
          ) : (
            <div style={treeScrollStyle}>
              <div style={{ padding: '4px 12px' }}>
                <Space size={8}>
                  <FolderOpenOutlined />
                  <Typography.Text strong>{rootLabel}/</Typography.Text>
                </Space>
              </div>

              <FolderToggle
                label="workflows/"
                open={workflowsOpen}
                count={workflows.data?.length ?? 0}
                onToggle={() => setWorkflowsOpen((current) => !current)}
              />
              {workflowsOpen ? (
                filteredWorkflows.length > 0 ? (
                  filteredWorkflows.map((workflow) => (
                    <TreeRow
                      key={workflow.workflowId}
                      active={selectedFile === `workflow:${workflow.workflowId}`}
                      icon={<FileOutlined />}
                      indent
                      label={workflow.fileName}
                      meta={workflow.directoryLabel}
                      onClick={() => setSelectedFile(`workflow:${workflow.workflowId}`)}
                    />
                  ))
                ) : (
                  <Typography.Text style={{ ...treeMetaStyle, paddingInline: 40 }}>
                    {workflows.isLoading ? 'Loading workflows...' : 'No workflow files matched.'}
                  </Typography.Text>
                )
              ) : null}

              {scriptsEnabled ? (
                <>
                  <FolderToggle
                    label="scripts/"
                    open={scriptsOpen}
                    count={scopeId ? scripts.data?.length ?? 0 : 0}
                    onToggle={() => setScriptsOpen((current) => !current)}
                  />
                  {scriptsOpen ? (
                    scopeId ? (
                      filteredScripts.length > 0 ? (
                        filteredScripts.map((detail) => (
                          <TreeRow
                            key={detail.script?.scriptId}
                            active={selectedFile === `script:${detail.script?.scriptId || ''}`}
                            icon={<CodeOutlined />}
                            indent
                            label={`${detail.script?.scriptId || 'script'}.cs`}
                            meta={detail.script?.activeRevision || 'Draft'}
                            onClick={() =>
                              detail.script?.scriptId
                                ? setSelectedFile(`script:${detail.script.scriptId}`)
                                : undefined
                            }
                          />
                        ))
                      ) : (
                        <Typography.Text style={{ ...treeMetaStyle, paddingInline: 40 }}>
                          {scripts.isLoading ? 'Loading scripts...' : 'No script files matched.'}
                        </Typography.Text>
                      )
                    ) : (
                      <Typography.Text style={{ ...treeMetaStyle, paddingInline: 40 }}>
                        Resolve a project scope to browse scripts.
                      </Typography.Text>
                    )
                  ) : null}
                </>
              ) : null}

              {visibleStaticFiles.length > 0 ? <div aria-hidden="true" style={treeDividerStyle} /> : null}

              {visibleStaticFiles.map((fileName) => {
                const descriptor = staticFiles.find((item) => item.file === fileName);
                if (!descriptor) {
                  return null;
                }

                return (
                  <TreeRow
                    key={descriptor.file}
                    active={selectedFile === descriptor.file}
                    icon={descriptor.icon}
                    label={descriptor.label}
                    onClick={() => setSelectedFile(descriptor.file)}
                  />
                );
              })}

              <div aria-hidden="true" style={treeDividerStyle} />
              <FolderToggle
                label="chat-histories/"
                open={chatHistoriesOpen}
                count={scopeId ? chatConversations.data?.length ?? 0 : 0}
                onToggle={() => setChatHistoriesOpen((current) => !current)}
              />
              {chatHistoriesOpen ? (
                scopeId ? (
                  filteredChatConversations.length > 0 ? (
                    filteredChatConversations.map((conversation: ConversationMeta) => (
                      <TreeRow
                        key={conversation.id}
                        active={selectedFile === `chat-history:${conversation.id}`}
                        icon={<MessageOutlined />}
                        indent
                        label={conversation.actorId || conversation.id}
                        meta={conversation.title || `${conversation.messageCount} messages`}
                        onClick={() => setSelectedFile(`chat-history:${conversation.id}`)}
                      />
                    ))
                  ) : (
                    <Typography.Text style={{ ...treeMetaStyle, paddingInline: 40 }}>
                      {chatConversations.isLoading
                        ? 'Loading conversations...'
                        : 'No conversations matched.'}
                    </Typography.Text>
                  )
                ) : (
                  <Typography.Text style={{ ...treeMetaStyle, paddingInline: 40 }}>
                    Resolve a project scope to browse chat histories.
                  </Typography.Text>
                )
              ) : null}
            </div>
          )}
        </section>
      </Col>

      <Col xs={24} xl={16} xxl={17} style={filesColumnStyle}>
        <section style={panelShellStyle}>
          {viewMode === 'explorer' ? (
            <ExplorerDetailPane
              content={explorer.selectedContent}
              contentErrorMessage={explorer.contentErrorMessage}
              contentLoading={explorer.contentLoading}
              onDeleteFile={explorer.deleteFile}
              onDirtyStateChange={setExplorerDirty}
              errorMessage={explorer.errorMessage}
              onOpenWorkflowInStudio={onOpenWorkflowInStudio}
              onOpenScriptInStudio={onOpenScriptInStudio}
              onSaveFile={explorer.saveFile}
              scopeId={scopeId}
              selectedEntry={explorer.selectedEntry}
            />
          ) : (
            <StudioFilesDetailPane
              selectedFile={selectedFile}
              workflows={workflows}
              workspaceSettings={workspaceSettings}
              roles={roles}
              connectors={connectors}
              settings={settings}
              scripts={scripts}
              chatConversations={chatConversations}
              scopeId={scopeId}
              workflowStorageMode={workflowStorageMode}
              scriptsEnabled={scriptsEnabled}
              onOpenWorkflowInStudio={onOpenWorkflowInStudio}
              onOpenScriptInStudio={onOpenScriptInStudio}
            />
          )}
        </section>
      </Col>
    </Row>
  );
};

export default StudioFilesPage;
