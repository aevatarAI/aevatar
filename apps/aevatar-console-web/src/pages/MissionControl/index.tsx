import {
  BorderBottomOutlined,
  ClockCircleOutlined,
  ReloadOutlined,
} from '@ant-design/icons';
import {
  Badge,
  Button,
  Card,
  Drawer,
  Grid,
  Segmented,
  Space,
  Tag,
  Typography,
  theme,
} from 'antd';
import React, {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
} from 'react';
import AevatarTooltip from '@/shared/ui/AevatarTooltip';
import { history } from '@/shared/navigation/history';
import { buildRuntimeRunsHref } from '@/shared/navigation/runtimeRoutes';
import { buildTeamDetailHref } from '@/shared/navigation/teamRoutes';
import {
  AevatarBackButton,
  AevatarPageShell,
  type AevatarBreadcrumbItem,
} from '@/shared/ui/aevatarPageShells';
import { AEVATAR_INTERACTIVE_BUTTON_CLASS } from '@/shared/ui/interactionStandards';
import { useMissionControlRuntime, type UseMissionControlRuntimeResult } from './hooks/useMissionControlRuntime';
import InspectorPanel from './InspectorPanel';
import type {
  MissionControlSnapshot,
  MissionInspectorMode,
  MissionInspectorPresentation,
  MissionInterventionState,
} from './models';
import {
  buildMissionOperatorHandoff,
  type MissionOperatorHandoff,
} from './operatorHandoff';
import {
  formatInterventionLabel,
  formatConnectionLabel,
  formatHandoffSeverityLabel,
  formatMissionLabel,
  resolveConnectionTagColor,
  resolveHandoffTagColor,
  resolveMissionStatusTone,
  resolveObservationTone,
  type MissionThemeToken,
} from './presentation';
import TopologyCanvas from './TopologyCanvas';
import { t } from "@/shared/i18n/messages";

const scrollerStyle: React.CSSProperties = {
  minHeight: 0,
  overflowX: 'hidden',
  overflowY: 'auto',
};

const monoStyle: React.CSSProperties = {
  fontFamily:
    "'SFMono-Regular', 'SFMono-Regular', Consolas, 'Liberation Mono', monospace",
};

type MissionStageView = 'topology' | 'execution_flow';
type MissionDockTab = 'timeline' | 'logs';

type MissionControlUiContextValue = {
  activeNodeId?: string;
  closeInspector: () => void;
  dockHeight: number;
  inspectorMode: MissionInspectorMode;
  inspectorPresentation: MissionInspectorPresentation;
  inspectorWidth: number;
  interventionRequired: boolean;
  isDockCollapsed: boolean;
  isInspectorOpen: boolean;
  openInterventionPanel: () => void;
  openNodeInspector: (nodeId: string) => void;
  setDockCollapsed: (collapsed: boolean) => void;
  setDockHeight: (height: number) => void;
};

const MissionControlUiContext =
  createContext<MissionControlUiContextValue | null>(null);

function buildMissionShellStyle(token: MissionThemeToken): React.CSSProperties {
  return {
    background: `linear-gradient(180deg, ${token.colorBgLayout} 0%, ${token.colorBgContainer} 100%)`,
    border: `1px solid ${token.colorBorderSecondary}`,
    borderRadius: 4,
    boxShadow: token.boxShadowSecondary,
    display: 'flex',
    flexDirection: 'column',
    gap: 12,
    flex: 1,
    height: '100%',
    minHeight: 0,
    overflow: 'hidden',
    padding: 12,
    position: 'relative',
  };
}

function clamp(value: number, min: number, max: number) {
  return Math.min(Math.max(value, min), max);
}

function useMissionControlUi() {
  const value = useContext(MissionControlUiContext);
  if (!value) {
    throw new Error('MissionControlUiContext is not available.');
  }

  return value;
}

