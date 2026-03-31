import {
  ApiOutlined,
  ApartmentOutlined,
  AppstoreOutlined,
  BarsOutlined,
  CaretRightFilled,
  CheckOutlined,
  CloseOutlined,
  CloseCircleFilled,
  CodeOutlined,
  CopyOutlined,
  DatabaseOutlined,
  DeleteOutlined,
  DownOutlined,
  ExpandOutlined,
  ExportOutlined,
  FileTextOutlined,
  FolderAddOutlined,
  LoadingOutlined,
  PlayCircleFilled,
  PlusOutlined,
  QuestionCircleOutlined,
  RobotOutlined,
  SafetyCertificateOutlined,
  SearchOutlined,
  SaveOutlined,
  UploadOutlined,
  UserOutlined,
} from '@ant-design/icons';
import {
  ProCard,
} from '@ant-design/pro-components';
import {
  Button,
  Col,
  Divider,
  Drawer,
  Empty,
  Input,
  Modal,
  Row,
  Select,
  Space,
  Tag,
  Typography,
} from 'antd';
import React from 'react';
import type { Edge, Node } from '@xyflow/react';
import ReactDOM from 'react-dom';
import GraphCanvas from '@/shared/graphs/GraphCanvas';
import type { PlaygroundPromptHistoryEntry } from '@/shared/playground/promptHistory';
import type {
  StudioConnectorCatalog,
  StudioConnectorDefinition,
  StudioExecutionDetail,
  StudioExecutionSummary,
  StudioProviderSettings,
  StudioProviderType,
  StudioRoleDefinition,
  StudioScopeBindingStatus,
  StudioRuntimeTestResult,
  StudioWorkflowFile,
  StudioWorkflowSummary,
  StudioWorkspaceSettings,
} from '@/shared/studio/models';
import type {
  StudioGraphEdgeData,
  StudioGraphRole,
  StudioGraphStep,
  StudioGraphNodeData,
} from '@/shared/studio/graph';
import {
  STUDIO_GRAPH_CATEGORIES,
} from '@/shared/studio/graph';
import {
  buildExecutionTrace,
  decorateEdgesForExecution,
  decorateNodesForExecution,
  findExecutionLogIndexForStep,
  formatDurationBetween,
  formatExecutionLogClipboard,
  formatExecutionLogsClipboard,
  type ExecutionInteractionState,
} from '@/shared/studio/execution';
import { formatDateTime } from '@/shared/datetime/dateTime';
import {
  cardListActionStyle,
  cardListHeaderStyle,
  cardListItemStyle,
  cardListMainStyle,
  cardListStyle,
  cardStackStyle,
  codeBlockStyle,
  drawerBodyStyle,
  drawerScrollStyle,
  embeddedPanelStyle,
  fillCardStyle,
  moduleCardProps,
  stretchColumnStyle,
  summaryFieldGridStyle,
  summaryFieldLabelStyle,
  summaryFieldStyle,
  summaryMetricGridStyle,
  summaryMetricStyle,
  summaryMetricValueStyle,
} from '@/shared/ui/proComponents';
import { describeError } from '@/shared/ui/errorText';

type QueryState<T> = {
  readonly isLoading: boolean;
  readonly isError: boolean;
  readonly error: unknown;
  readonly data: T | undefined;
};

type DraftMode = '' | 'new';
type LegacySource = '' | 'playground';
type StudioInspectorTab = 'node' | 'roles' | 'yaml';
type StudioToolDrawerMode = 'palette' | 'ask-ai';
type StudioGAgentBindingDraft = {
  readonly displayName: string;
  readonly actorTypeName: string;
  readonly preferredActorId: string;
  readonly endpointId: string;
  readonly endpointDisplayName: string;
  readonly requestTypeUrl: string;
  readonly responseTypeUrl: string;
  readonly description: string;
  readonly prompt: string;
  readonly payloadBase64: string;
};
type StudioSelectedGraphEdge = {
  readonly edgeId: string;
  readonly sourceStepId: string;
  readonly targetStepId: string;
  readonly branchLabel: string | null;
  readonly kind: 'next' | 'branch';
  readonly implicit: boolean;
};

const DEFAULT_GAGENT_REQUEST_TYPE_URL =
  'type.googleapis.com/google.protobuf.StringValue';

function createStudioGAgentBindingDraft(
  displayName: string,
): StudioGAgentBindingDraft {
  return {
    displayName,
    actorTypeName: '',
    preferredActorId: '',
    endpointId: 'run',
    endpointDisplayName: 'Run',
    requestTypeUrl: DEFAULT_GAGENT_REQUEST_TYPE_URL,
    responseTypeUrl: '',
    description: 'Run the bound GAgent.',
    prompt: '',
    payloadBase64: '',
  };
}

type StudioNoticeLike = {
  readonly type: 'success' | 'info' | 'warning' | 'error';
  readonly message: string;
};

type StudioSettingsDraftLike = {
  readonly runtimeBaseUrl: string;
  readonly defaultProviderName: string;
  readonly providerTypes: StudioProviderType[];
  readonly providers: StudioProviderSettings[];
};

function isScopeDirectoryPath(path: string): boolean {
  return path.trim().startsWith('scope://');
}

export type StudioWorkflowLayout = 'grid' | 'list';

const workflowWorkspaceRowStyle: React.CSSProperties = {
  flex: 1,
  minHeight: 0,
};

const workflowSidebarStackStyle: React.CSSProperties = {
  ...cardStackStyle,
  flex: 1,
  minHeight: 0,
};

const workflowSectionShellStyle: React.CSSProperties = {
  border: '1px solid #E6E3DE',
  borderRadius: 32,
  background: 'rgba(255, 255, 255, 0.94)',
  padding: 20,
  display: 'flex',
  flexDirection: 'column',
  gap: 16,
  boxShadow: '0 22px 52px rgba(17, 24, 39, 0.06)',
};

const workflowSectionFillStyle: React.CSSProperties = {
  flex: 1,
  minHeight: 0,
};

const workflowSectionHeaderStyle: React.CSSProperties = {
  display: 'flex',
  alignItems: 'flex-start',
  justifyContent: 'space-between',
  gap: 12,
};

const workflowSectionHeadingStyle: React.CSSProperties = {
  fontSize: 12,
  fontWeight: 600,
  letterSpacing: '0.14em',
  textTransform: 'uppercase',
  color: '#6B7280',
};

const workflowSurfaceStyle: React.CSSProperties = {
  border: '1px solid #EEEAE4',
  borderRadius: 20,
  background: '#FAF8F4',
  padding: 14,
};

const workflowSurfaceActiveStyle: React.CSSProperties = {
  borderColor: '#B8D2FF',
  background: '#EEF5FF',
  boxShadow: '0 12px 28px rgba(47, 111, 236, 0.12)',
};

const workflowToolbarSurfaceStyle: React.CSSProperties = {
  borderBottom: '1px solid #E6E3DE',
  background: 'rgba(255, 255, 255, 0.72)',
  backdropFilter: 'blur(10px)',
  padding: 20,
};

const workflowBrowserStyle: React.CSSProperties = {
  border: '1px solid #E6E3DE',
  borderRadius: 38,
  background: '#F2F1EE',
  boxShadow: '0 26px 64px rgba(17, 24, 39, 0.08)',
  overflow: 'hidden',
  width: '100%',
  flex: 1,
  minWidth: 0,
  minHeight: 0,
  height: '100%',
  display: 'flex',
  flexDirection: 'column',
};

const workflowSectionCopyStyle: React.CSSProperties = {
  fontSize: 12,
  lineHeight: 1.6,
  color: 'var(--ant-color-text-tertiary)',
};

const workflowPanelIconButtonStyle: React.CSSProperties = {
  width: 36,
  height: 36,
  borderRadius: 999,
  border: '1px solid #E5E1DA',
  background: '#FFFFFF',
  color: 'var(--ant-color-text-tertiary)',
  display: 'inline-flex',
  alignItems: 'center',
  justifyContent: 'center',
  boxShadow: '0 10px 24px rgba(31, 28, 24, 0.04)',
};

const workflowDirectorySelectButtonStyle: React.CSSProperties = {
  alignItems: 'flex-start',
  display: 'flex',
  flex: 1,
  height: 'auto',
  justifyContent: 'flex-start',
  minWidth: 0,
  padding: 0,
  textAlign: 'left',
  whiteSpace: 'normal',
};

const workflowDirectoryTextStackStyle: React.CSSProperties = {
  ...cardStackStyle,
  minWidth: 0,
};

const workflowDirectoryLabelStyle: React.CSSProperties = {
  display: 'block',
  fontSize: 13,
  fontWeight: 600,
  color: '#1F2937',
  maxWidth: '100%',
  overflow: 'hidden',
  textOverflow: 'ellipsis',
  whiteSpace: 'nowrap',
};

const workflowDirectoryPathStyle: React.CSSProperties = {
  display: 'block',
  fontSize: 11,
  lineHeight: 1.6,
  color: 'var(--ant-color-text-tertiary)',
  marginTop: 4,
  maxWidth: '100%',
  overflow: 'hidden',
  textOverflow: 'ellipsis',
  whiteSpace: 'nowrap',
};

const workflowToolbarLayoutStyle: React.CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  alignItems: 'center',
  justifyContent: 'space-between',
  gap: 12,
};

const workflowSearchFieldStyle: React.CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  gap: 10,
  flex: '1 1 260px',
  minWidth: 260,
  maxWidth: 420,
  padding: '0 14px',
  height: 44,
  border: '1px solid #E5E1DA',
  borderRadius: 18,
  background: '#FFFFFF',
  boxShadow: '0 10px 24px rgba(31, 28, 24, 0.04)',
};

const workflowSearchInputStyle: React.CSSProperties = {
  flex: 1,
  minWidth: 0,
  border: 0,
  outline: 'none',
  background: 'transparent',
  fontSize: 14,
  lineHeight: 1.5,
  color: '#1F2937',
};

const workflowToolbarActionsStyle: React.CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  alignItems: 'center',
  gap: 10,
  justifyContent: 'flex-end',
};

const workflowToggleGroupStyle: React.CSSProperties = {
  display: 'inline-flex',
  alignItems: 'center',
  gap: 8,
  padding: 8,
  border: '1px solid #E5E1DA',
  borderRadius: 18,
  background: '#FFFFFF',
  boxShadow: '0 10px 24px rgba(31, 28, 24, 0.04)',
};

const workflowToggleButtonStyle: React.CSSProperties = {
  width: 36,
  height: 36,
  borderRadius: 12,
  border: 0,
  cursor: 'pointer',
  background: 'transparent',
  color: 'var(--ant-color-text-tertiary)',
  display: 'inline-flex',
  alignItems: 'center',
  justifyContent: 'center',
};

const workflowToggleButtonActiveStyle: React.CSSProperties = {
  background: '#EAF2FF',
  color: '#2F6FEC',
};

const workflowGhostActionStyle: React.CSSProperties = {
  borderRadius: 999,
  border: '1px solid #E5E1DA',
  background: '#FFFFFF',
  color: '#374151',
  boxShadow: '0 10px 24px rgba(31, 28, 24, 0.04)',
};

const workflowSolidActionStyle: React.CSSProperties = {
  borderRadius: 999,
  border: '1px solid #2F6FEC',
  background: '#2F6FEC',
  color: '#FFFFFF',
  boxShadow: '0 14px 32px rgba(47, 111, 236, 0.24)',
};

const workflowResultsBodyStyle: React.CSSProperties = {
  flex: 1,
  minHeight: 0,
  display: 'flex',
  flexDirection: 'column',
  overflowY: 'auto',
  padding: 24,
};

const workflowCardButtonBaseStyle: React.CSSProperties = {
  width: '100%',
  border: '1px solid #EEEAE4',
  borderRadius: 24,
  cursor: 'pointer',
  background: '#FFFFFF',
  textAlign: 'left',
  padding: 20,
  display: 'block',
  transition:
    'background-color 0.2s ease, border-color 0.2s ease, box-shadow 0.2s ease',
  boxShadow: 'none',
};

const workflowCardButtonListStyle: React.CSSProperties = {
  ...workflowCardButtonBaseStyle,
  padding: '16px 20px',
};

const workflowCardButtonActiveStyle: React.CSSProperties = {
  borderColor: '#B8D2FF',
  background: '#EEF5FF',
  boxShadow: '0 14px 32px rgba(47, 111, 236, 0.14)',
};

const workflowCardRowStyle: React.CSSProperties = {
  display: 'flex',
  gap: 16,
  width: '100%',
};

const workflowCardListRowStyle: React.CSSProperties = {
  ...workflowCardRowStyle,
  alignItems: 'center',
};

const workflowCardIconStyle: React.CSSProperties = {
  width: 48,
  height: 48,
  borderRadius: 16,
  background: '#F3F0EA',
  color: 'var(--ant-color-text-tertiary)',
  display: 'flex',
  alignItems: 'center',
  justifyContent: 'center',
  flexShrink: 0,
};

const workflowCardIconActiveStyle: React.CSSProperties = {
  background: '#EAF2FF',
  color: '#2F6FEC',
};

const workflowCardTitleStyle: React.CSSProperties = {
  display: 'block',
  fontSize: 15,
  fontWeight: 600,
  color: '#1F2937',
};

const workflowCardMetaLineStyle: React.CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  gap: '4px 12px',
  marginTop: 4,
  fontSize: 12,
  lineHeight: 1.6,
  color: 'var(--ant-color-text-tertiary)',
};

const workflowCardDescriptionStyle: React.CSSProperties = {
  margin: '12px 0 0',
  fontSize: 12,
  lineHeight: 1.6,
  color: '#6B7280',
  display: '-webkit-box',
  WebkitBoxOrient: 'vertical',
  WebkitLineClamp: 2,
  overflow: 'hidden',
};

const workflowCardDescriptionCompactStyle: React.CSSProperties = {
  margin: '8px 0 0',
  fontSize: 12,
  lineHeight: 1.6,
  color: '#6B7280',
  whiteSpace: 'nowrap',
  overflow: 'hidden',
  textOverflow: 'ellipsis',
};

const workflowOpenHintStyle: React.CSSProperties = {
  display: 'none',
  flexShrink: 0,
  fontSize: 11,
  fontWeight: 600,
  letterSpacing: '0.16em',
  textTransform: 'uppercase',
  color: '#D1D5DB',
};

const workflowEmptyStateStyle: React.CSSProperties = {
  flex: 1,
  minHeight: 0,
  width: '100%',
  display: 'flex',
  alignItems: 'center',
  justifyContent: 'center',
};

const workflowEmptyStateInnerStyle: React.CSSProperties = {
  maxWidth: 420,
  width: '100%',
};

const workflowDirectoryContentStyle: React.CSSProperties = {
  ...cardStackStyle,
  flex: 1,
  minHeight: 0,
};

const workflowDirectoryListStyle: React.CSSProperties = {
  ...cardStackStyle,
  flex: 1,
  minHeight: 0,
  overflowY: 'auto',
};

const workflowResultsViewportStyle: React.CSSProperties = {
  width: '100%',
};

const studioTitleBarStyle: React.CSSProperties = {
  minWidth: 0,
  flex: '1 1 420px',
  maxWidth: '100%',
  display: 'flex',
  alignItems: 'center',
  gap: 8,
  minHeight: 40,
  padding: '0 6px 0 12px',
  border: '1px solid #E8E4DD',
  borderRadius: 12,
  background: '#FFFFFF',
  boxShadow: 'none',
};

const studioTitleGroupStyle: React.CSSProperties = {
  minWidth: 0,
  flex: '1 1 auto',
  display: 'flex',
  alignItems: 'center',
  gap: 10,
};

const studioTitleInputStyle: React.CSSProperties = {
  minWidth: 0,
  width: '100%',
  background: 'transparent',
  border: 0,
  outline: 'none',
  fontSize: 15,
  fontWeight: 700,
  letterSpacing: '-0.02em',
  lineHeight: 1.2,
  color: '#1F2937',
};

const studioInfoPopoverStyle: React.CSSProperties = {
  position: 'relative',
  display: 'inline-flex',
  zIndex: 40,
};

const studioInfoPopoverButtonStyle: React.CSSProperties = {
  width: 20,
  height: 20,
  display: 'inline-flex',
  alignItems: 'center',
  justifyContent: 'center',
  borderRadius: 999,
  border: 0,
  background: 'transparent',
  color: 'var(--ant-color-text-tertiary)',
  cursor: 'pointer',
  transition: 'background 0.18s ease, color 0.18s ease',
};

const studioInfoPopoverCardStyle: React.CSSProperties = {
  position: 'fixed',
  zIndex: 300,
  width: 320,
  border: '1px solid #E8E4DD',
  borderRadius: 16,
  background: 'rgba(255,255,255,0.98)',
  boxShadow: '0 18px 40px rgba(17, 24, 39, 0.08)',
  backdropFilter: 'blur(12px)',
  padding: 14,
};

const studioDescriptionEditorStyle: React.CSSProperties = {
  width: '100%',
  minHeight: 168,
  resize: 'vertical',
  border: '1px solid #E7E3DC',
  borderRadius: 12,
  background: '#FBFAF8',
  color: '#24211F',
  outline: 'none',
  padding: '12px 14px',
  fontSize: 15,
  lineHeight: 1.65,
  fontFamily:
    "'Inter', system-ui, -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif",
};

const studioInfoPopoverActionsStyle: React.CSSProperties = {
  display: 'flex',
  justifyContent: 'flex-end',
  gap: 8,
  marginTop: 12,
};

const studioNoticeStripStyle: React.CSSProperties = {
  display: 'grid',
  gap: 10,
  gridTemplateColumns: 'repeat(auto-fit, minmax(220px, 1fr))',
};

const studioNoticeCardStyle: React.CSSProperties = {
  ...embeddedPanelStyle,
  display: 'flex',
  flexDirection: 'column',
  gap: 8,
  minWidth: 0,
  padding: '12px 14px',
};

const studioStatusStripStyle: React.CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  gap: 10,
};

const studioStatusPillStyle: React.CSSProperties = {
  alignItems: 'flex-start',
  background: 'rgba(255, 255, 255, 0.92)',
  border: '1px solid #E8E2D9',
  borderRadius: 18,
  boxShadow: '0 10px 24px rgba(17, 24, 39, 0.08)',
  display: 'flex',
  flexDirection: 'column',
  gap: 4,
  minWidth: 0,
  padding: '10px 12px',
};

const studioCanvasViewportStyle: React.CSSProperties = {
  background: '#F2F1EE',
  borderRadius: 24,
  flex: 1,
  minHeight: 'calc(100vh - 278px)',
  overflow: 'hidden',
  position: 'relative',
};

const studioToolDrawerSectionStyle: React.CSSProperties = {
  ...embeddedPanelStyle,
  display: 'flex',
  flexDirection: 'column',
  gap: 12,
};

function getStudioNoticeAccent(
  type: StudioNoticeLike['type'] | 'default',
): { border: string; background: string; label: string } {
  switch (type) {
    case 'success':
      return {
        border: 'rgba(82, 196, 26, 0.28)',
        background: 'rgba(246, 255, 237, 0.96)',
        label: 'Success',
      };
    case 'warning':
      return {
        border: 'rgba(250, 173, 20, 0.28)',
        background: 'rgba(255, 251, 230, 0.96)',
        label: 'Warning',
      };
    case 'error':
      return {
        border: 'rgba(255, 77, 79, 0.28)',
        background: 'rgba(255, 241, 240, 0.96)',
        label: 'Error',
      };
    case 'info':
      return {
        border: 'rgba(22, 119, 255, 0.24)',
        background: 'rgba(240, 245, 255, 0.96)',
        label: 'Info',
      };
    default:
      return {
        border: 'var(--ant-color-border-secondary)',
        background: 'var(--ant-color-fill-quaternary)',
        label: 'Status',
      };
  }
}

type StudioNoticeCardProps = {
  readonly type?: StudioNoticeLike['type'] | 'default';
  readonly title: React.ReactNode;
  readonly description?: React.ReactNode;
  readonly action?: React.ReactNode;
};

const StudioNoticeCard: React.FC<StudioNoticeCardProps> = ({
  type = 'default',
  title,
  description,
  action,
}) => {
  const accent = getStudioNoticeAccent(type);

  return (
    <div
      style={{
        ...studioNoticeCardStyle,
        background: accent.background,
        borderColor: accent.border,
      }}
    >
      <Space wrap size={[8, 8]}>
        <Tag color={type === 'default' ? 'default' : type}>{accent.label}</Tag>
        <Typography.Text strong>{title}</Typography.Text>
      </Space>
      {description ? (
        typeof description === 'string' ? (
          <Typography.Paragraph style={{ margin: 0 }} type="secondary">
            {description}
          </Typography.Paragraph>
        ) : (
          description
        )
      ) : null}
      {action}
    </div>
  );
};

type StudioSummaryFieldProps = {
  readonly copyable?: boolean;
  readonly label: string;
  readonly value: React.ReactNode;
};

type StudioSummaryMetricProps = {
  readonly label: string;
  readonly tone?: 'default' | 'info' | 'success' | 'warning' | 'error';
  readonly value: React.ReactNode;
};

const studioSummaryMetricToneMap: Record<
  NonNullable<StudioSummaryMetricProps['tone']>,
  { color: string }
> = {
  default: { color: 'var(--ant-color-text)' },
  error: { color: 'var(--ant-color-error)' },
  info: { color: 'var(--ant-color-primary)' },
  success: { color: 'var(--ant-color-success)' },
  warning: { color: 'var(--ant-color-warning)' },
};

function renderStudioSummaryValue(
  value: React.ReactNode,
  copyable?: boolean,
): React.ReactNode {
  if (typeof value === 'string') {
    if (!value) {
      return <Typography.Text type="secondary">n/a</Typography.Text>;
    }

    return copyable ? (
      <Typography.Text copyable>{value}</Typography.Text>
    ) : (
      <Typography.Text>{value}</Typography.Text>
    );
  }

  if (typeof value === 'number') {
    return <Typography.Text>{value}</Typography.Text>;
  }

  return value;
}

const StudioSummaryField: React.FC<StudioSummaryFieldProps> = ({
  copyable,
  label,
  value,
}) => (
  <div style={summaryFieldStyle}>
    <Typography.Text style={summaryFieldLabelStyle}>{label}</Typography.Text>
    {renderStudioSummaryValue(value, copyable)}
  </div>
);

const StudioSummaryMetric: React.FC<StudioSummaryMetricProps> = ({
  label,
  tone = 'default',
  value,
}) => (
  <div style={summaryMetricStyle}>
    <Typography.Text style={summaryFieldLabelStyle}>{label}</Typography.Text>
    <Typography.Text
      style={{
        ...summaryMetricValueStyle,
        color: studioSummaryMetricToneMap[tone].color,
      }}
    >
      {value}
    </Typography.Text>
  </div>
);

function getScopeBindingRevisionTone(
  revision: StudioScopeBindingStatus['revisions'][number],
): 'default' | 'success' | 'warning' | 'error' | 'info' {
  const normalizedStatus = revision.status.trim().toLowerCase();
  if (normalizedStatus === 'retired' || normalizedStatus.includes('failed')) {
    return 'error';
  }

  if (revision.isDefaultServing || revision.isActiveServing) {
    return 'success';
  }

  if (revision.isServingTarget) {
    return 'info';
  }

  if (normalizedStatus === 'prepared') {
    return 'warning';
  }

  return 'default';
}

