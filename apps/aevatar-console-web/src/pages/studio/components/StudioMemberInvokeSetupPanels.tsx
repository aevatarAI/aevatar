import {
  ClearOutlined,
  LinkOutlined,
  PlayCircleOutlined,
  StopOutlined,
} from '@ant-design/icons';
import { Alert, Button, Drawer, Input, Tooltip, Typography } from 'antd';
import React from 'react';
import type { InvokeResultState } from './StudioMemberInvokePanel.currentRun';
import { AevatarPanel } from '@/shared/ui/aevatarPageShells';

type StudioMemberInvokeContractPanelProps = {
  readonly actorId: string;
  readonly contractError: string;
  readonly endpointLabel: string;
  readonly memberLabel: string;
  readonly publishedContext: string;
  readonly revisionId: string;
  readonly statusLabel: string;
};

type StudioMemberInvokeComposerPanelProps = {
  readonly canInvoke: boolean;
  readonly defaultPrompt: string;
  readonly effectiveRequestTypeUrl: string;
  readonly effectiveResponseTypeUrl: string;
  readonly endpointKind: string;
  readonly formError: string;
  readonly invokeStatus: InvokeResultState['status'];
  readonly isChatEndpoint: boolean;
  readonly layout?: 'panel' | 'dock';
  readonly payloadBase64: string;
  readonly payloadJsonPreview: string;
  readonly payloadTypeUrl: string;
  readonly prompt: string;
  readonly hasOpenRunsTarget: boolean;
  readonly onAbort: () => void;
  readonly onClear: () => void;
  readonly onInvoke: () => void;
  readonly onOpenRuns: () => void;
  readonly onPayloadBase64Change: (value: string) => void;
  readonly onPayloadJsonPreviewChange: (value: string) => void;
  readonly onPayloadTypeUrlChange: (value: string) => void;
  readonly onPromptChange: (value: string) => void;
};

function trimOptional(value: string | null | undefined): string {
  return value?.trim() ?? '';
}

function truncateMiddle(value: string, head = 18, tail = 12): string {
  if (value.length <= head + tail + 3) {
    return value;
  }

  return `${value.slice(0, head)}...${value.slice(-tail)}`;
}

function getStatusStyle(statusLabel: string): React.CSSProperties {
  if (statusLabel === '已就绪') {
    return {
      background: '#f0fdf4',
      border: '1px solid #86efac',
      color: '#15803d',
    };
  }

  return {
    background: '#f8fafc',
    border: '1px solid #e5e7eb',
    color: '#475569',
  };
}

const contractGridStyle: React.CSSProperties = {
  display: 'grid',
  gap: 12,
  gridTemplateColumns: 'repeat(auto-fit, minmax(140px, 1fr))',
  minWidth: 0,
};

const contractFieldStyle: React.CSSProperties = {
  display: 'grid',
  gap: 4,
  minWidth: 0,
};

const contractLabelStyle: React.CSSProperties = {
  color: '#64748b',
  fontSize: 11,
  fontWeight: 700,
  letterSpacing: 0.4,
  lineHeight: '16px',
  textTransform: 'uppercase',
};

const contractValueStyle: React.CSSProperties = {
  color: '#111827',
  display: 'block',
  fontSize: 13,
  fontWeight: 600,
  lineHeight: '20px',
  minWidth: 0,
  overflowWrap: 'anywhere',
  wordBreak: 'break-word',
};

const contractCompactValueStyle: React.CSSProperties = {
  ...contractValueStyle,
  maxWidth: '100%',
  overflow: 'hidden',
  overflowWrap: 'normal',
  textOverflow: 'ellipsis',
  whiteSpace: 'nowrap',
  wordBreak: 'normal',
};

const contractStatusPillBaseStyle: React.CSSProperties = {
  borderRadius: 999,
  display: 'inline-flex',
  fontSize: 12,
  fontWeight: 700,
  lineHeight: '18px',
  padding: '4px 10px',
  width: 'fit-content',
};

const helperTextStyle: React.CSSProperties = {
  color: '#64748b',
  fontSize: 13,
  lineHeight: 1.6,
  minWidth: 0,
};

const playgroundActionsStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  flexWrap: 'wrap',
  gap: 8,
  justifyContent: 'flex-start',
};

const dockComposerStyle: React.CSSProperties = {
  background: '#ffffff',
  borderTop: '1px solid #e5e7eb',
  display: 'grid',
  gap: 8,
  minWidth: 0,
  padding: '12px 4px 0',
};