function MissionControlUiProvider({
  children,
  intervention,
}: {
  children: React.ReactNode;
  intervention?: MissionInterventionState;
}) {
  const screens = Grid.useBreakpoint();
  const [isInspectorOpen, setIsInspectorOpen] = useState(false);
  const [activeNodeId, setActiveNodeId] = useState<string | undefined>();
  const [inspectorMode, setInspectorMode] =
    useState<MissionInspectorMode>('node');
  const [dismissedInterventionKey, setDismissedInterventionKey] = useState<
    string | undefined
  >();
  const [dockHeight, setDockHeight] = useState(248);
  const [isDockCollapsed, setDockCollapsed] = useState(false);

  const interventionRequired = Boolean(intervention?.required);
  const inspectorWidth = screens.xxl ? 420 : screens.xl ? 392 : 360;

  useEffect(() => {
    if (!interventionRequired || !intervention) {
      setDismissedInterventionKey(undefined);
      return;
    }

    if (dismissedInterventionKey === intervention.key) {
      return;
    }

    setActiveNodeId(intervention.nodeId);
    setInspectorMode('intervention');
    setIsInspectorOpen(true);
  }, [dismissedInterventionKey, intervention, interventionRequired]);

  const openNodeInspector = useCallback((nodeId: string) => {
    setActiveNodeId(nodeId);
    setInspectorMode('node');
    setIsInspectorOpen(true);
  }, []);

  const openInterventionPanel = useCallback(() => {
    if (!interventionRequired || !intervention) {
      return;
    }

    setActiveNodeId(intervention.nodeId);
    setInspectorMode('intervention');
    setIsInspectorOpen(true);
    setDismissedInterventionKey(undefined);
  }, [intervention, interventionRequired]);

  const closeInspector = useCallback(() => {
    if (interventionRequired && intervention) {
      setDismissedInterventionKey(intervention.key);
    } else {
      setActiveNodeId(undefined);
    }

    setIsInspectorOpen(false);
  }, [intervention, interventionRequired]);

  const inspectorPresentation: MissionInspectorPresentation =
    isInspectorOpen && screens.xxl && !interventionRequired ? 'push' : 'overlay';

  const value = useMemo<MissionControlUiContextValue>(
    () => ({
      activeNodeId,
      closeInspector,
      dockHeight,
      inspectorMode,
      inspectorPresentation,
      inspectorWidth,
      interventionRequired,
      isDockCollapsed,
      isInspectorOpen,
      openInterventionPanel,
      openNodeInspector,
      setDockCollapsed,
      setDockHeight,
    }),
    [
      activeNodeId,
      closeInspector,
      dockHeight,
      inspectorMode,
      inspectorPresentation,
      inspectorWidth,
      interventionRequired,
      isDockCollapsed,
      isInspectorOpen,
      openInterventionPanel,
      openNodeInspector,
    ],
  );

  return (
    <MissionControlUiContext.Provider value={value}>
      {children}
    </MissionControlUiContext.Provider>
  );
}

