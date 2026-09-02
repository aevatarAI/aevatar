import {
  ApiOutlined,
  DeleteOutlined,
  EditOutlined,
  PlusOutlined,
  TeamOutlined,
} from '@ant-design/icons';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Alert, Button, Empty, Space, Tag } from 'antd';
import React from 'react';
import { chatHistoryApi } from '@/pages/chat/chatHistoryApi';
import type {
  ConversationMeta,
  StoredChatMessage,
} from '@/pages/chat/chatTypes';
import { formatDateTime } from '@/shared/datetime/dateTime';
import { studioApi } from '@/shared/studio/api';
import type {
  StudioConnectorCatalog,
  StudioConnectorDefinition,
  StudioRoleCatalog,
  StudioRoleDefinition,
  StudioWorkflowSummary,
} from '@/shared/studio/models';
import type { ScopedScriptDetail } from '@/shared/studio/scriptsModels';
import ConsoleOperationNotice from '@/shared/ui/ConsoleOperationNotice';
import { describeError } from '@/shared/ui/errorText';
import {
  AEVATAR_INTERACTIVE_BUTTON_CLASS,
  AEVATAR_INTERACTIVE_CHIP_CLASS,
  joinInteractiveClassNames,
} from '@/shared/ui/interactionStandards';
import { t } from "@/shared/i18n/messages";

type QueryState<T> = {
  readonly isLoading: boolean;
  readonly isError: boolean;
  readonly error: unknown;
  readonly data: T | undefined;
};

type StudioFileKey =
  | 'role-catalog'
  | 'connector-catalog'
  | `chat-history:${string}`
  | `workflow:${string}`
  | `script:${string}`;

type StudioRoleCatalogItem = {
  readonly key: string;
  readonly id: string;
  readonly name: string;
  readonly systemPrompt: string;
  readonly provider: string;
  readonly model: string;
  readonly connectorsText: string;
};

type StudioConnectorType = 'http' | 'cli' | 'mcp';

type StudioConnectorCatalogItem = {
  readonly key: string;
  readonly name: string;
  readonly type: StudioConnectorType;
  readonly enabled: boolean;
  readonly timeoutMs: string;
  readonly retry: string;
  readonly http: {
    readonly baseUrl: string;
    readonly allowedMethods: string[];
    readonly allowedPaths: string[];
    readonly allowedInputKeys: string[];
    readonly defaultHeaders: Record<string, string>;
  };
  readonly cli: {
    readonly command: string;
    readonly fixedArguments: string[];
    readonly allowedOperations: string[];
    readonly allowedInputKeys: string[];
    readonly workingDirectory: string;
    readonly environment: Record<string, string>;
  };
  readonly mcp: {
    readonly serverName: string;
    readonly command: string;
    readonly arguments: string[];
    readonly environment: Record<string, string>;
    readonly defaultTool: string;
    readonly allowedTools: string[];
    readonly allowedInputKeys: string[];
  };
};

type NoticeState = {
  readonly type: 'success' | 'info' | 'warning' | 'error';
  readonly message: string;
};

type ChatHistoryMessageView = Omit<
  StoredChatMessage,
  'error' | 'status' | 'thinking'
> & {
  readonly authorName?: string | null;
  readonly error?: string | null;
  readonly status?: string | null;
  readonly thinking?: string | null;
};

function formatChatTurnCount(count: number): string {
  const normalizedCount = Number.isFinite(count) ? Math.max(0, count) : 0;
  return normalizedCount === 1
    ? t("pages.studio.studiofilesdetailpane.conversation.turn.count.one", "1 turn")
    : t(
        "pages.studio.studiofilesdetailpane.conversation.turn.count.many",
        "{count} turns",
        { count: normalizedCount },
      );
}

function formatChatMessageAuthor(message: ChatHistoryMessageView): string {
  const authorName = message.authorName?.trim();
  if (authorName) {
    return authorName;
  }

  if (message.role === 'assistant') {
    return t("pages.studio.studiofilesdetailpane.chat.author.assistant", "Assistant");
  }

  if (message.role === 'user') {
    return t("pages.studio.studiofilesdetailpane.chat.author.user", "User");
  }

  return message.role || t("pages.studio.studiofilesdetailpane.chat.author.unknown", "Unknown author");
}

function formatChatMessageStatus(status: string | null | undefined): string {
  const normalizedStatus = status?.trim().toLowerCase();
  if (normalizedStatus === 'complete') {
    return t("pages.studio.studiofilesdetailpane.chat.status.complete", "Complete");
  }

  if (normalizedStatus === 'error') {
    return t("pages.studio.studiofilesdetailpane.chat.status.error", "Error");
  }

  return status?.trim() || t("pages.studio.studiofilesdetailpane.chat.status.unknown", "Unknown");
}

function formatScriptDetailLabel(
  detail: ScopedScriptDetail | null | undefined,
): string {
  const record = detail as
    | (ScopedScriptDetail & {
        script?: { displayName?: string | null; name?: string | null } | null;
        source?: { displayName?: string | null; name?: string | null } | null;
      })
    | null
    | undefined;
  const candidate =
    record?.script?.displayName ||
    record?.script?.name ||
    record?.source?.displayName ||
    record?.source?.name ||
    '';
  return candidate.trim() || 'Script source';
}

type Props = {
  readonly selectedFile: StudioFileKey;
  readonly workflows: QueryState<StudioWorkflowSummary[]>;
  readonly roles: QueryState<StudioRoleCatalog>;
  readonly connectors: QueryState<StudioConnectorCatalog>;
  readonly scripts: QueryState<ScopedScriptDetail[]>;
  readonly chatConversations: QueryState<ConversationMeta[]>;
  readonly scopeId: string;
  readonly scriptsEnabled: boolean;
  readonly onChatConversationDeleted: (
    scopeId: string,
    conversationId: string,
  ) => void;
  readonly onOpenWorkflowInStudio: (workflowId: string) => void;
  readonly onOpenScriptInStudio: (scriptId: string) => void;
};

type ChatDeleteOperation = {
  readonly conversationId: string;
  readonly scopeId: string;
};

const detailScrollStyle: React.CSSProperties = {
  display: 'flex',
  flex: 1,
  flexDirection: 'column',
  height: 0,
  minHeight: 0,
  overflowX: 'hidden',
  overflowY: 'auto',
  paddingRight: 4,
};

const cliEditorShellStyle: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: 24,
  width: '100%',
};

const editorHeaderRowStyle: React.CSSProperties = {
  alignItems: 'flex-start',
  display: 'flex',
  gap: 12,
  justifyContent: 'space-between',
};

const editorHeaderLabelStyle: React.CSSProperties = {
  color: 'var(--ant-color-text-tertiary)',
  fontSize: 10,
  fontWeight: 600,
  letterSpacing: 0,
  textTransform: 'uppercase',
};

const editorHeaderTitleStyle: React.CSSProperties = {
  color: '#1f2937',
  fontSize: 16,
  fontWeight: 700,
  marginTop: 4,
};

const editorHeaderDescriptionStyle: React.CSSProperties = {
  color: 'var(--ant-color-text-tertiary)',
  fontSize: 12,
  marginTop: 4,
};


const codePreviewStyle: React.CSSProperties = {
  background: '#FAFAF9',
  border: '1px solid #E6E3DE',
  borderRadius: 14,
  color: '#374151',
  fontFamily:
    'ui-monospace, SFMono-Regular, SF Mono, Menlo, Monaco, Consolas, Liberation Mono, monospace',
  fontSize: 12,
  lineHeight: 1.7,
  margin: 0,
  maxHeight: '70vh',
  overflowX: 'auto',
  overflowY: 'auto',
  padding: 16,
  whiteSpace: 'pre-wrap',
};

const primaryActionStyle: React.CSSProperties = {
  background: '#18181B',
  border: 'none',
  borderRadius: 10,
  color: '#fff',
  cursor: 'pointer',
  fontSize: 13,
  fontWeight: 600,
  padding: '10px 16px',
};

const secondaryActionStyle: React.CSSProperties = {
  alignItems: 'center',
  background: '#fff',
  border: '1px solid #E6E3DE',
  borderRadius: 10,
  color: '#4b5563',
  cursor: 'pointer',
  display: 'inline-flex',
  fontSize: 13,
  fontWeight: 500,
  gap: 6,
  padding: '10px 14px',
};

const iconActionStyle: React.CSSProperties = {
  alignItems: 'center',
  background: 'transparent',
  border: 'none',
  borderRadius: 8,
  color: '#9ca3af',
  cursor: 'pointer',
  display: 'inline-flex',
  height: 30,
  justifyContent: 'center',
  width: 30,
};

const catalogListStyle: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: 8,
};

const catalogCardStyle: React.CSSProperties = {
  alignItems: 'center',
  background: 'var(--ant-color-bg-container)',
  border: '1px solid #EEEAE4',
  borderRadius: 16,
  cursor: 'pointer',
  display: 'flex',
  gap: 16,
  padding: '16px 20px',
  transition: 'border-color 0.15s ease',
};

const catalogMetaStyle: React.CSSProperties = {
  color: 'var(--ant-color-text-tertiary)',
  fontSize: 11,
  marginTop: 4,
};

const fieldLabelStyle: React.CSSProperties = {
  color: '#6b7280',
  display: 'block',
  fontSize: 11,
  fontWeight: 600,
  letterSpacing: 0,
  marginBottom: 6,
  textTransform: 'uppercase',
};

