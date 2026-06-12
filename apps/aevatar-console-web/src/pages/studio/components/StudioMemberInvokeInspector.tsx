import {
  ApartmentOutlined,
  HistoryOutlined,
  InfoCircleOutlined,
} from '@ant-design/icons';
import { Drawer, Grid, Input, Segmented, Typography } from 'antd';
import React from 'react';
import {
  AEVATAR_GLOBAL_UI_SPEC,
  aevatarDrawerBodyStyle,
  aevatarDrawerScrollStyle,
} from '@/shared/ui/aevatarWorkbench';
import type {
  CurrentRunRequest,
  InvokeHistoryEntry,
  InvokeResultState,
  StudioInvokeChatMessage,
} from './StudioMemberInvokePanel.currentRun';
import StudioMemberCurrentRunPanel from './StudioMemberCurrentRunPanel';
import StudioMemberInvokeHistoryPanel from './StudioMemberInvokeHistoryPanel';
import {
  contractValueStyle,
  helperTextStyle,
  studioInvokeColors,
  trimOptional,
} from './studioInvokeUi';
import { t } from "@/shared/i18n/messages";

type StudioMemberInvokeInspectorTab =
  | 'endpoint'
  | 'payload'
  | 'run'
  | 'history';

type InspectorField = {
  readonly label: string;
  readonly value: React.ReactNode;
};

type StudioMemberInvokeInspectorProps = {
  readonly activeRunCompletedAt: number | null;
  readonly activeRunTab: 'output' | 'timeline' | 'events' | 'metadata';
  readonly chatMessages: readonly StudioInvokeChatMessage[];
  readonly currentRawOutput: string;
  readonly currentRunHasData: boolean;
  readonly currentRunRequest: CurrentRunRequest | null;
  readonly endpointLabel: string;
  readonly entries: readonly InvokeHistoryEntry[];
  readonly getEntryOutputText: (entryId: string) => string;
  readonly invokeResult: InvokeResultState;
  readonly isChatEndpoint: boolean;
  readonly onClose: () => void;
  readonly onCopyError: () => void;
  readonly onCopyInput: (entryId: string) => void;
  readonly onCopyOutput: (entryId: string) => void;
  readonly onPayloadBase64Change: (value: string) => void;
  readonly onPayloadTypeUrlChange: (value: string) => void;
  readonly onRetryCurrentRunAsNewRun: () => void;
  readonly onRetryAsNewRun: (entryId: string) => void;
  readonly onRunTabChange: (
    tab: 'output' | 'timeline' | 'events' | 'metadata',
  ) => void;
  readonly onSelectEntry: (entryId: string) => void;
  readonly open: boolean;
  readonly payloadBase64: string;
  readonly payloadTypeUrl: string;
  readonly publishedServiceId: string;
  readonly revisionId: string;
  readonly runElapsedLabel: string;
  readonly runViewMode: 'latest' | 'historical';
  readonly selectedHistoryId: string;
  readonly transcriptViewportRef: React.RefObject<HTMLDivElement | null>;
};

function renderInspectorField(field: InspectorField): React.ReactNode {
  return (
    <div key={field.label} style={inspectorFieldStyle}>
      <Typography.Text style={fieldLabelStyle} type="secondary">
        {field.label}
      </Typography.Text>
      <div style={contractValueStyle}>{field.value}</div>
    </div>
  );
}

const inspectorBodyStyle: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: 14,
  minHeight: 0,
};

const inspectorHeaderStyle: React.CSSProperties = {
  display: 'grid',
  gap: 4,
  minWidth: 0,
};

const inspectorTabBodyStyle: React.CSSProperties = {
  display: 'grid',
  gap: 12,
  minWidth: 0,
};

const inspectorFieldGridStyle: React.CSSProperties = {
  display: 'grid',
  gap: 10,
  gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))',
  minWidth: 0,
};