function MissionHeaderBar({
  connectionMessage,
  connectionStatus,
  liveMode,
  loading,
  onRefresh,
  routeContext,
  snapshot,
  stageView,
  onStageViewChange,
}: {
  connectionMessage?: string;
  connectionStatus: UseMissionControlRuntimeResult['connectionStatus'];
  liveMode: boolean;
  loading: boolean;
  onRefresh: () => void;
  routeContext: UseMissionControlRuntimeResult['routeContext'];
  snapshot: MissionControlSnapshot;
  stageView: MissionStageView;
  onStageViewChange: (value: MissionStageView) => void;
}) {
  const ui = useMissionControlUi();
  const { token } = theme.useToken();
  const handoff = useMemo(
    () => buildMissionOperatorHandoff(snapshot, connectionStatus),
    [connectionStatus, snapshot],
  );

  return (
    <div
      style={{
        alignItems: 'center',
        backdropFilter: 'blur(12px)',
        background: token.colorBgContainer,
        border: `1px solid ${token.colorBorderSecondary}`,
        borderRadius: 4,
        display: 'flex',
        flexWrap: 'wrap',
        gap: 12,
        justifyContent: 'space-between',
        minHeight: 64,
        padding: '12px 14px',
      }}
    >
      <div style={{ display: 'flex', flexDirection: 'column', gap: 6, minWidth: 0 }}>
        <Space wrap size={[8, 8]}>
          <Badge
            color={resolveMissionStatusTone(token, snapshot.summary.status)}
            text={
              <Typography.Text style={{ color: token.colorTextHeading }}>
                {formatMissionLabel(snapshot.summary.status)}
              </Typography.Text>
            }
          />
          <Tag color={resolveObservationTone(token, snapshot.summary.observationStatus)}>
            Observation: {formatMissionLabel(snapshot.summary.observationStatus)}
          </Tag>
          <Tag color={resolveConnectionTagColor(connectionStatus)}>
            Connection: {formatConnectionLabel(connectionStatus)}
          </Tag>
          {snapshot.summary.scriptEvolutionStatus ? (
            <Tag color="cyan">
              {t("pages.missioncontrol.index.script.governance.2", "Script Governance:")}{formatMissionLabel(snapshot.summary.scriptEvolutionStatus)}
            </Tag>
          ) : null}
          {ui.interventionRequired ? (
            <Tag color="gold">
              {t("pages.missioncontrol.index.current.blocker.2", "Current Blocker:")}{formatInterventionLabel(snapshot.intervention?.kind || 'human_approval')}
            </Tag>
          ) : null}
        </Space>
        <Typography.Text style={{ color: token.colorTextTertiary }}>
          {snapshot.summary.workflowName}
        </Typography.Text>
        {connectionMessage ? (
          <Typography.Text style={{ color: token.colorTextTertiary }}>
            {connectionMessage}
          </Typography.Text>
        ) : null}
        <div
          aria-label={t("pages.missioncontrol.index.mission.control.operator.handoff", "Mission Control operator handoff")}
          style={{
            background: handoff.isActionable
              ? token.colorWarningBg
              : token.colorFillQuaternary,
            border: `1px solid ${
              handoff.isActionable
                ? token.colorWarningBorder
                : token.colorBorderSecondary
            }`,
            borderRadius: 4,
            display: 'grid',
            gap: 8,
            gridTemplateColumns: 'repeat(auto-fit, minmax(220px, 1fr))',
            marginTop: 4,
            maxWidth: 760,
            padding: '10px 12px',
          }}
        >
          <div style={{ minWidth: 0 }}>
            <Typography.Text
              style={{
                color: token.colorTextHeading,
                display: 'block',
                fontSize: 12,
                fontWeight: 700,
              }}
            >
              {handoff.actionLabel}
            </Typography.Text>
            <Typography.Text style={{ color: token.colorTextTertiary, fontSize: 12 }}>
              {handoff.actionDetail}
            </Typography.Text>
          </div>
          <div style={{ minWidth: 0 }}>
            <Typography.Text style={{ color: token.colorTextSecondary, fontSize: 12 }}>
              {handoff.connectionDetail}
            </Typography.Text>
          </div>
        </div>
      </div>
      <Space wrap size={[8, 8]} style={{ justifyContent: 'flex-end' }}>
        <Segmented<MissionStageView>
          options={[
            { label: t("pages.missioncontrol.index.decision.path.2", "Decision Path"), value: 'topology' },
            { label: t("pages.missioncontrol.index.execution.flow.2", "Execution Flow"), value: 'execution_flow' },
          ]}
          value={stageView}
          onChange={(value) => onStageViewChange(value as MissionStageView)}
        />
        {liveMode ? (
          <Button icon={<ReloadOutlined />} loading={loading} onClick={onRefresh}>
            {t("pages.missioncontrol.index.sync.now.2", "Sync now")}</Button>
        ) : (
          <>
            {routeContext.scopeId ? (
              <AevatarBackButton
                ariaLabel={t("pages.missioncontrol.index.back.to.team.2", "Back to Team")}
                onBack={() =>
                  history.push(
                    buildTeamDetailHref({
                      scopeId: routeContext.scopeId ?? '',
                      tab: 'overview',
                    }),
                  )
                }
                title={t("pages.missioncontrol.index.back.to.team.2", "Back to Team")}
              />
            ) : null}
            <Button
              type="primary"
              onClick={() =>
                history.push(
                  buildRuntimeRunsHref({
                    scopeId: routeContext.scopeId,
                    serviceId: routeContext.serviceId,
                  }),
                )
              }
            >
              {t("pages.missioncontrol.index.open.event.stream.2", "Open Event Stream")}</Button>
          </>
        )}
        {ui.interventionRequired ? (
          <Button type="primary" onClick={ui.openInterventionPanel}>
            {t("pages.missioncontrol.index.handle.blocker.2", "Handle blocker")}</Button>
        ) : null}
      </Space>
    </div>
  );
}