const fieldInputStyle: React.CSSProperties = {
  background: '#fff',
  border: '1px solid #E6E3DE',
  borderRadius: 10,
  color: '#374151',
  fontSize: 13,
  outline: 'none',
  padding: '10px 12px',
  width: '100%',
};

const fieldTextareaStyle: React.CSSProperties = {
  ...fieldInputStyle,
  background: '#FAFAF9',
  fontFamily:
    'ui-monospace, SFMono-Regular, SF Mono, Menlo, Monaco, Consolas, Liberation Mono, monospace',
  lineHeight: 1.6,
  minHeight: 72,
  resize: 'vertical',
};

const drawerOverlayStyle: React.CSSProperties = {
  background: 'rgba(15, 23, 42, 0.18)',
  bottom: 0,
  display: 'flex',
  justifyContent: 'flex-end',
  left: 0,
  position: 'fixed',
  right: 0,
  top: 0,
  zIndex: 1100,
};

const drawerBackdropStyle: React.CSSProperties = {
  flex: 1,
};

const drawerSheetStyle: React.CSSProperties = {
  background: 'var(--ant-color-bg-container)',
  borderLeft: '1px solid #E6E3DE',
  boxShadow: '-18px 0 60px rgba(15, 23, 42, 0.12)',
  display: 'flex',
  flexDirection: 'column',
  height: '100%',
  maxWidth: 460,
  width: '100%',
};

const drawerHeaderStyle: React.CSSProperties = {
  alignItems: 'flex-start',
  borderBottom: '1px solid #E6E3DE',
  display: 'flex',
  gap: 12,
  justifyContent: 'space-between',
  padding: '20px 24px 18px',
};

const drawerBodyStyle: React.CSSProperties = {
  display: 'flex',
  flex: 1,
  flexDirection: 'column',
  gap: 16,
  overflowY: 'auto',
  padding: 24,
};

const twoColumnGridStyle: React.CSSProperties = {
  display: 'grid',
  gap: 12,
  gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
};

const toggleRowStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  gap: 10,
};

const toggleTrackStyle: React.CSSProperties = {
  borderRadius: 999,
  cursor: 'pointer',
  height: 22,
  position: 'relative',
  width: 42,
};

const emptyCardStyle: React.CSSProperties = {
  alignItems: 'center',
  background: 'var(--ant-color-bg-container)',
  border: '1px dashed #E6E3DE',
  borderRadius: 16,
  color: 'var(--ant-color-text-tertiary)',
  display: 'flex',
  flexDirection: 'column',
  gap: 8,
  justifyContent: 'center',
  minHeight: 140,
  padding: 32,
  textAlign: 'center',
};

const chatMessageListStyle: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: 12,
};

const chatMessageCardStyle: React.CSSProperties = {
  background: 'var(--ant-color-bg-container)',
  border: '1px solid #EEEAE4',
  borderRadius: 18,
  padding: '16px 18px',
};

const chatMessageMetaStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  fontSize: 11,
  justifyContent: 'space-between',
  marginBottom: 10,
};

let filesLocalKeyCounter = 0;

function createLocalKey(prefix: string): string {
  const randomUuid = globalThis.crypto?.randomUUID?.();
  if (randomUuid) {
    return `${prefix}_${randomUuid}`;
  }

  filesLocalKeyCounter += 1;
  return `${prefix}_${Date.now().toString(36)}_${filesLocalKeyCounter.toString(36)}`;
}

function trimText(value: unknown): string {
  return String(value ?? '').trim();
}

function splitCatalogLines(value: string): string[] {
  return String(value || '')
    .split(/\r?\n|,/)
    .map((item) => item.trim())
    .filter(Boolean);
}

function normalizeCatalogInteger(value: string, fallback: number): number {
  const parsed = Number.parseInt(String(value || '').trim(), 10);
  return Number.isFinite(parsed) ? parsed : fallback;
}

function formatMapText(value: Record<string, string> | null | undefined): string {
  return Object.entries(value ?? {})
    .map(([key, item]) => `${key}: ${item}`)
    .join('\n');
}

function parseMapText(value: string): Record<string, string> {
  const result: Record<string, string> = {};
  for (const line of String(value || '').split(/\r?\n/)) {
    const trimmed = line.trim();
    if (!trimmed) {
      continue;
    }

    const separatorIndex = trimmed.indexOf(':');
    if (separatorIndex < 0) {
      result[trimmed] = '';
      continue;
    }

    const key = trimmed.slice(0, separatorIndex).trim();
    const nextValue = trimmed.slice(separatorIndex + 1).trim();
    if (!key) {
      continue;
    }

    result[key] = nextValue;
  }

  return result;
}

function createRoleCatalogItem(role: StudioRoleDefinition): StudioRoleCatalogItem {
  return {
    key: createLocalKey('role'),
    id: role.id || '',
    name: role.name || role.id || '',
    systemPrompt: role.systemPrompt || '',
    provider: role.provider || '',
    model: role.model || '',
    connectorsText: Array.isArray(role.connectors)
      ? role.connectors.join('\n')
      : '',
  };
}

function toRoleDefinition(role: StudioRoleCatalogItem): StudioRoleDefinition {
  return {
    id: trimText(role.id),
    name: trimText(role.name) || trimText(role.id),
    systemPrompt: role.systemPrompt || '',
    provider: trimText(role.provider),
    model: trimText(role.model),
    connectors: splitCatalogLines(role.connectorsText),
  };
}

function createUniqueRoleId(
  existingRoles: readonly StudioRoleCatalogItem[],
  base = 'role',
): string {
  const normalizedBase =
    (base || 'role').replace(/[^a-z0-9_]+/gi, '_').toLowerCase() || 'role';
  const used = new Set(
    existingRoles.map((role) => role.id.trim().toLowerCase()).filter(Boolean),
  );
  let index = 1;
  let candidate = normalizedBase;

  while (used.has(candidate)) {
    index += 1;
    candidate = `${normalizedBase}_${index}`;
  }

  return candidate;
}

function createEmptyConnectorDraft(
  type: StudioConnectorType = 'http',
  name = '',
): StudioConnectorCatalogItem {
  return {
    key: createLocalKey('connector'),
    name,
    type,
    enabled: true,
    timeoutMs: '30000',
    retry: '0',
    http: {
      baseUrl: '',
      allowedMethods: ['POST'],
      allowedPaths: ['/'],
      allowedInputKeys: [],
      defaultHeaders: {},
    },
    cli: {
      command: '',
      fixedArguments: [],
      allowedOperations: [],
      allowedInputKeys: [],
      workingDirectory: '',
      environment: {},
    },
    mcp: {
      serverName: '',
      command: '',
      arguments: [],
      environment: {},
      defaultTool: '',
      allowedTools: [],
      allowedInputKeys: [],
    },
  };
}

function toConnectorCatalogItem(
  connector: StudioConnectorDefinition,
): StudioConnectorCatalogItem {
  const empty = createEmptyConnectorDraft(
    (connector.type || 'http') as StudioConnectorType,
  );

  return {
    key: createLocalKey('connector'),
    name: connector.name || '',
    type: (connector.type || 'http') as StudioConnectorType,
    enabled: connector.enabled !== false,
    timeoutMs: String(connector.timeoutMs ?? 30000),
    retry: String(connector.retry ?? 0),
    http: {
      baseUrl: connector.http?.baseUrl ?? empty.http.baseUrl,
      allowedMethods:
        connector.http?.allowedMethods ?? empty.http.allowedMethods,
      allowedPaths: connector.http?.allowedPaths ?? empty.http.allowedPaths,
      allowedInputKeys:
        connector.http?.allowedInputKeys ?? empty.http.allowedInputKeys,
      defaultHeaders:
        connector.http?.defaultHeaders ?? empty.http.defaultHeaders,
    },
    cli: {
      command: connector.cli?.command ?? empty.cli.command,
      fixedArguments:
        connector.cli?.fixedArguments ?? empty.cli.fixedArguments,
      allowedOperations:
        connector.cli?.allowedOperations ?? empty.cli.allowedOperations,
      allowedInputKeys:
        connector.cli?.allowedInputKeys ?? empty.cli.allowedInputKeys,
      workingDirectory:
        connector.cli?.workingDirectory ?? empty.cli.workingDirectory,
      environment: connector.cli?.environment ?? empty.cli.environment,
    },
    mcp: {
      serverName: connector.mcp?.serverName ?? empty.mcp.serverName,
      command: connector.mcp?.command ?? empty.mcp.command,
      arguments: connector.mcp?.arguments ?? empty.mcp.arguments,
      environment: connector.mcp?.environment ?? empty.mcp.environment,
      defaultTool: connector.mcp?.defaultTool ?? empty.mcp.defaultTool,
      allowedTools: connector.mcp?.allowedTools ?? empty.mcp.allowedTools,
      allowedInputKeys:
        connector.mcp?.allowedInputKeys ?? empty.mcp.allowedInputKeys,
    },
  };
}