function canActivateScopeBindingRevision(
  binding: StudioScopeBindingStatus | undefined,
  revision: StudioScopeBindingStatus['revisions'][number],
): boolean {
  if (!binding?.available) {
    return false;
  }

  if (revision.revisionId === binding.defaultServingRevisionId) {
    return false;
  }

  const normalizedStatus = revision.status.trim().toLowerCase();
  return normalizedStatus === 'published' || normalizedStatus === 'prepared';
}

function formatScopeBindingRevisionTimestamp(
  revision: StudioScopeBindingStatus['revisions'][number],
): string {
  return (
    formatDateTime(revision.publishedAt) ||
    formatDateTime(revision.preparedAt) ||
    formatDateTime(revision.createdAt) ||
    'n/a'
  );
}

type StudioScopeBindingPanelProps = {
  readonly scopeId?: string;
  readonly binding?: StudioScopeBindingStatus;
  readonly loading: boolean;
  readonly error: unknown;
  readonly pendingRevisionId: string;
  readonly onActivateRevision: (revisionId: string) => void;
};

const StudioScopeBindingPanel: React.FC<StudioScopeBindingPanelProps> = ({
  scopeId,
  binding,
  loading,
  error,
  pendingRevisionId,
  onActivateRevision,
}) => {
  if (!scopeId) {
    return null;
  }

  const revisions = binding?.revisions ?? [];

  return (
    <div
      style={{
        ...workflowSectionShellStyle,
        gap: 18,
      }}
    >
      <div style={workflowSectionHeaderStyle}>
        <div style={workflowDirectoryTextStackStyle}>
          <Typography.Text style={workflowSectionHeadingStyle}>
            Scope Binding
          </Typography.Text>
          <Typography.Title
            level={5}
            style={{ margin: 0 }}
          >
            {binding?.available
              ? binding.displayName || binding.serviceId
              : 'No active binding'}
          </Typography.Title>
          <Typography.Text type="secondary">
            {binding?.available
              ? `Scope ${scopeId} is routed through the default service binding.`
              : `Scope ${scopeId} has not published a service binding yet.`}
          </Typography.Text>
        </div>
        <Space wrap size={[8, 8]}>
          <Tag color="processing">{scopeId}</Tag>
          {binding?.available ? (
            <Tag color="success">
              {binding.defaultServingRevisionId || 'default pending'}
            </Tag>
          ) : null}
        </Space>
      </div>

      {loading ? (
        <StudioNoticeCard
          title="Loading scope binding"
          description="Fetching the current revision history and serving state for this scope."
        />
      ) : error ? (
        <StudioNoticeCard
          type="error"
          title="Failed to load scope binding"
          description={describeError(error)}
        />
      ) : !binding?.available ? (
        <StudioNoticeCard
          type="info"
          title="No published binding"
          description="Use Bind scope to publish the current workflow as the scope's default service."
        />
      ) : (
        <>
          <div style={summaryMetricGridStyle}>
            <StudioSummaryMetric
              label="Revisions"
              value={binding.revisions.length}
            />
            <StudioSummaryMetric
              label="Default"
              tone="success"
              value={binding.defaultServingRevisionId || 'n/a'}
            />
            <StudioSummaryMetric
              label="Active"
              tone="info"
              value={binding.activeServingRevisionId || 'n/a'}
            />
            <StudioSummaryMetric
              label="Deployment"
              value={binding.deploymentStatus || 'n/a'}
            />
          </div>

          <div style={summaryFieldGridStyle}>
            <StudioSummaryField
              label="Service key"
              copyable
              value={binding.serviceKey}
            />
            <StudioSummaryField
              label="Primary actor"
              copyable
              value={binding.primaryActorId}
            />
            <StudioSummaryField
              label="Deployment"
              value={binding.deploymentId}
            />
            <StudioSummaryField
              label="Updated"
              value={formatDateTime(binding.updatedAt)}
            />
          </div>

          <div
            style={{
              ...cardStackStyle,
              maxHeight: 320,
              overflowY: 'auto',
            }}
          >
            {revisions.map((revision) => {
              const canActivate = canActivateScopeBindingRevision(
                binding,
                revision,
              );
              return (
                <div
                  key={revision.revisionId}
                  style={{
                    alignItems: 'flex-start',
                    border: '1px solid #E6E3DE',
                    borderRadius: 20,
                    background: '#FFFFFF',
                    display: 'flex',
                    gap: 16,
                    justifyContent: 'space-between',
                    padding: 16,
                  }}
                >
                  <div
                    style={{
                      ...cardStackStyle,
                      flex: 1,
                      minWidth: 0,
                    }}
                  >
                    <Space wrap size={[8, 8]}>
                      <Typography.Text strong copyable>
                        {revision.revisionId}
                      </Typography.Text>
                      <Tag
                        color={
                          getScopeBindingRevisionTone(revision) === 'default'
                            ? 'default'
                            : getScopeBindingRevisionTone(revision)
                        }
                      >
                        {revision.status || 'unknown'}
                      </Tag>
                      {revision.isDefaultServing ? (
                        <Tag color="success">default</Tag>
                      ) : null}
                      {revision.isActiveServing ? (
                        <Tag color="processing">active</Tag>
                      ) : null}
                      {revision.isServingTarget ? (
                        <Tag color="blue">
                          {revision.allocationWeight}% {revision.servingState || 'serving'}
                        </Tag>
                      ) : null}
                    </Space>
                    <Typography.Text type="secondary">
                      {revision.implementationKind || 'workflow'} · updated{' '}
                      {formatScopeBindingRevisionTimestamp(revision)}
                    </Typography.Text>
                    {revision.failureReason ? (
                      <Typography.Text type="danger">
                        {revision.failureReason}
                      </Typography.Text>
                    ) : null}
                  </div>

                  <Space direction="vertical" size={8} align="end">
                    <Typography.Text type="secondary">
                      {revision.deploymentId || 'draft only'}
                    </Typography.Text>
                    <Button
                      type={canActivate ? 'primary' : 'default'}
                      disabled={!canActivate}
                      loading={pendingRevisionId === revision.revisionId}
                      onClick={() => onActivateRevision(revision.revisionId)}
                    >
                      {revision.isDefaultServing ? 'Serving' : 'Activate'}
                    </Button>
                  </Space>
                </div>
              );
            })}
          </div>
        </>
      )}
    </div>
  );
};

const studioPaletteCategoryIcons: Record<
  string,
  React.ComponentType<{ style?: React.CSSProperties }>
> = {
  ai: RobotOutlined,
  composition: AppstoreOutlined,
  control: ApartmentOutlined,
  data: DatabaseOutlined,
  human: UserOutlined,
  integration: ApiOutlined,
  validation: SafetyCertificateOutlined,
  custom: CodeOutlined,
};

type StudioInfoPopoverProps = {
  readonly open: boolean;
  readonly ariaLabel: string;
  readonly onOpenChange: (open: boolean) => void;
  readonly children: React.ReactNode;
};

const StudioInfoPopover: React.FC<StudioInfoPopoverProps> = ({
  open,
  ariaLabel,
  onOpenChange,
  children,
}) => {
  const popoverRef = React.useRef<HTMLDivElement | null>(null);
  const buttonRef = React.useRef<HTMLButtonElement | null>(null);
  const [popoverPosition, setPopoverPosition] = React.useState<{
    top: number;
    left: number;
    placement: 'top' | 'bottom';
  } | null>(null);

  React.useEffect(() => {
    if (!open) {
      return undefined;
    }

    const handlePointerDown = (event: PointerEvent) => {
      const target = event.target;
      if (!(target instanceof globalThis.Node)) {
        return;
      }

      if (
        !popoverRef.current?.contains(target) &&
        !buttonRef.current?.contains(target)
      ) {
        onOpenChange(false);
      }
    };

    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        onOpenChange(false);
      }
    };

    document.addEventListener('pointerdown', handlePointerDown);
    document.addEventListener('keydown', handleKeyDown);

    return () => {
      document.removeEventListener('pointerdown', handlePointerDown);
      document.removeEventListener('keydown', handleKeyDown);
    };
  }, [onOpenChange, open]);

  React.useEffect(() => {
    if (!open) {
      setPopoverPosition(null);
      return undefined;
    }

    const updatePosition = () => {
      const rect = buttonRef.current?.getBoundingClientRect();
      if (!rect) {
        return;
      }

      const cardWidth = 320;
      const estimatedCardHeight = 280;
      const viewportWidth = window.innerWidth;
      const viewportHeight = window.innerHeight;
      const margin = 12;
      const left = Math.min(
        Math.max(margin, rect.right - cardWidth),
        viewportWidth - cardWidth - margin,
      );
      const openAbove =
        rect.bottom + 10 + estimatedCardHeight > viewportHeight - margin &&
        rect.top - 10 - estimatedCardHeight > margin;

      setPopoverPosition({
        top: openAbove ? rect.top - 10 : rect.bottom + 10,
        left,
        placement: openAbove ? 'top' : 'bottom',
      });
    };

    updatePosition();

    window.addEventListener('resize', updatePosition);
    window.addEventListener('scroll', updatePosition, true);

    return () => {
      window.removeEventListener('resize', updatePosition);
      window.removeEventListener('scroll', updatePosition, true);
    };
  }, [open]);

  return (
    <div style={studioInfoPopoverStyle}>
      <button
        ref={buttonRef}
        type="button"
        aria-label={ariaLabel}
        title={ariaLabel}
        onClick={(event) => {
          event.stopPropagation();
          onOpenChange(!open);
        }}
        style={{
          ...studioInfoPopoverButtonStyle,
          ...(open
            ? {
                background: 'rgba(148, 163, 184, 0.1)',
                color: '#6B7280',
              }
            : null),
        }}
      >
        <QuestionCircleOutlined />
      </button>
      {open && popoverPosition
        ? ReactDOM.createPortal(
            <div
              ref={popoverRef}
              style={{
                ...studioInfoPopoverCardStyle,
                top: popoverPosition.top,
                left: popoverPosition.left,
                transform:
                  popoverPosition.placement === 'top'
                    ? 'translateY(-100%)'
                    : undefined,
              }}
              onClick={(event) => event.stopPropagation()}
            >
              {children}
            </div>,
            document.body,
          )
        : null}
    </div>
  );
};

function parseListText(value: string): string[] {
  return value
    .split(/[\n,]/)
    .map((item) => item.trim())
    .filter(Boolean);
}

export type StudioWorkspaceAlertsProps = {
  readonly authSession: {
    readonly enabled: boolean;
    readonly authenticated: boolean;
    readonly errorMessage?: string;
    readonly loginUrl?: string;
  } | null | undefined;
  readonly templateWorkflow: string;
  readonly draftMode: DraftMode;
  readonly legacySource: LegacySource;
};

export const StudioWorkspaceAlerts: React.FC<StudioWorkspaceAlertsProps> = ({
  authSession,
  draftMode,
  legacySource,
}) => {
  const notices: React.ReactNode[] = [];

  if (authSession?.enabled && !authSession.authenticated) {
    notices.push(
      <StudioNoticeCard
        key="auth"
        type="warning"
        title="Studio sign-in required"
        description={
          <Typography.Paragraph style={{ margin: 0 }} type="secondary">
            {authSession.errorMessage ||
              'Sign in to access protected Studio APIs.'}
          </Typography.Paragraph>
        }
        action={
          authSession.loginUrl ? (
            <Button
              type="link"
              href={authSession.loginUrl}
              style={{ paddingInline: 0, alignSelf: 'flex-start' }}
            >
              Sign in
            </Button>
          ) : undefined
        }
      />,
    );
  }

  if (draftMode === 'new') {
    notices.push(
      <StudioNoticeCard
        key="blank-draft"
        type="info"
        title="Blank Studio draft"
        description="You are editing a new unsaved workflow draft inside Studio. Save it to workspace once the YAML and metadata are ready."
      />,
    );
  }

  if (draftMode === 'new' && legacySource === 'playground') {
    notices.push(
      <StudioNoticeCard
        key="imported-draft"
        type="warning"
        title="Imported local draft"
        description="This Studio draft was loaded from your browser-stored draft so you can keep editing it in Studio."
      />,
    );
  }

  if (notices.length === 0) {
    return null;
  }

  return <div style={studioNoticeStripStyle}>{notices}</div>;
};

export type StudioWorkflowsPageProps = {
  readonly workflows: QueryState<StudioWorkflowSummary[]>;
  readonly workspaceSettings: QueryState<StudioWorkspaceSettings>;
  readonly workflowStorageMode: string;
  readonly selectedWorkflowId: string;
  readonly selectedDirectoryId: string;
  readonly templateWorkflow: string;
  readonly draftMode: DraftMode;
  readonly activeWorkflowName: string;
  readonly activeWorkflowDescription: string;
  readonly activeWorkflowSourceKey: string;
  readonly workflowSearch: string;
  readonly workflowLayout: StudioWorkflowLayout;
  readonly showDirectoryForm: boolean;
  readonly directoryPath: string;
  readonly directoryLabel: string;
  readonly workflowImportPending: boolean;
  readonly workflowImportInputRef: React.RefObject<HTMLInputElement | null>;
  readonly onOpenWorkflow: (workflowId: string) => void;
  readonly onStartBlankDraft: () => void;
  readonly onOpenCurrentDraft: () => void;
  readonly onSelectDirectoryId: (directoryId: string) => void;
  readonly onSetWorkflowSearch: (value: string) => void;
  readonly onSetWorkflowLayout: (value: StudioWorkflowLayout) => void;
  readonly onToggleDirectoryForm: () => void;
  readonly onSetDirectoryPath: (value: string) => void;
  readonly onSetDirectoryLabel: (value: string) => void;
  readonly onAddDirectory: () => void;
  readonly onRemoveDirectory: (directoryId: string) => void;
  readonly onWorkflowImportClick: () => void;
  readonly onWorkflowImportChange: React.ChangeEventHandler<HTMLInputElement>;
};

export const StudioWorkflowsPage: React.FC<StudioWorkflowsPageProps> = ({
  workflows,
  workspaceSettings,
  workflowStorageMode,
  selectedWorkflowId,
  selectedDirectoryId,
  templateWorkflow,
  draftMode,
  activeWorkflowName,
  activeWorkflowDescription,
  activeWorkflowSourceKey,
  workflowSearch,
  workflowLayout,
  showDirectoryForm,
  directoryPath,
  directoryLabel,
  workflowImportPending,
  workflowImportInputRef,
  onOpenWorkflow,
  onStartBlankDraft,
  onOpenCurrentDraft,
  onSelectDirectoryId,
  onSetWorkflowSearch,
  onSetWorkflowLayout,
  onToggleDirectoryForm,
  onSetDirectoryPath,
  onSetDirectoryLabel,
  onAddDirectory,
  onRemoveDirectory,
  onWorkflowImportClick,
  onWorkflowImportChange,
}) => {
  const directories = workspaceSettings.data?.directories ?? [];
  const filteredWorkflows = (workflows.data ?? []).filter((workflow) => {
    const keyword = workflowSearch.trim().toLowerCase();
    if (!keyword) {
      return true;
    }

    return [
      workflow.name,
      workflow.description,
      workflow.directoryLabel,
      workflow.fileName,
    ].some((value) => value.toLowerCase().includes(keyword));
  });

  return (
    <Row gutter={[16, 16]} align="stretch" style={workflowWorkspaceRowStyle}>
      <Col xs={24} xl={8} xxl={7} style={stretchColumnStyle}>
        <div style={workflowSidebarStackStyle}>
          <section
            style={{
              ...workflowSectionShellStyle,
              ...workflowSectionFillStyle,
            }}
          >
            <div style={workflowSectionHeaderStyle}>
              <div style={cardStackStyle}>
                <Typography.Text style={workflowSectionHeadingStyle}>
                  {workflowStorageMode === 'scope' ? 'Scope' : 'Directories'}
                </Typography.Text>
                <Typography.Text style={workflowSectionCopyStyle}>
                  {workflowStorageMode === 'scope'
                    ? 'Workflows follow the current resolved scope. tenantId and appId stay platform-managed and hidden from Studio.'
                    : 'New workflows are created in the selected directory.'}
                </Typography.Text>
              </div>
              {workflowStorageMode !== 'scope' ? (
                <Button
                  type="text"
                  icon={<FolderAddOutlined />}
                  onClick={onToggleDirectoryForm}
                  aria-label="Toggle workflow directory form"
                  style={workflowPanelIconButtonStyle}
                />
              ) : null}
            </div>

            {workspaceSettings.isLoading ? (
              <Typography.Text type="secondary">
                Loading workflow directories...
              </Typography.Text>
            ) : workspaceSettings.isError ? (
              <StudioNoticeCard
                type="error"
                title="Failed to load workspace settings"
                description={describeError(workspaceSettings.error)}
              />
            ) : (
              <div style={workflowDirectoryContentStyle}>
                {showDirectoryForm && workflowStorageMode !== 'scope' ? (
                  <div style={workflowSurfaceStyle}>
                    <div style={cardStackStyle}>
                      <Input
                        aria-label="Studio directory path"
                        placeholder="/absolute/path/to/workflows"
                        value={directoryPath}
                        onChange={(event) => onSetDirectoryPath(event.target.value)}
                      />
                      <Input
                        aria-label="Studio directory label"
                        placeholder="Directory label"
                        value={directoryLabel}
                        onChange={(event) => onSetDirectoryLabel(event.target.value)}
                      />
                      <Button type="primary" onClick={onAddDirectory}>
                        Add directory
                      </Button>
                    </div>
                  </div>
                ) : null}

                {directories.length > 0 ? (
                  <div style={workflowDirectoryListStyle}>
                    {directories.map((directory) => {
                      const active = selectedDirectoryId === directory.directoryId;
                      const showDirectoryPath =
                        workflowStorageMode !== 'scope' &&
                        !isScopeDirectoryPath(directory.path);

                      return (
                        <div
                          key={directory.directoryId}
                          style={{
                            ...workflowSurfaceStyle,
                            ...(active ? workflowSurfaceActiveStyle : {}),
                          }}
                        >
                          <div
                            style={{
                              display: 'flex',
                              alignItems: 'flex-start',
                              gap: 12,
                              justifyContent: 'space-between',
                            }}
                          >
                            <Button
                              type="text"
                              style={workflowDirectorySelectButtonStyle}
                              onClick={() => onSelectDirectoryId(directory.directoryId)}
                            >
                              <div style={workflowDirectoryTextStackStyle}>
                                <Typography.Text
                                  style={workflowDirectoryLabelStyle}
                                  ellipsis={{ tooltip: directory.label }}
                                >
                                  {directory.label}
                                </Typography.Text>
                                {showDirectoryPath ? (
                                  <Typography.Text
                                    style={workflowDirectoryPathStyle}
                                    ellipsis={{ tooltip: directory.path }}
                                  >
                                    {directory.path}
                                  </Typography.Text>
                                ) : null}
                              </div>
                            </Button>
                            {!directory.isBuiltIn && workflowStorageMode !== 'scope' ? (
                              <Button
                                type="text"
                                icon={<DeleteOutlined />}
                                aria-label={`Remove workflow directory ${directory.label}`}
                                onClick={() => onRemoveDirectory(directory.directoryId)}
                                style={{
                                  ...workflowPanelIconButtonStyle,
                                  width: 32,
                                  height: 32,
                                  color: 'var(--ant-color-text-tertiary)',
                                }}
                              >
                                <span style={{ display: 'none' }}>Remove</span>
                              </Button>
                            ) : null}
                          </div>
                        </div>
                      );
                    })}
                  </div>
                ) : (
                  <div style={workflowEmptyStateStyle}>
                    <div style={workflowEmptyStateInnerStyle}>
                      <Empty
                        image={Empty.PRESENTED_IMAGE_SIMPLE}
                        description="No workflow directories are configured yet."
                      />
                    </div>
                  </div>
                )}
              </div>
            )}
          </section>

          <section style={workflowSectionShellStyle}>
            <div style={workflowSectionHeaderStyle}>
              <div style={cardStackStyle}>
                <Typography.Text style={workflowSectionHeadingStyle}>
                  Current draft
                </Typography.Text>
                <Typography.Text style={workflowSectionCopyStyle}>
                  Keep the active draft in view while browsing the workspace library.
                </Typography.Text>
              </div>
            </div>

            <div style={workflowSurfaceStyle}>
              <div style={cardStackStyle}>
                <Space wrap size={[8, 8]}>
                  {selectedWorkflowId ? (
                    <Tag color="processing">Workspace workflow</Tag>
                  ) : null}
                  {templateWorkflow ? (
                    <Tag color="success">Published template</Tag>
                  ) : null}
                  {draftMode === 'new' ? <Tag color="gold">Blank draft</Tag> : null}
                </Space>
                <Typography.Text strong>
                  {activeWorkflowName || 'No draft selected'}
                </Typography.Text>
                <Typography.Text style={workflowSectionCopyStyle}>
                  {activeWorkflowDescription ||
                    'Pick a workflow or start a blank draft to open the Studio editor.'}
                </Typography.Text>
                <Space wrap size={[8, 8]}>
                  <Button
                    type="primary"
                    disabled={!activeWorkflowSourceKey}
                    onClick={onOpenCurrentDraft}
                    style={activeWorkflowSourceKey ? workflowSolidActionStyle : undefined}
                  >
                    Open editor
                  </Button>
                  <Button onClick={onStartBlankDraft} style={workflowGhostActionStyle}>
                    New workflow
                  </Button>
                </Space>
              </div>
            </div>
          </section>
        </div>
      </Col>

      <Col xs={24} xl={16} xxl={17} style={stretchColumnStyle}>
        <section style={workflowBrowserStyle}>
          <input
            ref={workflowImportInputRef}
            hidden
            accept=".yaml,.yml,text/yaml,text/x-yaml"
            type="file"
            onChange={onWorkflowImportChange}
          />

          <div style={workflowToolbarSurfaceStyle}>
            <div style={workflowToolbarLayoutStyle}>
              <div style={workflowSearchFieldStyle}>
                <SearchOutlined style={{ color: 'var(--ant-color-text-tertiary)' }} />
                <input
                  aria-label="Search workflows"
                  placeholder="Search workflows"
                  style={workflowSearchInputStyle}
                  value={workflowSearch}
                  onChange={(event) => onSetWorkflowSearch(event.target.value)}
                />
              </div>

              <div style={workflowToolbarActionsStyle}>
                <div style={workflowToggleGroupStyle}>
                  <button
                    type="button"
                    aria-label="Show workflows in a grid"
                    title="Show workflows in a grid"
                    onClick={() => onSetWorkflowLayout('grid')}
                    style={{
                      ...workflowToggleButtonStyle,
                      ...(workflowLayout === 'grid'
                        ? workflowToggleButtonActiveStyle
                        : {}),
                    }}
                  >
                    <AppstoreOutlined />
                  </button>
                  <button
                    type="button"
                    aria-label="Show workflows in a list"
                    title="Show workflows in a list"
                    onClick={() => onSetWorkflowLayout('list')}
                    style={{
                      ...workflowToggleButtonStyle,
                      ...(workflowLayout === 'list'
                        ? workflowToggleButtonActiveStyle
                        : {}),
                    }}
                  >
                    <BarsOutlined />
                  </button>
                </div>
                <Button
                  icon={<UploadOutlined />}
                  loading={workflowImportPending}
                  onClick={onWorkflowImportClick}
                  style={workflowGhostActionStyle}
                >
                  Import
                </Button>
                <Button
                  type="primary"
                  icon={<PlusOutlined />}
                  onClick={onStartBlankDraft}
                  style={workflowSolidActionStyle}
                >
                  New workflow
                </Button>
              </div>
            </div>
          </div>

          <div style={workflowResultsBodyStyle}>
            {workflows.isLoading ? (
              <Typography.Text type="secondary">
                Loading workflows...
              </Typography.Text>
            ) : workflows.isError ? (
              <StudioNoticeCard
                type="error"
                title="Failed to load workspace workflows"
                description={describeError(workflows.error)}
              />
            ) : filteredWorkflows.length > 0 ? (
              workflowLayout === 'grid' ? (
                <Row
                  gutter={[16, 16]}
                  style={{
                    ...workflowResultsViewportStyle,
                    maxWidth: 1080,
                  }}
                >
                  {filteredWorkflows.map((workflow) => {
                    const active = workflow.workflowId === selectedWorkflowId;
                    return (
                      <Col
                        key={workflow.workflowId}
                        xs={24}
                        md={24}
                        xxl={24}
                        style={stretchColumnStyle}
                      >
                        <button
                          type="button"
                          onClick={() => onOpenWorkflow(workflow.workflowId)}
                          style={{
                            ...workflowCardButtonBaseStyle,
                            ...(active ? workflowCardButtonActiveStyle : {}),
                            height: '100%',
                          }}
                        >
                          <div style={workflowCardRowStyle}>
                            <div
                              style={{
                                ...workflowCardIconStyle,
                                ...(active ? workflowCardIconActiveStyle : {}),
                              }}
                            >
                              <FileTextOutlined />
                            </div>
                            <div style={{ ...cardStackStyle, minWidth: 0 }}>
                              <Typography.Text style={workflowCardTitleStyle}>
                                {workflow.name}
                              </Typography.Text>
                              <Typography.Text style={workflowDirectoryPathStyle}>
                                {workflow.directoryLabel}
                              </Typography.Text>
                              <Typography.Text style={workflowDirectoryPathStyle}>
                                {workflow.stepCount} steps ·{' '}
                                {formatDateTime(workflow.updatedAtUtc)}
                              </Typography.Text>
                              {workflow.description ? (
                                <Typography.Paragraph style={workflowCardDescriptionStyle}>
                                  {workflow.description}
                                </Typography.Paragraph>
                              ) : null}
                            </div>
                          </div>
                        </button>
                      </Col>
                    );
                  })}
                </Row>
              ) : (
                <div
                  style={{
                    ...cardStackStyle,
                    ...workflowResultsViewportStyle,
                    maxWidth: 1080,
                  }}
                >
                  {filteredWorkflows.map((workflow) => {
                    const active = workflow.workflowId === selectedWorkflowId;
                    return (
                      <button
                        key={workflow.workflowId}
                        type="button"
                        onClick={() => onOpenWorkflow(workflow.workflowId)}
                        style={{
                          ...workflowCardButtonListStyle,
                          ...(active ? workflowCardButtonActiveStyle : {}),
                        }}
                      >
                        <div
                          style={{
                            ...workflowCardListRowStyle,
                            justifyContent: 'space-between',
                          }}
                        >
                          <div
                            style={{
                              ...workflowCardListRowStyle,
                              minWidth: 0,
                            }}
                          >
                            <div
                              style={{
                                ...workflowCardIconStyle,
                                ...(active ? workflowCardIconActiveStyle : {}),
                                width: 44,
                                height: 44,
                              }}
                            >
                              <FileTextOutlined />
                            </div>
                            <div style={{ minWidth: 0 }}>
                              <Typography.Text style={workflowCardTitleStyle}>
                                {workflow.name}
                              </Typography.Text>
                              <div style={workflowCardMetaLineStyle}>
                                <span>{workflow.directoryLabel}</span>
                                <span>{workflow.stepCount} steps</span>
                                <span>{formatDateTime(workflow.updatedAtUtc)}</span>
                              </div>
                              <Typography.Paragraph
                                style={workflowCardDescriptionCompactStyle}
                              >
                                {workflow.description || 'No description provided.'}
                              </Typography.Paragraph>
                            </div>
                          </div>
                          <span
                            style={{
                              ...workflowOpenHintStyle,
                              display: 'block',
                            }}
                          >
                            Open
                          </span>
                        </div>
                      </button>
                    );
                  })}
                </div>
              )
            ) : (
              <div style={workflowEmptyStateStyle}>
                <div style={workflowEmptyStateInnerStyle}>
                  <Empty
                    image={Empty.PRESENTED_IMAGE_SIMPLE}
                    description={
                      workflowSearch.trim()
                        ? 'No workflows match the current search.'
                        : 'Create a workflow with the New workflow button above.'
                    }
                  />
                </div>
              </div>
            )}
          </div>
        </section>
      </Col>
    </Row>
  );
};