const dockComposerRowStyle: React.CSSProperties = {
  alignItems: 'flex-end',
  display: 'grid',
  gap: 10,
  gridTemplateColumns: 'minmax(0, 1fr) auto',
  minWidth: 0,
};

const promptLabelRowStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  justifyContent: 'space-between',
  minWidth: 0,
};

const promptKickerStyle: React.CSSProperties = {
  color: '#1677ff',
  fontSize: 11,
  fontWeight: 800,
  letterSpacing: 1.2,
  lineHeight: '16px',
  textTransform: 'uppercase',
};

const controlsGridStyle: React.CSSProperties = {
  display: 'grid',
  gap: 10,
  gridTemplateColumns: 'repeat(auto-fit, minmax(280px, 1fr))',
  minWidth: 0,
};

const advancedDetailsStyle: React.CSSProperties = {
  background: '#ffffff',
  border: '1px solid #e5e7eb',
  borderRadius: 8,
  boxSizing: 'border-box',
  maxHeight: 260,
  minWidth: 0,
  overflowY: 'auto',
  padding: '8px 12px',
  position: 'relative',
  zIndex: 1,
};

const advancedSummaryStyle: React.CSSProperties = {
  color: '#334155',
  cursor: 'pointer',
  fontSize: 13,
  fontWeight: 700,
  lineHeight: '20px',
};

const advancedBodyStyle: React.CSSProperties = {
  display: 'grid',
  gap: 10,
  marginTop: 10,
  minWidth: 0,
};

const advancedDrawerBodyStyle: React.CSSProperties = {
  display: 'grid',
  gap: 14,
  minWidth: 0,
};

const advancedDockTriggerStyle: React.CSSProperties = {
  justifySelf: 'start',
};

const CompactCopyableValue: React.FC<{
  readonly fallback?: string;
  readonly head?: number;
  readonly tail?: number;
  readonly value?: string;
}> = ({ fallback = '—', head = 10, tail = 8, value }) => {
  const normalized = trimOptional(value);
  if (!normalized) {
    return (
      <Typography.Text style={helperTextStyle} type="secondary">
        {fallback}
      </Typography.Text>
    );
  }

  const displayValue = truncateMiddle(normalized, head, tail);

  return (
    <Tooltip title={normalized}>
      <Typography.Text
        copyable={{ text: normalized }}
        style={contractCompactValueStyle}
      >
        {displayValue}
      </Typography.Text>
    </Tooltip>
  );
};

const AdvancedTypedPayloadFields: React.FC<{
  readonly effectiveRequestTypeUrl: string;
  readonly effectiveResponseTypeUrl: string;
  readonly endpointKind: string;
  readonly payloadBase64: string;
  readonly payloadJsonPreview: string;
  readonly payloadTypeUrl: string;
  readonly onPayloadBase64Change: (value: string) => void;
  readonly onPayloadJsonPreviewChange: (value: string) => void;
  readonly onPayloadTypeUrlChange: (value: string) => void;
}> = ({
  effectiveRequestTypeUrl,
  effectiveResponseTypeUrl,
  endpointKind,
  onPayloadBase64Change,
  onPayloadJsonPreviewChange,
  onPayloadTypeUrlChange,
  payloadBase64,
  payloadJsonPreview,
  payloadTypeUrl,
}) => (
  <div style={advancedDrawerBodyStyle}>
    <div style={contractGridStyle}>
      <div style={contractFieldStyle}>
        <div style={contractLabelStyle}>Request type</div>
        <CompactCopyableValue fallback="未声明" value={effectiveRequestTypeUrl} />
      </div>
      <div style={contractFieldStyle}>
        <div style={contractLabelStyle}>Response type</div>
        <CompactCopyableValue fallback="未声明" value={effectiveResponseTypeUrl} />
      </div>
      <div style={contractFieldStyle}>
        <div style={contractLabelStyle}>Endpoint kind</div>
        <div style={contractValueStyle}>{endpointKind || 'command'}</div>
      </div>
    </div>
    <div style={controlsGridStyle}>
      <div style={{ display: 'grid', gap: 8, minWidth: 0 }}>
        <Typography.Text strong>Request type URL</Typography.Text>
        <Input
          placeholder="type.googleapis.com/example.Command"
          value={payloadTypeUrl}
          onChange={(event) => onPayloadTypeUrlChange(event.target.value)}
        />
      </div>
      <div style={{ display: 'grid', gap: 8, minWidth: 0 }}>
        <Typography.Text strong>Protobuf payload Base64</Typography.Text>
        <Input.TextArea
          autoSize={{ minRows: 3, maxRows: 6 }}
          placeholder="粘贴与 request type 对应的 protobuf payload base64。"
          value={payloadBase64}
          onChange={(event) => onPayloadBase64Change(event.target.value)}
        />
      </div>
    </div>
    <div style={{ display: 'grid', gap: 8 }}>
      <Typography.Text strong>JSON scratchpad</Typography.Text>
      <Input.TextArea
        autoSize={{ minRows: 4, maxRows: 8 }}
        placeholder="可在这里整理 typed payload 的 JSON 形态；Studio 不会把 JSON 冒充成 protobuf bytes。"
        value={payloadJsonPreview}
        onChange={(event) => onPayloadJsonPreviewChange(event.target.value)}
      />
      <Typography.Text style={helperTextStyle} type="secondary">
        Invoke still sends protobuf `payloadBase64`; JSON scratchpad is only for composing or reviewing typed input.
      </Typography.Text>
    </div>
  </div>
);