function MissionMetricStrip({ snapshot }: { snapshot: MissionControlSnapshot }) {
  const { token } = theme.useToken();

  return (
    <div
      style={{
        display: 'grid',
        gap: 10,
        gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))',
      }}
    >
      {snapshot.metrics.map((metric) => (
        <Card
          key={metric.key}
          size="small"
          styles={{
            body: {
              display: 'flex',
              flexDirection: 'column',
              gap: 8,
              padding: 14,
            },
          }}
          style={{
            background: token.colorBgContainer,
            borderColor:
              metric.tone === 'warning'
                ? token.colorWarningBorder
                : metric.tone === 'success'
                  ? token.colorSuccessBorder
                  : token.colorBorderSecondary,
            borderRadius: 4,
            boxShadow: token.boxShadowSecondary,
          }}
        >
          <Typography.Text style={{ color: token.colorTextTertiary, fontSize: 12 }}>
            {metric.label}
          </Typography.Text>
          <div
            style={{
              alignItems: 'flex-end',
              display: 'flex',
              gap: 8,
              justifyContent: 'space-between',
            }}
          >
            <Typography.Text
              style={{
                color: token.colorTextHeading,
                fontSize: 24,
                fontWeight: 700,
                lineHeight: 1,
              }}
            >
              {metric.value}
            </Typography.Text>
            <Tag
              color={
                metric.trend === 'down'
                  ? 'green'
                  : metric.trend === 'up'
                    ? 'blue'
                    : 'default'
              }
            >
              {metric.trend === 'down'
                ? t("pages.missioncontrol.index.down.2", "Down")
                : metric.trend === 'up'
                  ? 'Up'
                  : t("pages.missioncontrol.index.steady.2", "Steady")}
            </Tag>
          </div>
        </Card>
      ))}
    </div>
  );
}