export type StudioExecutionPageProps = {
  readonly executions: QueryState<StudioExecutionSummary[]>;
  readonly selectedExecution: QueryState<StudioExecutionDetail>;
  readonly workflowGraph: {
    readonly roles: StudioGraphRole[];
    readonly steps: StudioGraphStep[];
    readonly nodes: Node<StudioGraphNodeData>[];
    readonly edges: Edge<StudioGraphEdgeData>[];
  };
  readonly draftWorkflowName: string;
  readonly activeWorkflowName: string;
  readonly activeWorkflowDescription: string;
  readonly activeDirectoryLabel: string;
  readonly savePending: boolean;
  readonly canSaveWorkflow: boolean;
  readonly runPending: boolean;
  readonly canOpenRunWorkflow: boolean;
  readonly canRunWorkflow: boolean;
  readonly executionCanStop: boolean;
  readonly executionStopPending: boolean;
  readonly runPrompt: string;
  readonly executionNotice: StudioNoticeLike | null;
  readonly logsPopoutMode?: boolean;
  readonly logsDetached?: boolean;
  readonly onSwitchStudioView: (view: 'editor' | 'execution') => void;
  readonly onOpenExecution: (executionId: string) => void;
  readonly onSaveDraft: () => void;
  readonly onExportDraft: () => void;
  readonly onSetDraftWorkflowName: (value: string) => void;
  readonly onSetWorkflowDescription: (value: string) => void;
  readonly onRunPromptChange: (value: string) => void;
  readonly onStartExecution: () => void;
  readonly onResumeExecution: (
    interaction: ExecutionInteractionState,
    action: 'submit' | 'approve' | 'reject',
    userInput: string,
  ) => Promise<void>;
  readonly onStopExecution: () => void;
  readonly onPopOutLogs?: () => void;
};

export const StudioExecutionPage: React.FC<StudioExecutionPageProps> = ({
  executions,
  selectedExecution,
  workflowGraph,
  draftWorkflowName,
  activeWorkflowName,
  activeWorkflowDescription,
  activeDirectoryLabel,
  savePending,
  canSaveWorkflow,
  runPending,
  canOpenRunWorkflow,
  canRunWorkflow,
  executionCanStop,
  executionStopPending,
  runPrompt,
  executionNotice,
  logsPopoutMode = false,
  logsDetached = false,
  onSwitchStudioView,
  onOpenExecution,
  onSaveDraft,
  onExportDraft,
  onSetDraftWorkflowName,
  onSetWorkflowDescription,
  onRunPromptChange,
  onStartExecution,
  onResumeExecution,
  onStopExecution,
  onPopOutLogs,
}) => {
  const clampExecutionLogsHeight = React.useCallback((value: number) => {
    const minHeight = 220;
    const maxHeight =
      typeof window === 'undefined'
        ? 520
        : Math.max(minHeight, Math.min(520, window.innerHeight - 240));
    return Math.min(Math.max(value, minHeight), maxHeight);
  }, []);
  const [logsCollapsed, setLogsCollapsed] = React.useState(false);
  const [logsHeight, setLogsHeight] = React.useState(264);
  const [logsResizing, setLogsResizing] = React.useState(false);
  const [activeExecutionLogIndex, setActiveExecutionLogIndex] =
    React.useState<number | null>(null);
  const [copiedExecutionLogIndex, setCopiedExecutionLogIndex] =
    React.useState<number | null>(null);
  const [copiedAllExecutionLogs, setCopiedAllExecutionLogs] = React.useState(false);
  const [copiedExecutionActorId, setCopiedExecutionActorId] =
    React.useState<string | null>(null);
  const [executionActionInput, setExecutionActionInput] = React.useState('');
  const [executionActionPendingKey, setExecutionActionPendingKey] =
    React.useState('');
  const [runModalOpen, setRunModalOpen] = React.useState(false);
  const [descriptionEditorOpen, setDescriptionEditorOpen] = React.useState(false);
  const [descriptionDraft, setDescriptionDraft] = React.useState(
    activeWorkflowDescription,
  );
  const logsResizeSessionRef = React.useRef<{
    readonly clientY: number;
    readonly height: number;
  } | null>(null);

  const selectedExecutionDetail = selectedExecution.data;
  const executionTrace = React.useMemo(
    () => buildExecutionTrace(selectedExecutionDetail),
    [selectedExecutionDetail],
  );

  React.useEffect(() => {
    setActiveExecutionLogIndex(executionTrace?.defaultLogIndex ?? null);
    setExecutionActionInput('');
    setExecutionActionPendingKey('');
  }, [executionTrace, selectedExecutionDetail?.executionId]);

  React.useEffect(() => {
    if (!descriptionEditorOpen) {
      setDescriptionDraft(activeWorkflowDescription);
    }
  }, [activeWorkflowDescription, descriptionEditorOpen]);

  React.useEffect(() => {
    if (!logsResizing || typeof window === 'undefined') {
      return undefined;
    }

    const handleMouseMove = (event: MouseEvent) => {
      const session = logsResizeSessionRef.current;
      if (!session) {
        return;
      }

      const delta = session.clientY - event.clientY;
      setLogsHeight(clampExecutionLogsHeight(session.height + delta));
    };

    const handleMouseUp = () => {
      logsResizeSessionRef.current = null;
      setLogsResizing(false);
    };

    window.addEventListener('mousemove', handleMouseMove);
    window.addEventListener('mouseup', handleMouseUp);
    document.body.style.cursor = 'ns-resize';
    document.body.style.userSelect = 'none';

    return () => {
      window.removeEventListener('mousemove', handleMouseMove);
      window.removeEventListener('mouseup', handleMouseUp);
      document.body.style.cursor = '';
      document.body.style.userSelect = '';
    };
  }, [clampExecutionLogsHeight, logsResizing]);

  const currentWorkflowExecutions = React.useMemo(() => {
    const workflowName = activeWorkflowName.trim().toLowerCase();
    const items = executions.data ?? [];
    if (!workflowName) {
      return items;
    }

    return items.filter(
      (item) => item.workflowName.trim().toLowerCase() === workflowName,
    );
  }, [activeWorkflowName, executions.data]);

  const executionSummaryLabel = selectedExecutionDetail
    ? `${formatDateTime(selectedExecutionDetail.startedAtUtc)} · ${selectedExecutionDetail.status}`
    : currentWorkflowExecutions.length > 0
      ? `${currentWorkflowExecutions.length} runs`
      : 'No runs';
  const selectedExecutionActorId = selectedExecutionDetail?.actorId || null;
  const activeExecutionLog =
    executionTrace && Number.isInteger(activeExecutionLogIndex)
      ? executionTrace.logs[activeExecutionLogIndex as number] || null
      : null;
  const activeExecutionInteraction =
    activeExecutionLog?.interaction &&
    activeExecutionLog.stepId &&
    executionTrace?.stepStates.get(activeExecutionLog.stepId)?.status === 'waiting'
      ? activeExecutionLog.interaction
      : null;
  const executionActionKeyBase =
    selectedExecutionDetail?.executionId && activeExecutionInteraction
      ? `${selectedExecutionDetail.executionId}:${activeExecutionInteraction.stepId}`
      : '';
  const decoratedExecutionNodes = React.useMemo(
    () =>
      decorateNodesForExecution(
        workflowGraph.nodes,
        executionTrace,
        activeExecutionLogIndex,
      ),
    [activeExecutionLogIndex, executionTrace, workflowGraph.nodes],
  );
  const decoratedExecutionEdges = React.useMemo(
    () =>
      decorateEdgesForExecution(
        workflowGraph.edges,
        workflowGraph.nodes,
        executionTrace,
        activeExecutionLogIndex,
      ),
    [activeExecutionLogIndex, executionTrace, workflowGraph.edges, workflowGraph.nodes],
  );

  const copyText = async (value: string): Promise<boolean> => {
    if (!value || typeof navigator === 'undefined' || !navigator.clipboard) {
      return false;
    }

    await navigator.clipboard.writeText(value);
    return true;
  };

  const showExecutionLogCopyFeedback = (mode: 'single' | 'all', index?: number) => {
    setCopiedExecutionLogIndex(mode === 'single' ? index ?? null : null);
    setCopiedAllExecutionLogs(mode === 'all');
    window.setTimeout(() => {
      setCopiedExecutionLogIndex(null);
      setCopiedAllExecutionLogs(false);
    }, 1600);
  };

  const handleExecutionLogClick = async (
    log: NonNullable<typeof executionTrace>['logs'][number],
    index: number,
  ) => {
    setActiveExecutionLogIndex(index);
    const copied = await copyText(formatExecutionLogClipboard(log));
    if (copied) {
      showExecutionLogCopyFeedback('single', index);
    }
  };

  const handleCopyAllExecutionLogs = async () => {
    const copied = await copyText(formatExecutionLogsClipboard(executionTrace));
    if (copied) {
      showExecutionLogCopyFeedback('all');
    }
  };

  const handleCopyExecutionActorId = async (actorId: string) => {
    const copied = await copyText(actorId);
    if (!copied) {
      return;
    }

    setCopiedExecutionActorId(actorId);
    window.setTimeout(() => {
      setCopiedExecutionActorId((current) =>
        current === actorId ? null : current,
      );
    }, 1600);
  };

  const handleExecutionInteraction = async (
    interaction: ExecutionInteractionState,
    action: 'submit' | 'approve' | 'reject',
  ) => {
    const trimmedInput = executionActionInput.trim();
    if (interaction.kind === 'human_input' && !trimmedInput) {
      return;
    }

    const pendingKey = `${executionActionKeyBase}:${action}`;
    setExecutionActionPendingKey(pendingKey);

    try {
      await onResumeExecution(interaction, action, trimmedInput);
      setExecutionActionInput('');
      setLogsCollapsed(false);
    } finally {
      setExecutionActionPendingKey('');
    }
  };

  const renderExecutionLogsSection = ({
    fullscreen = false,
    overlay = false,
  }: {
    readonly fullscreen?: boolean;
    readonly overlay?: boolean;
  } = {}) => {
    const sectionCollapsed = !fullscreen && logsCollapsed;
    const popoutButtonVisible =
      !fullscreen && Boolean(selectedExecutionDetail?.executionId);
    const sectionClassName = [
      'execution-logs',
      sectionCollapsed ? 'collapsed' : '',
      fullscreen ? 'execution-logs-fullscreen' : '',
      overlay ? 'execution-logs-overlay' : '',
    ]
      .filter(Boolean)
      .join(' ');
    const executionLogsToggleLabel = logsDetached
      ? 'Viewing in new window'
      : executionSummaryLabel;
    const overlayHeight = sectionCollapsed ? 56 : logsHeight;

    return (
      <section
        className={sectionClassName}
        style={overlay && !fullscreen ? { height: overlayHeight } : undefined}
      >
        {overlay && !fullscreen && !sectionCollapsed ? (
          <button
            type="button"
            className="execution-logs-resize-handle"
            aria-label="Resize execution logs"
            title="Drag to resize logs"
            onMouseDown={(event) => {
              event.preventDefault();
              logsResizeSessionRef.current = {
                clientY: event.clientY,
                height: logsHeight,
              };
              setLogsResizing(true);
            }}
          />
        ) : null}
        <div className="execution-logs-header">
          <div>
            <div
              style={{
                color: 'var(--ant-color-text-tertiary)',
                fontSize: 11,
                letterSpacing: '0.16em',
                textTransform: 'uppercase',
              }}
            >
              Execution
            </div>
            <div style={{ color: '#1F2937', fontSize: 14, fontWeight: 600 }}>
              {fullscreen ? 'Execution logs' : 'Logs'}
            </div>
          </div>
          <div className="execution-logs-header-actions">
            {executionTrace?.logs?.length ? (
              <button
                type="button"
                className={`panel-icon-button execution-logs-copy-action ${copiedAllExecutionLogs ? 'active' : ''}`}
                title="Copy all execution logs."
                aria-label="Copy all execution logs."
                onClick={() => void handleCopyAllExecutionLogs()}
              >
                {copiedAllExecutionLogs ? <CheckOutlined /> : <CopyOutlined />}
              </button>
            ) : null}
            {popoutButtonVisible ? (
              <button
                type="button"
                className={`panel-icon-button execution-logs-window-action ${logsDetached ? 'active' : ''}`}
                title={logsDetached ? 'Focus logs window.' : 'Pop out'}
                aria-label="Pop out execution logs."
                onClick={onPopOutLogs}
              >
                <ExpandOutlined />
              </button>
            ) : null}
            {fullscreen ? (
              <button
                type="button"
                className="panel-icon-button execution-logs-window-action"
                title="Close window."
                aria-label="Close logs window."
                onClick={() => {
                  if (typeof window !== 'undefined') {
                    window.close();
                  }
                }}
              >
                <CloseOutlined />
              </button>
            ) : (
              <button
                type="button"
                onClick={() => {
                  if (logsDetached) {
                    onPopOutLogs?.();
                    return;
                  }

                  setLogsCollapsed((current) => !current);
                }}
                className="execution-logs-collapse-action"
                aria-expanded={!sectionCollapsed}
              >
                <span style={{ color: '#6B7280', fontSize: 12 }}>
                  {executionLogsToggleLabel}
                </span>
                {logsDetached ? (
                  <ExpandOutlined />
                ) : (
                  <DownOutlined
                    className={`execution-logs-collapse-icon ${sectionCollapsed ? 'collapsed' : ''}`}
                  />
                )}
              </button>
            )}
          </div>
        </div>

        {!sectionCollapsed ? (
          <div
            className="execution-logs-body"
            style={
              activeExecutionInteraction
                ? { gridTemplateColumns: '280px minmax(0, 1fr) 340px' }
                : undefined
            }
          >
            <div className="execution-runs-list">
              {currentWorkflowExecutions.length === 0 ? (
                <StudioCatalogEmptyPanel
                  icon={<CaretRightFilled style={{ color: '#CBD5E1' }} />}
                  title="No runs"
                  copy="Run the current workflow to inspect execution."
                />
              ) : (
                currentWorkflowExecutions.map((execution) => (
                  <button
                    key={execution.executionId}
                    type="button"
                    onClick={() => onOpenExecution(execution.executionId)}
                    className={`execution-run-card ${selectedExecutionDetail?.executionId === execution.executionId ? 'active' : ''}`}
                  >
                    <div
                      style={{
                        alignItems: 'center',
                        display: 'flex',
                        gap: 8,
                        justifyContent: 'space-between',
                      }}
                    >
                      <div style={{ color: '#1F2937', fontSize: 13, fontWeight: 600 }}>
                        {formatDateTime(execution.startedAtUtc)}
                      </div>
                      <span
                        style={{
                          color: 'var(--ant-color-text-tertiary)',
                          fontSize: 10,
                          letterSpacing: '0.08em',
                          textTransform: 'uppercase',
                        }}
                      >
                        {execution.status}
                      </span>
                    </div>
                    <div style={{ color: 'var(--ant-color-text-tertiary)', fontSize: 11, marginTop: 4 }}>
                      {formatDurationBetween(
                        execution.startedAtUtc,
                        execution.completedAtUtc,
                      )}
                    </div>
                    {execution.actorId ? (
                      <div className="execution-run-card-actor">
                        <div className="execution-run-card-actor-label">Actor ID</div>
                        <code
                          className="execution-run-card-actor-value"
                          title={execution.actorId}
                        >
                          {execution.actorId}
                        </code>
                      </div>
                    ) : null}
                  </button>
                ))
              )}
            </div>

            <div className="execution-log-stream">
              <div className="execution-log-list">
                {selectedExecutionActorId ? (
                  <div className="execution-run-identity">
                    <div className="execution-run-identity-head">
                      <div className="execution-run-identity-label">Actor ID</div>
                      <button
                        type="button"
                        className={`panel-icon-button execution-logs-copy-action ${copiedExecutionActorId === selectedExecutionActorId ? 'active' : ''}`}
                        title="Copy Actor ID."
                        aria-label="Copy Actor ID."
                        onClick={() =>
                          void handleCopyExecutionActorId(
                            selectedExecutionActorId,
                          )
                        }
                      >
                        {copiedExecutionActorId === selectedExecutionActorId ? (
                          <CheckOutlined />
                        ) : (
                          <CopyOutlined />
                        )}
                      </button>
                    </div>
                    <code
                      className="execution-run-identity-value"
                      title={selectedExecutionActorId}
                    >
                      {selectedExecutionActorId}
                    </code>
                  </div>
                ) : null}
                {executionTrace?.logs?.length ? (
                  executionTrace.logs.map((log, index) => (
                    <button
                      key={`${log.timestamp}-${log.stepId || 'run'}-${log.title}`}
                      type="button"
                      onClick={() => void handleExecutionLogClick(log, index)}
                      className={`execution-log-card tone-${log.tone} ${activeExecutionLogIndex === index ? 'active' : ''}`}
                      title="Click to copy this log."
                    >
                      <div className="execution-log-card-head">
                        <div style={{ color: '#1F2937', fontSize: 12, fontWeight: 600 }}>
                          {log.title}
                        </div>
                        <div className="execution-log-card-meta">
                          {copiedExecutionLogIndex === index ? (
                            <span className="execution-log-card-copied">
                              <CheckOutlined /> Copied
                            </span>
                          ) : null}
                          <div style={{ color: 'var(--ant-color-text-tertiary)', fontSize: 11 }}>
                            {formatDateTime(log.timestamp)}
                          </div>
                        </div>
                      </div>
                      {log.meta ? (
                        <div style={{ color: 'var(--ant-color-text-tertiary)', fontSize: 11, marginTop: 4 }}>
                          {log.meta}
                        </div>
                      ) : null}
                      {log.previewText ? (
                        <div className="execution-log-card-preview">
                          {log.previewText}
                        </div>
                      ) : null}
                    </button>
                  ))
                ) : (
                  <StudioCatalogEmptyPanel
                    icon={<FileTextOutlined style={{ color: '#CBD5E1' }} />}
                    title="No logs yet"
                    copy="Pick a run to inspect frames and step transitions."
                  />
                )}
              </div>

              {activeExecutionInteraction ? (
                <div className="execution-action-panel">
                  <div className="execution-action-intro">
                    <div
                      style={{
                        alignItems: 'flex-start',
                        display: 'flex',
                        gap: 12,
                        justifyContent: 'space-between',
                      }}
                    >
                      <div>
                        <div
                          style={{
                            color: 'var(--ant-color-text-tertiary)',
                            fontSize: 11,
                            letterSpacing: '0.14em',
                            textTransform: 'uppercase',
                          }}
                        >
                          Action required
                        </div>
                        <div
                          style={{
                            color: '#1F2937',
                            fontSize: 15,
                            fontWeight: 600,
                            marginTop: 4,
                          }}
                        >
                          {activeExecutionInteraction.kind === 'human_approval'
                            ? 'Human approval'
                            : 'Human input'}
                        </div>
                        <div className="execution-action-subtitle">
                          {activeExecutionInteraction.kind === 'human_approval'
                            ? 'Review the pending gate and approve or reject the run.'
                            : 'Provide the missing value to resume this workflow step.'}
                        </div>
                      </div>
                      <span className="execution-action-badge">
                        {activeExecutionInteraction.stepId}
                      </span>
                    </div>

                    <div className="execution-action-meta">
                      <span className="execution-action-chip">
                        <UserOutlined /> Human required
                      </span>
                      {activeExecutionInteraction.variableName ? (
                        <span className="execution-action-chip">
                          stores as {activeExecutionInteraction.variableName}
                        </span>
                      ) : null}
                      {activeExecutionInteraction.timeoutSeconds ? (
                        <span className="execution-action-chip">
                          timeout {activeExecutionInteraction.timeoutSeconds}s
                        </span>
                      ) : null}
                    </div>
                  </div>

                  {activeExecutionInteraction.prompt ? (
                    <div className="execution-action-block">
                      <div className="execution-action-block-label">Prompt</div>
                      <div className="execution-action-prompt">
                        {activeExecutionInteraction.prompt}
                      </div>
                    </div>
                  ) : null}

                  <div className="execution-action-block">
                    <div className="execution-action-field-head">
                      <label
                        className="field-label"
                        htmlFor="studio-execution-action-input"
                      >
                        {activeExecutionInteraction.kind === 'human_approval'
                          ? 'Feedback'
                          : activeExecutionInteraction.variableName || 'Input'}
                      </label>
                      <span
                        className={`execution-action-requirement ${activeExecutionInteraction.kind === 'human_approval' ? 'optional' : 'required'}`}
                      >
                        {activeExecutionInteraction.kind === 'human_approval'
                          ? 'Optional note'
                          : 'Required'}
                      </span>
                    </div>
                    <div className="execution-action-helper">
                      {activeExecutionInteraction.kind === 'human_approval'
                        ? 'Add context for the operator if needed, then approve or reject this gate.'
                        : activeExecutionInteraction.variableName
                          ? `The submitted value will resume the run and be available as ${activeExecutionInteraction.variableName}.`
                          : 'This response resumes the workflow immediately.'}
                    </div>
                    <textarea
                      id="studio-execution-action-input"
                      value={executionActionInput}
                      onChange={(event) => setExecutionActionInput(event.target.value)}
                      className="panel-textarea execution-action-textarea"
                      placeholder={
                        activeExecutionInteraction.kind === 'human_approval'
                          ? 'Optional feedback'
                          : 'Enter the value to continue this step'
                      }
                    />
                  </div>

                  <div className="execution-action-footer">
                    {activeExecutionInteraction.kind === 'human_approval' ? (
                      <>
                        <button
                          type="button"
                          className="ghost-action execution-danger-action"
                          disabled={
                            executionActionPendingKey ===
                            `${executionActionKeyBase}:reject`
                          }
                          onClick={() =>
                            void handleExecutionInteraction(
                              activeExecutionInteraction,
                              'reject',
                            )
                          }
                        >
                          {executionActionPendingKey ===
                          `${executionActionKeyBase}:reject`
                            ? 'Rejecting...'
                            : 'Reject'}
                        </button>
                        <button
                          type="button"
                          className="solid-action"
                          disabled={
                            executionActionPendingKey ===
                            `${executionActionKeyBase}:approve`
                          }
                          onClick={() =>
                            void handleExecutionInteraction(
                              activeExecutionInteraction,
                              'approve',
                            )
                          }
                        >
                          {executionActionPendingKey ===
                          `${executionActionKeyBase}:approve`
                            ? 'Approving...'
                            : 'Approve'}
                        </button>
                      </>
                    ) : (
                      <button
                        type="button"
                        className="solid-action"
                        disabled={
                          executionActionPendingKey ===
                          `${executionActionKeyBase}:submit`
                        }
                        onClick={() =>
                          void handleExecutionInteraction(
                            activeExecutionInteraction,
                            'submit',
                          )
                        }
                      >
                        {executionActionPendingKey ===
                        `${executionActionKeyBase}:submit`
                          ? 'Submitting...'
                          : 'Submit input'}
                      </button>
                    )}
                  </div>
                </div>
              ) : null}
            </div>
          </div>
        ) : null}
      </section>
    );
  };

  if (logsPopoutMode) {
    return (
      <div style={fillCardStyle}>
        {selectedExecution.isError ? (
          <StudioNoticeCard
            type="error"
            title="Failed to load execution detail"
            description={describeError(selectedExecution.error)}
          />
        ) : selectedExecution.data ? (
          renderExecutionLogsSection({ fullscreen: true })
        ) : (
          <Empty
            image={Empty.PRESENTED_IMAGE_SIMPLE}
            description="Pick a Studio execution to inspect its logs."
          />
        )}
      </div>
    );
  }

  const executionLogsBottomInset = logsCollapsed ? 84 : logsHeight + 28;

  return (
    <div style={cardStackStyle}>
      {executions.isError ? (
        <StudioNoticeCard
          type="error"
          title="Failed to load Studio executions"
          description={describeError(executions.error)}
        />
      ) : null}
      {selectedExecution.isError ? (
        <StudioNoticeCard
          type="error"
          title="Failed to load execution detail"
          description={describeError(selectedExecution.error)}
        />
      ) : null}
      {executionNotice ? (
        <StudioNoticeCard
          type={executionNotice.type}
          title={
            executionNotice.type === 'error'
              ? 'Execution action failed'
              : executionNotice.type === 'info'
                ? 'Execution stop requested'
                : 'Execution updated'
          }
          description={executionNotice.message}
        />
      ) : null}
      {selectedExecutionDetail?.error ? (
        <StudioNoticeCard
          type="error"
          title="Execution error"
          description={selectedExecutionDetail.error}
        />
      ) : null}

      <div
        style={{
          background: '#F2F1EE',
          border: '1px solid #E6E3DE',
          borderRadius: 36,
          boxShadow: '0 30px 72px rgba(15,23,42,0.08)',
          display: 'flex',
          flexDirection: 'column',
          minHeight: 'calc(100vh - 176px)',
          overflow: 'hidden',
        }}
      >
        <header className="studio-editor-header">
          <div className="studio-editor-toolbar">
            <div className="studio-view-switch">
              {(['editor', 'execution'] as const).map((view) => (
                <button
                  key={view}
                  type="button"
                  onClick={() => onSwitchStudioView(view)}
                  className={`studio-view-switch-button ${view === 'execution' ? 'active' : ''}`}
                >
                  {view === 'editor' ? 'Edit' : 'Runs'}
                </button>
              ))}
            </div>
            <div className="studio-title-bar">
              <div className="studio-title-group">
                <input
                  aria-label="Studio workflow title"
                  className="studio-title-input"
                  placeholder={activeWorkflowName || 'draft'}
                  value={draftWorkflowName}
                  onChange={(event) => onSetDraftWorkflowName(event.target.value)}
                />
                <StudioInfoPopover
                  open={descriptionEditorOpen}
                  ariaLabel="Edit workflow description"
                  onOpenChange={(open) => {
                    setDescriptionEditorOpen(open);
                    if (open) {
                      setDescriptionDraft(activeWorkflowDescription);
                    }
                  }}
                >
                  <textarea
                    aria-label="Studio workflow description"
                    placeholder="Workflow description"
                    value={descriptionDraft}
                    onChange={(event) => setDescriptionDraft(event.target.value)}
                    style={studioDescriptionEditorStyle}
                  />
                  <div style={studioInfoPopoverActionsStyle}>
                    <Button
                      size="small"
                      style={{ minWidth: 72, borderRadius: 10 }}
                      onClick={() => {
                        setDescriptionDraft(activeWorkflowDescription);
                        setDescriptionEditorOpen(false);
                      }}
                    >
                      Cancel
                    </Button>
                    <Button
                      type="primary"
                      size="small"
                      style={{ minWidth: 72, borderRadius: 10 }}
                      onClick={() => {
                        onSetWorkflowDescription(descriptionDraft);
                        setDescriptionEditorOpen(false);
                      }}
                    >
                      Save
                    </Button>
                  </div>
                </StudioInfoPopover>
              </div>
              <div className="studio-header-actions">
                <button
                  type="button"
                  onClick={onSaveDraft}
                  disabled={!canSaveWorkflow || savePending}
                  aria-label="Save"
                  title="Save"
                  className="panel-icon-button header-toolbar-action header-save-action"
                >
                  {savePending ? <LoadingOutlined /> : <SaveOutlined />}
                </button>
                <button
                  type="button"
                  onClick={onExportDraft}
                  aria-label="Export"
                  title="Export"
                  className="panel-icon-button header-toolbar-action header-export-action"
                >
                  <ExportOutlined />
                </button>
                <button
                  type="button"
                  onClick={() => setRunModalOpen(true)}
                  disabled={!canOpenRunWorkflow || runPending}
                  aria-label="Run"
                  title="Run"
                  className="panel-icon-button header-toolbar-action header-run-action"
                >
                  {runPending ? <LoadingOutlined /> : <PlayCircleFilled />}
                </button>
                {executionCanStop ? (
                  <button
                    type="button"
                    onClick={onStopExecution}
                    disabled={executionStopPending}
                    aria-label="Stop"
                    title="Stop"
                    className="panel-icon-button header-toolbar-action header-stop-action"
                  >
                    {executionStopPending ? <LoadingOutlined /> : <CloseCircleFilled />}
                  </button>
                ) : null}
              </div>
            </div>
          </div>
        </header>

        <section
          style={{
            background: '#F2F1EE',
            display: 'flex',
            flex: 1,
            flexDirection: 'column',
            minHeight: 0,
            overflow: 'hidden',
          }}
        >
          <div
            style={{
              flex: 1,
              minHeight: 0,
              overflow: 'hidden',
              position: 'relative',
            }}
          >
            <div className="canvas-overlay-stack">
              <div className="canvas-meta-card">
                <div className="canvas-meta-label">{activeDirectoryLabel}</div>
                <div className="canvas-meta-value">
                  {workflowGraph.nodes.length} nodes · {workflowGraph.edges.length} edges
                </div>
              </div>

              <div className="canvas-meta-card canvas-meta-card-wide">
                <div className="canvas-meta-label">Run</div>
                <select
                  className="canvas-meta-select"
                  value={selectedExecutionDetail?.executionId || ''}
                  onChange={(event) => {
                    if (event.target.value) {
                      onOpenExecution(event.target.value);
                    }
                  }}
                >
                  <option value="">{executionSummaryLabel}</option>
                  {currentWorkflowExecutions.map((execution) => (
                    <option key={execution.executionId} value={execution.executionId}>
                      {formatDateTime(execution.startedAtUtc)} · {execution.status}
                    </option>
                  ))}
                </select>
              </div>
            </div>

            <GraphCanvas
              height="100%"
              bottomInset={executionLogsBottomInset}
              variant="studio"
              nodes={decoratedExecutionNodes}
              edges={decoratedExecutionEdges}
              selectedNodeId={
                activeExecutionLog?.stepId
                  ? decoratedExecutionNodes.find(
                      (node) => node.data.stepId === activeExecutionLog.stepId,
                    )?.id
                  : undefined
              }
              onNodeSelect={(nodeId) => {
                const stepId =
                  decoratedExecutionNodes.find((node) => node.id === nodeId)?.data.stepId ||
                  '';
                const logIndex = findExecutionLogIndexForStep(executionTrace, stepId);
                if (logIndex !== null) {
                  setActiveExecutionLogIndex(logIndex);
                }
              }}
            />
            {renderExecutionLogsSection({ overlay: true })}
          </div>
        </section>
      </div>

      <Modal
        open={runModalOpen}
        title="Run"
        onCancel={() => setRunModalOpen(false)}
        onOk={() => {
          void onStartExecution();
          setRunModalOpen(false);
        }}
        okText="Run"
        okButtonProps={{
          disabled: !canRunWorkflow,
          loading: runPending,
          icon: <CaretRightFilled />,
        }}
      >
        <div style={cardStackStyle}>
          <Typography.Text type="secondary">
            Execution prompt will be passed into the workflow as{' '}
            <Typography.Text code>$input</Typography.Text>.
          </Typography.Text>
          <Input.TextArea
            aria-label="Studio execution prompt"
            autoSize={{ minRows: 6, maxRows: 10 }}
            className="run-prompt-textarea"
            placeholder="What should this run do?"
            value={runPrompt}
            onChange={(event) => onRunPromptChange(event.target.value)}
          />
        </div>
      </Modal>
    </div>
  );
};