export const StudioMemberInvokeContractPanel: React.FC<
  StudioMemberInvokeContractPanelProps
> = ({
  actorId,
  contractError,
  endpointLabel,
  memberLabel,
  publishedContext,
  revisionId,
  statusLabel,
}) => (
  <AevatarPanel
    layoutMode="document"
    padding={10}
    title="调用契约"
    titleHelp="这里只保留当前调用对象和契约准备状态，不展示运行结果，也不读取输入校验。"
  >
    <div style={contractGridStyle}>
      <div style={contractFieldStyle}>
        <div style={contractLabelStyle}>状态</div>
        <span
          style={{
            ...contractStatusPillBaseStyle,
            ...getStatusStyle(statusLabel),
          }}
        >
          {statusLabel}
        </span>
      </div>
      <div style={contractFieldStyle}>
        <div style={contractLabelStyle}>Member</div>
        <div style={contractValueStyle}>{memberLabel}</div>
      </div>
      <div style={contractFieldStyle}>
        <div style={contractLabelStyle}>Endpoint</div>
        <CompactCopyableValue
          fallback="未选择"
          head={16}
          tail={10}
          value={endpointLabel}
        />
      </div>
      <div style={contractFieldStyle}>
        <div style={contractLabelStyle}>Revision</div>
        <CompactCopyableValue
          fallback="尚未开始服务"
          head={8}
          tail={8}
          value={revisionId}
        />
      </div>
      <div style={contractFieldStyle}>
        <div style={contractLabelStyle}>Published Context</div>
        <CompactCopyableValue
          fallback="尚未配置"
          head={8}
          tail={8}
          value={publishedContext}
        />
      </div>
      <div style={contractFieldStyle}>
        <div style={contractLabelStyle}>Actor ID</div>
        <CompactCopyableValue
          fallback="尚未分配"
          head={12}
          tail={10}
          value={actorId}
        />
      </div>
    </div>
    {contractError ? (
      <Alert
        showIcon
        message="调用契约详情暂时不可用，当前会使用服务目录里的端点声明。"
        type="warning"
      />
    ) : null}
  </AevatarPanel>
);

export const StudioMemberInvokeComposerPanel: React.FC<
  StudioMemberInvokeComposerPanelProps