const inspectorFieldStyle: React.CSSProperties = {
  background: studioInvokeColors.surface,
  border: `1px solid ${studioInvokeColors.border}`,
  borderRadius: 8,
  display: 'grid',
  gap: 4,
  minWidth: 0,
  padding: '10px 12px',
};

const fieldLabelStyle: React.CSSProperties = {
  fontSize: 12,
  lineHeight: '16px',
};

const typedPayloadGridStyle: React.CSSProperties = {
  display: 'grid',
  gap: 8,
  minWidth: 0,
};

const historyInspectorStyle: React.CSSProperties = {
  border: 'none',
  boxShadow: 'none',
};

const drawerBodyStyle: React.CSSProperties = {
  ...aevatarDrawerBodyStyle,
};

const drawerScrollStyle: React.CSSProperties = {
  ...aevatarDrawerScrollStyle,
  gap: 0,
};

const StudioMemberInvokeInspector: React.FC<
  StudioMemberInvokeInspectorProps
> = ({
  activeRunCompletedAt,
  activeRunTab,
  chatMessages,
  currentRawOutput,
  currentRunHasData,
  currentRunRequest,
  endpointLabel,
  entries,
  getEntryOutputText,
  invokeResult,
  isChatEndpoint,
  onClose,
  onCopyError,
  onCopyInput,
  onCopyOutput,
  onPayloadBase64Change,
  onPayloadTypeUrlChange,
  onRetryCurrentRunAsNewRun,
  onRetryAsNewRun,
  onRunTabChange,
  onSelectEntry,
  open,
  payloadBase64,
  payloadTypeUrl,
  publishedServiceId,
  revisionId,
  runElapsedLabel,
  runViewMode,
  selectedHistoryId,
  transcriptViewportRef,
}) => {
  const screens = Grid.useBreakpoint();
  const isDesktop = Boolean(screens.md);
  const [activeTab, setActiveTab] =
    React.useState<StudioMemberInvokeInspectorTab>('endpoint');
  const placement = isDesktop ? 'right' : 'bottom';
  const canEditPayload = !isChatEndpoint;
  const tabOptions = React.useMemo(
    () => [
      {
        icon: <InfoCircleOutlined />,
        label: t(
          "pages.studio.studiomemberinvokeinspector.endpoint",
          "Endpoint",
        ),
        value: 'endpoint',
      },
      {
        disabled: !canEditPayload,
        icon: <ApartmentOutlined />,
        label: t(
          "pages.studio.studiomemberinvokeinspector.payload",
          "Payload",
        ),
        value: 'payload',
      },
      {
        icon: <ApartmentOutlined />,
        label: t(
          "pages.studio.studiomemberinvokeinspector.run",
          "Run",
        ),
        value: 'run',
      },
      {
        icon: <HistoryOutlined />,
        label: t(
          "pages.studio.studiomemberinvokeinspector.history",
          "History",
        ),
        value: 'history',
      },
    ],
    [canEditPayload],
  );

  React.useEffect(() => {
    if (activeTab === 'payload' && !canEditPayload) {
      setActiveTab('endpoint');
    }
  }, [activeTab, canEditPayload]);

  const fields: InspectorField[] = [
    {
      label: t(
        "pages.studio.studiomemberinvokeinspector.endpoint.2",
        "Endpoint",
      ),
      value: endpointLabel || 'chat',
    },
    {
      label: t(
        "pages.studio.studiomemberinvokeinspector.service.target",
        "Service target",
      ),
      value: trimOptional(publishedServiceId) || '—',
    },
    {
      label: t(
        "pages.studio.studiomemberinvokeinspector.revision",
        "Revision",
      ),
      value: trimOptional(revisionId) || '—',
    },
    {
      label: t(
        "pages.studio.studiomemberinvokeinspector.current.run",
        "Current run",
      ),
      value: invokeResult.status,
    },
  ];

  const inspectorTitle = t(
    "pages.studio.studiomemberinvokeinspector.title",
    "Details",
  );

  const inspectorContent = (
    <div style={inspectorBodyStyle}>
      <div style={inspectorHeaderStyle}>
        <Typography.Text style={helperTextStyle} type="secondary">
          {t(
            "pages.studio.studiomemberinvokeinspector.copy",
            "Endpoint, payload, run events, and recent history are available here without taking over the task page.",
          )}
        </Typography.Text>
        <Segmented
          block
          options={tabOptions}
          value={activeTab}
          onChange={(value) =>
            setActiveTab(value as StudioMemberInvokeInspectorTab)
          }
        />
      </div>

      {activeTab === 'history' ? (
        <StudioMemberInvokeHistoryPanel
          entries={entries}
          getEntryOutputText={getEntryOutputText}
          selectedHistoryId={selectedHistoryId}
          style={historyInspectorStyle}
          onCopyInput={onCopyInput}
          onCopyOutput={onCopyOutput}
          onRetryAsNewRun={onRetryAsNewRun}
          onSelectEntry={onSelectEntry}
        />
      ) : activeTab === 'run' ? (
        <StudioMemberCurrentRunPanel
          activeRunCompletedAt={activeRunCompletedAt}
          activeTab={activeRunTab}
          chatMessages={chatMessages}
          currentRawOutput={currentRawOutput}
          currentRunHasData={currentRunHasData}
          currentRunRequest={currentRunRequest}
          endpointLabel={endpointLabel}
          invokeResult={invokeResult}
          runElapsedLabel={runElapsedLabel}
          runViewMode={runViewMode}
          transcriptViewportRef={transcriptViewportRef}
          onCopyError={onCopyError}
          onRetryAsNewRun={onRetryCurrentRunAsNewRun}
          onTabChange={onRunTabChange}
        />
      ) : activeTab === 'payload' ? (
        <div style={inspectorTabBodyStyle}>
          <div style={typedPayloadGridStyle}>
            <Typography.Text style={helperTextStyle} type="secondary">
              {t(
                "pages.studio.studiomemberinvokeinspector.payload.type.url",
                "Payload type URL",
              )}
            </Typography.Text>
            <Input
              aria-label={t(
                "pages.studio.studiomemberinvokeinspector.payload.type.url.2",
                "Payload type URL",
              )}
              placeholder="type.googleapis.com/google.protobuf.StringValue"
              value={payloadTypeUrl}
              onChange={(event) => onPayloadTypeUrlChange(event.target.value)}
            />
          </div>
          <div style={typedPayloadGridStyle}>
            <Typography.Text style={helperTextStyle} type="secondary">
              {t(
                "pages.studio.studiomemberinvokeinspector.payload.base64",
                "Payload base64",
              )}
            </Typography.Text>
            <Input.TextArea
              aria-label={t(
                "pages.studio.studiomemberinvokeinspector.payload.base64.2",
                "Payload base64",
              )}
              autoSize={{ minRows: 3, maxRows: 8 }}
              placeholder={t(
                "pages.studio.studiomemberinvokeinspector.paste.encoded.protobuf.payload.when",
                "Paste encoded protobuf payload when this type cannot be built from text.",
              )}
              value={payloadBase64}
              onChange={(event) => onPayloadBase64Change(event.target.value)}
            />
          </div>
        </div>
      ) : (
        <div style={inspectorFieldGridStyle}>
          {fields.map(renderInspectorField)}
        </div>
      )}
    </div>
  );

  return (
    <Drawer
      destroyOnHidden
      data-testid="studio-invoke-inspector"
      onClose={onClose}
      open={open}
      placement={placement}
      size={
        isDesktop ? AEVATAR_GLOBAL_UI_SPEC.tokens.inspectorWidth : '72vh'
      }
      styles={{ body: drawerBodyStyle }}
      title={inspectorTitle}
    >
      <div style={drawerScrollStyle}>{inspectorContent}</div>
    </Drawer>
  );
};

export default StudioMemberInvokeInspector;