export type StudioEditorPageProps = {
  readonly selectedWorkflow: QueryState<unknown>;
  readonly templateWorkflow: QueryState<unknown>;
  readonly connectors: QueryState<StudioConnectorCatalog>;
  readonly draftYaml: string;
  readonly draftWorkflowName: string;
  readonly draftDirectoryId: string;
  readonly draftFileName: string;
  readonly draftMode: DraftMode;
  readonly selectedWorkflowId: string;
  readonly templateWorkflowName: string;
  readonly activeWorkflowDescription: string;
  readonly activeWorkflowFile: StudioWorkflowFile | null | undefined;
  readonly isDraftDirty: boolean;
  readonly workflowGraph: {
    readonly roles: StudioGraphRole[];
    readonly steps: StudioGraphStep[];
    readonly nodes: Parameters<typeof GraphCanvas>[0]['nodes'];
    readonly edges: Parameters<typeof GraphCanvas>[0]['edges'];
  };
  readonly parseYaml: QueryState<{
    readonly document?: { readonly name?: string } | null;
    readonly findings: unknown[];
  }>;
  readonly selectedGraphNodeId: string;
  readonly selectedGraphEdge: StudioSelectedGraphEdge | null;
  readonly workflowRoleIds: string[];
  readonly workflowStepIds: string[];
  readonly inspectorTab: StudioInspectorTab;
  readonly inspectorContent: React.ReactNode;
  readonly workspaceSettings: QueryState<StudioWorkspaceSettings>;
  readonly savePending: boolean;
  readonly canSaveWorkflow: boolean;
  readonly saveNotice: StudioNoticeLike | null;
  readonly workflowImportPending: boolean;
  readonly workflowImportNotice: StudioNoticeLike | null;
  readonly workflowImportInputRef: React.RefObject<HTMLInputElement | null>;
  readonly askAiPrompt: string;
  readonly askAiPending: boolean;
  readonly askAiNotice: StudioNoticeLike | null;
  readonly askAiReasoning: string;
  readonly askAiAnswer: string;
  readonly runPrompt: string;
  readonly recentPromptHistory: readonly PlaygroundPromptHistoryEntry[];
  readonly promptHistoryCount: number;
  readonly runPending: boolean;
  readonly canOpenRunWorkflow: boolean;
  readonly canRunWorkflow: boolean;
  readonly runNotice: StudioNoticeLike | null;
  readonly resolvedScopeId?: string;
  readonly publishPending: boolean;
  readonly canPublishWorkflow: boolean;
  readonly publishNotice: StudioNoticeLike | null;
  readonly scopeBinding?: StudioScopeBindingStatus;
  readonly scopeBindingLoading: boolean;
  readonly scopeBindingError: unknown;
  readonly bindingActivationRevisionId: string;
  readonly onSwitchStudioView: (view: 'editor' | 'execution') => void;
  readonly onExportDraft: () => void;
  readonly onSelectGraphNode: (nodeId: string) => void;
  readonly onSelectGraphEdge: (edgeId: string) => void;
  readonly onClearGraphSelection: () => void;
  readonly onAddGraphNode: (
    stepType: string,
    connectorName?: string,
    preferredPosition?: { x: number; y: number } | null,
  ) => void;
  readonly onConnectGraphNodes: (sourceId: string, targetId: string) => void;
  readonly onUpdateGraphLayout: (
    nodes: Parameters<typeof GraphCanvas>[0]['nodes'],
  ) => void;
  readonly onDeleteSelectedGraphEdge: () => void;
  readonly onSetWorkflowDescription: (value: string) => void;
  readonly onSetDraftYaml: (value: string) => void;
  readonly onSetDraftWorkflowName: (value: string) => void;
  readonly onSetDraftDirectoryId: (value: string) => void;
  readonly onSetDraftFileName: (value: string) => void;
  readonly onSetInspectorTab: (tab: StudioInspectorTab) => void;
  readonly onValidateDraft: () => void;
  readonly onWorkflowImportClick: () => void;
  readonly onWorkflowImportChange: React.ChangeEventHandler<HTMLInputElement>;
  readonly onResetDraft: () => void;
  readonly onSaveDraft: () => void;
  readonly onPublishWorkflow: () => void;
  readonly onOpenProjectOverview: () => void;
  readonly onOpenProjectInvoke: () => void;
  readonly onBindGAgent: (input: {
    displayName?: string;
    actorTypeName: string;
    preferredActorId?: string;
    endpointId: string;
    endpointDisplayName?: string;
    requestTypeUrl?: string;
    responseTypeUrl?: string;
    description?: string;
    prompt?: string;
    payloadBase64?: string;
  }, options?: {
    openRuns?: boolean;
  }) => Promise<void>;
  readonly onActivateBindingRevision: (revisionId: string) => void;
  readonly onInspectPublishedWorkflow: () => void;
  readonly onRunInConsole: () => void;
  readonly onAskAiPromptChange: (value: string) => void;
  readonly onAskAiGenerate: () => void;
  readonly onRunPromptChange: (value: string) => void;
  readonly onClearPromptHistory: () => void;
  readonly onReusePrompt: (prompt: string) => void;
  readonly onOpenWorkflowFromHistory: (workflowName: string, prompt: string) => void;
  readonly onStartExecution: () => void;
  readonly onOpenExecutions: () => void;
};