function toConnectorDefinition(
  connector: StudioConnectorCatalogItem,
): StudioConnectorDefinition {
  return {
    name: trimText(connector.name),
    type: connector.type,
    enabled: connector.enabled,
    timeoutMs: normalizeCatalogInteger(connector.timeoutMs, 30000),
    retry: normalizeCatalogInteger(connector.retry, 0),
    http: {
      baseUrl: trimText(connector.http.baseUrl),
      allowedMethods: connector.http.allowedMethods
        .map((item) => item.trim().toUpperCase())
        .filter(Boolean),
      allowedPaths: connector.http.allowedPaths
        .map((item) => item.trim())
        .filter(Boolean),
      allowedInputKeys: connector.http.allowedInputKeys
        .map((item) => item.trim())
        .filter(Boolean),
      defaultHeaders: connector.http.defaultHeaders,
    },
    cli: {
      command: trimText(connector.cli.command),
      fixedArguments: connector.cli.fixedArguments
        .map((item) => item.trim())
        .filter(Boolean),
      allowedOperations: connector.cli.allowedOperations
        .map((item) => item.trim())
        .filter(Boolean),
      allowedInputKeys: connector.cli.allowedInputKeys
        .map((item) => item.trim())
        .filter(Boolean),
      workingDirectory: trimText(connector.cli.workingDirectory),
      environment: connector.cli.environment,
    },
    mcp: {
      serverName: trimText(connector.mcp.serverName),
      command: trimText(connector.mcp.command),
      arguments: connector.mcp.arguments
        .map((item) => item.trim())
        .filter(Boolean),
      environment: connector.mcp.environment,
      defaultTool: trimText(connector.mcp.defaultTool),
      allowedTools: connector.mcp.allowedTools
        .map((item) => item.trim())
        .filter(Boolean),
      allowedInputKeys: connector.mcp.allowedInputKeys
        .map((item) => item.trim())
        .filter(Boolean),
    },
  };
}

function createUniqueConnectorName(
  connectors: readonly StudioConnectorCatalogItem[],
  type: StudioConnectorType,
): string {
  const used = new Set(
    connectors.map((connector) => connector.name.trim().toLowerCase()),
  );
  const base = `${type}_connector`;
  let index = 1;
  let candidate = base;

  while (used.has(candidate.toLowerCase())) {
    index += 1;
    candidate = `${base}_${index}`;
  }

  return candidate;
}

function FilesDrawer(props: {
  readonly open: boolean;
  readonly title: string;
  readonly onClose: () => void;
  readonly children: React.ReactNode;
}) {
  if (!props.open) {
    return null;
  }

  return (
    <div style={drawerOverlayStyle}>
      <div style={drawerBackdropStyle} onClick={props.onClose} />
      <div
        role="dialog"
        aria-modal="true"
        aria-label={props.title}
        style={drawerSheetStyle}
      >
        <div style={drawerHeaderStyle}>
          <div>
            <div style={editorHeaderLabelStyle}>{t("pages.studio.studiofilesdetailpane.catalog", "Catalog")}</div>
            <div style={editorHeaderTitleStyle}>{props.title}</div>
          </div>
          <button
            className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
            type="button"
            onClick={props.onClose}
            style={secondaryActionStyle}
          >
            {t("pages.studio.studiofilesdetailpane.close", "Close")}</button>
        </div>
        <div style={drawerBodyStyle}>{props.children}</div>
      </div>
    </div>
  );
}

function FieldInput(props: {
  readonly label: string;
  readonly value: string;
  readonly onChange: (value: string) => void;
  readonly placeholder?: string;
  readonly mono?: boolean;
}) {
  return (
    <label>
      <span style={fieldLabelStyle}>{props.label}</span>
      <input
        value={props.value}
        onChange={(event) => props.onChange(event.target.value)}
        placeholder={props.placeholder}
        style={{
          ...fieldInputStyle,
          fontFamily: props.mono
            ? 'ui-monospace, SFMono-Regular, SF Mono, Menlo, Monaco, Consolas, Liberation Mono, monospace'
            : fieldInputStyle.fontFamily,
        }}
      />
    </label>
  );
}

function FieldTextArea(props: {
  readonly label: string;
  readonly value: string;
  readonly onChange: (value: string) => void;
  readonly placeholder?: string;
  readonly rows?: number;
}) {
  return (
    <label>
      <span style={fieldLabelStyle}>{props.label}</span>
      <textarea
        value={props.value}
        onChange={(event) => props.onChange(event.target.value)}
        placeholder={props.placeholder}
        rows={props.rows}
        style={fieldTextareaStyle}
      />
    </label>
  );
}

