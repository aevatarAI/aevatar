import {
  ApartmentOutlined,
  CloseOutlined,
  HistoryOutlined,
  InfoCircleOutlined,
} from '@ant-design/icons';
import { Button, Drawer, Grid, Input, Segmented, Typography } from 'antd';
import React from 'react';
import { aevatarDrawerBodyStyle } from '@/shared/ui/aevatarWorkbench';
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

type DesktopFrame = {
  readonly width: number;
  readonly x: number;
  readonly y: number;
};

type DesktopInteraction = {
  readonly mode: 'move' | 'resize';
  readonly startClientX: number;
  readonly startClientY: number;
  readonly startFrame: DesktopFrame;
};

type StudioMemberInvokeInspectorProps = {
  readonly chatMessages: readonly StudioInvokeChatMessage[];
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

const DESKTOP_INSPECTOR_MIN_WIDTH = 360;
const DESKTOP_INSPECTOR_MAX_WIDTH = 500;
const DESKTOP_INSPECTOR_DEFAULT_WIDTH = 420;
const DESKTOP_INSPECTOR_MARGIN = 16;
const DESKTOP_INSPECTOR_DEFAULT_TOP = 96;
const DESKTOP_INSPECTOR_MIN_HEIGHT = 220;

function getViewportSize(): { readonly height: number; readonly width: number } {
  if (typeof window === 'undefined') {
    return { height: 900, width: 1280 };
  }

  return {
    height: window.innerHeight || 900,
    width: window.innerWidth || 1280,
  };
}

function clampValue(value: number, min: number, max: number): number {
  if (max < min) {
    return min;
  }

  return Math.min(Math.max(value, min), max);
}

function clampInspectorWidth(width: number): number {
  const viewport = getViewportSize();
  const viewportMaxWidth = Math.max(
    DESKTOP_INSPECTOR_MIN_WIDTH,
    viewport.width - DESKTOP_INSPECTOR_MARGIN * 2,
  );
  return clampValue(
    width,
    DESKTOP_INSPECTOR_MIN_WIDTH,
    Math.min(DESKTOP_INSPECTOR_MAX_WIDTH, viewportMaxWidth),
  );
}

function readPointerCoordinate(value: number, fallback: number): number {
  return Number.isFinite(value) ? value : fallback;
}

function clampDesktopFrame(frame: DesktopFrame): DesktopFrame {
  const viewport = getViewportSize();
  const width = clampInspectorWidth(frame.width);
  const maxX = Math.max(
    DESKTOP_INSPECTOR_MARGIN,
    viewport.width - width - DESKTOP_INSPECTOR_MARGIN,
  );
  const maxY = Math.max(
    DESKTOP_INSPECTOR_MARGIN,
    viewport.height - DESKTOP_INSPECTOR_MIN_HEIGHT - DESKTOP_INSPECTOR_MARGIN,
  );

  return {
    width,
    x: clampValue(frame.x, DESKTOP_INSPECTOR_MARGIN, maxX),
    y: clampValue(frame.y, DESKTOP_INSPECTOR_MARGIN, maxY),
  };
}

function createDefaultDesktopFrame(): DesktopFrame {
  const viewport = getViewportSize();
  const width = clampInspectorWidth(DESKTOP_INSPECTOR_DEFAULT_WIDTH);
  return clampDesktopFrame({
    width,
    x: viewport.width - width - 32,
    y: DESKTOP_INSPECTOR_DEFAULT_TOP,
  });
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
  overflow: 'auto',
};

const desktopInspectorShellBaseStyle: React.CSSProperties = {
  background: studioInvokeColors.panel,
  border: `1px solid ${studioInvokeColors.border}`,
  borderRadius: 8,
  boxShadow: '0 24px 64px rgba(15, 23, 42, 0.22)',
  boxSizing: 'border-box',
  color: studioInvokeColors.text,
  display: 'flex',
  flexDirection: 'column',
  minHeight: DESKTOP_INSPECTOR_MIN_HEIGHT,
  overflow: 'hidden',
  position: 'fixed',
  zIndex: 1050,
};

const desktopInspectorDragHandleBaseStyle: React.CSSProperties = {
  alignItems: 'center',
  borderBottom: `1px solid ${studioInvokeColors.border}`,
  cursor: 'grab',
  display: 'flex',
  flex: '0 0 auto',
  gap: 12,
  justifyContent: 'space-between',
  minWidth: 0,
  padding: '12px 14px',
  touchAction: 'none',
};

const desktopInspectorTitleStyle: React.CSSProperties = {
  fontSize: 14,
  fontWeight: 700,
  lineHeight: '20px',
  minWidth: 0,
};

const desktopInspectorContentStyle: React.CSSProperties = {
  flex: '1 1 auto',
  minHeight: 0,
  overflow: 'auto',
  padding: 16,
};

const desktopResizeHandleStyle: React.CSSProperties = {
  bottom: 0,
  cursor: 'ew-resize',
  left: -5,
  outline: 'none',
  position: 'absolute',
  top: 0,
  touchAction: 'none',
  width: 10,
  zIndex: 2,
};

const desktopResizeRailStyle: React.CSSProperties = {
  background: studioInvokeColors.activeBorder,
  borderRadius: 999,
  bottom: 16,
  left: 4,
  opacity: 0.72,
  position: 'absolute',
  top: 16,
  width: 2,
};

const StudioMemberInvokeInspector: React.FC<
  StudioMemberInvokeInspectorProps
> = ({
  chatMessages,
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
  const [desktopFrame, setDesktopFrame] = React.useState<DesktopFrame>(
    createDefaultDesktopFrame,
  );
  const [desktopInteractionMode, setDesktopInteractionMode] = React.useState<
    DesktopInteraction['mode'] | null
  >(null);
  const desktopFrameRef = React.useRef(desktopFrame);
  const desktopInteractionRef = React.useRef<DesktopInteraction | null>(null);
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

  React.useEffect(() => {
    desktopFrameRef.current = desktopFrame;
  }, [desktopFrame]);

  React.useEffect(() => {
    if (!open || !isDesktop) {
      desktopInteractionRef.current = null;
      setDesktopInteractionMode(null);
      return;
    }

    setDesktopFrame((currentFrame) => clampDesktopFrame(currentFrame));
  }, [isDesktop, open]);

  React.useEffect(() => {
    if (!open || !isDesktop || typeof window === 'undefined') {
      return undefined;
    }

    const handleResize = () => {
      setDesktopFrame((currentFrame) => clampDesktopFrame(currentFrame));
    };

    window.addEventListener('resize', handleResize);
    return () => {
      window.removeEventListener('resize', handleResize);
    };
  }, [isDesktop, open]);

  React.useEffect(() => {
    if (
      !desktopInteractionMode ||
      !open ||
      !isDesktop ||
      typeof window === 'undefined'
    ) {
      return undefined;
    }

    const previousCursor = document.body.style.cursor;
    const previousUserSelect = document.body.style.userSelect;
    document.body.style.cursor =
      desktopInteractionMode === 'move' ? 'grabbing' : 'ew-resize';
    document.body.style.userSelect = 'none';

    const stopInteraction = () => {
      desktopInteractionRef.current = null;
      setDesktopInteractionMode(null);
    };

    const handlePointerMove = (event: PointerEvent) => {
      const interaction = desktopInteractionRef.current;
      if (!interaction) {
        return;
      }

      event.preventDefault();
      const clientX = readPointerCoordinate(
        event.clientX,
        interaction.startClientX,
      );
      const clientY = readPointerCoordinate(
        event.clientY,
        interaction.startClientY,
      );
      if (interaction.mode === 'move') {
        setDesktopFrame(
          clampDesktopFrame({
            ...interaction.startFrame,
            x:
              interaction.startFrame.x +
              clientX -
              interaction.startClientX,
            y:
              interaction.startFrame.y +
              clientY -
              interaction.startClientY,
          }),
        );
        return;
      }

      const rightEdge = interaction.startFrame.x + interaction.startFrame.width;
      const width = clampInspectorWidth(
        interaction.startFrame.width +
          interaction.startClientX -
          clientX,
      );
      setDesktopFrame(
        clampDesktopFrame({
          ...interaction.startFrame,
          width,
          x: rightEdge - width,
        }),
      );
    };

    window.addEventListener('pointermove', handlePointerMove);
    window.addEventListener('pointerup', stopInteraction);
    window.addEventListener('pointercancel', stopInteraction);

    return () => {
      window.removeEventListener('pointermove', handlePointerMove);
      window.removeEventListener('pointerup', stopInteraction);
      window.removeEventListener('pointercancel', stopInteraction);
      document.body.style.cursor = previousCursor;
      document.body.style.userSelect = previousUserSelect;
    };
  }, [desktopInteractionMode, isDesktop, open]);

  React.useEffect(() => {
    if (!open || !isDesktop || typeof window === 'undefined') {
      return undefined;
    }

    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        onClose();
      }
    };

    window.addEventListener('keydown', handleKeyDown);
    return () => {
      window.removeEventListener('keydown', handleKeyDown);
    };
  }, [isDesktop, onClose, open]);

  const startDesktopInteraction = React.useCallback(
    (mode: DesktopInteraction['mode'], event: React.PointerEvent) => {
      event.preventDefault();
      const currentFrame = desktopFrameRef.current;
      desktopInteractionRef.current = {
        mode,
        startClientX: readPointerCoordinate(event.clientX, currentFrame.x),
        startClientY: readPointerCoordinate(event.clientY, currentFrame.y),
        startFrame: currentFrame,
      };
      setDesktopInteractionMode(mode);
    },
    [],
  );

  const resizeDesktopInspectorWithKeyboard = React.useCallback(
    (event: React.KeyboardEvent<HTMLDivElement>) => {
      if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') {
        return;
      }

      event.preventDefault();
      setDesktopFrame((currentFrame) => {
        const rightEdge = currentFrame.x + currentFrame.width;
        const delta = event.key === 'ArrowLeft' ? 16 : -16;
        const width = clampInspectorWidth(currentFrame.width + delta);
        return clampDesktopFrame({
          ...currentFrame,
          width,
          x: rightEdge - width,
        });
      });
    },
    [],
  );

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
      value: trimOptional(publishedServiceId)
        ? t("pages.studio.studiomemberinvokeinspector.service.ready", "Service ready")
        : '—',
    },
    {
      label: t(
        "pages.studio.studiomemberinvokeinspector.revision",
        "Revision",
      ),
      value: trimOptional(revisionId)
        ? t("pages.studio.studiomemberinvokeinspector.version.ready", "Version ready")
        : '—',
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
          chatMessages={chatMessages}
          currentRunHasData={currentRunHasData}
          currentRunRequest={currentRunRequest}
          endpointLabel={endpointLabel}
          invokeResult={invokeResult}
          runElapsedLabel={runElapsedLabel}
          runViewMode={runViewMode}
          transcriptViewportRef={transcriptViewportRef}
          onCopyError={onCopyError}
          onRetryAsNewRun={onRetryCurrentRunAsNewRun}
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

  if (isDesktop) {
    if (!open) {
      return null;
    }

    return (
      <div
        aria-label={inspectorTitle}
        data-testid="studio-invoke-inspector"
        role="dialog"
        style={{
          ...desktopInspectorShellBaseStyle,
          height: `min(720px, calc(100vh - ${Math.round(
            desktopFrame.y + DESKTOP_INSPECTOR_MARGIN,
          )}px))`,
          left: desktopFrame.x,
          top: desktopFrame.y,
          width: desktopFrame.width,
        }}
      >
        <div
          aria-label={t(
            "pages.studio.studiomemberinvokeinspector.drag.handle",
            "Drag details panel",
          )}
          data-testid="studio-invoke-inspector-drag-handle"
          onPointerDown={(event) => startDesktopInteraction('move', event)}
          style={{
            ...desktopInspectorDragHandleBaseStyle,
            cursor:
              desktopInteractionMode === 'move' ? 'grabbing' : 'grab',
          }}
        >
          <Typography.Text style={desktopInspectorTitleStyle}>
            {inspectorTitle}
          </Typography.Text>
          <Button
            aria-label={t(
              "pages.studio.studiomemberinvokeinspector.close",
              "Close details",
            )}
            icon={<CloseOutlined />}
            type="text"
            onClick={onClose}
            onPointerDown={(event) => event.stopPropagation()}
          />
        </div>
        <div
          aria-label={t(
            "pages.studio.studiomemberinvokeinspector.resize.handle",
            "Resize details panel",
          )}
          data-testid="studio-invoke-inspector-resize-handle"
          role="separator"
          tabIndex={0}
          onKeyDown={resizeDesktopInspectorWithKeyboard}
          onPointerDown={(event) => startDesktopInteraction('resize', event)}
          style={desktopResizeHandleStyle}
        >
          <span aria-hidden style={desktopResizeRailStyle} />
        </div>
        <div style={desktopInspectorContentStyle}>{inspectorContent}</div>
      </div>
    );
  }

  return (
    <Drawer
      destroyOnHidden
      data-testid="studio-invoke-inspector"
      onClose={onClose}
      open={open}
      placement={placement}
      styles={{
        body: drawerBodyStyle,
        wrapper: {
          height: '72vh',
        },
      }}
      title={inspectorTitle}
    >
      {inspectorContent}
    </Drawer>
  );
};

export default StudioMemberInvokeInspector;