export const StudioEditorPage: React.FC<StudioEditorPageProps> = ({
  selectedWorkflow,
  templateWorkflow,
  connectors,
  draftYaml,
  draftWorkflowName,
  draftDirectoryId,
  templateWorkflowName,
  activeWorkflowDescription,
  activeWorkflowFile,
  isDraftDirty,
  workflowGraph,
  selectedGraphNodeId,
  selectedGraphEdge,
  workflowRoleIds,
  workflowStepIds,
  inspectorTab,
  inspectorContent,
  workspaceSettings,
  savePending,
  canSaveWorkflow,
  saveNotice,
  workflowImportNotice,
  workflowImportInputRef,
  askAiPrompt,
  askAiPending,
  askAiNotice,
  askAiReasoning,
  askAiAnswer,
  runPrompt,
  runPending,
  canOpenRunWorkflow,
  canRunWorkflow,
  runNotice,
  resolvedScopeId,
  publishPending,
  canPublishWorkflow,
  publishNotice,
  scopeBinding,
  scopeBindingLoading,
  scopeBindingError,
  bindingActivationRevisionId,
  onSwitchStudioView,
  onExportDraft,
  onSelectGraphNode,
  onSelectGraphEdge,
  onClearGraphSelection,
  onAddGraphNode,
  onConnectGraphNodes,
  onUpdateGraphLayout,
  onDeleteSelectedGraphEdge,
  onSetWorkflowDescription,
  onSetDraftWorkflowName,
  onSetInspectorTab,
  onWorkflowImportChange,
  onSaveDraft,
  onPublishWorkflow,
  onOpenProjectOverview,
  onOpenProjectInvoke,
  onBindGAgent,
  onActivateBindingRevision,
  onRunInConsole,
  onAskAiPromptChange,
  onAskAiGenerate,
  onRunPromptChange,
  onStartExecution,
}) => {
  const hasSelectedGraphNode = Boolean(selectedGraphNodeId);
  const [toolDrawerMode, setToolDrawerMode] =
    React.useState<StudioToolDrawerMode | null>(null);
  const [nodePaletteSearch, setNodePaletteSearch] = React.useState('');
  const [nodePaletteSection, setNodePaletteSection] = React.useState('AI');
  const [inspectorDrawerOpen, setInspectorDrawerOpen] = React.useState(false);
  const [runModalOpen, setRunModalOpen] = React.useState(false);
  const [gAgentModalOpen, setGAgentModalOpen] = React.useState(false);
  const [gAgentBindingPending, setGAgentBindingPending] = React.useState(false);
  const [gAgentDraft, setGAgentDraft] = React.useState<StudioGAgentBindingDraft>(
    () => createStudioGAgentBindingDraft(draftWorkflowName || templateWorkflowName || ''),
  );
  const [descriptionEditorOpen, setDescriptionEditorOpen] = React.useState(false);
  const [descriptionDraft, setDescriptionDraft] = React.useState(activeWorkflowDescription);
  const [pendingAddPosition, setPendingAddPosition] = React.useState({
    x: 420,
    y: 220,
  });
  const hasResolvedProject = Boolean(resolvedScopeId);
  const hasNamedDraft = Boolean(draftWorkflowName.trim() && draftYaml.trim());

  const filteredPrimitiveCategories = React.useMemo(() => {
    const keyword = nodePaletteSearch.trim().toLowerCase();
    if (!keyword) {
      return STUDIO_GRAPH_CATEGORIES;
    }

    return STUDIO_GRAPH_CATEGORIES.map((category) => ({
      ...category,
      items: category.items.filter((item) => item.toLowerCase().includes(keyword)),
    })).filter((category) => category.items.length > 0);
  }, [nodePaletteSearch]);

  const filteredConnectorPalette = React.useMemo(() => {
    const keyword = nodePaletteSearch.trim().toLowerCase();
    const items = connectors.data?.connectors ?? [];
    if (!keyword) {
      return items;
    }

    return items.filter((connector) =>
      [connector.name, connector.type].some((value) =>
        value.toLowerCase().includes(keyword),
      ),
    );
  }, [connectors.data?.connectors, nodePaletteSearch]);

  React.useEffect(() => {
    if (toolDrawerMode !== 'palette') {
      return;
    }

    const hasExpandedCategory = filteredPrimitiveCategories.some(
      (category) => category.label === nodePaletteSection,
    );
    if (hasExpandedCategory || nodePaletteSection === 'Configured connectors') {
      return;
    }

    if (filteredPrimitiveCategories[0]?.label) {
      setNodePaletteSection(filteredPrimitiveCategories[0].label);
      return;
    }

    if (filteredConnectorPalette.length > 0) {
      setNodePaletteSection('Configured connectors');
    }
  }, [
    filteredConnectorPalette.length,
    filteredPrimitiveCategories,
    nodePaletteSection,
    toolDrawerMode,
  ]);

  const closeToolDrawer = () => {
    setToolDrawerMode(null);
  };

  const openGAgentModal = React.useCallback(() => {
    setGAgentDraft((current) => ({
      ...current,
      displayName: current.displayName || draftWorkflowName || templateWorkflowName || '',
    }));
    setGAgentModalOpen(true);
  }, [draftWorkflowName, templateWorkflowName]);

  const submitGAgentBinding = React.useCallback(async (openRuns: boolean) => {
    setGAgentBindingPending(true);
    try {
      await onBindGAgent(
        {
          displayName: gAgentDraft.displayName,
          actorTypeName: gAgentDraft.actorTypeName,
          preferredActorId: gAgentDraft.preferredActorId,
          endpointId: gAgentDraft.endpointId,
          endpointDisplayName: gAgentDraft.endpointDisplayName,
          requestTypeUrl: gAgentDraft.requestTypeUrl,
          responseTypeUrl: gAgentDraft.responseTypeUrl,
          description: gAgentDraft.description,
          prompt: gAgentDraft.prompt,
          payloadBase64: gAgentDraft.payloadBase64,
        },
        {
          openRuns,
        },
      );
      setGAgentModalOpen(false);
    } finally {
      setGAgentBindingPending(false);
    }
  }, [gAgentDraft, onBindGAgent]);

  const activeDirectoryLabel =
    workspaceSettings.data?.directories.find(
      (item) => item.directoryId === draftDirectoryId,
    )?.label ||
    activeWorkflowFile?.directoryLabel ||
    'No directory';
  const workflowDisplayName =
    draftWorkflowName.trim() ||
    activeWorkflowFile?.name ||
    templateWorkflowName ||
    'draft';

  React.useEffect(() => {
    if (inspectorTab === 'node') {
      setInspectorDrawerOpen(hasSelectedGraphNode);
    }
  }, [hasSelectedGraphNode, inspectorTab]);

  React.useEffect(() => {
    if (askAiPending || askAiNotice || askAiAnswer || askAiReasoning) {
      setToolDrawerMode('ask-ai');
    }
  }, [askAiAnswer, askAiNotice, askAiPending, askAiReasoning]);

  React.useEffect(() => {
    if (!descriptionEditorOpen) {
      setDescriptionDraft(activeWorkflowDescription);
    }
  }, [activeWorkflowDescription, descriptionEditorOpen]);

  const handleAddNodeFromPalette = (stepType: string, connectorName?: string) => {
    onAddGraphNode(stepType, connectorName, pendingAddPosition);
    setToolDrawerMode(null);
  };

  const askAiStatusText = askAiPending
    ? 'Generating and validating YAML...'
    : askAiAnswer.trim()
      ? 'Validated YAML applied to the current draft.'
      : 'Return format: workflow YAML only.';
  const toolDrawerVisible = Boolean(draftYaml) && toolDrawerMode !== null;
  const toolDrawerTitle =
    toolDrawerMode === 'palette' ? 'Add node' : 'Ask AI';
  const inspectorDrawerVisible =
    Boolean(draftYaml) &&
    inspectorDrawerOpen &&
    (inspectorTab !== 'node' || hasSelectedGraphNode);
  const inspectorDrawerTitle =
    inspectorTab === 'node'
      ? selectedGraphNodeId
        ? `Node · ${selectedGraphNodeId}`
        : 'Node'
      : inspectorTab === 'roles'
        ? 'Roles'
        : 'YAML';
  const nodePaletteDrawerContent = (
    <div style={cardStackStyle}>
      <div style={studioToolDrawerSectionStyle}>
        <Typography.Text strong>Node library</Typography.Text>
        <Typography.Paragraph style={{ margin: 0 }} type="secondary">
          Search primitives or configured connectors, then insert the next step at
          the current canvas target.
        </Typography.Paragraph>
        <Input
          allowClear
          prefix={<SearchOutlined />}
          placeholder="Search primitives or connectors"
          value={nodePaletteSearch}
          onChange={(event) => setNodePaletteSearch(event.target.value)}
        />
      </div>

      {filteredPrimitiveCategories.map((category) => {
        const Icon =
          studioPaletteCategoryIcons[category.key] ??
          studioPaletteCategoryIcons.custom;
        const expanded = nodePaletteSection === category.label;

        return (
          <div key={category.key} style={studioToolDrawerSectionStyle}>
            <Button
              type="text"
              block
              onClick={() =>
                setNodePaletteSection(expanded ? '' : category.label)
              }
              style={{
                alignItems: 'center',
                display: 'flex',
                gap: 12,
                height: 'auto',
                justifyContent: 'flex-start',
                padding: 0,
                textAlign: 'left',
              }}
            >
              <div
                style={{
                  alignItems: 'center',
                  background: `${category.color}18`,
                  borderRadius: 12,
                  color: category.color,
                  display: 'flex',
                  height: 32,
                  justifyContent: 'center',
                  width: 32,
                }}
              >
                <Icon />
              </div>
              <Typography.Text strong style={{ flex: 1 }}>
                {category.label}
              </Typography.Text>
              <Tag>{category.items.length}</Tag>
            </Button>
            {expanded ? (
              <div style={cardListStyle}>
                {category.items.map((item) => (
                  <div key={item} style={cardListItemStyle}>
                    <div style={cardListHeaderStyle}>
                      <div style={cardListMainStyle}>
                        <Typography.Text strong>{item}</Typography.Text>
                        <Typography.Text type="secondary">
                          {category.label}
                        </Typography.Text>
                      </div>
                      <div style={cardListActionStyle}>
                        <Button
                          size="small"
                          onClick={() => handleAddNodeFromPalette(item)}
                        >
                          Insert
                        </Button>
                      </div>
                    </div>
                  </div>
                ))}
              </div>
            ) : null}
          </div>
        );
      })}

      {filteredConnectorPalette.length > 0 ? (
        <div style={studioToolDrawerSectionStyle}>
          <Button
            type="text"
            block
            onClick={() =>
              setNodePaletteSection(
                nodePaletteSection === 'Configured connectors'
                  ? ''
                  : 'Configured connectors',
              )
            }
            style={{
              alignItems: 'center',
              display: 'flex',
              gap: 12,
              height: 'auto',
              justifyContent: 'flex-start',
              padding: 0,
              textAlign: 'left',
            }}
          >
            <div
              style={{
                alignItems: 'center',
                background: '#64748B18',
                borderRadius: 12,
                color: '#64748B',
                display: 'flex',
                height: 32,
                justifyContent: 'center',
                width: 32,
              }}
            >
              <ApiOutlined />
            </div>
            <Typography.Text strong style={{ flex: 1 }}>
              Configured connectors
            </Typography.Text>
            <Tag>{filteredConnectorPalette.length}</Tag>
          </Button>
          {nodePaletteSection === 'Configured connectors' ? (
            <div style={cardListStyle}>
              {filteredConnectorPalette.map((connector) => (
                <div key={connector.name} style={cardListItemStyle}>
                  <div style={cardListHeaderStyle}>
                    <div style={cardListMainStyle}>
                      <Typography.Text strong>{connector.name}</Typography.Text>
                      <Space wrap size={[6, 6]}>
                        <Tag>{connector.type}</Tag>
                        {connector.enabled ? (
                          <Tag color="success">enabled</Tag>
                        ) : (
                          <Tag color="default">disabled</Tag>
                        )}
                      </Space>
                    </div>
                    <div style={cardListActionStyle}>
                      <Button
                        size="small"
                        onClick={() =>
                          handleAddNodeFromPalette(
                            'connector_call',
                            connector.name,
                          )
                        }
                      >
                        Insert
                      </Button>
                    </div>
                  </div>
                </div>
              ))}
            </div>
          ) : null}
        </div>
      ) : null}
    </div>
  );
  const askAiDrawerContent = (
    <div style={cardStackStyle}>
      <div style={studioToolDrawerSectionStyle}>
        <Typography.Text strong>Workflow prompt</Typography.Text>
        <Typography.Paragraph style={{ margin: 0 }} type="secondary">
          Describe the workflow outcome. Studio applies validated YAML back into
          the current draft automatically.
        </Typography.Paragraph>
        <Input.TextArea
          aria-label="Studio AI workflow prompt"
          autoSize={{ minRows: 5, maxRows: 10 }}
          placeholder="Build a workflow that triages incidents, routes risky cases to human approval, and posts the result to Slack."
          value={askAiPrompt}
          onChange={(event) => onAskAiPromptChange(event.target.value)}
        />
        <div
          style={{
            alignItems: 'center',
            display: 'flex',
            gap: 12,
            justifyContent: 'space-between',
          }}
        >
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            {askAiStatusText}
          </Typography.Text>
          <Button
            type="primary"
            loading={askAiPending}
            onClick={onAskAiGenerate}
          >
            {askAiPending ? 'Thinking' : 'Generate'}
          </Button>
        </div>
      </div>

      {askAiNotice ? (
        <StudioNoticeCard
          type={askAiNotice.type}
          title={
            askAiNotice.type === 'error'
              ? 'Studio AI generation failed'
              : 'Studio AI generation updated the draft'
          }
          description={askAiNotice.message}
        />
      ) : null}

      <div style={studioToolDrawerSectionStyle}>
        <Typography.Text strong>Thinking</Typography.Text>
        <pre style={{ ...codeBlockStyle, margin: 0, maxHeight: 160 }}>
          {askAiReasoning || 'LLM reasoning will stream here.'}
        </pre>
      </div>

      <div style={studioToolDrawerSectionStyle}>
        <div
          style={{
            alignItems: 'center',
            display: 'flex',
            gap: 12,
            justifyContent: 'space-between',
          }}
        >
          <Typography.Text strong>Validated YAML</Typography.Text>
          <Tag color={askAiAnswer.trim() ? 'success' : 'default'}>
            {askAiAnswer.trim() ? 'Applied to draft' : 'Waiting for valid YAML'}
          </Tag>
        </div>
        <pre style={{ ...codeBlockStyle, margin: 0, maxHeight: 260 }}>
          {askAiAnswer || 'Validated workflow YAML will appear here.'}
        </pre>
      </div>
    </div>
  );

  const editorStatusItems: React.ReactNode[] = [];

  if (!hasResolvedProject) {
    editorStatusItems.push(
      <StudioNoticeCard
        key="recommended-project-step"
        type="warning"
        title="Next step: resolve the current project"
        description="This workflow path only becomes a real project flow after Studio resolves the current project scope. Once resolved, save the asset, run the draft, then bind the project."
      />,
    );
  } else if (!hasNamedDraft) {
    editorStatusItems.push(
      <StudioNoticeCard
        key="recommended-draft-step"
        type="warning"
        title="Next step: finish the workflow draft"
        description="Add a workflow name and valid YAML first. Then save the asset before you run the draft or bind the project."
      />,
    );
  } else if (isDraftDirty && canSaveWorkflow) {
    editorStatusItems.push(
      <StudioNoticeCard
        key="recommended-save-step"
        type="info"
        title="Next step: Save asset"
        description="Save stores this workflow as a named project asset. That keeps the project catalog in sync before you verify the draft or publish the default binding."
        action={
          <Space wrap>
            <Button type="primary" onClick={onSaveDraft} loading={savePending}>
              Save asset
            </Button>
          </Space>
        }
      />,
    );
  } else if (saveNotice?.type === 'success') {
    editorStatusItems.push(
      <StudioNoticeCard
        key="recommended-run-step"
        type="success"
        title="Next step: Run draft"
        description="Use Run draft to verify the inline workflow bundle first. After the draft run looks right, bind the project so Project Invoke can use the published entrypoint."
        action={
          <Space wrap>
            <Button
              type="primary"
              onClick={onRunInConsole}
              disabled={!canOpenRunWorkflow}
            >
              Run draft
            </Button>
            <Button
              onClick={onPublishWorkflow}
              loading={publishPending}
              disabled={!canPublishWorkflow}
            >
              Bind project
            </Button>
          </Space>
        }
      />,
    );
  } else if (!scopeBinding?.available) {
    editorStatusItems.push(
      <StudioNoticeCard
        key="recommended-bind-step"
        type="warning"
        title="Next step: Bind project"
        description="The workflow asset is ready, but Project Invoke still has no active default project binding. Bind this workflow when you want the published project path to use it."
        action={
          <Space wrap>
            <Button
              type="primary"
              onClick={onPublishWorkflow}
              loading={publishPending}
              disabled={!canPublishWorkflow}
            >
              Bind project
            </Button>
            <Button onClick={onOpenProjectOverview}>Open Project Overview</Button>
          </Space>
        }
      />,
    );
  } else {
    editorStatusItems.push(
      <StudioNoticeCard
        key="recommended-invoke-step"
        type="success"
        title="Next step: Open Project Invoke"
        description="The default project binding is already active. Move to Project Invoke to test the published entrypoint, then continue in Runs for the full event trace."
        action={
          <Space wrap>
            <Button type="primary" onClick={onOpenProjectInvoke}>
              Open Project Invoke
            </Button>
            <Button onClick={onOpenProjectOverview}>Open Project Overview</Button>
            <Button
              onClick={onRunInConsole}
              disabled={!canOpenRunWorkflow}
            >
              Run draft
            </Button>
          </Space>
        }
      />,
    );
  }

  if (saveNotice) {
    editorStatusItems.push(
      <StudioNoticeCard
        key="save-notice"
        type={saveNotice.type}
        title={
          saveNotice.type === 'success'
            ? 'Workflow saved'
            : 'Workflow save failed'
        }
        description={saveNotice.message}
      />,
    );
  }

  if (workflowImportNotice) {
    editorStatusItems.push(
      <StudioNoticeCard
        key="workflow-import-notice"
        type={workflowImportNotice.type}
        title={
          workflowImportNotice.type === 'error'
            ? 'Workflow import failed'
            : 'Workflow imported'
        }
        description={workflowImportNotice.message}
      />,
    );
  }

  if (runNotice) {
    editorStatusItems.push(
      <StudioNoticeCard
        key="run-notice"
        type={runNotice.type}
        title={
          runNotice.type === 'success'
            ? 'Draft run ready'
            : 'Draft run failed'
        }
        description={runNotice.message}
      />,
    );
  }

  if (publishNotice) {
    editorStatusItems.push(
      <StudioNoticeCard
        key="publish-notice"
        type={publishNotice.type}
        title={
          publishNotice.type === 'error'
            ? 'Scope binding failed'
            : 'Scope binding updated'
        }
        description={publishNotice.message}
      />,
    );
  }

  const editorFatalNotice = selectedWorkflow.isError ? (
    <StudioNoticeCard
      key="selected-workflow-error"
      type="error"
      title="Failed to load Studio workflow"
      description={describeError(selectedWorkflow.error)}
    />
  ) : templateWorkflow.isError ? (
    <StudioNoticeCard
      key="template-workflow-error"
      type="error"
      title="Failed to load published workflow template"
      description={describeError(templateWorkflow.error)}
    />
  ) : null;

  return (
    <div style={cardStackStyle}>
      {editorStatusItems.length > 0 ? (
        <div style={studioNoticeStripStyle}>{editorStatusItems}</div>
      ) : null}

      <StudioScopeBindingPanel
        scopeId={resolvedScopeId}
        binding={scopeBinding}
        loading={scopeBindingLoading}
        error={scopeBindingError}
        pendingRevisionId={bindingActivationRevisionId}
        onActivateRevision={onActivateBindingRevision}
      />

      {editorFatalNotice ? (
        <div style={studioNoticeStripStyle}>{editorFatalNotice}</div>
      ) : draftYaml ? (
        <>
          <input
            ref={workflowImportInputRef}
            hidden
            accept=".yaml,.yml,text/yaml,text/x-yaml"
            type="file"
            onChange={onWorkflowImportChange}
          />

          <div
            style={{
              background: '#F2F1EE',
              border: '1px solid #E6E3DE',
              borderRadius: 36,
              boxShadow: '0 30px 72px rgba(15,23,42,0.08)',
              display: 'flex',
              flexDirection: 'column',
              minHeight: 'calc(100vh - 176px)',
              overflow: 'hidden',
            }}
          >
            <div
              style={{
                alignItems: 'center',
                background: 'rgba(255,255,255,0.96)',
                borderBottom: '1px solid #E8E2D9',
                display: 'flex',
                gap: 12,
                justifyContent: 'space-between',
                padding: '14px 18px',
              }}
            >
              <div
                style={{
                  alignItems: 'center',
                  display: 'flex',
                  flex: 1,
                  gap: 12,
                  minWidth: 0,
                }}
              >
                <Space.Compact>
                  <Button type="primary" onClick={() => onSwitchStudioView('editor')}>
                    Edit
                  </Button>
                  <Button onClick={() => onSwitchStudioView('execution')}>Runs</Button>
                </Space.Compact>
                <div style={studioTitleBarStyle}>
                  <div style={studioTitleGroupStyle}>
                    <input
                      aria-label="Studio workflow title"
                      placeholder={workflowDisplayName}
                      value={draftWorkflowName}
                      onChange={(event) => onSetDraftWorkflowName(event.target.value)}
                      style={studioTitleInputStyle}
                    />
                    <StudioInfoPopover
                      open={descriptionEditorOpen}
                      ariaLabel="Edit workflow description"
                      onOpenChange={(open) => {
                        setDescriptionEditorOpen(open);
                        if (open) {
                          setDescriptionDraft(activeWorkflowDescription);
                        }
                      }}
                    >
                      <textarea
                        aria-label="Studio workflow description"
                        placeholder="Workflow description"
                        value={descriptionDraft}
                        onChange={(event) =>
                          setDescriptionDraft(event.target.value)
                        }
                        style={studioDescriptionEditorStyle}
                      />
                      <div style={studioInfoPopoverActionsStyle}>
                        <Button
                          size="small"
                          style={{ minWidth: 72, borderRadius: 10 }}
                          onClick={() => {
                            setDescriptionDraft(activeWorkflowDescription);
                            setDescriptionEditorOpen(false);
                          }}
                        >
                          Cancel
                        </Button>
                        <Button
                          type="primary"
                          size="small"
                          style={{ minWidth: 72, borderRadius: 10 }}
                          onClick={() => {
                            onSetWorkflowDescription(descriptionDraft);
                            setDescriptionEditorOpen(false);
                          }}
                        >
                          Save
                        </Button>
                      </div>
                    </StudioInfoPopover>
                  </div>
                </div>
              </div>

              <div
                style={{
                  alignItems: 'center',
                  display: 'flex',
                  gap: 10,
                  justifyContent: 'flex-end',
                  minWidth: 0,
                }}
              >
                <Button
                  icon={<AppstoreOutlined />}
                  onClick={() => setToolDrawerMode('palette')}
                >
                  Add node
                </Button>
                <Button
                  icon={<RobotOutlined />}
                  type={toolDrawerMode === 'ask-ai' ? 'primary' : 'default'}
                  onClick={() => setToolDrawerMode('ask-ai')}
                >
                  Ask AI
                </Button>
                <Button
                  icon={<BarsOutlined />}
                  onClick={onRunInConsole}
                  disabled={!resolvedScopeId}
                >
                  Run draft
                </Button>
                <Button
                  icon={<RobotOutlined />}
                  onClick={openGAgentModal}
                  disabled={!resolvedScopeId}
                >
                  GAgent service
                </Button>
                <Button
                  icon={<SafetyCertificateOutlined />}
                  loading={publishPending}
                  disabled={!canPublishWorkflow}
                  onClick={onPublishWorkflow}
                >
                  Bind project
                </Button>
                <Button
                  type="text"
                  shape="circle"
                  icon={<CheckOutlined />}
                  loading={savePending}
                  disabled={!canSaveWorkflow}
                  onClick={onSaveDraft}
                  aria-label="Save"
                  title="Save"
                />
                <Button
                  type="text"
                  shape="circle"
                  icon={<UploadOutlined />}
                  onClick={onExportDraft}
                  aria-label="Export"
                  title="Export"
                />
                <Button
                  type="primary"
                  shape="circle"
                  icon={<CaretRightFilled />}
                  disabled={!canOpenRunWorkflow}
                  onClick={() => setRunModalOpen(true)}
                  aria-label="Run"
                  title="Run"
                />
              </div>
            </div>

            <div style={{ padding: '12px 12px 0' }}>
              <div style={studioStatusStripStyle}>
                <div style={studioStatusPillStyle}>
                  <Typography.Text type="secondary" style={{ fontSize: 11 }}>
                    Active directory
                  </Typography.Text>
                  <Typography.Text strong ellipsis={{ tooltip: activeDirectoryLabel }}>
                    {activeDirectoryLabel}
                  </Typography.Text>
                </div>
                <div style={studioStatusPillStyle}>
                  <Typography.Text type="secondary" style={{ fontSize: 11 }}>
                    Graph
                  </Typography.Text>
                  <Typography.Text strong>
                    {workflowGraph.nodes.length} nodes · {workflowGraph.edges.length}{' '}
                    edges
                  </Typography.Text>
                </div>
                <div style={studioStatusPillStyle}>
                  <Typography.Text type="secondary" style={{ fontSize: 11 }}>
                    Draft state
                  </Typography.Text>
                  <Space wrap size={[6, 6]}>
                    <Tag color={isDraftDirty ? 'warning' : 'success'}>
                      {isDraftDirty ? 'Unsaved changes' : 'In sync'}
                    </Tag>
                    {selectedGraphNodeId ? (
                      <Typography.Text type="secondary">
                        Selected: {selectedGraphNodeId}
                      </Typography.Text>
                    ) : null}
                  </Space>
                </div>
              </div>
            </div>

            <div
              style={{
                display: 'flex',
                flex: 1,
                minHeight: 0,
                padding: 12,
              }}
            >
              <div
                style={studioCanvasViewportStyle}
              >
                <div
                  style={{
                    alignItems: 'center',
                    display: 'flex',
                    flexDirection: 'column',
                    gap: 10,
                    position: 'absolute',
                    right: 16,
                    top: 16,
                    zIndex: 4,
                  }}
                >
                  <Button
                    shape="circle"
                    icon={<UserOutlined />}
                    onClick={() => {
                      onSetInspectorTab('roles');
                      setInspectorDrawerOpen(true);
                    }}
                    type={inspectorTab === 'roles' && inspectorDrawerOpen ? 'primary' : 'default'}
                    aria-label="Open roles inspector"
                    title="Roles"
                    style={{ height: 40, width: 40 }}
                  />
                  <Button
                    shape="circle"
                    icon={<CodeOutlined />}
                    onClick={() => {
                      onSetInspectorTab('yaml');
                      setInspectorDrawerOpen(true);
                    }}
                    type={inspectorTab === 'yaml' && inspectorDrawerOpen ? 'primary' : 'default'}
                    aria-label="Open YAML inspector"
                    title="YAML"
                    style={{ height: 40, width: 40 }}
                  />
                </div>

                {selectedGraphEdge ? (
                  <div
                    style={{
                      bottom: 24,
                      left: 24,
                      position: 'absolute',
                      zIndex: 2,
                    }}
                  >
                    <div
                      style={{
                        backdropFilter: 'blur(12px)',
                        background: 'rgba(255, 255, 255, 0.96)',
                        border: '1px solid #E8E2D9',
                        borderRadius: 20,
                        boxShadow: '0 14px 32px rgba(17, 24, 39, 0.10)',
                        maxWidth: 320,
                        padding: '12px 14px',
                      }}
                    >
                      <div style={cardStackStyle}>
                        <Typography.Text strong>Connection selected</Typography.Text>
                        <Typography.Text type="secondary">
                          {selectedGraphEdge.branchLabel
                            ? `${selectedGraphEdge.sourceStepId} -> ${selectedGraphEdge.targetStepId} (${selectedGraphEdge.branchLabel})`
                            : `${selectedGraphEdge.sourceStepId} -> ${selectedGraphEdge.targetStepId}`}
                        </Typography.Text>
                        <Space wrap size={[8, 8]}>
                          <Tag color={selectedGraphEdge.kind === 'branch' ? 'purple' : 'processing'}>
                            {selectedGraphEdge.kind === 'branch' ? 'branch' : 'next'}
                          </Tag>
                          {selectedGraphEdge.implicit ? <Tag>auto-flow</Tag> : null}
                          <Button
                            danger
                            size="small"
                            disabled={selectedGraphEdge.implicit}
                            onClick={onDeleteSelectedGraphEdge}
                          >
                            Remove connection
                          </Button>
                        </Space>
                      </div>
                    </div>
                  </div>
                ) : null}

                {workflowGraph.nodes.length === 0 ? (
                  <div
                    style={{
                      alignItems: 'center',
                      color: '#6B7280',
                      display: 'flex',
                      inset: 12,
                      justifyContent: 'center',
                      pointerEvents: 'none',
                      position: 'absolute',
                      textAlign: 'center',
                      zIndex: 1,
                    }}
                  >
                    <div>
                      <Typography.Text strong style={{ display: 'block' }}>
                        This workflow has no steps yet.
                      </Typography.Text>
                      <Typography.Text type="secondary">
                        Right click the canvas to place the first workflow step.
                      </Typography.Text>
                    </div>
                  </div>
                ) : null}

                <GraphCanvas
                  nodes={workflowGraph.nodes}
                  edges={workflowGraph.edges}
                  height="calc(100vh - 278px)"
                  variant="studio"
                  selectedNodeId={selectedGraphNodeId}
                  selectedEdgeId={selectedGraphEdge?.edgeId}
                  onNodeSelect={(nodeId) => {
                    setInspectorDrawerOpen(true);
                    onSelectGraphNode(nodeId);
                  }}
                  onEdgeSelect={(edgeId) => {
                    onSelectGraphEdge(edgeId);
                  }}
                  onCanvasSelect={() => {
                    onClearGraphSelection();
                  }}
                  onCanvasContextMenu={({ flowX, flowY }) => {
                    setPendingAddPosition({
                      x: flowX,
                      y: flowY,
                    });
                    setToolDrawerMode('palette');
                  }}
                  onConnectNodes={onConnectGraphNodes}
                  onNodeLayoutChange={onUpdateGraphLayout}
                />

              </div>
            </div>
          </div>

          <Drawer
            open={toolDrawerVisible}
            title={toolDrawerTitle}
            placement="left"
            size={420}
            mask={false}
            onClose={closeToolDrawer}
            destroyOnClose={false}
            styles={{ body: drawerBodyStyle }}
          >
            <div style={drawerScrollStyle}>
              {toolDrawerMode === 'palette'
                ? nodePaletteDrawerContent
                : askAiDrawerContent}
            </div>
          </Drawer>

          <Drawer
            open={inspectorDrawerVisible}
            title={inspectorDrawerTitle}
            placement="right"
            size={420}
            onClose={() => setInspectorDrawerOpen(false)}
            destroyOnClose={false}
            styles={{ body: drawerBodyStyle }}
          >
            <div style={drawerScrollStyle}>
              <datalist id="studio-workflow-role-options">
                {workflowRoleIds.map((roleId) => (
                  <option key={roleId} value={roleId} />
                ))}
              </datalist>
              <datalist id="studio-workflow-step-options">
                {workflowStepIds.map((stepId) => (
                  <option key={stepId} value={stepId} />
                ))}
              </datalist>
              {inspectorContent}
            </div>
          </Drawer>

          <Modal
            open={gAgentModalOpen}
            title="Bind GAgent service"
            onCancel={() => setGAgentModalOpen(false)}
            footer={[
              <Button
                key="cancel"
                onClick={() => setGAgentModalOpen(false)}
                disabled={gAgentBindingPending}
              >
                Cancel
              </Button>,
              <Button
                key="bind"
                onClick={() => void submitGAgentBinding(false)}
                loading={gAgentBindingPending}
                disabled={!resolvedScopeId}
              >
                Bind
              </Button>,
              <Button
                key="bind-open-runs"
                type="primary"
                onClick={() => void submitGAgentBinding(true)}
                loading={gAgentBindingPending}
                disabled={!resolvedScopeId}
              >
                Bind + Open Runs
              </Button>,
            ]}
          >
            <div style={cardStackStyle}>
              <Typography.Text type="secondary">
                Bind the scope default service directly to a static GAgent and optionally open the runtime runs workbench for the configured endpoint.
              </Typography.Text>
              <Input
                aria-label="GAgent display name"
                placeholder="Display name"
                value={gAgentDraft.displayName}
                onChange={(event) =>
                  setGAgentDraft((current) => ({
                    ...current,
                    displayName: event.target.value,
                  }))
                }
              />
              <Input
                aria-label="GAgent actor type name"
                placeholder="Assembly-qualified actor type name"
                value={gAgentDraft.actorTypeName}
                onChange={(event) =>
                  setGAgentDraft((current) => ({
                    ...current,
                    actorTypeName: event.target.value,
                  }))
                }
              />
              <Input
                aria-label="GAgent preferred actor id"
                placeholder="Preferred actor id (optional)"
                value={gAgentDraft.preferredActorId}
                onChange={(event) =>
                  setGAgentDraft((current) => ({
                    ...current,
                    preferredActorId: event.target.value,
                  }))
                }
              />
              <Input
                aria-label="GAgent endpoint id"
                placeholder="Endpoint ID"
                value={gAgentDraft.endpointId}
                onChange={(event) =>
                  setGAgentDraft((current) => ({
                    ...current,
                    endpointId: event.target.value,
                  }))
                }
              />
              <Input
                aria-label="GAgent endpoint display name"
                placeholder="Endpoint display name"
                value={gAgentDraft.endpointDisplayName}
                onChange={(event) =>
                  setGAgentDraft((current) => ({
                    ...current,
                    endpointDisplayName: event.target.value,
                  }))
                }
              />
              <Input
                aria-label="GAgent request type URL"
                placeholder="Request type URL"
                value={gAgentDraft.requestTypeUrl}
                onChange={(event) =>
                  setGAgentDraft((current) => ({
                    ...current,
                    requestTypeUrl: event.target.value,
                  }))
                }
              />
              <Input
                aria-label="GAgent response type URL"
                placeholder="Response type URL (optional)"
                value={gAgentDraft.responseTypeUrl}
                onChange={(event) =>
                  setGAgentDraft((current) => ({
                    ...current,
                    responseTypeUrl: event.target.value,
                  }))
                }
              />
              <Input.TextArea
                aria-label="GAgent endpoint description"
                autoSize={{ minRows: 2, maxRows: 4 }}
                placeholder="Endpoint description"
                value={gAgentDraft.description}
                onChange={(event) =>
                  setGAgentDraft((current) => ({
                    ...current,
                    description: event.target.value,
                  }))
                }
              />
              <Divider style={{ marginBlock: 8 }} />
              <Input.TextArea
                aria-label="GAgent run prompt"
                autoSize={{ minRows: 4, maxRows: 8 }}
                placeholder="Prompt for the runtime runs console"
                value={gAgentDraft.prompt}
                onChange={(event) =>
                  setGAgentDraft((current) => ({
                    ...current,
                    prompt: event.target.value,
                  }))
                }
              />
              <Input.TextArea
                aria-label="GAgent payload base64"
                autoSize={{ minRows: 3, maxRows: 6 }}
                placeholder="Payload base64 for custom request types (optional)"
                value={gAgentDraft.payloadBase64}
                onChange={(event) =>
                  setGAgentDraft((current) => ({
                    ...current,
                    payloadBase64: event.target.value,
                  }))
                }
              />
            </div>
          </Modal>

          <Modal
            open={runModalOpen}
            title="Test run"
            onCancel={() => setRunModalOpen(false)}
            onOk={() => {
              void onStartExecution();
              setRunModalOpen(false);
            }}
            okText="Open runtime runs"
            okButtonProps={{
              disabled: !canRunWorkflow,
              loading: runPending,
              icon: <CaretRightFilled />,
            }}
          >
            <div style={cardStackStyle}>
              <Typography.Text type="secondary">
                Studio will open the runtime runs console and execute this draft through <Typography.Text code>/api/scopes/{'{scopeId}'}/workflow/draft-run</Typography.Text>.
              </Typography.Text>
              <Input.TextArea
                aria-label="Studio execution prompt"
                autoSize={{ minRows: 6, maxRows: 10 }}
                placeholder="What should this run do?"
                value={runPrompt}
                onChange={(event) => onRunPromptChange(event.target.value)}
              />
            </div>
          </Modal>
        </>
      ) : (
        <Empty
          image={Empty.PRESENTED_IMAGE_SIMPLE}
          description="Pick a workspace workflow, open Studio from the published workflow catalog, or start a blank draft."
        />
      )}
    </div>
  );
};