function MissionStage({
  connectionMessage,
  connectionStatus,
  snapshot,
  stageView,
}: {
  connectionMessage?: string;
  connectionStatus: UseMissionControlRuntimeResult['connectionStatus'];
  snapshot: MissionControlSnapshot;
  stageView: MissionStageView;
}) {
  const ui = useMissionControlUi();
  const { token } = theme.useToken();

  return (
    <Card
      styles={{
        body: {
          display: 'flex',
          flex: 1,
          flexDirection: 'column',
          minHeight: 0,
          overflow: 'hidden',
          padding: 0,
        },
      }}
      style={{
        background: token.colorBgContainer,
        borderColor: token.colorBorderSecondary,
        borderRadius: 4,
        boxShadow: token.boxShadowSecondary,
        display: 'flex',
        flex: 1,
        minHeight: 0,
      }}
    >
      <div
        style={{
          alignItems: 'center',
          borderBottom: `1px solid ${token.colorBorderSecondary}`,
          display: 'flex',
          flexWrap: 'wrap',
          gap: 8,
          justifyContent: 'space-between',
          padding: '12px 14px',
        }}
      >
        <div style={{ minWidth: 0 }}>
          <Typography.Title level={5} style={{ color: token.colorTextHeading, margin: 0 }}>
            {stageView === 'topology' ? t("pages.missioncontrol.index.decision.path.3", "Decision Path") : t("pages.missioncontrol.index.execution.flow.3", "Execution Flow")}
          </Typography.Title>
          <Typography.Text style={{ color: token.colorTextTertiary }}>
            {stageView === 'topology'
              ? t("pages.missioncontrol.index.trace.the.evidence.chain.from.market", "Trace the evidence chain from market signal to execution decision, with data flow and freshness intact.")
              : t("pages.missioncontrol.index.compress.multi.agent.execution.into.an", "Compress multi-agent execution into an operator-readable event narrative.")}
          </Typography.Text>
        </div>
        <Space wrap size={[8, 8]}>
          <Tag color="processing">Stage: {snapshot.summary.activeStageLabel}</Tag>
          <Tag color={resolveConnectionTagColor(connectionStatus)}>
            Connection: {formatConnectionLabel(connectionStatus)}
          </Tag>
          <Tag color="blue">{snapshot.nodes.length} {t("pages.missioncontrol.index.nodes.2", "nodes")}</Tag>
        </Space>
      </div>
      <div style={{ display: 'flex', flex: 1, minHeight: 0 }}>
        {stageView === 'topology' ? (
          <div style={{ display: 'flex', flex: 1, minHeight: 0, padding: 14 }}>
            <TopologyCanvas
              activeNodeId={ui.activeNodeId}
              connectionMessage={connectionMessage}
              connectionStatus={connectionStatus}
              onCanvasSelect={() => {
                if (ui.interventionRequired) {
                  ui.openInterventionPanel();
                  return;
                }

                ui.closeInspector();
              }}
              onNodeSelect={ui.openNodeInspector}
              snapshot={snapshot}
            />
          </div>
        ) : (
          <div style={{ ...scrollerStyle, flex: 1, padding: 14 }}>
            <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              {snapshot.events.map((event) => (
                <Card
                  key={event.id}
                  size="small"
                  styles={{ body: { display: 'flex', flexDirection: 'column', gap: 8, padding: 12 } }}
                  style={{
                    background: token.colorFillAlter,
                    borderColor:
                      event.severity === 'error'
                        ? token.colorErrorBorder
                        : event.severity === 'warning'
                          ? token.colorWarningBorder
                          : token.colorBorderSecondary,
                    borderRadius: 4,
                  }}
                >
                  <div
                    style={{
                      alignItems: 'center',
                      display: 'flex',
                      gap: 10,
                      justifyContent: 'space-between',
                    }}
                  >
                    <Space size={8}>
                      <Tag
                        color={
                          event.severity === 'success'
                            ? 'green'
                            : event.severity === 'warning'
                              ? 'gold'
                              : event.severity === 'error'
                                ? 'red'
                                : 'processing'
                        }
                      >
                        {formatMissionLabel(event.type)}
                      </Tag>
                      <Typography.Text strong style={{ color: token.colorTextHeading }}>
                        {event.title}
                      </Typography.Text>
                    </Space>
                    <Typography.Text style={{ color: token.colorTextTertiary, ...monoStyle }}>
                      {event.timestamp}
                    </Typography.Text>
                  </div>
                  <Typography.Text style={{ color: token.colorTextSecondary }}>
                    {event.detail}
                  </Typography.Text>
                  <Space wrap size={[8, 8]}>
                    {event.stepId ? <Tag>{event.stepId}</Tag> : null}
                    <Tag color={resolveHandoffTagColor(event.handoff.severity)}>
                      {formatHandoffSeverityLabel(event.handoff.severity)}
                    </Tag>
                  </Space>
                  <Typography.Text style={{ color: token.colorTextTertiary }}>
                    {event.handoff.title}: {event.handoff.nextStep}
                  </Typography.Text>
                </Card>
              ))}
            </div>
          </div>
        )}
      </div>
    </Card>
  );
}