> = ({
  canInvoke,
  defaultPrompt,
  effectiveRequestTypeUrl,
  effectiveResponseTypeUrl,
  endpointKind,
  formError,
  hasOpenRunsTarget,
  invokeStatus,
  isChatEndpoint,
  layout = 'panel',
  onAbort,
  onClear,
  onInvoke,
  onOpenRuns,
  onPayloadBase64Change,
  onPayloadJsonPreviewChange,
  onPayloadTypeUrlChange,
  onPromptChange,
  payloadBase64,
  payloadJsonPreview,
  payloadTypeUrl,
  prompt,
}) => {
  const [advancedOpen, setAdvancedOpen] = React.useState(false);
  const promptPlaceholder = isChatEndpoint
    ? defaultPrompt || '输入你想发给当前成员的消息...'
    : '输入一句话发起调用；需要 typed payload 时展开 Advanced。';
  const primaryButtonLabel = isChatEndpoint ? 'Send' : 'Invoke';
  const advancedFields = (
    <AdvancedTypedPayloadFields
      effectiveRequestTypeUrl={effectiveRequestTypeUrl}
      effectiveResponseTypeUrl={effectiveResponseTypeUrl}
      endpointKind={endpointKind}
      payloadBase64={payloadBase64}
      payloadJsonPreview={payloadJsonPreview}
      payloadTypeUrl={payloadTypeUrl}
      onPayloadBase64Change={onPayloadBase64Change}
      onPayloadJsonPreviewChange={onPayloadJsonPreviewChange}
      onPayloadTypeUrlChange={onPayloadTypeUrlChange}
    />
  );
  const content = (
    <div style={layout === 'dock' ? dockComposerStyle : { display: 'grid', gap: 12 }}>
      <div style={{ display: 'grid', gap: 8, minWidth: 0 }}>
        <div style={promptLabelRowStyle}>
          <span style={promptKickerStyle}>Prompt</span>
          {layout === 'dock' ? (
            <Typography.Text style={helperTextStyle} type="secondary">
              Enter to send
            </Typography.Text>
          ) : null}
        </div>
        {layout === 'dock' ? (
          <div style={dockComposerRowStyle}>
            <Input.TextArea
              aria-label="调用请求输入"
              autoSize={{ minRows: 1, maxRows: 4 }}
              placeholder={promptPlaceholder}
              value={prompt}
              onChange={(event) => onPromptChange(event.target.value)}
            />
            <Button
              disabled={!canInvoke || invokeStatus === 'running'}
              icon={<PlayCircleOutlined />}
              onClick={onInvoke}
              size="large"
              type="primary"
            >
              {primaryButtonLabel}
            </Button>
          </div>
        ) : (
          <Input.TextArea
            aria-label="调用请求输入"
            autoSize={{ minRows: 4, maxRows: 8 }}
            placeholder={promptPlaceholder}
            value={prompt}
            onChange={(event) => onPromptChange(event.target.value)}
          />
        )}
        {formError ? (
          <Typography.Text type="danger">{formError}</Typography.Text>
        ) : isChatEndpoint ? (
          <Typography.Text style={helperTextStyle} type="secondary">
            这是当前成员的对话输入区。开始对话后，结果会直接显示在下方工作台。
          </Typography.Text>
        ) : (
          <Typography.Text style={helperTextStyle} type="secondary">
            直接输入一句话并点击 Invoke；需要 typed payload 时再展开 Advanced。
          </Typography.Text>
        )}
      </div>

      <div data-testid="studio-invoke-playground-actions" style={playgroundActionsStyle}>
        {layout === 'panel' ? (
          <Button
            disabled={!canInvoke || invokeStatus === 'running'}
            icon={<PlayCircleOutlined />}
            onClick={onInvoke}
            type="primary"
          >
            {primaryButtonLabel}
          </Button>
        ) : null}
        <Button
          disabled={invokeStatus !== 'running'}
          icon={<StopOutlined />}
          onClick={onAbort}
        >
          中止
        </Button>
        <Button
          disabled={!hasOpenRunsTarget}
          icon={<LinkOutlined />}
          onClick={onOpenRuns}
        >
          打开运行记录
        </Button>
        <Button icon={<ClearOutlined />} onClick={onClear}>
          清空
        </Button>
      </div>

      {!isChatEndpoint ? (
        layout === 'dock' ? (
          <>
            <Button
              style={advancedDockTriggerStyle}
              type="link"
              onClick={() => setAdvancedOpen(true)}
            >
              Advanced typed payload
            </Button>
            <Drawer
              destroyOnHidden
              open={advancedOpen}
              placement="right"
              size="large"
              title="Advanced typed payload"
              onClose={() => setAdvancedOpen(false)}
            >
              <div aria-label="Typed invoke form">{advancedFields}</div>
            </Drawer>
          </>
        ) : (
          <details aria-label="Typed invoke form" style={advancedDetailsStyle}>
            <summary style={advancedSummaryStyle}>Advanced typed payload</summary>
            <div style={advancedBodyStyle}>{advancedFields}</div>
          </details>
        )
      ) : null}
    </div>
  );

  if (layout === 'dock') {
    return content;
  }

  return (
    <AevatarPanel
      layoutMode="document"
      padding={14}
      title="调试台"
      titleHelp="先输入 prompt 或载荷，再直接执行当前成员调用。"
    >
      {content}
    </AevatarPanel>
  );
};