export type StudioCatalogDraftMeta = {
  readonly filePath: string;
  readonly fileExists: boolean;
  readonly updatedAtUtc: string | null;
};

export type StudioRoleCatalogItem = {
  readonly key: string;
  readonly id: string;
  readonly name: string;
  readonly systemPrompt: string;
  readonly provider: string;
  readonly model: string;
  readonly connectorsText: string;
};

export type StudioRoleDraftItem = Omit<StudioRoleCatalogItem, 'key'>;

export type StudioConnectorType = 'http' | 'cli' | 'mcp';

export type StudioConnectorCatalogItem = {
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

export type StudioConnectorDraftItem = Omit<StudioConnectorCatalogItem, 'key'>;

type StudioCatalogModalShellProps = {
  readonly open: boolean;
  readonly title: string;
  readonly onClose: () => void;
  readonly actions: React.ReactNode;
  readonly children: React.ReactNode;
};

const selectedCatalogSurfaceStyle: React.CSSProperties = {
  borderColor: 'var(--accent-border)',
  background: 'var(--accent-soft-end)',
};

const pageHeaderStyle: React.CSSProperties = {
  height: 88,
  flexShrink: 0,
  borderBottom: '1px solid #E6E3DE',
  background: 'rgba(255,255,255,0.92)',
  backdropFilter: 'blur(8px)',
  paddingInline: 24,
  display: 'flex',
  alignItems: 'center',
  justifyContent: 'space-between',
  gap: 16,
};

const sidebarMetaTextStyle: React.CSSProperties = {
  fontSize: 11,
  color: 'var(--ant-color-text-tertiary)',
  wordBreak: 'break-all',
};

const catalogListItemTitleStyle: React.CSSProperties = {
  fontSize: 13,
  fontWeight: 600,
  color: '#1f2937',
  overflow: 'hidden',
  textOverflow: 'ellipsis',
  whiteSpace: 'nowrap',
};

const catalogListItemMetaStyle: React.CSSProperties = {
  fontSize: 11,
  color: 'var(--ant-color-text-tertiary)',
  overflow: 'hidden',
  textOverflow: 'ellipsis',
  whiteSpace: 'nowrap',
};

const catalogCardStackStyle: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: 12,
};

const catalogSectionTitleStyle: React.CSSProperties = {
  display: 'flex',
  alignItems: 'center',
  gap: 8,
  fontSize: 13,
  fontWeight: 600,
  color: '#1f2937',
};

function StudioCatalogModalShell(props: StudioCatalogModalShellProps) {
  if (!props.open) {
    return null;
  }

  return (
    <div className="modal-overlay" onClick={props.onClose}>
      <div className="modal-shell" onClick={(event) => event.stopPropagation()}>
        <div className="modal-header">
          <div>
            <div className="panel-eyebrow">Catalog</div>
            <div className="panel-title">{props.title}</div>
          </div>
          <button
            type="button"
            onClick={props.onClose}
            title="Close dialog."
            className="panel-icon-button"
          >
            <CloseOutlined />
          </button>
        </div>
        <div className="modal-body">{props.children}</div>
        <div className="modal-footer">{props.actions}</div>
      </div>
    </div>
  );
}

function StudioCatalogInputField(props: {
  readonly inputId: string;
  readonly label: string;
  readonly value: string;
  readonly onChange: (value: string) => void;
  readonly type?: 'text' | 'number';
}) {
  return (
    <div style={catalogCardStackStyle}>
      <label className="field-label" htmlFor={props.inputId}>
        {props.label}
      </label>
      <input
        id={props.inputId}
        type={props.type || 'text'}
        className="panel-input"
        value={props.value}
        onChange={(event) => props.onChange(event.target.value)}
      />
    </div>
  );
}

function StudioCatalogTextAreaField(props: {
  readonly inputId: string;
  readonly label: string;
  readonly value: string;
  readonly rows?: number;
  readonly onChange: (value: string) => void;
}) {
  return (
    <div style={catalogCardStackStyle}>
      <label className="field-label" htmlFor={props.inputId}>
        {props.label}
      </label>
      <textarea
        id={props.inputId}
        rows={props.rows ?? 4}
        className="panel-textarea"
        value={props.value}
        onChange={(event) => props.onChange(event.target.value)}
      />
    </div>
  );
}

function StudioCatalogEmptyPanel(props: {
  readonly icon: React.ReactNode;
  readonly title: string;
  readonly copy: string;
}) {
  return (
    <div className="studio-empty-panel">
      <div className="studio-empty-panel-icon">{props.icon}</div>
      <div className="studio-empty-panel-title">{props.title}</div>
      <div className="studio-empty-panel-copy">{props.copy}</div>
    </div>
  );
}

export type StudioRolesPageProps = {
  readonly roles: QueryState<{ readonly roles: StudioRoleDefinition[] }>;
  readonly appearanceTheme: string;
  readonly colorMode: string;
  readonly roleCatalogDraft: StudioRoleCatalogItem[];
  readonly roleCatalogMeta: {
    readonly filePath: string;
    readonly fileExists: boolean;
  };
  readonly roleCatalogIsRemote: boolean;
  readonly roleCatalogDirty: boolean;
  readonly roleCatalogPending: boolean;
  readonly roleCatalogNotice: StudioNoticeLike | null;
  readonly roleImportPending: boolean;
  readonly roleImportInputRef: React.RefObject<HTMLInputElement | null>;
  readonly roleSearch: string;
  readonly roleModalOpen: boolean;
  readonly roleDraft: StudioRoleDraftItem | null;
  readonly roleDraftMeta: StudioCatalogDraftMeta;
  readonly selectedRole: StudioRoleCatalogItem | null;
  readonly connectors: readonly { readonly name: string }[];
  readonly settingsProviders: readonly {
    readonly providerName: string;
    readonly model: string;
  }[];
  readonly onRoleSearchChange: (value: string) => void;
  readonly onOpenRoleModal: () => void;
  readonly onCloseRoleModal: () => void | Promise<void>;
  readonly onRoleDraftChange: (
    updater: (draft: StudioRoleDraftItem) => StudioRoleDraftItem,
  ) => void;
  readonly onSubmitRoleDraft: () => void | Promise<void>;
  readonly onRoleImportClick: () => void;
  readonly onRoleImportChange: React.ChangeEventHandler<HTMLInputElement>;
  readonly onSaveRoles: () => void;
  readonly onSelectRoleKey: (roleKey: string) => void;
  readonly onDeleteRole: (roleKey: string) => void;
  readonly onApplyRoleToWorkflow: (roleKey: string) => void | Promise<void>;
  readonly onUpdateRoleCatalog: (
    roleKey: string,
    updater: (role: StudioRoleCatalogItem) => StudioRoleCatalogItem,
  ) => void;
};

export const StudioRolesPage: React.FC<StudioRolesPageProps> = ({
  roles,
  appearanceTheme,
  colorMode,
  roleCatalogDraft,
  roleCatalogMeta,
  roleCatalogIsRemote,
  roleCatalogPending,
  roleCatalogNotice,
  roleImportPending,
  roleImportInputRef,
  roleSearch,
  roleModalOpen,
  roleDraft,
  roleDraftMeta,
  selectedRole,
  connectors,
  settingsProviders,
  onRoleSearchChange,
  onOpenRoleModal,
  onCloseRoleModal,
  onRoleDraftChange,
  onSubmitRoleDraft,
  onRoleImportClick,
  onRoleImportChange,
  onSaveRoles,
  onSelectRoleKey,
  onDeleteRole,
  onApplyRoleToWorkflow,
  onUpdateRoleCatalog,
}) => {
  const filteredRoleCatalog = roleCatalogDraft.filter((role) => {
    const normalizedQuery = roleSearch.trim().toLowerCase();
    if (!normalizedQuery) {
      return true;
    }

    return [
      role.id,
      role.name,
      role.systemPrompt,
      role.provider,
      role.model,
      role.connectorsText,
    ]
      .join(' ')
      .toLowerCase()
      .includes(normalizedQuery);
  });

  return (
    <div
      className="studio-shell studio-catalog-page"
      data-appearance={appearanceTheme || 'blue'}
      data-color-mode={colorMode || 'light'}
    >
      <header className="workspace-page-header" style={pageHeaderStyle}>
        <div>
          <div className="panel-eyebrow">Catalog</div>
          <div className="panel-title">Roles</div>
        </div>
      </header>

      <section className="studio-catalog-page-body">
        <aside className="studio-catalog-sidebar">
          <div style={{ ...catalogCardStackStyle, marginBottom: 16 }}>
            <div className="catalog-sidebar-actions">
              <input
                ref={roleImportInputRef}
                type="file"
                accept=".json,application/json"
                hidden
                onChange={onRoleImportChange}
              />
              <button
                type="button"
                onClick={onOpenRoleModal}
                className="solid-action"
                style={{ flex: 1 }}
              >
                <PlusOutlined />
                Add role
              </button>
              <button
                type="button"
                onClick={onSaveRoles}
                className="ghost-action catalog-save-action"
                disabled={roleCatalogPending}
              >
                Save
              </button>
              <button
                type="button"
                onClick={onRoleImportClick}
                className="ghost-action catalog-save-action"
                disabled={roleImportPending}
              >
                <UploadOutlined />
                {roleImportPending ? 'Importing...' : 'Import'}
              </button>
            </div>

            <div className="search-field">
              <SearchOutlined style={{ color: 'var(--ant-color-text-tertiary)' }} />
              <input
                className="search-input"
                placeholder="Search roles"
                value={roleSearch}
                onChange={(event) => onRoleSearchChange(event.target.value)}
              />
            </div>

            {roleCatalogMeta.filePath ? (
              <div style={sidebarMetaTextStyle}>
                {roleCatalogIsRemote
                  ? `${roleCatalogMeta.fileExists ? 'Remote object' : 'Remote object pending'} · ${roleCatalogMeta.filePath}`
                  : `${roleCatalogMeta.fileExists ? 'File' : 'Will create'} · ${roleCatalogMeta.filePath}`}
              </div>
            ) : null}

            {roleDraftMeta.filePath ? (
              <div style={sidebarMetaTextStyle}>Draft · {roleDraftMeta.filePath}</div>
            ) : null}
          </div>

          {roles.isError ? (
            <div className="empty-card">{describeError(roles.error)}</div>
          ) : roles.isLoading ? (
            <div className="empty-card">Loading roles...</div>
          ) : (
            <div className="studio-catalog-list">
              {filteredRoleCatalog.length === 0 ? (
                <div className="empty-card">No roles matched</div>
              ) : (
                filteredRoleCatalog.map((role) => (
                  <button
                    key={role.key}
                    type="button"
                    onClick={() => onSelectRoleKey(role.key)}
                    className={`studio-catalog-item ${selectedRole?.key === role.key ? 'active' : ''}`}
                    style={
                      selectedRole?.key === role.key
                        ? selectedCatalogSurfaceStyle
                        : undefined
                    }
                  >
                    <div
                      style={{
                        display: 'flex',
                        justifyContent: 'space-between',
                        alignItems: 'center',
                        gap: 8,
                      }}
                    >
                      <div style={catalogListItemTitleStyle}>
                        {role.name || role.id || 'Role'}
                      </div>
                      <span style={{ ...catalogListItemMetaStyle, textTransform: 'uppercase', letterSpacing: '0.08em' }}>
                        {role.id || 'role'}
                      </span>
                    </div>
                    <div style={{ ...catalogListItemMetaStyle, marginTop: 4 }}>
                      {role.provider || 'default'}
                      {role.model ? ` · ${role.model}` : ''}
                    </div>
                  </button>
                ))
              )}
            </div>
          )}
        </aside>

        <div className="studio-catalog-content">
          {selectedRole ? (
            <div className="studio-catalog-detail-card" style={catalogCardStackStyle}>
              <div
                style={{
                  display: 'flex',
                  justifyContent: 'space-between',
                  alignItems: 'flex-start',
                  gap: 12,
                }}
              >
                <div>
                  <div className="studio-catalog-title">
                    {selectedRole.name || selectedRole.id || 'Role'}
                  </div>
                  <div className="studio-catalog-subtitle" style={{ marginTop: 4 }}>
                    {selectedRole.id || 'role_id'}
                  </div>
                </div>
                <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                  <button
                    type="button"
                    onClick={() => void onApplyRoleToWorkflow(selectedRole.key)}
                    className="ghost-action"
                  >
                    Use in workflow
                  </button>
                  <button
                    type="button"
                    onClick={() => onDeleteRole(selectedRole.key)}
                    title="Delete role."
                    className="panel-icon-button"
                    style={{ color: '#ef4444' }}
                  >
                    <DeleteOutlined />
                  </button>
                </div>
              </div>

              <div className="studio-catalog-grid-two">
                <StudioCatalogInputField
                  inputId="studio-role-id"
                  label="Role ID"
                  value={selectedRole.id}
                  onChange={(value) =>
                    onUpdateRoleCatalog(selectedRole.key, (role) => ({
                      ...role,
                      id: value,
                    }))
                  }
                />
                <StudioCatalogInputField
                  inputId="studio-role-name"
                  label="Role name"
                  value={selectedRole.name}
                  onChange={(value) =>
                    onUpdateRoleCatalog(selectedRole.key, (role) => ({
                      ...role,
                      name: value,
                    }))
                  }
                />
              </div>

              <div className="studio-catalog-grid-two">
                <div style={catalogCardStackStyle}>
                  <label className="field-label" htmlFor="studio-role-provider">
                    Provider
                  </label>
                  <select
                    id="studio-role-provider"
                    className="panel-input"
                    value={selectedRole.provider}
                    onChange={(event) => {
                      const nextProviderName = event.target.value;
                      const configuredProvider = settingsProviders.find(
                        (provider) => provider.providerName === nextProviderName,
                      );
                      onUpdateRoleCatalog(selectedRole.key, (role) => ({
                        ...role,
                        provider: nextProviderName,
                        model: configuredProvider?.model || role.model,
                      }));
                    }}
                  >
                    <option value="">Default</option>
                    {settingsProviders.map((provider) => (
                      <option
                        key={provider.providerName}
                        value={provider.providerName}
                      >
                        {provider.providerName}
                      </option>
                    ))}
                  </select>
                </div>

                <StudioCatalogInputField
                  inputId="studio-role-model"
                  label="Model"
                  value={selectedRole.model}
                  onChange={(value) =>
                    onUpdateRoleCatalog(selectedRole.key, (role) => ({
                      ...role,
                      model: value,
                    }))
                  }
                />
              </div>

              <StudioCatalogTextAreaField
                inputId="studio-role-system-prompt"
                label="System prompt"
                value={selectedRole.systemPrompt}
                rows={8}
                onChange={(value) =>
                  onUpdateRoleCatalog(selectedRole.key, (role) => ({
                    ...role,
                    systemPrompt: value,
                  }))
                }
              />

              <div style={catalogCardStackStyle}>
                <div className="field-label">Allowed connectors</div>
                <div
                  style={{
                    display: 'flex',
                    flexWrap: 'wrap',
                    gap: 8,
                  }}
                >
                  {connectors.length === 0 ? (
                    <div className="studio-catalog-muted">
                      No connectors configured
                    </div>
                  ) : (
                    connectors.map((connector) => {
                      const active = parseListText(
                        selectedRole.connectorsText,
                      ).includes(connector.name);
                      return (
                        <button
                          key={`${selectedRole.key}:${connector.name}`}
                          type="button"
                          onClick={() =>
                            onUpdateRoleCatalog(selectedRole.key, (role) => {
                              const nextValues = new Set(
                                parseListText(role.connectorsText),
                              );
                              if (nextValues.has(connector.name)) {
                                nextValues.delete(connector.name);
                              } else {
                                nextValues.add(connector.name);
                              }

                              return {
                                ...role,
                                connectorsText: Array.from(nextValues).join('\n'),
                              };
                            })
                          }
                          className={`chip-button ${active ? 'chip-button-active' : ''}`}
                        >
                          {connector.name}
                        </button>
                      );
                    })
                  )}
                </div>
              </div>
            </div>
          ) : (
            <div style={{ maxWidth: 420 }}>
              <StudioCatalogEmptyPanel
                icon={<UserOutlined />}
                title="No role selected"
                copy="Create a role or pick one from the catalog."
              />
            </div>
          )}
        </div>
      </section>

      <StudioCatalogModalShell
        open={roleModalOpen}
        title="Add Role"
        onClose={() => {
          void onCloseRoleModal();
        }}
        actions={
          <>
            <button
              type="button"
              onClick={() => {
                void onCloseRoleModal();
              }}
              className="ghost-action"
            >
              Close
            </button>
            <button
              type="button"
              onClick={() => {
                void onSubmitRoleDraft();
              }}
              className="solid-action"
            >
              <PlusOutlined />
              Add role
            </button>
          </>
        }
      >
        {roleDraft ? (
          <div style={catalogCardStackStyle}>
            <div className="studio-catalog-muted">
              The latest unfinished role draft is stored automatically when you
              close this dialog.
            </div>

            <div className="studio-catalog-grid-two">
              <StudioCatalogInputField
                inputId="studio-role-draft-id"
                label="Role ID"
                value={roleDraft.id}
                onChange={(value) =>
                  onRoleDraftChange((draft) => ({ ...draft, id: value }))
                }
              />
              <StudioCatalogInputField
                inputId="studio-role-draft-name"
                label="Role name"
                value={roleDraft.name}
                onChange={(value) =>
                  onRoleDraftChange((draft) => ({ ...draft, name: value }))
                }
              />
            </div>

            <div className="studio-catalog-grid-two">
              <div style={catalogCardStackStyle}>
                <label
                  className="field-label"
                  htmlFor="studio-role-draft-provider"
                >
                  Provider
                </label>
                <select
                  id="studio-role-draft-provider"
                  className="panel-input"
                  value={roleDraft.provider}
                  onChange={(event) => {
                    const nextProviderName = event.target.value;
                    const configuredProvider = settingsProviders.find(
                      (provider) => provider.providerName === nextProviderName,
                    );
                    onRoleDraftChange((draft) => ({
                      ...draft,
                      provider: nextProviderName,
                      model: configuredProvider?.model || draft.model,
                    }));
                  }}
                >
                  <option value="">Default</option>
                  {settingsProviders.map((provider) => (
                    <option
                      key={provider.providerName}
                      value={provider.providerName}
                    >
                      {provider.providerName}
                    </option>
                  ))}
                </select>
              </div>

              <StudioCatalogInputField
                inputId="studio-role-draft-model"
                label="Model"
                value={roleDraft.model}
                onChange={(value) =>
                  onRoleDraftChange((draft) => ({ ...draft, model: value }))
                }
              />
            </div>

            <StudioCatalogTextAreaField
              inputId="studio-role-draft-system-prompt"
              label="System prompt"
              value={roleDraft.systemPrompt}
              rows={6}
              onChange={(value) =>
                onRoleDraftChange((draft) => ({
                  ...draft,
                  systemPrompt: value,
                }))
              }
            />

            <StudioCatalogTextAreaField
              inputId="studio-role-draft-connectors"
              label="Allowed connectors"
              value={roleDraft.connectorsText}
              rows={4}
              onChange={(value) =>
                onRoleDraftChange((draft) => ({
                  ...draft,
                  connectorsText: value,
                }))
              }
            />
          </div>
        ) : null}
      </StudioCatalogModalShell>

      {roleCatalogNotice ? (
        <div className={`studio-catalog-toast ${roleCatalogNotice.type}`}>
          {roleCatalogNotice.message}
        </div>
      ) : null}
    </div>
  );
};