function MissionDock({
  activeTab,
  handoff,
  onTabChange,
  snapshot,
}: {
  activeTab: MissionDockTab;
  handoff: MissionOperatorHandoff;
  onTabChange: (key: MissionDockTab) => void;
  snapshot: MissionControlSnapshot;
}) {
  const ui = useMissionControlUi();
  const { token } = theme.useToken();

  const startDockResize = useCallback(
    (event: React.MouseEvent<HTMLButtonElement>) => {
      if (ui.isDockCollapsed) {
        return;
      }

      event.preventDefault();
      const startY = event.clientY;
      const startHeight = ui.dockHeight;

      const handleMouseMove = (moveEvent: MouseEvent) => {
        const nextHeight = clamp(startHeight + (startY - moveEvent.clientY), 188, 420);
        ui.setDockHeight(nextHeight);
      };

      const handleMouseUp = () => {
        window.removeEventListener('mousemove', handleMouseMove);
        window.removeEventListener('mouseup', handleMouseUp);
      };

      window.addEventListener('mousemove', handleMouseMove);
      window.addEventListener('mouseup', handleMouseUp);
    },
    [ui],
  );

  return (
    <div
      style={{
        background: token.colorBgContainer,
        border: `1px solid ${token.colorBorderSecondary}`,
        borderRadius: 4,
        display: 'flex',
        flex: `0 0 ${ui.isDockCollapsed ? 54 : ui.dockHeight}px`,
        flexDirection: 'column',
        minHeight: ui.isDockCollapsed ? 54 : 188,
        overflow: 'hidden',
      }}
    >
      <button
        aria-label={t("pages.missioncontrol.index.resize.dock.2", "Resize dock")}
        className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
        onKeyDown={(event) => {
          if (ui.isDockCollapsed) {
            return;
          }

          if (event.key === 'ArrowUp') {
            ui.setDockHeight(clamp(ui.dockHeight + 24, 188, 420));
          }

          if (event.key === 'ArrowDown') {
            ui.setDockHeight(clamp(ui.dockHeight - 24, 188, 420));
          }
        }}
        onMouseDown={startDockResize}
        style={{
          background: 'transparent',
          border: 'none',
          cursor: ui.isDockCollapsed ? 'default' : 'row-resize',
          display: 'flex',
          height: 14,
          justifyContent: 'center',
          padding: 0,
        }}
        type="button"
      >
        <span
          style={{
            background: token.colorBorderSecondary,
            borderRadius: 999,
            display: 'inline-flex',
            height: 4,
            marginTop: 6,
            width: 56,
          }}
        />
      </button>
      <div
        style={{
          alignItems: 'center',
          borderBottom: `1px solid ${token.colorBorderSecondary}`,
          display: 'flex',
          gap: 8,
          justifyContent: 'space-between',
          padding: '0 14px 12px',
        }}
      >
        <Space wrap size={[8, 8]}>
          <AevatarTooltip title={t("pages.missioncontrol.index.key.events.2", "Key events")}>
            <Button
              icon={<ClockCircleOutlined />}
              onClick={() => onTabChange('timeline')}
              type={activeTab === 'timeline' ? 'primary' : 'default'}
            />
          </AevatarTooltip>
          <AevatarTooltip title={t("pages.missioncontrol.index.raw.logs.2", "Raw logs")}>
            <Button
              icon={<BorderBottomOutlined />}
              onClick={() => onTabChange('logs')}
              type={activeTab === 'logs' ? 'primary' : 'default'}
            />
          </AevatarTooltip>
          <Typography.Text style={{ color: token.colorTextHeading }}>
            {t("pages.missioncontrol.index.event.dock.2", "Event Dock")}</Typography.Text>
        </Space>
        <Space wrap size={[8, 8]}>
          <Tag color="processing">{snapshot.events.length} {t("pages.missioncontrol.index.events.2", "events")}</Tag>
          <Tag color={handoff.isActionable ? 'gold' : 'default'}>
            {handoff.isActionable ? t("pages.missioncontrol.index.evidence.before.action", "Evidence before action") : t("pages.missioncontrol.index.read.only.evidence", "Read-only evidence")}
          </Tag>
          <Button onClick={() => ui.setDockCollapsed(!ui.isDockCollapsed)}>
            {ui.isDockCollapsed ? t("pages.missioncontrol.index.expand.dock.2", "Expand Dock") : t("pages.missioncontrol.index.collapse.dock.2", "Collapse Dock")}
          </Button>
        </Space>
      </div>
      {!ui.isDockCollapsed ? (
        <div style={{ ...scrollerStyle, flex: 1, padding: '12px 14px 14px' }}>
          <Card
            size="small"
            styles={{ body: { padding: 12 } }}
            style={{
              background: token.colorFillQuaternary,
              borderColor: token.colorBorderSecondary,
              borderRadius: 4,
              marginBottom: 12,
            }}
          >
            <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
              <Typography.Text strong style={{ color: token.colorTextHeading }}>
                {t("pages.missioncontrol.index.evidence.handoff", "Evidence handoff")}</Typography.Text>
              <Typography.Text style={{ color: token.colorTextSecondary }}>
                {handoff.evidenceDetail}
              </Typography.Text>
              <Typography.Text style={{ color: token.colorTextTertiary }}>
                {handoff.expectedResult}
              </Typography.Text>
            </div>
          </Card>
          {activeTab === 'timeline' ? (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
              {snapshot.events.map((event) => (
                <div
                  key={event.id}
                  style={{
                    alignItems: 'flex-start',
                    borderBottom: `1px solid ${token.colorBorderSecondary}`,
                    display: 'grid',
                    gap: 10,
                    gridTemplateColumns: '90px minmax(0, 1fr)',
                    paddingBottom: 10,
                  }}
                >
                  <Typography.Text style={{ color: token.colorTextTertiary, ...monoStyle }}>
                    {event.timestamp}
                  </Typography.Text>
                  <div style={{ minWidth: 0 }}>
                    <Space wrap size={[8, 8]} style={{ marginBottom: 4 }}>
                      <Tag
                        color={
                          event.severity === 'success'
                            ? 'green'
                            : event.severity === 'warning'
                              ? 'gold'
                              : event.severity === 'error'
                                ? 'red'
                                : 'blue'
                        }
                      >
                        {event.title}
                      </Tag>
                      {event.stepId ? <Tag>{event.stepId}</Tag> : null}
                    </Space>
                    <Typography.Text style={{ color: token.colorTextSecondary }}>
                      {event.detail}
                    </Typography.Text>
                  </div>
                </div>
              ))}
            </div>
          ) : (
            <pre
              style={{
                ...monoStyle,
                background: token.colorFillAlter,
                border: `1px solid ${token.colorBorderSecondary}`,
                borderRadius: 4,
                color: token.colorText,
                margin: 0,
                minHeight: '100%',
                padding: 12,
                whiteSpace: 'pre-wrap',
                wordBreak: 'break-word',
              }}
            >
              {snapshot.liveLogs.join('\n')}
            </pre>
          )}
        </div>
      ) : null}
    </div>
  );
}