const StudioFilesDetailPane: React.FC<Props> = ({
  selectedFile,
  workflows,
  roles,
  connectors,
  scripts,
  chatConversations,
  scopeId,
  scriptsEnabled,
  onChatConversationDeleted,
  onOpenWorkflowInStudio,
  onOpenScriptInStudio,
}) => {
  const queryClient = useQueryClient();
  const scopeIdRef = React.useRef(scopeId);
  scopeIdRef.current = scopeId;
  const chatDeleteOperationRef = React.useRef<ChatDeleteOperation | null>(null);
  const selectedConversationIdRef = React.useRef("");

  const [roleCatalogDraft, setRoleCatalogDraft] = React.useState<
    StudioRoleCatalogItem[]
  >([]);
  const [rolePending, setRolePending] = React.useState(false);
  const [roleNotice, setRoleNotice] = React.useState<NoticeState | null>(null);
  const [editingRoleKey, setEditingRoleKey] = React.useState<string | null>(null);

  const [connectorCatalogDraft, setConnectorCatalogDraft] = React.useState<
    StudioConnectorCatalogItem[]
  >([]);
  const [connectorPending, setConnectorPending] = React.useState(false);
  const [connectorNotice, setConnectorNotice] =
    React.useState<NoticeState | null>(null);
  const [editingConnectorKey, setEditingConnectorKey] = React.useState<
    string | null
  >(null);
  const [connectorAddMenuOpen, setConnectorAddMenuOpen] =
    React.useState(false);
  const [chatNotice, setChatNotice] = React.useState<NoticeState | null>(null);
  const [chatDeleteConfirmationOpen, setChatDeleteConfirmationOpen] =
    React.useState(false);
  const [chatDeletePending, setChatDeletePending] = React.useState(false);

  React.useEffect(() => {
    setRoleCatalogDraft((roles.data?.roles ?? []).map(createRoleCatalogItem));
  }, [roles.data]);

  React.useEffect(() => {
    setConnectorCatalogDraft(
      (connectors.data?.connectors ?? []).map(toConnectorCatalogItem),
    );
  }, [connectors.data]);

  const roleCatalogDirty = React.useMemo(
    () =>
      JSON.stringify(roleCatalogDraft.map(toRoleDefinition)) !==
      JSON.stringify(roles.data?.roles ?? []),
    [roleCatalogDraft, roles.data?.roles],
  );

  const connectorCatalogDirty = React.useMemo(
    () =>
      JSON.stringify(connectorCatalogDraft.map(toConnectorDefinition)) !==
      JSON.stringify(connectors.data?.connectors ?? []),
    [connectorCatalogDraft, connectors.data?.connectors],
  );

  const editingRole =
    roleCatalogDraft.find((role) => role.key === editingRoleKey) ?? null;
  const editingConnector =
    connectorCatalogDraft.find(
      (connector) => connector.key === editingConnectorKey,
    ) ?? null;

  const connectorNames = React.useMemo(
    () =>
      connectorCatalogDraft
        .map((connector) => connector.name.trim())
        .filter(Boolean),
    [connectorCatalogDraft],
  );

  const selectedWorkflowId = selectedFile.startsWith('workflow:')
    ? selectedFile.slice('workflow:'.length)
    : '';
  const selectedWorkflowSummary =
    workflows.data?.find((workflow) => workflow.workflowId === selectedWorkflowId) ??
    null;
  const workflowFile = useQuery({
    queryKey: ['studio-files-workflow', scopeId || 'workspace', selectedWorkflowId],
    enabled: Boolean(selectedWorkflowId),
    queryFn: () => studioApi.getWorkflow(selectedWorkflowId, scopeId || undefined),
  });

  const selectedScriptId = selectedFile.startsWith('script:')
    ? selectedFile.slice('script:'.length)
    : '';
  const selectedScriptDetail =
    scripts.data?.find((detail) => detail.script?.scriptId === selectedScriptId) ??
    null;
  const selectedConversationId = selectedFile.startsWith('chat-history:')
    ? selectedFile.slice('chat-history:'.length)
    : '';
  selectedConversationIdRef.current = selectedConversationId;
  const selectedConversationMeta =
    chatConversations.data?.find(
      (conversation) => conversation.id === selectedConversationId,
    ) ?? null;
  const selectedConversationMessages = useQuery({
    queryKey: ['studio-files-chat-history', scopeId, selectedConversationId],
    enabled: Boolean(scopeId && selectedConversationId),
    queryFn: () => chatHistoryApi.loadConversation(scopeId, selectedConversationId),
  });

  React.useEffect(() => {
    setChatDeleteConfirmationOpen(false);
    setChatNotice(null);
    if (chatDeleteOperationRef.current?.scopeId !== scopeId) {
      chatDeleteOperationRef.current = null;
      setChatDeletePending(false);
    }
  }, [scopeId, selectedConversationId]);

  const handleAddRole = () => {
    const nextRole: StudioRoleCatalogItem = {
      key: createLocalKey('role'),
      id: createUniqueRoleId(roleCatalogDraft),
      name: '',
      systemPrompt: '',
      provider: '',
      model: '',
      connectorsText: '',
    };
    setRoleCatalogDraft((current) => [...current, nextRole]);
    setEditingRoleKey(nextRole.key);
    setRoleNotice(null);
  };

  const handleSaveRoles = async () => {
    setRolePending(true);
    setRoleNotice(null);
    try {
      const response = await studioApi.saveRoleCatalog({
        roles: roleCatalogDraft.map(toRoleDefinition),
      });
      queryClient.setQueryData(['studio-roles'], response);
      setRoleCatalogDraft(response.roles.map(createRoleCatalogItem));
      setRoleNotice({
        type: 'success',
        message: t("pages.studio.studiofilesdetailpane.role.catalog.saved", "Role catalog saved."),
      });
    } catch (error) {
      setRoleNotice({
        type: 'error',
        message:
          error instanceof Error
            ? error.message
            : 'Failed to save role catalog.',
      });
    } finally {
      setRolePending(false);
    }
  };

  const handleAddConnector = (type: StudioConnectorType) => {
    const nextConnector = createEmptyConnectorDraft(
      type,
      createUniqueConnectorName(connectorCatalogDraft, type),
    );
    setConnectorCatalogDraft((current) => [...current, nextConnector]);
    setConnectorAddMenuOpen(false);
    setEditingConnectorKey(nextConnector.key);
    setConnectorNotice(null);
  };

  const handleSaveConnectors = async () => {
    setConnectorPending(true);
    setConnectorNotice(null);
    try {
      const response = await studioApi.saveConnectorCatalog({
        connectors: connectorCatalogDraft.map(toConnectorDefinition),
      });
      queryClient.setQueryData(['studio-connectors'], response);
      setConnectorCatalogDraft(response.connectors.map(toConnectorCatalogItem));
      setConnectorNotice({
        type: 'success',
        message: t("pages.studio.studiofilesdetailpane.connector.catalog.saved", "Connector catalog saved."),
      });
    } catch (error) {
      setConnectorNotice({
        type: 'error',
        message:
          error instanceof Error
            ? error.message
            : 'Failed to save connector catalog.',
      });
    } finally {
      setConnectorPending(false);
    }
  };

  const handleDeleteConversation = async () => {
    if (!scopeId || !selectedConversationId || chatDeleteOperationRef.current) {
      return;
    }

    setChatNotice(null);
    setChatDeletePending(true);
    const operation: ChatDeleteOperation = {
      conversationId: selectedConversationId,
      scopeId,
    };
    chatDeleteOperationRef.current = operation;

    try {
      await chatHistoryApi.deleteConversation(
        operation.scopeId,
        operation.conversationId,
      );
      if (scopeIdRef.current !== operation.scopeId) {
        return;
      }
      await queryClient.cancelQueries({
        queryKey: ['studio-files-chat-histories', operation.scopeId],
      });
      onChatConversationDeleted(operation.scopeId, operation.conversationId);
      await queryClient.invalidateQueries({
        queryKey: ['studio-files-chat-histories', operation.scopeId],
      });
      queryClient.removeQueries({
        queryKey: [
          'studio-files-chat-history',
          operation.scopeId,
          operation.conversationId,
        ],
      });
      if (selectedConversationIdRef.current === operation.conversationId) {
        setChatNotice({
          type: 'success',
          message: t("pages.studio.studiofilesdetailpane.conversation.deleted", "Conversation deleted."),
        });
        setChatDeleteConfirmationOpen(false);
      }
    } catch (error) {
      if (
        scopeIdRef.current !== operation.scopeId ||
        selectedConversationIdRef.current !== operation.conversationId
      ) {
        return;
      }
      setChatNotice({
        type: 'error',
        message:
          error instanceof Error
            ? error.message
            : t("pages.studio.studiofilesdetailpane.failed.to.delete.conversation", "Failed to delete conversation."),
      });
    } finally {
      if (chatDeleteOperationRef.current === operation) {
        chatDeleteOperationRef.current = null;
        setChatDeletePending(false);
      }
    }
  };

  const renderRolesPanel = () => (
    <div style={detailScrollStyle}>
      <div style={cliEditorShellStyle}>
        <div style={editorHeaderRowStyle}>
          <div>
            <div style={editorHeaderLabelStyle}>{t("pages.studio.studiofilesdetailpane.role.catalog.2", "role-catalog")}</div>
            <div style={editorHeaderTitleStyle}>{t("pages.studio.studiofilesdetailpane.role.catalog", "Role Catalog")}</div>
          </div>
          <div style={{ display: 'flex', gap: 8 }}>
            <button
              className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
              type="button"
              onClick={handleAddRole}
              style={secondaryActionStyle}
            >
              <PlusOutlined />
              {t("pages.studio.studiofilesdetailpane.add.role", "Add Role")}</button>
            <button
              aria-disabled={!roleCatalogDirty || rolePending}
              className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
              type="button"
              disabled={!roleCatalogDirty || rolePending}
              onClick={() => void handleSaveRoles()}
              style={{
                ...primaryActionStyle,
                opacity: !roleCatalogDirty || rolePending ? 0.3 : 1,
            }}
          >
              {rolePending
                ? t("pages.studio.studiofilesdetailpane.saving", "Saving...")
                : t("pages.studio.studiofilesdetailpane.save", "Save")}
            </button>
          </div>
        </div>

        <ConsoleOperationNotice
          errorMessage={t(
            'pages.studio.studiofilesdetailpane.roleSaveFailed',
            'Could not save role catalog. Try again.',
          )}
          notice={roleNotice}
        />

        {roles.isError ? (
          <Alert
            type="error"
            showIcon
            message={t("pages.studio.studiofilesdetailpane.failed.to.load.role.catalog", "Failed to load role catalog")}
            description={describeError(roles.error)}
          />
        ) : roles.isLoading ? (
          <div style={emptyCardStyle}>{t("pages.studio.studiofilesdetailpane.loading.roles", "Loading roles...")}</div>
        ) : roleCatalogDraft.length === 0 ? (
          <div style={emptyCardStyle}>{t("pages.studio.studiofilesdetailpane.no.roles.defined", "No roles defined")}</div>
        ) : (
          <div style={catalogListStyle}>
            {roleCatalogDraft.map((role) => (
              <div
                key={role.key}
                onClick={() => setEditingRoleKey(role.key)}
                style={catalogCardStyle}
              >
                <TeamOutlined style={{ color: '#8b5cf6', fontSize: 18 }} />
                <div style={{ flex: 1, minWidth: 0 }}>
                  <div style={{ color: '#1f2937', fontSize: 14, fontWeight: 600 }}>
                    {role.name || role.id || t("pages.studio.studiofilesdetailpane.role", "Role")}
                  </div>
                  <div style={catalogMetaStyle}>
                    <span
                      style={{
                        fontFamily:
                          'ui-monospace, SFMono-Regular, SF Mono, Menlo, Monaco, Consolas, Liberation Mono, monospace',
                      }}
                    >
                      {role.id || 'role_id'}
                    </span>
                    {role.model ? t("pages.studio.studiofilesdetailpane.copy", "/ {value1}", { value1: role.model }) : ''}
                    {role.connectorsText
                      ? t("pages.studio.studiofilesdetailpane.connector.count.suffix", " / {count} connector(s)", {
                          count: splitCatalogLines(role.connectorsText).length,
                        })
                      : ''}
                  </div>
                </div>
                <button
                  className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                  type="button"
                  onClick={(event) => {
                    event.stopPropagation();
                    setRoleCatalogDraft((current) =>
                      current.filter((item) => item.key !== role.key),
                    );
                    if (editingRoleKey === role.key) {
                      setEditingRoleKey(null);
                    }
                  }}
                  style={iconActionStyle}
                  title={t("pages.studio.studiofilesdetailpane.delete.role", "Delete role")}
                >
                  <DeleteOutlined />
                </button>
              </div>
            ))}
          </div>
        )}
      </div>

      <FilesDrawer
        open={Boolean(editingRole)}
        title={
          editingRole
            ? t("pages.studio.studiofilesdetailpane.edit.role.named", "Edit Role: {name}", {
                name:
                  editingRole.name ||
                  editingRole.id ||
                  t("pages.studio.studiofilesdetailpane.role", "Role"),
              })
            : t("pages.studio.studiofilesdetailpane.edit.role", "Edit Role")
        }
        onClose={() => setEditingRoleKey(null)}
      >
        {editingRole ? (
          <>
            <div style={twoColumnGridStyle}>
              <FieldInput
                label={t("pages.studio.studiofilesdetailpane.role.id", "Role ID")}
                value={editingRole.id}
                mono
                onChange={(value) =>
                  setRoleCatalogDraft((current) =>
                    current.map((item) =>
                      item.key === editingRole.key
                        ? {
                            ...item,
                            id: value,
                          }
                        : item,
                    ),
                  )
                }
              />
              <FieldInput
                label={t("pages.studio.studiofilesdetailpane.name", "Name")}
                value={editingRole.name}
                onChange={(value) =>
                  setRoleCatalogDraft((current) =>
                    current.map((item) =>
                      item.key === editingRole.key
                        ? {
                            ...item,
                            name: value,
                          }
                        : item,
                    ),
                  )
                }
              />
            </div>

            <div style={twoColumnGridStyle}>
              <FieldInput
                label={t("pages.studio.studiofilesdetailpane.provider", "Provider")}
                value={editingRole.provider}
                placeholder={t("pages.studio.studiofilesdetailpane.default", "Default")}
                onChange={(value) =>
                  setRoleCatalogDraft((current) =>
                    current.map((item) =>
                      item.key === editingRole.key
                        ? {
                            ...item,
                            provider: value,
                          }
                        : item,
                    ),
                  )
                }
              />
              <FieldInput
                label={t("pages.studio.studiofilesdetailpane.model", "Model")}
                value={editingRole.model}
                placeholder={t("pages.studio.studiofilesdetailpane.default", "Default")}
                onChange={(value) =>
                  setRoleCatalogDraft((current) =>
                    current.map((item) =>
                      item.key === editingRole.key
                        ? {
                            ...item,
                            model: value,
                          }
                        : item,
                    ),
                  )
                }
              />
            </div>

            <FieldTextArea
              label={t("pages.studio.studiofilesdetailpane.system.prompt", "System Prompt")}
              value={editingRole.systemPrompt}
              rows={7}
              onChange={(value) =>
                setRoleCatalogDraft((current) =>
                  current.map((item) =>
                    item.key === editingRole.key
                      ? {
                          ...item,
                          systemPrompt: value,
                        }
                      : item,
                  ),
                )
              }
            />

            <div>
              <div style={fieldLabelStyle}>{t("pages.studio.studiofilesdetailpane.connectors", "Connectors")}</div>
              {connectorNames.length > 0 ? (
                <div style={{ display: 'flex', flexWrap: 'wrap', gap: 8 }}>
                  {connectorNames.map((name) => {
                    const active = splitCatalogLines(
                      editingRole.connectorsText,
                    ).includes(name);
                    return (
                      <button
                        aria-pressed={active}
                        className={joinInteractiveClassNames(
                          AEVATAR_INTERACTIVE_BUTTON_CLASS,
                          AEVATAR_INTERACTIVE_CHIP_CLASS,
                        )}
                        key={`${editingRole.key}:${name}`}
                        type="button"
                        onClick={() =>
                          setRoleCatalogDraft((current) =>
                            current.map((item) => {
                              if (item.key !== editingRole.key) {
                                return item;
                              }

                              const nextValues = new Set(
                                splitCatalogLines(item.connectorsText),
                              );
                              if (nextValues.has(name)) {
                                nextValues.delete(name);
                              } else {
                                nextValues.add(name);
                              }

                              return {
                                ...item,
                                connectorsText: Array.from(nextValues).join('\n'),
                              };
                            }),
                          )
                        }
                        style={{
                          border: `1px solid ${active ? '#bbf7d0' : '#E6E3DE'}`,
                          borderRadius: 999,
                          background: active ? '#f0fdf4' : '#fff',
                          color: active ? '#15803d' : '#6b7280',
                          cursor: 'pointer',
                          fontSize: 12,
                          fontWeight: 500,
                          padding: '7px 12px',
                        }}
                      >
                        {name}
                      </button>
                    );
                  })}
                </div>
              ) : (
                <FieldTextArea
                  label="Connectors"
                  value={editingRole.connectorsText}
                  rows={4}
                  onChange={(value) =>
                    setRoleCatalogDraft((current) =>
                      current.map((item) =>
                        item.key === editingRole.key
                          ? {
                              ...item,
                              connectorsText: value,
                            }
                          : item,
                      ),
                    )
                  }
                />
              )}
            </div>
          </>
        ) : null}
      </FilesDrawer>
    </div>
  );

  const renderConnectorsPanel = () => (
    <div style={detailScrollStyle}>
      <div style={cliEditorShellStyle}>
        <div style={editorHeaderRowStyle}>
          <div>
            <div style={editorHeaderLabelStyle}>{t("pages.studio.studiofilesdetailpane.connector.catalog.2", "connector-catalog")}</div>
            <div style={editorHeaderTitleStyle}>{t("pages.studio.studiofilesdetailpane.connector.catalog", "Connector Catalog")}</div>
          </div>
          <div style={{ display: 'flex', gap: 8 }}>
            <div style={{ position: 'relative' }}>
              <button
                className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                type="button"
                onClick={() => setConnectorAddMenuOpen((current) => !current)}
                style={secondaryActionStyle}
              >
                <PlusOutlined />
                {t("pages.studio.studiofilesdetailpane.add", "Add")}</button>
              {connectorAddMenuOpen ? (
                <div
                  style={{
                    background: '#fff',
                    border: '1px solid #E6E3DE',
                    borderRadius: 14,
                    boxShadow: '0 16px 40px rgba(15, 23, 42, 0.08)',
                    display: 'flex',
                    flexDirection: 'column',
                    gap: 4,
                    padding: 6,
                    position: 'absolute',
                    right: 0,
                    top: 'calc(100% + 6px)',
                    width: 152,
                    zIndex: 10,
                  }}
                >
                  {(['http', 'cli', 'mcp'] as const).map((type) => (
                    <button
                      className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                      key={type}
                      type="button"
                      onClick={() => handleAddConnector(type)}
                      style={{
                        ...secondaryActionStyle,
                        border: 'none',
                        justifyContent: 'flex-start',
                        padding: '8px 10px',
                      }}
                    >
                      {type.toUpperCase()}
                    </button>
                  ))}
                </div>
              ) : null}
            </div>
            <button
              aria-disabled={!connectorCatalogDirty || connectorPending}
              className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
              type="button"
              disabled={!connectorCatalogDirty || connectorPending}
              onClick={() => void handleSaveConnectors()}
              style={{
                ...primaryActionStyle,
                opacity: !connectorCatalogDirty || connectorPending ? 0.3 : 1,
            }}
          >
              {connectorPending
                ? t("pages.studio.studiofilesdetailpane.saving", "Saving...")
                : t("pages.studio.studiofilesdetailpane.save", "Save")}
            </button>
          </div>
        </div>

        <ConsoleOperationNotice
          errorMessage={t(
            'pages.studio.studiofilesdetailpane.connectorSaveFailed',
            'Could not save connector catalog. Try again.',
          )}
          notice={connectorNotice}
        />

        {connectors.isError ? (
          <Alert
            type="error"
            showIcon
            message={t("pages.studio.studiofilesdetailpane.failed.to.load.connector.catalog", "Failed to load connector catalog")}
            description={describeError(connectors.error)}
          />
        ) : connectors.isLoading ? (
          <div style={emptyCardStyle}>{t("pages.studio.studiofilesdetailpane.loading.connectors", "Loading connectors...")}</div>
        ) : connectorCatalogDraft.length === 0 ? (
          <div style={emptyCardStyle}>{t("pages.studio.studiofilesdetailpane.no.connectors.defined", "No connectors defined")}</div>
        ) : (
          <div style={catalogListStyle}>
            {connectorCatalogDraft.map((connector) => {
              const preview =
                connector.type === 'http'
                  ? connector.http.baseUrl
                  : connector.type === 'cli'
                    ? connector.cli.command
                    : connector.mcp.serverName || connector.mcp.command;

              return (
                <div
                  key={connector.key}
                  onClick={() => setEditingConnectorKey(connector.key)}
                  style={catalogCardStyle}
                >
                  <ApiOutlined style={{ color: '#10b981', fontSize: 18 }} />
                  <div style={{ flex: 1, minWidth: 0 }}>
                    <div
                      style={{
                        alignItems: 'center',
                        display: 'flex',
                        gap: 8,
                        minWidth: 0,
                      }}
                    >
                      <div
                        style={{
                          color: '#1f2937',
                          fontSize: 14,
                          fontWeight: 600,
                          overflow: 'hidden',
                          textOverflow: 'ellipsis',
                          whiteSpace: 'nowrap',
                        }}
                      >
                        {connector.name || t("pages.studio.studiofilesdetailpane.connector", "Connector")}
                      </div>
                      <Tag color="processing" style={{ marginInlineEnd: 0 }}>
                        {connector.type}
                      </Tag>
                      {!connector.enabled ? <Tag>{t("pages.studio.studiofilesdetailpane.disabled", "disabled")}</Tag> : null}
                    </div>
                    <div style={catalogMetaStyle}>
                      {preview ||
                        t("pages.studio.studiofilesdetailpane.no.target.configured", "No target configured")}
                    </div>
                  </div>
                  <button
                    className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                    type="button"
                    onClick={(event) => {
                      event.stopPropagation();
                      setConnectorCatalogDraft((current) =>
                        current.filter((item) => item.key !== connector.key),
                      );
                      if (editingConnectorKey === connector.key) {
                        setEditingConnectorKey(null);
                      }
                    }}
                    style={iconActionStyle}
                    title={t("pages.studio.studiofilesdetailpane.delete.connector", "Delete connector")}
                  >
                    <DeleteOutlined />
                  </button>
                </div>
              );
            })}
          </div>
        )}
      </div>

      <FilesDrawer
        open={Boolean(editingConnector)}
        title={
          editingConnector
            ? t("pages.studio.studiofilesdetailpane.edit.connector.named", "Edit Connector: {name}", {
                name:
                  editingConnector.name ||
                  t("pages.studio.studiofilesdetailpane.connector", "Connector"),
              })
            : t("pages.studio.studiofilesdetailpane.edit.connector", "Edit Connector")
        }
        onClose={() => setEditingConnectorKey(null)}
      >
        {editingConnector ? (
          <>
            <div style={twoColumnGridStyle}>
              <FieldInput
                label={t("pages.studio.studiofilesdetailpane.name", "Name")}
                value={editingConnector.name}
                mono
                onChange={(value) =>
                  setConnectorCatalogDraft((current) =>
                    current.map((item) =>
                      item.key === editingConnector.key
                        ? {
                            ...item,
                            name: value,
                          }
                        : item,
                    ),
                  )
                }
              />
              <label>
                <span style={fieldLabelStyle}>{t("pages.studio.studiofilesdetailpane.type", "Type")}</span>
                <select
                  value={editingConnector.type}
                  onChange={(event) =>
                    setConnectorCatalogDraft((current) =>
                      current.map((item) =>
                        item.key === editingConnector.key
                          ? {
                              ...item,
                              type: event.target.value as StudioConnectorType,
                            }
                          : item,
                      ),
                    )
                  }
                  style={fieldInputStyle}
                >
                  <option value="http">{t("pages.studio.studiofilesdetailpane.http", "HTTP")}</option>
                  <option value="cli">{t("pages.studio.studiofilesdetailpane.cli", "CLI")}</option>
                  <option value="mcp">{t("pages.studio.studiofilesdetailpane.mcp", "MCP")}</option>
                </select>
              </label>
            </div>

            <div style={twoColumnGridStyle}>
              <div style={toggleRowStyle}>
                <span style={fieldLabelStyle}>{t("pages.studio.studiofilesdetailpane.enabled", "Enabled")}</span>
                <button
                  aria-pressed={editingConnector.enabled}
                  className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                  type="button"
                  onClick={() =>
                    setConnectorCatalogDraft((current) =>
                      current.map((item) =>
                        item.key === editingConnector.key
                          ? {
                              ...item,
                              enabled: !item.enabled,
                            }
                          : item,
                      ),
                    )
                  }
                  style={{
                    ...toggleTrackStyle,
                    background: editingConnector.enabled ? '#22c55e' : '#d1d5db',
                  }}
                >
                  <span
                    style={{
                      background: '#fff',
                      borderRadius: 999,
                      boxShadow: '0 1px 3px rgba(15, 23, 42, 0.2)',
                      height: 18,
                      left: editingConnector.enabled ? 21 : 2,
                      position: 'absolute',
                      top: 2,
                      transition: 'left 0.15s ease',
                      width: 18,
                    }}
                  />
                </button>
              </div>
              <div style={twoColumnGridStyle}>
                <FieldInput
                  label={t("pages.studio.studiofilesdetailpane.timeout.ms", "Timeout (ms)")}
                  mono
                  value={editingConnector.timeoutMs}
                  onChange={(value) =>
                    setConnectorCatalogDraft((current) =>
                      current.map((item) =>
                        item.key === editingConnector.key
                          ? {
                              ...item,
                              timeoutMs: value,
                            }
                          : item,
                      ),
                    )
                  }
                />
                <FieldInput
                  label={t("pages.studio.studiofilesdetailpane.retry", "Retry")}
                  mono
                  value={editingConnector.retry}
                  onChange={(value) =>
                    setConnectorCatalogDraft((current) =>
                      current.map((item) =>
                        item.key === editingConnector.key
                          ? {
                              ...item,
                              retry: value,
                            }
                          : item,
                      ),
                    )
                  }
                />
              </div>
            </div>

            {editingConnector.type === 'http' ? (
              <>
                <FieldInput
                  label={t("pages.studio.studiofilesdetailpane.base.url", "Base URL")}
                  mono
                  value={editingConnector.http.baseUrl}
                  onChange={(value) =>
                    setConnectorCatalogDraft((current) =>
                      current.map((item) =>
                        item.key === editingConnector.key
                          ? {
                              ...item,
                              http: {
                                ...item.http,
                                baseUrl: value,
                              },
                            }
                          : item,
                      ),
                    )
                  }
                />
                <FieldInput
                  label={t("pages.studio.studiofilesdetailpane.allowed.methods", "Allowed Methods")}
                  value={editingConnector.http.allowedMethods.join(', ')}
                  onChange={(value) =>
                    setConnectorCatalogDraft((current) =>
                      current.map((item) =>
                        item.key === editingConnector.key
                          ? {
                              ...item,
                              http: {
                                ...item.http,
                                allowedMethods: value
                                  .split(',')
                                  .map((part) => part.trim())
                                  .filter(Boolean),
                              },
                            }
                          : item,
                      ),
                    )
                  }
                />
                <FieldInput
                  label={t("pages.studio.studiofilesdetailpane.allowed.paths", "Allowed Paths")}
                  value={editingConnector.http.allowedPaths.join(', ')}
                  onChange={(value) =>
                    setConnectorCatalogDraft((current) =>
                      current.map((item) =>
                        item.key === editingConnector.key
                          ? {
                              ...item,
                              http: {
                                ...item.http,
                                allowedPaths: value
                                  .split(',')
                                  .map((part) => part.trim())
                                  .filter(Boolean),
                              },
                            }
                          : item,
                      ),
                    )
                  }
                />
                <FieldInput
                  label={t("pages.studio.studiofilesdetailpane.allowed.input.keys", "Allowed Input Keys")}
                  value={editingConnector.http.allowedInputKeys.join(', ')}
                  onChange={(value) =>
                    setConnectorCatalogDraft((current) =>
                      current.map((item) =>
                        item.key === editingConnector.key
                          ? {
                              ...item,
                              http: {
                                ...item.http,
                                allowedInputKeys: value
                                  .split(',')
                                  .map((part) => part.trim())
                                  .filter(Boolean),
                              },
                            }
                          : item,
                      ),
                    )
                  }
                />
                <FieldTextArea
                  label={t("pages.studio.studiofilesdetailpane.default.headers", "Default Headers")}
                  value={formatMapText(editingConnector.http.defaultHeaders)}
                  placeholder={t("pages.studio.studiofilesdetailpane.key.value.one.per.line", "key: value (one per line)")}
                  onChange={(value) =>
                    setConnectorCatalogDraft((current) =>
                      current.map((item) =>
                        item.key === editingConnector.key
                          ? {
                              ...item,
                              http: {
                                ...item.http,
                                defaultHeaders: parseMapText(value),
                              },
                            }
                          : item,
                      ),
                    )
                  }
                />
              </>
            ) : null}

            {editingConnector.type === 'cli' ? (
              <>
                <FieldInput
                  label={t("pages.studio.studiofilesdetailpane.command", "Command")}
                  mono
                  value={editingConnector.cli.command}
                  onChange={(value) =>
                    setConnectorCatalogDraft((current) =>
                      current.map((item) =>
                        item.key === editingConnector.key
                          ? {
                              ...item,
                              cli: {
                                ...item.cli,
                                command: value,
                              },
                            }
                          : item,
                      ),
                    )
                  }
                />
                <FieldInput
                  label={t("pages.studio.studiofilesdetailpane.fixed.arguments", "Fixed Arguments")}
                  value={editingConnector.cli.fixedArguments.join(', ')}
                  onChange={(value) =>
                    setConnectorCatalogDraft((current) =>
                      current.map((item) =>
                        item.key === editingConnector.key
                          ? {
                              ...item,
                              cli: {
                                ...item.cli,
                                fixedArguments: value
                                  .split(',')
                                  .map((part) => part.trim())
                                  .filter(Boolean),
                              },
                            }
                          : item,
                      ),
                    )
                  }
                />
                <FieldInput
                  label={t("pages.studio.studiofilesdetailpane.allowed.operations", "Allowed Operations")}
                  value={editingConnector.cli.allowedOperations.join(', ')}
                  onChange={(value) =>
                    setConnectorCatalogDraft((current) =>
                      current.map((item) =>
                        item.key === editingConnector.key
                          ? {
                              ...item,
                              cli: {
                                ...item.cli,
                                allowedOperations: value
                                  .split(',')
                                  .map((part) => part.trim())
                                  .filter(Boolean),
                              },
                            }
                          : item,
                      ),
                    )
                  }
                />
                <FieldInput
                  label={t("pages.studio.studiofilesdetailpane.allowed.input.keys.2", "Allowed Input Keys")}
                  value={editingConnector.cli.allowedInputKeys.join(', ')}
                  onChange={(value) =>
                    setConnectorCatalogDraft((current) =>
                      current.map((item) =>
                        item.key === editingConnector.key
                          ? {
                              ...item,
                              cli: {
                                ...item.cli,
                                allowedInputKeys: value
                                  .split(',')
                                  .map((part) => part.trim())
                                  .filter(Boolean),
                              },
                            }
                          : item,
                      ),
                    )
                  }
                />
                <FieldInput
                  label={t("pages.studio.studiofilesdetailpane.working.directory", "Working Directory")}
                  mono
                  value={editingConnector.cli.workingDirectory}
                  onChange={(value) =>
                    setConnectorCatalogDraft((current) =>
                      current.map((item) =>
                        item.key === editingConnector.key
                          ? {
                              ...item,
                              cli: {
                                ...item.cli,
                                workingDirectory: value,
                              },
                            }
                          : item,
                      ),
                    )
                  }
                />
                <FieldTextArea
                  label={t("pages.studio.studiofilesdetailpane.environment", "Environment")}
                  value={formatMapText(editingConnector.cli.environment)}
                  placeholder={t("pages.studio.studiofilesdetailpane.key.value.one.per.line.2", "KEY: value (one per line)")}
                  onChange={(value) =>
                    setConnectorCatalogDraft((current) =>
                      current.map((item) =>
                        item.key === editingConnector.key
                          ? {
                              ...item,
                              cli: {
                                ...item.cli,
                                environment: parseMapText(value),
                              },
                            }
                          : item,
                      ),
                    )
                  }
                />
              </>
            ) : null}

            {editingConnector.type === 'mcp' ? (
              <>
                <FieldInput
                  label={t("pages.studio.studiofilesdetailpane.server.name", "Server Name")}
                  mono
                  value={editingConnector.mcp.serverName}
                  onChange={(value) =>
                    setConnectorCatalogDraft((current) =>
                      current.map((item) =>
                        item.key === editingConnector.key
                          ? {
                              ...item,
                              mcp: {
                                ...item.mcp,
                                serverName: value,
                              },
                            }
                          : item,
                      ),
                    )
                  }
                />
                <FieldInput
                  label={t("pages.studio.studiofilesdetailpane.command", "Command")}
                  mono
                  value={editingConnector.mcp.command}
                  onChange={(value) =>
                    setConnectorCatalogDraft((current) =>
                      current.map((item) =>
                        item.key === editingConnector.key
                          ? {
                              ...item,
                              mcp: {
                                ...item.mcp,
                                command: value,
                              },
                            }
                          : item,
                      ),
                    )
                  }
                />
                <FieldInput
                  label={t("pages.studio.studiofilesdetailpane.arguments", "Arguments")}
                  value={editingConnector.mcp.arguments.join(', ')}
                  onChange={(value) =>
                    setConnectorCatalogDraft((current) =>
                      current.map((item) =>
                        item.key === editingConnector.key
                          ? {
                              ...item,
                              mcp: {
                                ...item.mcp,
                                arguments: value
                                  .split(',')
                                  .map((part) => part.trim())
                                  .filter(Boolean),
                              },
                            }
                          : item,
                      ),
                    )
                  }
                />
                <FieldInput
                  label={t("pages.studio.studiofilesdetailpane.default.tool", "Default Tool")}
                  mono
                  value={editingConnector.mcp.defaultTool}
                  onChange={(value) =>
                    setConnectorCatalogDraft((current) =>
                      current.map((item) =>
                        item.key === editingConnector.key
                          ? {
                              ...item,
                              mcp: {
                                ...item.mcp,
                                defaultTool: value,
                              },
                            }
                          : item,
                      ),
                    )
                  }
                />
                <FieldInput
                  label={t("pages.studio.studiofilesdetailpane.allowed.tools", "Allowed Tools")}
                  value={editingConnector.mcp.allowedTools.join(', ')}
                  onChange={(value) =>
                    setConnectorCatalogDraft((current) =>
                      current.map((item) =>
                        item.key === editingConnector.key
                          ? {
                              ...item,
                              mcp: {
                                ...item.mcp,
                                allowedTools: value
                                  .split(',')
                                  .map((part) => part.trim())
                                  .filter(Boolean),
                              },
                            }
                          : item,
                      ),
                    )
                  }
                />
                <FieldInput
                  label={t("pages.studio.studiofilesdetailpane.allowed.input.keys.3", "Allowed Input Keys")}
                  value={editingConnector.mcp.allowedInputKeys.join(', ')}
                  onChange={(value) =>
                    setConnectorCatalogDraft((current) =>
                      current.map((item) =>
                        item.key === editingConnector.key
                          ? {
                              ...item,
                              mcp: {
                                ...item.mcp,
                                allowedInputKeys: value
                                  .split(',')
                                  .map((part) => part.trim())
                                  .filter(Boolean),
                              },
                            }
                          : item,
                      ),
                    )
                  }
                />
                <FieldTextArea
                  label={t("pages.studio.studiofilesdetailpane.environment", "Environment")}
                  value={formatMapText(editingConnector.mcp.environment)}
                  placeholder={t("pages.studio.studiofilesdetailpane.key.value.one.per.line.3", "KEY: value (one per line)")}
                  onChange={(value) =>
                    setConnectorCatalogDraft((current) =>
                      current.map((item) =>
                        item.key === editingConnector.key
                          ? {
                              ...item,
                              mcp: {
                                ...item.mcp,
                                environment: parseMapText(value),
                              },
                            }
                          : item,
                      ),
                    )
                  }
                />
              </>
            ) : null}
          </>
        ) : null}
      </FilesDrawer>
    </div>
  );

  const renderWorkflowPanel = () => {
    if (!selectedWorkflowSummary) {
      return (
        <div style={detailScrollStyle}>
          <div style={emptyCardStyle}>
            <Empty
              image={Empty.PRESENTED_IMAGE_SIMPLE}
              description={t("pages.studio.studiofilesdetailpane.select.workflow.file.to.preview", "Select a workflow file to preview its YAML.")}
            />
          </div>
        </div>
      );
    }

    return (
      <div style={detailScrollStyle}>
        <div style={cliEditorShellStyle}>
          <div style={editorHeaderRowStyle}>
            <div>
              <div style={editorHeaderTitleStyle}>
                {selectedWorkflowSummary.name || selectedWorkflowId}
              </div>
              {selectedWorkflowSummary.description ? (
                <div style={editorHeaderDescriptionStyle}>
                  {selectedWorkflowSummary.description}
                </div>
              ) : null}
              <div style={catalogMetaStyle}>
                {selectedWorkflowSummary.stepCount} {t("pages.studio.studiofilesdetailpane.steps", "steps ·")}{' '}
                {selectedWorkflowSummary.directoryLabel}
              </div>
            </div>
            <button
              className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
              type="button"
              onClick={() =>
                onOpenWorkflowInStudio(selectedWorkflowSummary.workflowId)
              }
              style={primaryActionStyle}
            >
              <EditOutlined />
              {' '}
              {t("pages.studio.studiofilesdetailpane.open.in.studio", "Open in Studio")}</button>
          </div>

          {workflowFile.isLoading ? (
            <div style={emptyCardStyle}>{t("pages.studio.studiofilesdetailpane.loading.workflow", "Loading workflow...")}</div>
          ) : workflowFile.isError ? (
            <Alert
              type="error"
              showIcon
              message={t("pages.studio.studiofilesdetailpane.failed.to.load.workflow.yaml", "Failed to load workflow YAML")}
              description={describeError(workflowFile.error)}
            />
          ) : (
            <section aria-label={t("pages.studio.studiofilesdetailpane.workflow.yaml.preview", "Workflow YAML preview")}>
              <pre style={codePreviewStyle}>
                {workflowFile.data?.yaml ||
                  t("pages.studio.studiofilesdetailpane.workflow.yaml.empty.comment", "# Workflow YAML is empty.")}
              </pre>
            </section>
          )}
        </div>
      </div>
    );
  };

  const renderChatHistoryPanel = () => {
    if (!scopeId) {
      return (
        <div style={detailScrollStyle}>
          <div style={cliEditorShellStyle}>
            <Alert
              type="info"
              showIcon
              message={t("pages.studio.studiofilesdetailpane.resolve.workspace.to.browse.chat", "Resolve a workspace to browse chat histories.")}
              description={t("pages.studio.studiofilesdetailpane.chat.histories.are.loaded.from", "Chat histories are loaded from the active workspace once the Studio host resolves a workspace context.")}
            />
          </div>
        </div>
      );
    }

    if (chatConversations.isLoading) {
      return (
        <div style={detailScrollStyle}>
          <div style={emptyCardStyle}>{t("pages.studio.studiofilesdetailpane.loading.conversations", "Loading conversations...")}</div>
        </div>
      );
    }

    if (chatConversations.isError) {
      return (
        <div style={detailScrollStyle}>
          <div style={cliEditorShellStyle}>
            <Alert
              type="error"
              showIcon
              message={t("pages.studio.studiofilesdetailpane.failed.to.load.chat.histories", "Failed to load chat histories")}
              description={describeError(chatConversations.error)}
            />
          </div>
        </div>
      );
    }

    if (!selectedConversationId) {
      return (
        <div style={detailScrollStyle}>
          <div style={emptyCardStyle}>
            <Empty
              image={Empty.PRESENTED_IMAGE_SIMPLE}
              description={t("pages.studio.studiofilesdetailpane.select.conversation.to.preview.its", "Select a conversation to preview its messages.")}
            />
          </div>
        </div>
      );
    }

    return (
      <div style={detailScrollStyle}>
        <div style={cliEditorShellStyle}>
          <div style={editorHeaderRowStyle}>
            <div>
              <div style={editorHeaderLabelStyle}>chat-histories/</div>
              <div style={editorHeaderTitleStyle}>
                {selectedConversationMeta?.title || selectedConversationId}
              </div>
              <div style={editorHeaderDescriptionStyle}>
                {selectedConversationMeta
                  ? t("pages.studio.studiofilesdetailpane.conversation.turn.meta", "{turnCount} · {serviceKind} · {updatedAt}", {
                      turnCount: formatChatTurnCount(selectedConversationMeta.messageCount),
                      serviceKind: selectedConversationMeta.serviceKind || 'chat',
                      updatedAt: selectedConversationMeta.updatedAt
                        ? formatDateTime(selectedConversationMeta.updatedAt)
                        : t("pages.studio.studiofilesdetailpane.unknown.update.time", "unknown update time"),
                    })
                  : t("pages.studio.studiofilesdetailpane.conversation.metadata.unavailable", "Conversation metadata is not available.")}
              </div>
            </div>
            <button
              aria-controls="chat-history-delete-confirmation"
              aria-expanded={chatDeleteConfirmationOpen}
              className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
              type="button"
              disabled={chatDeletePending}
              onClick={() => {
                setChatNotice(null);
                setChatDeleteConfirmationOpen(true);
              }}
              style={{
                ...secondaryActionStyle,
                borderColor: '#fecaca',
                color: '#dc2626',
                opacity: chatDeletePending ? 0.5 : 1,
              }}
            >
              <DeleteOutlined />
              {t("pages.studio.studiofilesdetailpane.delete", "Delete")}</button>
          </div>

          {chatDeleteConfirmationOpen ? (
            <Alert
              id="chat-history-delete-confirmation"
              type="warning"
              showIcon
              message={t(
                "pages.studio.studiofilesdetailpane.delete.conversation.confirmation.title",
                "Delete this conversation?",
              )}
              description={t(
                "pages.studio.studiofilesdetailpane.delete.conversation.confirmation.description",
                "{title} will be removed from chat history and cannot be recovered from this view.",
                {
                  title:
                    selectedConversationMeta?.title || selectedConversationId,
                },
              )}
              action={
                <Space wrap size={[8, 8]}>
                  <Button
                    size="small"
                    disabled={chatDeletePending}
                    onClick={() => setChatDeleteConfirmationOpen(false)}
                  >
                    {t(
                      "pages.studio.studiofilesdetailpane.keep.conversation",
                      "Keep conversation",
                    )}
                  </Button>
                  <Button
                    danger
                    size="small"
                    type="primary"
                    loading={chatDeletePending}
                    onClick={() => void handleDeleteConversation()}
                  >
                    {t(
                      "pages.studio.studiofilesdetailpane.delete.conversation.now",
                      "Delete now",
                    )}
                  </Button>
                </Space>
              }
            />
          ) : null}

          <ConsoleOperationNotice
            errorMessage={t(
              'pages.studio.studiofilesdetailpane.conversationDeleteFailed',
              'Could not delete conversation. Try again.',
            )}
            notice={chatNotice}
          />

          {selectedConversationMessages.isLoading ? (
            <div style={emptyCardStyle}>{t("pages.studio.studiofilesdetailpane.loading.conversation", "Loading conversation...")}</div>
          ) : selectedConversationMessages.isError ? (
            <Alert
              type="error"
              showIcon
              message={t("pages.studio.studiofilesdetailpane.failed.to.load.conversation", "Failed to load conversation")}
              description={describeError(selectedConversationMessages.error)}
            />
          ) : (selectedConversationMessages.data?.messages.length ?? 0) === 0 ? (
            <div style={emptyCardStyle}>{t("pages.studio.studiofilesdetailpane.no.messages.in.this.conversation", "No messages in this conversation")}</div>
          ) : (
            <section aria-label={t("pages.studio.studiofilesdetailpane.chat.history.messages", "Chat history messages")} style={chatMessageListStyle}>
              {(
                (selectedConversationMessages.data?.messages ?? []) as ChatHistoryMessageView[]
              ).map(
                (message) => (
                  <article
                    key={message.id}
                    style={{
                      ...chatMessageCardStyle,
                      background:
                        message.role === 'user'
                          ? 'rgba(59, 130, 246, 0.06)'
                          : 'var(--ant-color-bg-container)',
                      borderColor:
                        message.role === 'user' ? '#bfdbfe' : '#EEEAE4',
                    }}
                  >
                    <div style={chatMessageMetaStyle}>
                      <span style={{ alignItems: 'center', display: 'flex', gap: 8 }}>
                        <span
                          style={{
                            color: message.role === 'user' ? '#2563eb' : '#6b7280',
                            fontWeight: 700,
                          }}
                        >
                          {formatChatMessageAuthor(message)}
                        </span>
                        {message.authorName?.trim() ? (
                          <span
                            style={{
                              color: '#9ca3af',
                              fontSize: 10,
                              fontWeight: 600,
                              textTransform: 'uppercase',
                            }}
                          >
                            {message.role}
                          </span>
                        ) : null}
                        <Tag
                          color={
                            message.status?.trim().toLowerCase() === 'error'
                              ? 'error'
                              : message.status?.trim().toLowerCase() === 'complete'
                                ? 'success'
                                : undefined
                          }
                          style={{ marginInlineEnd: 0 }}
                        >
                          {formatChatMessageStatus(message.status)}
                        </Tag>
                      </span>
                      <span style={{ color: '#9ca3af' }}>
                        {message.timestamp
                          ? formatDateTime(new Date(message.timestamp).toISOString())
                          : t("pages.studio.studiofilesdetailpane.unknown", "unknown")}
                      </span>
                    </div>

                    {message.thinking ? (
                      <div
                        style={{
                          borderLeft: '2px solid #e5e7eb',
                          color: '#6b7280',
                          fontSize: 12,
                          fontStyle: 'italic',
                          marginBottom: 12,
                          paddingLeft: 12,
                          whiteSpace: 'pre-wrap',
                        }}
                      >
                        <div
                          style={{
                            fontStyle: 'normal',
                            fontWeight: 700,
                            marginBottom: 4,
                          }}
                        >
                          {t(
                            "pages.studio.studiofilesdetailpane.chat.message.thinking",
                            "Thinking",
                          )}
                        </div>
                        {message.thinking}
                      </div>
                    ) : null}

                    {message.content || !message.error ? (
                      <div
                        style={{
                          color: '#374151',
                          fontSize: 13,
                          lineHeight: 1.8,
                          whiteSpace: 'pre-wrap',
                          wordBreak: 'break-word',
                        }}
                      >
                        {message.content ||
                          t("pages.studio.studiofilesdetailpane.empty.message", "(empty message)")}
                      </div>
                    ) : null}

                    {message.error ? (
                      <div
                        style={{ color: '#dc2626', fontSize: 12, marginTop: 12 }}
                      >
                        <strong>
                          {t(
                            "pages.studio.studiofilesdetailpane.chat.message.error",
                            "Error",
                          )}
                          :{' '}
                        </strong>
                        <span style={{ whiteSpace: 'pre-wrap', wordBreak: 'break-word' }}>
                          {message.error}
                        </span>
                      </div>
                    ) : null}
                  </article>
                ),
              )}
            </section>
          )}
        </div>
      </div>
    );
  };

  const renderScriptPanel = () => {
    if (!scriptsEnabled) {
      return (
        <div style={detailScrollStyle}>
          <div style={cliEditorShellStyle}>
            <Alert
              type="info"
              showIcon
              message={t("pages.studio.studiofilesdetailpane.scripts.are.disabled.for.this", "Scripts are disabled for this Studio host.")}
              description={t("pages.studio.studiofilesdetailpane.enable.the.scripts.feature.to", "Enable the scripts feature to browse script files here.")}
            />
          </div>
        </div>
      );
    }

    if (!scopeId) {
      return (
        <div style={detailScrollStyle}>
          <div style={cliEditorShellStyle}>
            <Alert
              type="info"
              showIcon
              message={t("pages.studio.studiofilesdetailpane.resolve.workspace.to.browse.scripts", "Resolve a workspace to browse scripts.")}
              description={t("pages.studio.studiofilesdetailpane.script.files.are.loaded.from", "Script files are loaded from the active workspace once Studio resolves a workspace context.")}
            />
          </div>
        </div>
      );
    }

    if (scripts.isLoading) {
      return (
        <div style={detailScrollStyle}>
          <div style={emptyCardStyle}>{t("pages.studio.studiofilesdetailpane.loading.scripts", "Loading scripts...")}</div>
        </div>
      );
    }

    if (scripts.isError) {
      return (
        <div style={detailScrollStyle}>
          <div style={cliEditorShellStyle}>
            <Alert
              type="error"
              showIcon
              message={t("pages.studio.studiofilesdetailpane.failed.to.load.scripts", "Failed to load scripts")}
              description={describeError(scripts.error)}
            />
          </div>
        </div>
      );
    }

    if (!selectedScriptDetail?.script) {
      return (
        <div style={detailScrollStyle}>
          <div style={emptyCardStyle}>
            <Empty
              image={Empty.PRESENTED_IMAGE_SIMPLE}
              description={t("pages.studio.studiofilesdetailpane.select.script.file.to.preview", "Select a script file to preview its source.")}
            />
          </div>
        </div>
      );
    }

    const scriptId = selectedScriptDetail.script.scriptId;

    return (
      <div style={detailScrollStyle}>
        <div style={cliEditorShellStyle}>
          <div style={editorHeaderRowStyle}>
            <div>
              <div style={editorHeaderTitleStyle}>{formatScriptDetailLabel(selectedScriptDetail)}</div>
              <div style={catalogMetaStyle}>
                {selectedScriptDetail.script.activeRevision
                  ? t("pages.studio.studiofilesdetailpane.version.ready", "Version ready")
                  : t("pages.studio.studiofilesdetailpane.draft", "Draft")}{' '}
                {t("pages.studio.studiofilesdetailpane.updated", "· Updated:")}{' '}
                {selectedScriptDetail.script.updatedAt
                  ? formatDateTime(selectedScriptDetail.script.updatedAt)
                  : t("pages.studio.studiofilesdetailpane.unknown", "unknown")}
              </div>
            </div>
            <button
              className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
              type="button"
              onClick={() => onOpenScriptInStudio(scriptId)}
              style={primaryActionStyle}
            >
              <EditOutlined />
              {' '}
              {t("pages.studio.studiofilesdetailpane.open.script.build", "Open Script Build")}
            </button>
          </div>

          <section aria-label={t("pages.studio.studiofilesdetailpane.script.source.preview", "Script source preview")}>
            <pre style={codePreviewStyle}>
              {selectedScriptDetail.source?.sourceText ||
                t("pages.studio.studiofilesdetailpane.script.source.unavailable.comment", "// Source code not available. The script may need to be promoted first.")}
            </pre>
          </section>
        </div>
      </div>
    );
  };

  if (selectedFile === 'role-catalog') {
    return renderRolesPanel();
  }

  if (selectedFile === 'connector-catalog') {
    return renderConnectorsPanel();
  }

  if (selectedWorkflowId) {
    return renderWorkflowPanel();
  }

  if (selectedConversationId) {
    return renderChatHistoryPanel();
  }

  return renderScriptPanel();
};

export default StudioFilesDetailPane;