export type StudioConnectorsPageProps = {
  readonly connectors: QueryState<{ readonly connectors: StudioConnectorDefinition[] }>;
  readonly appearanceTheme: string;
  readonly colorMode: string;
  readonly connectorCatalogDraft: StudioConnectorCatalogItem[];
  readonly connectorCatalogMeta: {
    readonly filePath: string;
    readonly fileExists: boolean;
  };
  readonly connectorCatalogIsRemote: boolean;
  readonly connectorCatalogDirty: boolean;
  readonly connectorCatalogPending: boolean;
  readonly connectorImportPending: boolean;
  readonly connectorCatalogNotice: StudioNoticeLike | null;
  readonly connectorImportInputRef: React.RefObject<HTMLInputElement | null>;
  readonly connectorSearch: string;
  readonly connectorModalOpen: boolean;
  readonly connectorDraft: StudioConnectorDraftItem | null;
  readonly connectorDraftMeta: StudioCatalogDraftMeta;
  readonly selectedConnector: StudioConnectorCatalogItem | null;
  readonly onConnectorSearchChange: (value: string) => void;
  readonly onOpenConnectorModal: () => void;
  readonly onCloseConnectorModal: () => void | Promise<void>;
  readonly onConnectorDraftChange: (
    updater: (draft: StudioConnectorDraftItem) => StudioConnectorDraftItem,
  ) => void;
  readonly onSubmitConnectorDraft: () => void | Promise<void>;
  readonly onConnectorImportClick: () => void;
  readonly onConnectorImportChange: React.ChangeEventHandler<HTMLInputElement>;
  readonly onSaveConnectors: () => void;
  readonly onSelectConnectorKey: (connectorKey: string) => void;
  readonly onDeleteConnector: (connectorKey: string) => void;
  readonly onUpdateConnectorCatalog: (
    connectorKey: string,
    updater: (
      connector: StudioConnectorCatalogItem,
    ) => StudioConnectorCatalogItem,
  ) => void;
};

export const StudioConnectorsPage: React.FC<StudioConnectorsPageProps> = ({
  connectors,
  appearanceTheme,
  colorMode,
  connectorCatalogDraft,
  connectorCatalogMeta,
  connectorCatalogIsRemote,
  connectorCatalogPending,
  connectorImportPending,
  connectorCatalogNotice,
  connectorImportInputRef,
  connectorSearch,
  connectorModalOpen,
  connectorDraft,
  connectorDraftMeta,
  selectedConnector,
  onConnectorSearchChange,
  onOpenConnectorModal,
  onCloseConnectorModal,
  onConnectorDraftChange,
  onSubmitConnectorDraft,
  onConnectorImportClick,
  onConnectorImportChange,
  onSaveConnectors,
  onSelectConnectorKey,
  onDeleteConnector,
  onUpdateConnectorCatalog,
}) => {
  const filteredConnectorList = connectorCatalogDraft.filter((connector) => {
    const normalizedQuery = connectorSearch.trim().toLowerCase();
    if (!normalizedQuery) {
      return true;
    }

    return [
      connector.name,
      connector.type,
      connector.http.baseUrl,
      connector.cli.command,
      connector.mcp.serverName,
      connector.mcp.command,
    ]
      .join(' ')
      .toLowerCase()
      .includes(normalizedQuery);
  });

  return (
    <div
      className="studio-shell studio-catalog-page"
      data-appearance={appearanceTheme || 'blue'}
      data-color-mode={colorMode || 'light'}
    >
      <header className="workspace-page-header" style={pageHeaderStyle}>
        <div>
          <div className="panel-eyebrow">Catalog</div>
          <div className="panel-title">Connectors</div>
        </div>
      </header>

      <section className="studio-catalog-page-body">
        <aside className="studio-catalog-sidebar">
          <div style={{ ...catalogCardStackStyle, marginBottom: 16 }}>
            <div className="catalog-sidebar-actions">
              <input
                ref={connectorImportInputRef}
                type="file"
                accept=".json,application/json"
                hidden
                onChange={onConnectorImportChange}
              />
              <button
                type="button"
                onClick={onOpenConnectorModal}
                className="solid-action"
                style={{ flex: 1 }}
              >
                <PlusOutlined />
                Add connector
              </button>
              <button
                type="button"
                onClick={onSaveConnectors}
                className="ghost-action catalog-save-action"
                disabled={connectorCatalogPending}
              >
                Save
              </button>
              <button
                type="button"
                onClick={onConnectorImportClick}
                className="ghost-action catalog-save-action"
                disabled={connectorImportPending}
              >
                <UploadOutlined />
                {connectorImportPending ? 'Importing...' : 'Import'}
              </button>
            </div>

            <div className="search-field">
              <SearchOutlined style={{ color: 'var(--ant-color-text-tertiary)' }} />
              <input
                className="search-input"
                placeholder="Search connectors"
                value={connectorSearch}
                onChange={(event) => onConnectorSearchChange(event.target.value)}
              />
            </div>

            {connectorCatalogMeta.filePath ? (
              <div style={sidebarMetaTextStyle}>
                {connectorCatalogIsRemote
                  ? `${connectorCatalogMeta.fileExists ? 'Remote object' : 'Remote object pending'} · ${connectorCatalogMeta.filePath}`
                  : `${connectorCatalogMeta.fileExists ? 'File' : 'Will create'} · ${connectorCatalogMeta.filePath}`}
              </div>
            ) : null}

            {connectorDraftMeta.filePath ? (
              <div style={sidebarMetaTextStyle}>
                Draft · {connectorDraftMeta.filePath}
              </div>
            ) : null}
          </div>

          {connectors.isError ? (
            <div className="empty-card">{describeError(connectors.error)}</div>
          ) : connectors.isLoading ? (
            <div className="empty-card">Loading connectors...</div>
          ) : (
            <div className="studio-catalog-list">
              {filteredConnectorList.length === 0 ? (
                <div className="empty-card">No connectors matched</div>
              ) : (
                filteredConnectorList.map((connector) => (
                  <button
                    key={connector.key}
                    type="button"
                    onClick={() => onSelectConnectorKey(connector.key)}
                    className={`studio-catalog-item ${selectedConnector?.key === connector.key ? 'active' : ''}`}
                    style={
                      selectedConnector?.key === connector.key
                        ? selectedCatalogSurfaceStyle
                        : undefined
                    }
                  >
                    <div
                      style={{
                        display: 'flex',
                        justifyContent: 'space-between',
                        alignItems: 'center',
                        gap: 8,
                      }}
                    >
                      <div style={catalogListItemTitleStyle}>
                        {connector.name || 'New connector'}
                      </div>
                      <span style={{ ...catalogListItemMetaStyle, textTransform: 'uppercase', letterSpacing: '0.08em' }}>
                        {connector.type}
                      </span>
                    </div>
                    <div style={{ ...catalogListItemMetaStyle, marginTop: 4 }}>
                      {connector.type === 'http'
                        ? connector.http.baseUrl || 'HTTP connector'
                        : connector.type === 'cli'
                          ? connector.cli.command || 'CLI connector'
                          : connector.mcp.serverName || connector.mcp.command || 'MCP connector'}
                    </div>
                  </button>
                ))
              )}
            </div>
          )}
        </aside>

        <div className="studio-catalog-content">
          {selectedConnector ? (
            <div className="studio-catalog-detail-card" style={catalogCardStackStyle}>
              <div
                style={{
                  display: 'flex',
                  justifyContent: 'space-between',
                  alignItems: 'flex-start',
                  gap: 12,
                }}
              >
                <div>
                  <div className="studio-catalog-title">
                    {selectedConnector.name || 'Connector'}
                  </div>
                  <div className="studio-catalog-subtitle" style={{ marginTop: 4 }}>
                    {selectedConnector.type.toUpperCase()}
                  </div>
                </div>
                <button
                  type="button"
                  onClick={() => onDeleteConnector(selectedConnector.key)}
                  title="Delete connector."
                  className="panel-icon-button"
                  style={{ color: '#ef4444' }}
                >
                  <DeleteOutlined />
                </button>
              </div>

              <StudioCatalogInputField
                inputId="studio-connector-name"
                label="Name"
                value={selectedConnector.name}
                onChange={(value) =>
                  onUpdateConnectorCatalog(selectedConnector.key, (connector) => ({
                    ...connector,
                    name: value,
                  }))
                }
              />

              <div style={catalogCardStackStyle}>
                <label className="field-label" htmlFor="studio-connector-type">
                  Type
                </label>
                <select
                  id="studio-connector-type"
                  className="panel-input"
                  value={selectedConnector.type}
                  onChange={(event) =>
                    onUpdateConnectorCatalog(selectedConnector.key, (connector) => ({
                      ...connector,
                      type: event.target.value as StudioConnectorType,
                    }))
                  }
                >
                  <option value="http">HTTP</option>
                  <option value="cli">CLI</option>
                  <option value="mcp">MCP</option>
                </select>
              </div>

              <div className="studio-catalog-grid-two">
                <StudioCatalogInputField
                  inputId="studio-connector-timeout"
                  label="Timeout"
                  value={selectedConnector.timeoutMs}
                  onChange={(value) =>
                    onUpdateConnectorCatalog(selectedConnector.key, (connector) => ({
                      ...connector,
                      timeoutMs: value,
                    }))
                  }
                />
                <StudioCatalogInputField
                  inputId="studio-connector-retry"
                  label="Retry"
                  value={selectedConnector.retry}
                  onChange={(value) =>
                    onUpdateConnectorCatalog(selectedConnector.key, (connector) => ({
                      ...connector,
                      retry: value,
                    }))
                  }
                />
              </div>

              <label
                className={`studio-catalog-toggle ${selectedConnector.enabled ? '' : 'disabled'}`}
              >
                <input
                  type="checkbox"
                  checked={selectedConnector.enabled}
                  onChange={(event) =>
                    onUpdateConnectorCatalog(selectedConnector.key, (connector) => ({
                      ...connector,
                      enabled: event.target.checked,
                    }))
                  }
                />
                Enabled
              </label>

              {selectedConnector.type === 'http' ? (
                <div className="studio-catalog-section-card">
                  <div style={catalogSectionTitleStyle}>
                    <ApiOutlined />
                    HTTP
                  </div>
                  <StudioCatalogInputField
                    inputId="studio-connector-http-base-url"
                    label="Base URL"
                    value={selectedConnector.http.baseUrl}
                    onChange={(value) =>
                      onUpdateConnectorCatalog(selectedConnector.key, (connector) => ({
                        ...connector,
                        http: {
                          ...connector.http,
                          baseUrl: value,
                        },
                      }))
                    }
                  />
                  <StudioCatalogTextAreaField
                    inputId="studio-connector-http-methods"
                    label="Allowed methods"
                    value={selectedConnector.http.allowedMethods.join('\n')}
                    onChange={(value) =>
                      onUpdateConnectorCatalog(selectedConnector.key, (connector) => ({
                        ...connector,
                        http: {
                          ...connector.http,
                          allowedMethods: parseListText(value).map((item) =>
                            item.toUpperCase(),
                          ),
                        },
                      }))
                    }
                  />
                  <StudioCatalogTextAreaField
                    inputId="studio-connector-http-paths"
                    label="Allowed paths"
                    value={selectedConnector.http.allowedPaths.join('\n')}
                    onChange={(value) =>
                      onUpdateConnectorCatalog(selectedConnector.key, (connector) => ({
                        ...connector,
                        http: {
                          ...connector.http,
                          allowedPaths: parseListText(value),
                        },
                      }))
                    }
                  />
                </div>
              ) : null}

              {selectedConnector.type === 'cli' ? (
                <div className="studio-catalog-section-card">
                  <div style={catalogSectionTitleStyle}>
                    <CodeOutlined />
                    CLI
                  </div>
                  <StudioCatalogInputField
                    inputId="studio-connector-cli-command"
                    label="Command"
                    value={selectedConnector.cli.command}
                    onChange={(value) =>
                      onUpdateConnectorCatalog(selectedConnector.key, (connector) => ({
                        ...connector,
                        cli: {
                          ...connector.cli,
                          command: value,
                        },
                      }))
                    }
                  />
                  <StudioCatalogTextAreaField
                    inputId="studio-connector-cli-fixed-arguments"
                    label="Fixed arguments"
                    value={selectedConnector.cli.fixedArguments.join('\n')}
                    onChange={(value) =>
                      onUpdateConnectorCatalog(selectedConnector.key, (connector) => ({
                        ...connector,
                        cli: {
                          ...connector.cli,
                          fixedArguments: parseListText(value),
                        },
                      }))
                    }
                  />
                </div>
              ) : null}

              {selectedConnector.type === 'mcp' ? (
                <div className="studio-catalog-section-card">
                  <div style={catalogSectionTitleStyle}>
                    <AppstoreOutlined />
                    MCP
                  </div>
                  <StudioCatalogInputField
                    inputId="studio-connector-mcp-server-name"
                    label="Server name"
                    value={selectedConnector.mcp.serverName}
                    onChange={(value) =>
                      onUpdateConnectorCatalog(selectedConnector.key, (connector) => ({
                        ...connector,
                        mcp: {
                          ...connector.mcp,
                          serverName: value,
                        },
                      }))
                    }
                  />
                  <StudioCatalogInputField
                    inputId="studio-connector-mcp-command"
                    label="Command"
                    value={selectedConnector.mcp.command}
                    onChange={(value) =>
                      onUpdateConnectorCatalog(selectedConnector.key, (connector) => ({
                        ...connector,
                        mcp: {
                          ...connector.mcp,
                          command: value,
                        },
                      }))
                    }
                  />
                </div>
              ) : null}
            </div>
          ) : (
            <div style={{ maxWidth: 420 }}>
              <StudioCatalogEmptyPanel
                icon={<ApiOutlined />}
                title="No connector selected"
                copy="Create a connector or pick one from the catalog."
              />
            </div>
          )}
        </div>
      </section>

      <StudioCatalogModalShell
        open={connectorModalOpen}
        title="Add Connector"
        onClose={() => {
          void onCloseConnectorModal();
        }}
        actions={
          <>
            <button
              type="button"
              onClick={() => {
                void onCloseConnectorModal();
              }}
              className="ghost-action"
            >
              Close
            </button>
            <button
              type="button"
              onClick={() => {
                void onSubmitConnectorDraft();
              }}
              className="solid-action"
            >
              <PlusOutlined />
              Add connector
            </button>
          </>
        }
      >
        {connectorDraft ? (
          <div style={catalogCardStackStyle}>
            <div className="studio-catalog-muted">
              Close this dialog at any time and the latest text will be kept as
              a draft.
            </div>

            <div style={catalogCardStackStyle}>
              <label
                className="field-label"
                htmlFor="studio-connector-draft-type"
              >
                Type
              </label>
              <select
                id="studio-connector-draft-type"
                className="panel-input"
                value={connectorDraft.type}
                onChange={(event) =>
                  onConnectorDraftChange((draft) => ({
                    ...draft,
                    type: event.target.value as StudioConnectorType,
                  }))
                }
              >
                <option value="http">HTTP</option>
                <option value="cli">CLI</option>
                <option value="mcp">MCP</option>
              </select>
            </div>

            <StudioCatalogInputField
              inputId="studio-connector-draft-name"
              label="Name"
              value={connectorDraft.name}
              onChange={(value) =>
                onConnectorDraftChange((draft) => ({ ...draft, name: value }))
              }
            />

            {connectorDraft.type === 'http' ? (
              <div className="studio-catalog-section-card">
                <StudioCatalogInputField
                  inputId="studio-connector-draft-http-base-url"
                  label="Base URL"
                  value={connectorDraft.http.baseUrl}
                  onChange={(value) =>
                    onConnectorDraftChange((draft) => ({
                      ...draft,
                      http: {
                        ...draft.http,
                        baseUrl: value,
                      },
                    }))
                  }
                />
                <StudioCatalogTextAreaField
                  inputId="studio-connector-draft-http-methods"
                  label="Allowed methods"
                  rows={3}
                  value={connectorDraft.http.allowedMethods.join('\n')}
                  onChange={(value) =>
                    onConnectorDraftChange((draft) => ({
                      ...draft,
                      http: {
                        ...draft.http,
                        allowedMethods: parseListText(value).map((item) =>
                          item.toUpperCase(),
                        ),
                      },
                    }))
                  }
                />
                <StudioCatalogTextAreaField
                  inputId="studio-connector-draft-http-paths"
                  label="Allowed paths"
                  rows={3}
                  value={connectorDraft.http.allowedPaths.join('\n')}
                  onChange={(value) =>
                    onConnectorDraftChange((draft) => ({
                      ...draft,
                      http: {
                        ...draft.http,
                        allowedPaths: parseListText(value),
                      },
                    }))
                  }
                />
              </div>
            ) : null}

            {connectorDraft.type === 'cli' ? (
              <div className="studio-catalog-section-card">
                <StudioCatalogInputField
                  inputId="studio-connector-draft-cli-command"
                  label="Command"
                  value={connectorDraft.cli.command}
                  onChange={(value) =>
                    onConnectorDraftChange((draft) => ({
                      ...draft,
                      cli: {
                        ...draft.cli,
                        command: value,
                      },
                    }))
                  }
                />
                <StudioCatalogTextAreaField
                  inputId="studio-connector-draft-cli-fixed-arguments"
                  label="Fixed arguments"
                  rows={3}
                  value={connectorDraft.cli.fixedArguments.join('\n')}
                  onChange={(value) =>
                    onConnectorDraftChange((draft) => ({
                      ...draft,
                      cli: {
                        ...draft.cli,
                        fixedArguments: parseListText(value),
                      },
                    }))
                  }
                />
              </div>
            ) : null}

            {connectorDraft.type === 'mcp' ? (
              <div className="studio-catalog-section-card">
                <StudioCatalogInputField
                  inputId="studio-connector-draft-mcp-server-name"
                  label="Server name"
                  value={connectorDraft.mcp.serverName}
                  onChange={(value) =>
                    onConnectorDraftChange((draft) => ({
                      ...draft,
                      mcp: {
                        ...draft.mcp,
                        serverName: value,
                      },
                    }))
                  }
                />
                <StudioCatalogInputField
                  inputId="studio-connector-draft-mcp-command"
                  label="Command"
                  value={connectorDraft.mcp.command}
                  onChange={(value) =>
                    onConnectorDraftChange((draft) => ({
                      ...draft,
                      mcp: {
                        ...draft.mcp,
                        command: value,
                      },
                    }))
                  }
                />
                <StudioCatalogTextAreaField
                  inputId="studio-connector-draft-mcp-arguments"
                  label="Arguments"
                  rows={3}
                  value={connectorDraft.mcp.arguments.join('\n')}
                  onChange={(value) =>
                    onConnectorDraftChange((draft) => ({
                      ...draft,
                      mcp: {
                        ...draft.mcp,
                        arguments: parseListText(value),
                      },
                    }))
                  }
                />
              </div>
            ) : null}
          </div>
        ) : null}
      </StudioCatalogModalShell>

      {connectorCatalogNotice ? (
        <div className={`studio-catalog-toast ${connectorCatalogNotice.type}`}>
          {connectorCatalogNotice.message}
        </div>
      ) : null}
    </div>
  );
};