function MissionControlCanvas({
  runtime,
}: {
  runtime: UseMissionControlRuntimeResult;
}) {
  const ui = useMissionControlUi();
  const [stageView, setStageView] = useState<MissionStageView>('topology');
  const [dockTab, setDockTab] = useState<MissionDockTab>('timeline');
  const snapshot = runtime.snapshot;
  const selectedNode = useMemo(
    () => snapshot.nodes.find((node) => node.id === ui.activeNodeId),
    [snapshot.nodes, ui.activeNodeId],
  );
  const handoff = useMemo(
    () => buildMissionOperatorHandoff(snapshot, runtime.connectionStatus),
    [runtime.connectionStatus, snapshot],
  );

  const shouldPushInspector =
    ui.isInspectorOpen && ui.inspectorPresentation === 'push';

  return (
    <>
      <MissionHeaderBar
        connectionMessage={runtime.connectionMessage}
        connectionStatus={runtime.connectionStatus}
        liveMode={runtime.liveMode}
        loading={runtime.loading}
        onRefresh={() => {
          void runtime.refresh();
        }}
        routeContext={runtime.routeContext}
        onStageViewChange={setStageView}
        snapshot={snapshot}
        stageView={stageView}
      />
      <MissionMetricStrip snapshot={snapshot} />
      <div
        style={{
          display: 'grid',
          flex: 1,
          gap: 12,
          gridTemplateColumns: shouldPushInspector
            ? `minmax(0, 1fr) ${ui.inspectorWidth}px`
            : 'minmax(0, 1fr)',
          minHeight: 0,
        }}
      >
        <div style={{ display: 'flex', flexDirection: 'column', minHeight: 0 }}>
          <MissionStage
            connectionMessage={runtime.connectionMessage}
            connectionStatus={runtime.connectionStatus}
            snapshot={snapshot}
            stageView={stageView}
          />
        </div>
        {shouldPushInspector ? (
          <Card
            styles={{
              body: {
                display: 'flex',
                flex: 1,
                flexDirection: 'column',
                minHeight: 0,
                overflow: 'hidden',
                padding: 0,
              },
            }}
            style={{
              background: 'transparent',
              borderRadius: 4,
              display: 'flex',
              flexDirection: 'column',
              minHeight: 0,
              overflow: 'hidden',
            }}
          >
            <InspectorPanel
              actionFeedback={runtime.actionFeedback}
              connectionStatus={runtime.connectionStatus}
              operatorHandoff={handoff}
              mode={ui.inspectorMode}
              onSubmitAction={(action) => {
                if (!snapshot.intervention) {
                  return Promise.resolve();
                }

                return runtime.submitIntervention(snapshot.intervention, action);
              }}
              presentation={ui.inspectorPresentation}
              selectedNode={selectedNode}
              submittingActionKind={runtime.submittingActionKind}
              snapshot={snapshot}
            />
          </Card>
        ) : null}
      </div>
      <MissionDock
        activeTab={dockTab}
        handoff={handoff}
        onTabChange={setDockTab}
        snapshot={snapshot}
      />
      {!shouldPushInspector ? (
        <Drawer
          closable
          getContainer={false}
          mask={false}
          onClose={ui.closeInspector}
          open={ui.isInspectorOpen}
          rootStyle={{ position: 'absolute' }}
          styles={{
            body: {
              padding: 0,
            },
            header: {
              display: 'none',
            },
          }}
          size={ui.inspectorWidth}
        >
          <InspectorPanel
            actionFeedback={runtime.actionFeedback}
            connectionStatus={runtime.connectionStatus}
            operatorHandoff={handoff}
            mode={ui.inspectorMode}
            onSubmitAction={(action) => {
              if (!snapshot.intervention) {
                return Promise.resolve();
              }

              return runtime.submitIntervention(snapshot.intervention, action);
            }}
            presentation={ui.inspectorPresentation}
            selectedNode={selectedNode}
            submittingActionKind={runtime.submittingActionKind}
            snapshot={snapshot}
          />
        </Drawer>
      ) : null}
    </>
  );
}

const MissionControlPage: React.FC = () => {
  const { token } = theme.useToken();
  const runtime = useMissionControlRuntime();
  const shellStyle = useMemo(() => buildMissionShellStyle(token), [token]);
  const missionControlBreadcrumbItems: AevatarBreadcrumbItem[] = [
    {
      title: t('pages.missioncontrol.index.platformBreadcrumb', 'Platform'),
    },
    {
      current: true,
      title: t('pages.missioncontrol.index.missionControlBreadcrumb', 'Mission Control'),
    },
  ];

  return (
    <MissionControlUiProvider intervention={runtime.snapshot.intervention}>
      <AevatarPageShell
        breadcrumbItems={missionControlBreadcrumbItems}
        content={t(
          'pages.missioncontrol.index.header.subtitle',
          'Observe, explain, and intervene in critical live runs.',
        )}
        title={t('pages.missioncontrol.index.header.title', 'Mission Control')}
      >
        <div style={shellStyle}>
          <MissionControlCanvas runtime={runtime} />
        </div>
      </AevatarPageShell>
    </MissionControlUiProvider>
  );
};

export default MissionControlPage;