export type StudioSettingsPageProps = {
  readonly workspaceSettings: QueryState<StudioWorkspaceSettings>;
  readonly settings: QueryState<unknown>;
  readonly settingsDraft: StudioSettingsDraftLike | null;
  readonly selectedProvider: StudioProviderSettings | null;
  readonly hostMode: 'embedded' | 'proxy';
  readonly workflowStorageMode: 'workspace' | 'scope';
  readonly settingsDirty: boolean;
  readonly settingsPending: boolean;
  readonly runtimeTestPending: boolean;
  readonly settingsNotice: StudioNoticeLike | null;
  readonly runtimeTestResult: StudioRuntimeTestResult | null;
  readonly directoryPath: string;
  readonly directoryLabel: string;
  readonly onSaveSettings: () => void;
  readonly onTestRuntime: () => void;
  readonly onSetSettingsDraft: React.Dispatch<
    React.SetStateAction<StudioSettingsDraftLike | null>
  >;
  readonly onAddProvider: () => void;
  readonly onSelectProviderName: (providerName: string) => void;
  readonly onDeleteSelectedProvider: () => void;
  readonly onSetDefaultProvider: () => void;
  readonly onSetDirectoryPath: (value: string) => void;
  readonly onSetDirectoryLabel: (value: string) => void;
  readonly onAddDirectory: () => void;
  readonly onRemoveDirectory: (directoryId: string) => void;
};

export const StudioSettingsPage: React.FC<StudioSettingsPageProps> = ({
  workspaceSettings,
  settings,
  settingsDraft,
  selectedProvider,
  hostMode,
  workflowStorageMode,
  settingsDirty,
  settingsPending,
  runtimeTestPending,
  settingsNotice,
  runtimeTestResult,
  directoryPath,
  directoryLabel,
  onSaveSettings,
  onTestRuntime,
  onSetSettingsDraft,
  onAddProvider,
  onSelectProviderName,
  onDeleteSelectedProvider,
  onSetDefaultProvider,
  onSetDirectoryPath,
  onSetDirectoryLabel,
  onAddDirectory,
  onRemoveDirectory,
}) => {
  const canEditRuntime = true;
  const canManageDirectories = workflowStorageMode === 'workspace';
  const runtimeBaseUrl =
    settingsDraft?.runtimeBaseUrl ?? workspaceSettings.data?.runtimeBaseUrl ?? '';
  const runtimeFieldLabel = canEditRuntime
    ? 'Runtime endpoint'
    : 'Host-managed runtime endpoint';
  const runtimeActionLabel = canEditRuntime
    ? 'Test runtime'
    : 'Check host runtime';
  const [advancedSettingsOpen, setAdvancedSettingsOpen] = React.useState(false);
  const providerFormGridStyle: React.CSSProperties = {
    display: 'grid',
    gap: 12,
    gridTemplateColumns: 'repeat(auto-fit, minmax(220px, 1fr))',
  };
  const sectionPanelStyle: React.CSSProperties = {
    ...embeddedPanelStyle,
    display: 'flex',
    flexDirection: 'column',
    gap: 12,
  };
  const providers = settingsDraft?.providers ?? [];
  const selectedProviderIsDefault = Boolean(
    selectedProvider &&
      settingsDraft?.defaultProviderName === selectedProvider.providerName,
  );
  const runtimeModeLabel = canEditRuntime ? 'Editable' : 'Host managed';
  const workflowSourcesLabel = canManageDirectories
    ? 'Workspace directories'
    : 'Scope bound';

  return (
    <>
      <ProCard
        title="Provider settings"
        extra={
          <Space wrap size={[8, 8]}>
            <Button onClick={() => setAdvancedSettingsOpen(true)}>
              Advanced settings
            </Button>
          </Space>
        }
        {...moduleCardProps}
        style={fillCardStyle}
        loading={settings.isLoading}
      >
        {settings.isError ? (
          <StudioNoticeCard
            type="error"
            title="Failed to load workbench config"
            description={describeError(settings.error)}
          />
        ) : settingsDraft ? (
          <div style={cardStackStyle}>
            <div style={studioNoticeStripStyle}>
              <StudioNoticeCard
                type={settingsDirty ? 'warning' : 'success'}
                title="Workbench config"
                description={
                  settingsDirty
                    ? 'You have unsaved Studio settings changes.'
                    : 'Studio settings are in sync with workbench config.'
                }
                action={
                  <Button
                    type="primary"
                    loading={settingsPending}
                    disabled={!settingsDirty}
                    onClick={onSaveSettings}
                  >
                    Save workbench config
                  </Button>
                }
              />
              <StudioNoticeCard
                title="Provider routing"
                description={`Default provider: ${
                  settingsDraft.defaultProviderName || 'n/a'
                }`}
              />
              {settingsNotice ? (
                <StudioNoticeCard
                  type={settingsNotice.type}
                  title={
                    settingsNotice.type === 'error'
                      ? 'Config action failed'
                      : 'Config updated'
                  }
                  description={settingsNotice.message}
                />
              ) : null}
            </div>

            <div style={sectionPanelStyle}>
              <div style={summaryMetricGridStyle}>
                <StudioSummaryMetric label="Providers" value={providers.length} />
                <StudioSummaryMetric
                  label="Default"
                  tone="info"
                  value={settingsDraft.defaultProviderName || 'n/a'}
                />
                <StudioSummaryMetric label="Runtime mode" value={runtimeModeLabel} />
                <StudioSummaryMetric
                  label="Workflow sources"
                  value={workflowSourcesLabel}
                />
              </div>
              <div style={summaryFieldGridStyle}>
                <StudioSummaryField
                  label="Selected provider"
                  value={selectedProvider?.providerName || 'None selected'}
                />
                <StudioSummaryField
                  label="Runtime endpoint"
                  value={runtimeBaseUrl || 'n/a'}
                  copyable={Boolean(runtimeBaseUrl)}
                />
              </div>
            </div>

            <Row gutter={[16, 16]} align="stretch">
              <Col xs={24} xl={9} style={stretchColumnStyle}>
                <div style={sectionPanelStyle}>
                  <div
                    style={{
                      alignItems: 'center',
                      display: 'flex',
                      gap: 12,
                      justifyContent: 'space-between',
                    }}
                  >
                    <div>
                      <Typography.Text strong>Provider catalog</Typography.Text>
                      <Typography.Paragraph
                        style={{ margin: '4px 0 0' }}
                        type="secondary"
                      >
                        Pick a provider to edit, or create a new one before wiring it
                        into Studio.
                      </Typography.Paragraph>
                    </div>
                    <Button type="primary" onClick={onAddProvider}>
                      Add provider
                    </Button>
                  </div>

                  {providers.length > 0 ? (
                    <div style={cardListStyle}>
                      {providers.map((provider) => {
                        const isSelected =
                          selectedProvider?.providerName === provider.providerName;

                        return (
                          <div
                            key={provider.providerName}
                            style={{
                              ...cardListItemStyle,
                              background: isSelected
                                ? 'rgba(240, 245, 255, 0.96)'
                                : cardListItemStyle.background,
                              borderColor: isSelected
                                ? 'rgba(22, 119, 255, 0.28)'
                                : 'var(--ant-color-border-secondary)',
                            }}
                          >
                            <div style={cardListHeaderStyle}>
                              <div style={cardListMainStyle}>
                                <Typography.Text strong>
                                  {provider.providerName}
                                </Typography.Text>
                                <Typography.Text type="secondary">
                                  {provider.model ||
                                    provider.displayName ||
                                    provider.providerType}
                                </Typography.Text>
                                <Space wrap size={[6, 6]}>
                                  <Tag>{provider.providerType}</Tag>
                                  {provider.endpoint ? (
                                    <Tag color="processing">endpoint</Tag>
                                  ) : null}
                                  {settingsDraft.defaultProviderName ===
                                  provider.providerName ? (
                                    <Tag color="success">default</Tag>
                                  ) : null}
                                </Space>
                              </div>
                              <div style={cardListActionStyle}>
                                <Button
                                  type={isSelected ? 'primary' : 'default'}
                                  size="small"
                                  onClick={() =>
                                    onSelectProviderName(provider.providerName)
                                  }
                                >
                                  {isSelected ? 'Selected' : 'Edit'}
                                </Button>
                              </div>
                            </div>
                            {provider.endpoint ? (
                              <Typography.Paragraph
                                style={{ margin: 0 }}
                                type="secondary"
                              >
                                {provider.endpoint}
                              </Typography.Paragraph>
                            ) : null}
                          </div>
                        );
                      })}
                    </div>
                  ) : (
                    <Empty
                      image={Empty.PRESENTED_IMAGE_SIMPLE}
                      description="No providers are configured in workbench config."
                    />
                  )}
                </div>
              </Col>
              <Col xs={24} xl={15} style={stretchColumnStyle}>
                {selectedProvider ? (
                  <div style={cardStackStyle}>
                    <div style={sectionPanelStyle}>
                      <div
                        style={{
                          alignItems: 'center',
                          display: 'flex',
                          gap: 12,
                          justifyContent: 'space-between',
                        }}
                      >
                        <div>
                          <Typography.Text strong>Provider detail</Typography.Text>
                          <Typography.Paragraph
                            style={{ margin: '4px 0 0' }}
                            type="secondary"
                          >
                            Editing {selectedProvider.providerName} inside the current
                            Studio workbench config.
                          </Typography.Paragraph>
                        </div>
                        <div style={cardListActionStyle}>
                          <Button danger onClick={onDeleteSelectedProvider}>
                            Delete provider
                          </Button>
                          <Button
                            disabled={selectedProviderIsDefault}
                            onClick={onSetDefaultProvider}
                          >
                            {selectedProviderIsDefault
                              ? 'Default provider'
                              : 'Set as default'}
                          </Button>
                        </div>
                      </div>
                      <div style={summaryMetricGridStyle}>
                        <StudioSummaryMetric
                          label="Provider type"
                          tone="info"
                          value={selectedProvider.providerType}
                        />
                        <StudioSummaryMetric
                          label="Default"
                          tone={selectedProviderIsDefault ? 'success' : 'default'}
                          value={selectedProviderIsDefault ? 'Yes' : 'No'}
                        />
                        <StudioSummaryMetric
                          label="Endpoint"
                          value={selectedProvider.endpoint ? 'Configured' : 'Missing'}
                          tone={selectedProvider.endpoint ? 'success' : 'warning'}
                        />
                        <StudioSummaryMetric
                          label="API key"
                          value={
                            selectedProvider.clearApiKeyRequested
                              ? 'Will clear'
                              : selectedProvider.apiKeyConfigured
                                ? 'Stored'
                                : selectedProvider.apiKey
                                  ? 'Pending update'
                                  : 'Empty'
                          }
                          tone={
                            selectedProvider.clearApiKeyRequested
                              ? 'warning'
                              : selectedProvider.apiKeyConfigured
                                ? 'success'
                                : 'default'
                          }
                        />
                      </div>
                      <div style={summaryFieldGridStyle}>
                        <StudioSummaryField
                          label="Display name"
                          value={selectedProvider.displayName || 'n/a'}
                        />
                        <StudioSummaryField
                          label="Category"
                          value={selectedProvider.category || 'n/a'}
                        />
                      </div>
                      {selectedProvider.description ? (
                        <div>
                          <Typography.Text style={summaryFieldLabelStyle}>
                            Description
                          </Typography.Text>
                          <Typography.Paragraph
                            style={{ margin: '8px 0 0' }}
                            type="secondary"
                          >
                            {selectedProvider.description}
                          </Typography.Paragraph>
                        </div>
                      ) : null}
                    </div>

                    <div style={sectionPanelStyle}>
                      <Typography.Text strong>Connection and model</Typography.Text>
                      <div style={providerFormGridStyle}>
                        <div style={cardStackStyle}>
                          <Typography.Text strong>Provider name</Typography.Text>
                          <Input
                            aria-label="Studio provider name"
                            value={selectedProvider.providerName}
                            onChange={(event) => {
                              const nextName = event.target.value;
                              onSetSettingsDraft((current) =>
                                current
                                  ? {
                                      ...current,
                                      providers: current.providers.map((provider) =>
                                        provider.providerName ===
                                        selectedProvider.providerName
                                          ? {
                                              ...provider,
                                              providerName: nextName,
                                            }
                                          : provider,
                                      ),
                                      defaultProviderName:
                                        current.defaultProviderName ===
                                        selectedProvider.providerName
                                          ? nextName
                                          : current.defaultProviderName,
                                    }
                                  : current,
                              );
                              onSelectProviderName(nextName);
                            }}
                          />
                        </div>
                        <div style={cardStackStyle}>
                          <Typography.Text strong>Provider type</Typography.Text>
                          <Select
                            aria-label="Studio provider type"
                            value={selectedProvider.providerType}
                            options={settingsDraft.providerTypes.map((type) => ({
                              label: type.displayName,
                              value: type.id,
                            }))}
                            onChange={(value) => {
                              const profile =
                                settingsDraft.providerTypes.find(
                                  (type) => type.id === value,
                                ) || null;
                              onSetSettingsDraft((current) =>
                                current
                                  ? {
                                      ...current,
                                      providers: current.providers.map((provider) =>
                                        provider.providerName ===
                                        selectedProvider.providerName
                                          ? {
                                              ...provider,
                                              providerType: value,
                                              displayName:
                                                profile?.displayName ||
                                                provider.displayName,
                                              category:
                                                profile?.category ||
                                                provider.category,
                                              description:
                                                profile?.description ||
                                                provider.description,
                                              endpoint:
                                                provider.endpoint ||
                                                profile?.defaultEndpoint ||
                                                '',
                                              model:
                                                provider.model ||
                                                profile?.defaultModel ||
                                                '',
                                            }
                                          : provider,
                                      ),
                                    }
                                  : current,
                              );
                            }}
                          />
                        </div>
                        <div style={cardStackStyle}>
                          <Typography.Text strong>Model</Typography.Text>
                          <Input
                            aria-label="Studio provider model"
                            value={selectedProvider.model}
                            onChange={(event) =>
                              onSetSettingsDraft((current) =>
                                current
                                  ? {
                                      ...current,
                                      providers: current.providers.map((provider) =>
                                        provider.providerName ===
                                        selectedProvider.providerName
                                          ? { ...provider, model: event.target.value }
                                          : provider,
                                      ),
                                    }
                                  : current,
                              )
                            }
                          />
                        </div>
                        <div style={cardStackStyle}>
                          <Typography.Text strong>Endpoint</Typography.Text>
                          <Input
                            aria-label="Studio provider endpoint"
                            value={selectedProvider.endpoint}
                            onChange={(event) =>
                              onSetSettingsDraft((current) =>
                                current
                                  ? {
                                      ...current,
                                      providers: current.providers.map((provider) =>
                                        provider.providerName ===
                                        selectedProvider.providerName
                                          ? {
                                              ...provider,
                                              endpoint: event.target.value,
                                            }
                                          : provider,
                                      ),
                                    }
                                  : current,
                              )
                            }
                          />
                        </div>
                      </div>
                    </div>

                    <div style={sectionPanelStyle}>
                      <Typography.Text strong>Secrets</Typography.Text>
                      <Space wrap size={[8, 8]}>
                        {selectedProvider.apiKeyConfigured ? (
                          <Tag color="success">saved key configured</Tag>
                        ) : null}
                        {selectedProvider.clearApiKeyRequested ? (
                          <Tag color="warning">key will be cleared on save</Tag>
                        ) : null}
                        {selectedProvider.apiKeyConfigured ||
                        selectedProvider.clearApiKeyRequested ? (
                          <Button
                            danger={!selectedProvider.clearApiKeyRequested}
                            onClick={() =>
                              onSetSettingsDraft((current) =>
                                current
                                  ? {
                                      ...current,
                                      providers: current.providers.map((provider) =>
                                        provider.providerName ===
                                        selectedProvider.providerName
                                          ? {
                                              ...provider,
                                              apiKey: '',
                                              clearApiKeyRequested:
                                                !provider.clearApiKeyRequested,
                                            }
                                          : provider,
                                      ),
                                    }
                                  : current,
                              )
                            }
                          >
                            {selectedProvider.clearApiKeyRequested
                              ? 'Keep saved key'
                              : 'Clear saved key'}
                          </Button>
                        ) : null}
                      </Space>
                      <Input.Password
                        aria-label="Studio provider API key"
                        value={selectedProvider.apiKey}
                        placeholder={
                          selectedProvider.clearApiKeyRequested
                            ? 'Saved key will be removed when you save'
                            : selectedProvider.apiKeyConfigured
                              ? 'Configured. Enter a new key to replace it'
                              : 'optional'
                        }
                        onChange={(event) =>
                          onSetSettingsDraft((current) =>
                            current
                              ? {
                                  ...current,
                                  providers: current.providers.map((provider) =>
                                    provider.providerName ===
                                    selectedProvider.providerName
                                      ? {
                                          ...provider,
                                          apiKey: event.target.value,
                                          clearApiKeyRequested: false,
                                        }
                                      : provider,
                                  ),
                                }
                              : current,
                          )
                        }
                      />
                      <Typography.Text type="secondary">
                        Leave this blank to keep the saved secret. Enter a new value
                        only when you want to replace it.
                      </Typography.Text>
                    </div>
                  </div>
                ) : (
                  <div style={sectionPanelStyle}>
                    <Typography.Text strong>Provider detail</Typography.Text>
                    <Empty
                      image={Empty.PRESENTED_IMAGE_SIMPLE}
                      description="Select a provider to edit or add a new one."
                    />
                  </div>
                )}
              </Col>
            </Row>
          </div>
        ) : null}
      </ProCard>

      <Drawer
        open={advancedSettingsOpen}
        onClose={() => setAdvancedSettingsOpen(false)}
        title="Advanced settings"
        size={560}
        styles={{ body: drawerBodyStyle }}
      >
        <div style={drawerScrollStyle}>
          <div style={cardStackStyle}>
          <ProCard
            title="Runtime connection"
            {...moduleCardProps}
            style={fillCardStyle}
            loading={workspaceSettings.isLoading || settings.isLoading}
          >
            {workspaceSettings.isError ? (
              <StudioNoticeCard
                type="error"
                title="Failed to load workspace settings"
                description={describeError(workspaceSettings.error)}
              />
            ) : settings.isError ? (
              <StudioNoticeCard
                type="error"
                title="Failed to load workbench config"
                description={describeError(settings.error)}
              />
            ) : settingsDraft ? (
              <div style={cardStackStyle}>
                {canEditRuntime ? null : (
                  <StudioNoticeCard
                    type="info"
                    title="Runtime is host-managed in embedded mode"
                    description="Studio runs against the local runtime hosted by aevatar app. The endpoint is shown for reference and health checks only."
                  />
                )}
                <div style={sectionPanelStyle}>
                  <div style={summaryFieldGridStyle}>
                    <StudioSummaryField label="Runtime mode" value={runtimeModeLabel} />
                    <StudioSummaryField
                      label={runtimeFieldLabel}
                      value={runtimeBaseUrl || 'n/a'}
                      copyable={Boolean(runtimeBaseUrl)}
                    />
                  </div>
                  <Input
                    aria-label="Studio runtime base URL"
                    value={runtimeBaseUrl}
                    disabled={!canEditRuntime}
                    onChange={(event) =>
                      onSetSettingsDraft((current) =>
                        current
                          ? { ...current, runtimeBaseUrl: event.target.value }
                          : current,
                      )
                    }
                  />
                  <Button loading={runtimeTestPending} onClick={onTestRuntime}>
                    {runtimeActionLabel}
                  </Button>
                </div>
                {runtimeTestResult ? (
                  <StudioNoticeCard
                    type={runtimeTestResult.reachable ? 'success' : 'warning'}
                    title={
                      runtimeTestResult.reachable
                        ? 'Runtime is reachable'
                        : 'Runtime check failed'
                    }
                    description={`${runtimeTestResult.message} · ${runtimeTestResult.checkedUrl}`}
                  />
                ) : null}
              </div>
            ) : null}
          </ProCard>

          <ProCard
            title="Workflow sources"
            {...moduleCardProps}
            style={fillCardStyle}
            loading={workspaceSettings.isLoading}
          >
            {workspaceSettings.isError ? (
              <StudioNoticeCard
                type="error"
                title="Failed to load workflow sources"
                description={describeError(workspaceSettings.error)}
              />
            ) : workspaceSettings.data ? (
              <div style={cardStackStyle}>
                {!canManageDirectories ? (
                  <StudioNoticeCard
                    type="info"
                    title="Workflow source is bound to the current scope"
                    description="Studio hides directory management when workflows are resolved from the active login scope."
                  />
                ) : null}
                <div style={summaryMetricGridStyle}>
                  <StudioSummaryMetric
                    label="Sources"
                    value={workspaceSettings.data.directories.length}
                  />
                  <StudioSummaryMetric
                    label="Mode"
                    tone="info"
                    value={workflowSourcesLabel}
                  />
                </div>
                {workspaceSettings.data.directories.length > 0 ? (
                  <div style={cardListStyle}>
                    {workspaceSettings.data.directories.map((directory) => {
                      const showDirectoryPath =
                        workflowStorageMode !== 'scope' &&
                        !isScopeDirectoryPath(directory.path);

                      return (
                        <div key={directory.directoryId} style={cardListItemStyle}>
                          <div style={cardListHeaderStyle}>
                            <div style={cardListMainStyle}>
                              <Typography.Text strong>{directory.label}</Typography.Text>
                              <Space wrap size={[6, 6]}>
                                {directory.isBuiltIn ? <Tag>built-in</Tag> : null}
                                <Tag>{directory.directoryId}</Tag>
                              </Space>
                            </div>
                            {canManageDirectories && !directory.isBuiltIn ? (
                              <div style={cardListActionStyle}>
                                <Button
                                  danger
                                  size="small"
                                  loading={settingsPending}
                                  onClick={() => onRemoveDirectory(directory.directoryId)}
                                >
                                  Remove
                                </Button>
                              </div>
                            ) : null}
                          </div>
                          {showDirectoryPath ? (
                            <Typography.Paragraph
                              copyable
                              style={{ margin: 0 }}
                              type="secondary"
                            >
                              {directory.path}
                            </Typography.Paragraph>
                          ) : null}
                        </div>
                      );
                    })}
                  </div>
                ) : (
                  <Empty
                    image={Empty.PRESENTED_IMAGE_SIMPLE}
                    description="No workflow sources are configured."
                  />
                )}

                {canManageDirectories ? (
                  <>
                    <Divider style={{ margin: 0 }}>Add workflow directory</Divider>
                    <div style={sectionPanelStyle}>
                      <Input
                        aria-label="Studio directory path"
                        placeholder="/path/to/workflows"
                        value={directoryPath}
                        onChange={(event) => onSetDirectoryPath(event.target.value)}
                      />
                      <Input
                        aria-label="Studio directory label"
                        placeholder="optional label"
                        value={directoryLabel}
                        onChange={(event) => onSetDirectoryLabel(event.target.value)}
                      />
                      <Button
                        type="primary"
                        loading={settingsPending}
                        onClick={onAddDirectory}
                      >
                        Add directory
                      </Button>
                    </div>
                  </>
                ) : null}
              </div>
            ) : null}
          </ProCard>
          </div>
        </div>
      </Drawer>
    </>
  );
};
