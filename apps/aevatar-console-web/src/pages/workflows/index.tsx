import {
  ApartmentOutlined,
  FilterOutlined,
  FullscreenExitOutlined,
  FullscreenOutlined,
  MenuFoldOutlined,
  MenuUnfoldOutlined,
  SearchOutlined,
} from "@ant-design/icons";
import type {
  ProColumns,
  ProDescriptionsItemProps,
} from "@ant-design/pro-components";
import {
  PageContainer,
  ProCard,
  ProDescriptions,
  ProTable,
} from "@ant-design/pro-components";
import { useQuery } from "@tanstack/react-query";
import { history } from "@/shared/navigation/history";
import type { MenuProps } from "antd";
import {
  Alert,
  Button,
  Col,
  Empty,
  Input,
  Modal,
  Row,
  Select,
  Space,
  Statistic,
  Tabs,
  Tag,
  Typography,
} from "antd";
import React, {
  useCallback,
  useContext,
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
} from "react";
import { runtimeCatalogApi } from "@/shared/api/runtimeCatalogApi";
import type { WorkflowCatalogRole } from "@/shared/models/runtime/catalog";
import { buildWorkflowGraphElements } from "@/shared/graphs/buildGraphElements";
import GraphCanvas from "@/shared/graphs/GraphCanvas";
import {
  listVisibleWorkflowCatalogItems,
  resolveWorkflowCatalogSelection,
} from "@/shared/workflows/catalogVisibility";
import {
  cardStackStyle,
  compactTableCardProps,
  embeddedPanelStyle,
  fillCardStyle,
  moduleCardProps,
  scrollPanelStyle,
  stretchColumnStyle,
} from "@/shared/ui/proComponents";
import WorkflowYamlViewer from "./WorkflowYamlViewer";
import {
  buildStepRows,
  buildStringOptions,
  buildWorkflowRows,
  defaultWorkflowLibraryFilter,
  filterWorkflowRows,
  findWorkflowStepTargetRole,
  type WorkflowLibraryFilter,
  type WorkflowLibraryRow,
  type WorkflowStepRow,
} from "./workflowPresentation";

type WorkflowDetailTab = "yaml" | "roles" | "steps" | "graph";

type WorkflowSummaryRecord = {
  closedWorldStatus: "success" | "default";
  roleCount: number;
  stepCount: number;
  edgeCount: number;
  primitiveCount: number;
  sourceLabel: string;
};

type WorkflowFocusRecord = {
  focusType: "role" | "step";
  focusId: string;
  relatedRole: string;
  relatedStepCount: number;
  graphNodeId: string;
};

type WorkflowSummaryMetric = {
  id: string;
  title: string;
  value: number | string;
};

type WorkflowGraphInteractionContextValue = {
  focusRoleGraph: (roleId: string) => void;
  focusStepGraph: (stepId: string, targetRole?: string) => void;
};

type WorkflowRoleSplitPanelProps = {
  roles: WorkflowCatalogRole[];
  selectedRoleId: string;
  onOpenRoleSteps: (role: WorkflowCatalogRole) => void;
  onSelectRole: (roleId: string) => void;
};

type WorkflowStepSplitPanelProps = {
  onInspectRole: (roleId: string) => void;
  onRunWorkflow: () => void;
  onSelectStep: (stepId: string) => void;
  selectedStepId: string;
  steps: WorkflowStepRow[];
};

type DictionarySectionProps = {
  emptyText: string;
  title: string;
  values: Record<string, string>;
};

type ChildStepSectionProps = {
  childrenSteps: WorkflowStepRow["children"];
};

const llmValueEnum = {
  processing: { text: "Required", status: "Processing" },
  success: { text: "Optional", status: "Success" },
} as const;

const llmFilterOptions = [
  { label: "All", value: "all" },
  { label: "Requires LLM", value: "required" },
  { label: "Optional", value: "optional" },
] as const;

const focusTypeLabels: Record<WorkflowFocusRecord["focusType"], string> = {
  role: "Role",
  step: "Step",
};

const workflowNameCellStyle = {
  display: "flex",
  flexDirection: "column",
  gap: 4,
} as const;

const compactFilterPanelStyle = {
  ...embeddedPanelStyle,
  padding: 12,
} as const;

const workflowDetailHeaderStyle = {
  display: "flex",
  justifyContent: "space-between",
  alignItems: "flex-start",
  gap: 16,
  flexWrap: "wrap",
} as const;

const workflowDetailHeaderMainStyle = {
  flex: "1 1 420px",
  minWidth: 0,
  display: "flex",
  flexDirection: "column",
  gap: 12,
} as const;

const workflowDetailActionGroupStyle = {
  flex: "0 0 auto",
  display: "flex",
  justifyContent: "flex-end",
  flexWrap: "wrap",
  gap: 8,
} as const;

const workflowDetailDescriptionStyle = {
  marginBottom: 0,
  maxWidth: "100%",
} as const;

const workflowSummaryCardStyle = {
  height: "100%",
  border: "1px solid var(--ant-color-border-secondary)",
  borderRadius: 12,
  padding: 12,
  background: "var(--ant-color-fill-quaternary)",
} as const;

const collapsedLibraryBodyStyle = {
  ...embeddedPanelStyle,
  alignItems: "flex-start",
  display: "flex",
  flexDirection: "column",
  gap: 12,
  minHeight: 220,
  justifyContent: "space-between",
} as const;

const splitPaneListShellStyle = {
  ...embeddedPanelStyle,
  height: "100%",
  padding: 8,
} as const;

const splitPaneDetailShellStyle = {
  ...embeddedPanelStyle,
  minHeight: 540,
  background: "var(--ant-color-fill-quaternary)",
} as const;

const splitPaneScrollableListStyle = {
  ...scrollPanelStyle,
  maxHeight: 540,
  paddingRight: 0,
} as const;

const splitPaneItemButtonStyle = {
  alignItems: "flex-start",
  borderRadius: 12,
  display: "flex",
  flexDirection: "column",
  gap: 6,
  height: "auto",
  justifyContent: "flex-start",
  padding: "12px 14px",
  textAlign: "left",
  whiteSpace: "normal",
  width: "100%",
} as const;

const splitPaneItemMetaStyle = {
  display: "flex",
  flexDirection: "column",
  gap: 4,
  width: "100%",
} as const;

const definitionSectionTitleStyle = {
  alignItems: "center",
  display: "flex",
  gap: 8,
  justifyContent: "space-between",
  marginBottom: 12,
} as const;

const detailMetaGridStyle = {
  display: "grid",
  gap: 12,
  gridTemplateColumns: "repeat(auto-fit, minmax(140px, 1fr))",
} as const;

const detailActionRowStyle = {
  display: "flex",
  flexWrap: "wrap",
  gap: 8,
} as const;

const detailTextBlockStyle = {
  marginBottom: 0,
  whiteSpace: "pre-wrap",
  wordBreak: "break-word",
} as const;

const graphPanelShellStyle = {
  ...embeddedPanelStyle,
  scrollMarginTop: 24,
} as const;

const graphPanelHeaderStyle = {
  alignItems: "flex-start",
  display: "flex",
  gap: 12,
  justifyContent: "space-between",
  marginBottom: 12,
  flexWrap: "wrap",
} as const;

const graphModalBodyStyle = {
  display: "flex",
  flexDirection: "column",
  gap: 16,
  height: "100%",
} as const;

const highlightedTabLabelStyle = {
  alignItems: "center",
  borderRadius: 999,
  display: "inline-flex",
  gap: 8,
  padding: "4px 10px",
  transition: "all 0.2s ease",
} as const;

const WorkflowGraphInteractionContext =
  React.createContext<WorkflowGraphInteractionContextValue | null>(null);

function useWorkflowGraphInteraction(): WorkflowGraphInteractionContextValue {
  const value = useContext(WorkflowGraphInteractionContext);

  if (!value) {
    throw new Error(
      "Workflow graph interaction context is unavailable in this tree."
    );
  }

  return value;
}

function renderMetricValue(
  value: number | string | null | undefined
): number | string {
  if (value === null || value === undefined || value === "") {
    return "n/a";
  }

  return value;
}

const DictionarySection: React.FC<DictionarySectionProps> = ({
  emptyText,
  title,
  values,
}) => {
  const entries = Object.entries(values);

  return (
    <div style={embeddedPanelStyle}>
      <div style={definitionSectionTitleStyle}>
        <Typography.Text strong>{title}</Typography.Text>
        <Tag>{entries.length}</Tag>
      </div>
      {entries.length === 0 ? (
        <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={emptyText} />
      ) : (
        <div style={cardStackStyle}>
          {entries.map(([key, value]) => (
            <div
              key={`${title}-${key}`}
              style={{
                alignItems: "start",
                borderBottom: "1px solid var(--ant-color-border-secondary)",
                display: "flex",
                gap: 8,
                justifyContent: "space-between",
                paddingBottom: 12,
              }}
            >
              <Typography.Text code>{key}</Typography.Text>
              <Typography.Text style={detailTextBlockStyle}>
                {value || "n/a"}
              </Typography.Text>
            </div>
          ))}
        </div>
      )}
    </div>
  );
};

const ChildStepSection: React.FC<ChildStepSectionProps> = ({
  childrenSteps,
}) => {
  return (
    <div style={embeddedPanelStyle}>
      <div style={definitionSectionTitleStyle}>
        <Typography.Text strong>Child steps</Typography.Text>
        <Tag>{childrenSteps.length}</Tag>
      </div>
      {childrenSteps.length === 0 ? (
        <Empty
          image={Empty.PRESENTED_IMAGE_SIMPLE}
          description="No child steps are attached to this step."
        />
      ) : (
        <div style={cardStackStyle}>
          {childrenSteps.map((childStep) => (
            <div
              key={childStep.id}
              style={{
                borderBottom: "1px solid var(--ant-color-border-secondary)",
                paddingBottom: 12,
              }}
            >
              <Space wrap size={[8, 8]}>
                <Typography.Text strong>{childStep.id}</Typography.Text>
                <Tag color="blue">{childStep.type}</Tag>
                {childStep.targetRole ? (
                  <Tag>{childStep.targetRole}</Tag>
                ) : null}
              </Space>
            </div>
          ))}
        </div>
      )}
    </div>
  );
};

const WorkflowRoleSplitPanel: React.FC<WorkflowRoleSplitPanelProps> = ({
  roles,
  selectedRoleId,
  onOpenRoleSteps,
  onSelectRole,
}) => {
  const { focusRoleGraph } = useWorkflowGraphInteraction();
  const activeRole =
    roles.find((role) => role.id === selectedRoleId) ?? roles[0];

  if (!activeRole) {
    return (
      <Empty
        image={Empty.PRESENTED_IMAGE_SIMPLE}
        description="No roles defined for this workflow."
      />
    );
  }

  return (
    <Row gutter={[16, 16]} align="stretch">
      <Col xs={24} lg={9}>
        <div style={splitPaneListShellStyle}>
          <div style={splitPaneScrollableListStyle}>
            <div style={{ display: "flex", flexDirection: "column", gap: 4 }}>
              {roles.map((role) => {
                const isActive = role.id === activeRole.id;

                return (
                  <div key={role.id} style={{ paddingBlock: 4 }}>
                    <Button
                      block
                      style={{
                        ...splitPaneItemButtonStyle,
                        background: isActive
                          ? "var(--ant-color-primary-bg)"
                          : "transparent",
                        border: `1px solid ${
                          isActive
                            ? "var(--ant-color-primary-border)"
                            : "transparent"
                        }`,
                      }}
                      type="text"
                      onClick={() => onSelectRole(role.id)}
                    >
                      <div style={splitPaneItemMetaStyle}>
                        <Typography.Text strong>{role.id}</Typography.Text>
                        <Typography.Text type="secondary">
                          {role.name || "Unnamed role"}
                        </Typography.Text>
                      </div>
                      <Space wrap size={[6, 6]}>
                        {role.provider ? (
                          <Tag color="processing">{role.provider}</Tag>
                        ) : null}
                        {role.model ? (
                          <Tag color="blue">{role.model}</Tag>
                        ) : null}
                      </Space>
                    </Button>
                  </div>
                );
              })}
            </div>
          </div>
        </div>
      </Col>
      <Col xs={24} lg={15}>
        <div style={splitPaneDetailShellStyle}>
          <div style={cardStackStyle}>
            <div style={definitionSectionTitleStyle}>
              <div>
                <Typography.Title level={5} style={{ margin: 0 }}>
                  {activeRole.name || activeRole.id}
                </Typography.Title>
                <Typography.Paragraph
                  type="secondary"
                  style={{ margin: "4px 0 0" }}
                >
                  Role ID · {activeRole.id}
                </Typography.Paragraph>
              </div>
              <Space wrap size={[8, 8]}>
                <Tag color="processing">
                  {activeRole.provider || "No provider"}
                </Tag>
                <Tag>{activeRole.model || "No model"}</Tag>
              </Space>
            </div>

            <div style={detailActionRowStyle}>
              <Button
                type="primary"
                onClick={() => onOpenRoleSteps(activeRole)}
              >
                Show related steps
              </Button>
              <Button onClick={() => focusRoleGraph(activeRole.id)}>
                Highlight in graph
              </Button>
            </div>

            <div style={detailMetaGridStyle}>
              <div style={workflowSummaryCardStyle}>
                <Statistic
                  title="Temperature"
                  value={renderMetricValue(activeRole.temperature)}
                />
              </div>
              <div style={workflowSummaryCardStyle}>
                <Statistic
                  title="Max tokens"
                  value={renderMetricValue(activeRole.maxTokens)}
                />
              </div>
              <div style={workflowSummaryCardStyle}>
                <Statistic
                  title="Tool rounds"
                  value={renderMetricValue(activeRole.maxToolRounds)}
                />
              </div>
              <div style={workflowSummaryCardStyle}>
                <Statistic
                  title="History"
                  value={renderMetricValue(activeRole.maxHistoryMessages)}
                />
              </div>
              <div style={workflowSummaryCardStyle}>
                <Statistic
                  title="Stream buffer"
                  value={renderMetricValue(activeRole.streamBufferCapacity)}
                />
              </div>
              <div style={workflowSummaryCardStyle}>
                <Statistic
                  title="Connectors"
                  value={activeRole.connectors.length}
                />
              </div>
            </div>

            <div style={embeddedPanelStyle}>
              <div style={definitionSectionTitleStyle}>
                <Typography.Text strong>System prompt</Typography.Text>
              </div>
              <Typography.Paragraph style={detailTextBlockStyle}>
                {activeRole.systemPrompt || "No system prompt provided."}
              </Typography.Paragraph>
            </div>

            <div style={embeddedPanelStyle}>
              <div style={definitionSectionTitleStyle}>
                <Typography.Text strong>Connectors</Typography.Text>
                <Tag>{activeRole.connectors.length}</Tag>
              </div>
              {activeRole.connectors.length > 0 ? (
                <Space wrap size={[8, 8]}>
                  {activeRole.connectors.map((connector) => (
                    <Tag key={`${activeRole.id}-${connector}`}>{connector}</Tag>
                  ))}
                </Space>
              ) : (
                <Empty
                  image={Empty.PRESENTED_IMAGE_SIMPLE}
                  description="No connectors declared for this role."
                />
              )}
            </div>

            <div style={embeddedPanelStyle}>
              <div style={definitionSectionTitleStyle}>
                <Typography.Text strong>Event modules</Typography.Text>
                <Tag>{activeRole.eventModules.length}</Tag>
              </div>
              {activeRole.eventModules.length > 0 ? (
                <Space wrap size={[8, 8]}>
                  {activeRole.eventModules.map((moduleName) => (
                    <Tag color="purple" key={`${activeRole.id}-${moduleName}`}>
                      {moduleName}
                    </Tag>
                  ))}
                </Space>
              ) : (
                <Empty
                  image={Empty.PRESENTED_IMAGE_SIMPLE}
                  description="No event modules declared for this role."
                />
              )}
            </div>

            <div style={embeddedPanelStyle}>
              <div style={definitionSectionTitleStyle}>
                <Typography.Text strong>Event routes</Typography.Text>
              </div>
              <Typography.Paragraph style={detailTextBlockStyle}>
                {activeRole.eventRoutes || "No explicit event routes provided."}
              </Typography.Paragraph>
            </div>
          </div>
        </div>
      </Col>
    </Row>
  );
};

const WorkflowStepSplitPanel: React.FC<WorkflowStepSplitPanelProps> = ({
  onInspectRole,
  onRunWorkflow,
  onSelectStep,
  selectedStepId,
  steps,
}) => {
  const { focusStepGraph } = useWorkflowGraphInteraction();
  const activeStep =
    steps.find((step) => step.id === selectedStepId) ?? steps[0];

  if (!activeStep) {
    return (
      <Empty
        image={Empty.PRESENTED_IMAGE_SIMPLE}
        description="No steps defined for this workflow."
      />
    );
  }

  return (
    <Row gutter={[16, 16]} align="stretch">
      <Col xs={24} lg={9}>
        <div style={splitPaneListShellStyle}>
          <div style={splitPaneScrollableListStyle}>
            <div style={{ display: "flex", flexDirection: "column", gap: 4 }}>
              {steps.map((step) => {
                const isActive = step.id === activeStep.id;

                return (
                  <div key={step.id} style={{ paddingBlock: 4 }}>
                    <Button
                      block
                      style={{
                        ...splitPaneItemButtonStyle,
                        background: isActive
                          ? "var(--ant-color-primary-bg)"
                          : "transparent",
                        border: `1px solid ${
                          isActive
                            ? "var(--ant-color-primary-border)"
                            : "transparent"
                        }`,
                      }}
                      type="text"
                      onClick={() => onSelectStep(step.id)}
                    >
                      <div style={splitPaneItemMetaStyle}>
                        <Typography.Text strong>{step.id}</Typography.Text>
                        <Typography.Text type="secondary">
                          {step.type}
                        </Typography.Text>
                      </div>
                      <Space wrap size={[6, 6]}>
                        {step.targetRole ? <Tag>{step.targetRole}</Tag> : null}
                        {step.next ? <Tag color="blue">Next</Tag> : null}
                      </Space>
                    </Button>
                  </div>
                );
              })}
            </div>
          </div>
        </div>
      </Col>
      <Col xs={24} lg={15}>
        <div style={splitPaneDetailShellStyle}>
          <div style={cardStackStyle}>
            <div style={definitionSectionTitleStyle}>
              <div>
                <Typography.Title level={5} style={{ margin: 0 }}>
                  {activeStep.id}
                </Typography.Title>
                <Typography.Paragraph
                  type="secondary"
                  style={{ margin: "4px 0 0" }}
                >
                  {activeStep.type} step
                </Typography.Paragraph>
              </div>
              <Space wrap size={[8, 8]}>
                <Tag color="blue">{activeStep.type}</Tag>
                {activeStep.targetRole ? (
                  <Tag>{activeStep.targetRole}</Tag>
                ) : null}
              </Space>
            </div>

            <div style={detailActionRowStyle}>
              <Button
                type="primary"
                onClick={() =>
                  focusStepGraph(activeStep.id, activeStep.targetRole)
                }
              >
                Focus in graph
              </Button>
              <Button onClick={onRunWorkflow}>Run workflow</Button>
              {activeStep.targetRole ? (
                <Button onClick={() => onInspectRole(activeStep.targetRole)}>
                  Inspect target role
                </Button>
              ) : null}
              {activeStep.next ? (
                <Button onClick={() => onSelectStep(activeStep.next)}>
                  Jump to next step
                </Button>
              ) : null}
            </div>

            <div style={detailMetaGridStyle}>
              <div style={workflowSummaryCardStyle}>
                <Statistic
                  title="Target role"
                  value={activeStep.targetRole || "n/a"}
                />
              </div>
              <div style={workflowSummaryCardStyle}>
                <Statistic title="Next" value={activeStep.next || "n/a"} />
              </div>
              <div style={workflowSummaryCardStyle}>
                <Statistic
                  title="Parameters"
                  value={activeStep.parameterCount}
                />
              </div>
              <div style={workflowSummaryCardStyle}>
                <Statistic title="Branches" value={activeStep.branchCount} />
              </div>
              <div style={workflowSummaryCardStyle}>
                <Statistic title="Children" value={activeStep.childCount} />
              </div>
            </div>

            <DictionarySection
              emptyText="No parameters are defined for this step."
              title="Parameters"
              values={activeStep.parameters}
            />
            <DictionarySection
              emptyText="No branch routing is configured for this step."
              title="Branches"
              values={activeStep.branches}
            />
            <ChildStepSection childrenSteps={activeStep.children} />
          </div>
        </div>
      </Col>
    </Row>
  );
};

const focusColumns: ProDescriptionsItemProps<WorkflowFocusRecord>[] = [
  {
    title: "Focus type",
    dataIndex: "focusType",
    render: (_, record) => focusTypeLabels[record.focusType],
  },
  {
    title: "Identifier",
    dataIndex: "focusId",
    render: (_, record) => (
      <Typography.Text copyable>{record.focusId}</Typography.Text>
    ),
  },
  {
    title: "Related role",
    dataIndex: "relatedRole",
    render: (_, record) => record.relatedRole || "n/a",
  },
  {
    title: "Related steps",
    dataIndex: "relatedStepCount",
    valueType: "digit",
  },
  {
    title: "Graph node",
    dataIndex: "graphNodeId",
    render: (_, record) => (
      <Typography.Text copyable>{record.graphNodeId}</Typography.Text>
    ),
  },
];

function parseDetailTab(value: string | null): WorkflowDetailTab {
  if (
    value === "yaml" ||
    value === "roles" ||
    value === "steps" ||
    value === "graph"
  ) {
    return value;
  }

  return "yaml";
}

function readInitialSelection(): { workflow: string; tab: WorkflowDetailTab } {
  if (typeof window === "undefined") {
    return { workflow: "", tab: "yaml" };
  }

  const params = new URLSearchParams(window.location.search);
  return {
    workflow: params.get("workflow") ?? "",
    tab: parseDetailTab(params.get("tab")),
  };
}

function sortFilterValues(values: string[]): string[] {
  return [...values].sort((left, right) => left.localeCompare(right));
}

function areWorkflowFiltersEqual(
  left: WorkflowLibraryFilter,
  right: WorkflowLibraryFilter
): boolean {
  return (
    left.keyword.trim() === right.keyword.trim() &&
    left.llmRequirement === right.llmRequirement &&
    JSON.stringify(sortFilterValues(left.groups)) ===
      JSON.stringify(sortFilterValues(right.groups)) &&
    JSON.stringify(sortFilterValues(left.sources)) ===
      JSON.stringify(sortFilterValues(right.sources)) &&
    JSON.stringify(sortFilterValues(left.primitives)) ===
      JSON.stringify(sortFilterValues(right.primitives))
  );
}

function countAdvancedFilters(filters: WorkflowLibraryFilter): number {
  return [
    filters.groups.length > 0,
    filters.sources.length > 0,
    filters.llmRequirement !== "all",
    filters.primitives.length > 0,
  ].filter(Boolean).length;
}

function summarizeAppliedFilters(filters: WorkflowLibraryFilter): string {
  const parts: string[] = [];

  if (filters.keyword.trim()) {
    parts.push(`Keyword: ${filters.keyword.trim()}`);
  }
  if (filters.groups.length > 0) {
    parts.push(`${filters.groups.length} group filter(s)`);
  }
  if (filters.sources.length > 0) {
    parts.push(`${filters.sources.length} source filter(s)`);
  }
  if (filters.llmRequirement !== "all") {
    parts.push(
      filters.llmRequirement === "required"
        ? "Requires LLM only"
        : "Optional LLM only"
    );
  }
  if (filters.primitives.length > 0) {
    parts.push(`${filters.primitives.length} primitive filter(s)`);
  }

  return parts.length > 0 ? parts.join(" · ") : "All workflows";
}

function createWorkflowColumns(
  onInspect: (workflowName: string) => void,
  onRun: (workflowName: string) => void
): ProColumns<WorkflowLibraryRow>[] {
  return [
    {
      title: "Workflow",
      dataIndex: "name",
      width: 260,
      render: (_, record) => (
        <div style={workflowNameCellStyle}>
          <Typography.Text strong>{record.name}</Typography.Text>
          <Typography.Text
            type="secondary"
            ellipsis={{ tooltip: record.description }}
            style={{ display: "block", fontSize: 12, maxWidth: "100%" }}
          >
            {record.description || "No description provided."}
          </Typography.Text>
        </div>
      ),
    },
    {
      title: "Group",
      dataIndex: "groupLabel",
      width: 140,
    },
    {
      title: "Source",
      dataIndex: "sourceLabel",
      width: 120,
    },
    {
      title: "Primitives",
      dataIndex: "primitives",
      width: 220,
      render: (_, record) => {
        const visiblePrimitives = record.primitives.slice(0, 2);
        const remainingCount =
          record.primitives.length - visiblePrimitives.length;

        if (visiblePrimitives.length === 0) {
          return <Typography.Text type="secondary">n/a</Typography.Text>;
        }

        return (
          <Space wrap size={[4, 4]}>
            {visiblePrimitives.map((primitive) => (
              <Tag key={`${record.name}-${primitive}`} color="blue">
                {primitive}
              </Tag>
            ))}
            {remainingCount > 0 ? <Tag>+{remainingCount}</Tag> : null}
          </Space>
        );
      },
    },
    {
      title: "LLM",
      dataIndex: "llmStatus",
      width: 100,
      valueType: "status" as any,
      valueEnum: llmValueEnum,
    },
    {
      title: "Actions",
      valueType: "option",
      width: 180,
      align: "right",
      render: (_, record) => (
        <Space key={`${record.name}-actions`} size={4}>
          <Button
            type="link"
            onClick={(event) => {
              event.stopPropagation();
              onInspect(record.name);
            }}
          >
            Inspect
          </Button>
          <Button
            type="link"
            onClick={(event) => {
              event.stopPropagation();
              onRun(record.name);
            }}
          >
            Run
          </Button>
        </Space>
      ),
    },
  ];
}

const WorkflowsPage: React.FC = () => {
  const initialSelection = useMemo(() => readInitialSelection(), []);
  const [filters, setFilters] = useState<WorkflowLibraryFilter>(
    defaultWorkflowLibraryFilter
  );
  const [filterDraft, setFilterDraft] = useState<WorkflowLibraryFilter>(
    defaultWorkflowLibraryFilter
  );
  const [showAdvancedFilters, setShowAdvancedFilters] = useState(false);
  const [isLibraryCollapsed, setIsLibraryCollapsed] = useState(false);
  const [selectedWorkflow, setSelectedWorkflow] = useState<string>(
    initialSelection.workflow
  );
  const [activeDetailTab, setActiveDetailTab] = useState<WorkflowDetailTab>(
    initialSelection.tab
  );
  const [selectedRoleId, setSelectedRoleId] = useState<string>("");
  const [selectedStepId, setSelectedStepId] = useState<string>("");
  const [selectedGraphNodeId, setSelectedGraphNodeId] = useState<string>("");
  const [graphFocusSequence, setGraphFocusSequence] = useState(0);
  const [isGraphFullscreenOpen, setIsGraphFullscreenOpen] = useState(false);
  const [viewportHeight, setViewportHeight] = useState(() =>
    typeof window === "undefined" ? 960 : window.innerHeight
  );
  const graphPanelRef = useRef<HTMLDivElement | null>(null);
  const pendingGraphScrollRef = useRef(false);

  const catalogQuery = useQuery({
    queryKey: ["workflow-catalog"],
    queryFn: () => runtimeCatalogApi.listWorkflowCatalog(),
  });

  const detailQuery = useQuery({
    queryKey: ["workflow-detail", selectedWorkflow],
    enabled: Boolean(selectedWorkflow),
    queryFn: () => runtimeCatalogApi.getWorkflowDetail(selectedWorkflow),
  });

  const workflowRows = useMemo(
    () =>
      buildWorkflowRows(
        listVisibleWorkflowCatalogItems(catalogQuery.data ?? [])
      ),
    [catalogQuery.data]
  );

  const filteredCatalog = useMemo(
    () => filterWorkflowRows(workflowRows, filters),
    [filters, workflowRows]
  );

  const groupOptions = useMemo(
    () => buildStringOptions(workflowRows.map((item) => item.groupLabel)),
    [workflowRows]
  );

  const sourceOptions = useMemo(
    () => buildStringOptions(workflowRows.map((item) => item.sourceLabel)),
    [workflowRows]
  );

  const primitiveOptions = useMemo(
    () => buildStringOptions(workflowRows.flatMap((item) => item.primitives)),
    [workflowRows]
  );
  const advancedFilterCount = useMemo(
    () => countAdvancedFilters(filterDraft),
    [filterDraft]
  );
  const appliedFilterSummary = useMemo(
    () => summarizeAppliedFilters(filters),
    [filters]
  );
  const hasPendingFilterChanges = useMemo(
    () => !areWorkflowFiltersEqual(filters, filterDraft),
    [filterDraft, filters]
  );

  useEffect(() => {
    if (
      (catalogQuery.data ?? []).some((item) => item.name === selectedWorkflow)
    ) {
      return;
    }

    const nextSelection = resolveWorkflowCatalogSelection(
      catalogQuery.data ?? [],
      selectedWorkflow
    );
    if (nextSelection !== selectedWorkflow) {
      setSelectedWorkflow(nextSelection);
    }
  }, [catalogQuery.data, selectedWorkflow]);

  useEffect(() => {
    if (typeof window === "undefined") {
      return;
    }

    const handleResize = () => setViewportHeight(window.innerHeight);
    window.addEventListener("resize", handleResize);
    return () => window.removeEventListener("resize", handleResize);
  }, []);

  useEffect(() => {
    setSelectedRoleId("");
    setSelectedStepId("");
    setSelectedGraphNodeId("");
    setIsGraphFullscreenOpen(false);
    setActiveDetailTab((current) => (current === "graph" ? current : "yaml"));
  }, [selectedWorkflow]);

  useEffect(() => {
    if (typeof window === "undefined") {
      return;
    }

    const url = new URL(window.location.href);
    if (selectedWorkflow) {
      url.searchParams.set("workflow", selectedWorkflow);
    } else {
      url.searchParams.delete("workflow");
    }
    url.searchParams.set("tab", activeDetailTab);
    window.history.replaceState(null, "", `${url.pathname}${url.search}`);
  }, [activeDetailTab, selectedWorkflow]);

  const workflowColumns = useMemo(
    () =>
      createWorkflowColumns(
        (workflowName) => setSelectedWorkflow(workflowName),
        (workflowName) =>
          history.push(`/runs?workflow=${encodeURIComponent(workflowName)}`)
      ),
    []
  );

  const stepRows = useMemo<WorkflowStepRow[]>(
    () => buildStepRows(detailQuery.data?.definition.steps ?? []),
    [detailQuery.data?.definition.steps]
  );

  const openRunsPage = useCallback(() => {
    if (!selectedWorkflow) {
      return;
    }

    history.push(`/runs?workflow=${encodeURIComponent(selectedWorkflow)}`);
  }, [selectedWorkflow]);

  const workflowSummary = useMemo<WorkflowSummaryRecord | undefined>(() => {
    if (!detailQuery.data) {
      return undefined;
    }

    return {
      closedWorldStatus: detailQuery.data.definition.closedWorldMode
        ? "success"
        : "default",
      roleCount: detailQuery.data.definition.roles.length,
      stepCount: detailQuery.data.definition.steps.length,
      edgeCount: detailQuery.data.edges.length,
      primitiveCount: detailQuery.data.catalog.primitives.length,
      sourceLabel: detailQuery.data.catalog.sourceLabel,
    };
  }, [detailQuery.data]);
  const workflowSummaryMetrics = useMemo<WorkflowSummaryMetric[]>(
    () =>
      workflowSummary
        ? [
            {
              id: "mode",
              title: "Mode",
              value:
                workflowSummary.closedWorldStatus === "success"
                  ? "Closed world"
                  : "Open world",
            },
            {
              id: "source",
              title: "Source",
              value: workflowSummary.sourceLabel,
            },
            {
              id: "roles",
              title: "Roles",
              value: workflowSummary.roleCount,
            },
            {
              id: "steps",
              title: "Steps",
              value: workflowSummary.stepCount,
            },
            {
              id: "edges",
              title: "Edges",
              value: workflowSummary.edgeCount,
            },
            {
              id: "primitives",
              title: "Primitives",
              value: workflowSummary.primitiveCount,
            },
          ]
        : [],
    [workflowSummary]
  );

  const graphElements = useMemo(() => {
    if (!detailQuery.data) {
      return { nodes: [], edges: [] };
    }

    return buildWorkflowGraphElements(detailQuery.data);
  }, [detailQuery.data]);
  const graphTabLabel = useMemo(() => {
    const isActive = activeDetailTab === "graph";
    const hasFocus = Boolean(selectedGraphNodeId);
    const isHighlighted = isActive || hasFocus;

    return (
      <span
        data-active={isActive ? "true" : "false"}
        data-highlighted={isHighlighted ? "true" : "false"}
        data-testid="workflow-graph-tab-label"
        style={{
          ...highlightedTabLabelStyle,
          background: isActive
            ? "linear-gradient(135deg, rgba(22, 119, 255, 0.2), rgba(22, 119, 255, 0.08))"
            : hasFocus
            ? "rgba(22, 119, 255, 0.08)"
            : "transparent",
          border: `1px solid ${
            isActive
              ? "rgba(22, 119, 255, 0.36)"
              : hasFocus
              ? "rgba(22, 119, 255, 0.24)"
              : "transparent"
          }`,
          boxShadow: isActive ? "0 6px 16px rgba(22, 119, 255, 0.14)" : "none",
          color: isActive ? "var(--ant-color-primary)" : "inherit",
        }}
      >
        <ApartmentOutlined
          aria-hidden
          style={{
            color: isHighlighted
              ? "var(--ant-color-primary)"
              : "var(--ant-color-text-secondary)",
          }}
        />
        <span>Graph</span>
        <span
          aria-hidden
          style={{
            background: isActive ? "#1677ff" : hasFocus ? "#52c41a" : "#d9d9d9",
            borderRadius: "50%",
            boxShadow: isHighlighted
              ? "0 0 0 4px rgba(22, 119, 255, 0.12)"
              : "none",
            display: "inline-block",
            height: 8,
            width: 8,
          }}
        />
      </span>
    );
  }, [activeDetailTab, selectedGraphNodeId]);
  const fullscreenGraphHeight = useMemo(
    () => Math.max(viewportHeight - 220, 560),
    [viewportHeight]
  );

  const focusedRole = useMemo(
    () =>
      detailQuery.data?.definition.roles.find(
        (role) => role.id === selectedRoleId
      ),
    [detailQuery.data?.definition.roles, selectedRoleId]
  );

  const focusedStep = useMemo(
    () => stepRows.find((step) => step.id === selectedStepId),
    [selectedStepId, stepRows]
  );

  const focusRecord = useMemo<WorkflowFocusRecord | undefined>(() => {
    if (focusedStep) {
      return {
        focusType: "step",
        focusId: focusedStep.id,
        relatedRole: focusedStep.targetRole || "",
        relatedStepCount: 1,
        graphNodeId: focusedStep.id,
      };
    }

    if (focusedRole) {
      const relatedSteps = stepRows.filter(
        (step) => step.targetRole === focusedRole.id
      );
      return {
        focusType: "role",
        focusId: focusedRole.id,
        relatedRole: focusedRole.name || focusedRole.id,
        relatedStepCount: relatedSteps.length,
        graphNodeId: `role:${focusedRole.id}`,
      };
    }

    return undefined;
  }, [focusedRole, focusedStep, stepRows]);

  const handleSelectRole = useCallback((roleId: string) => {
    setSelectedRoleId(roleId);
    setSelectedStepId("");
  }, []);

  const handleSelectStep = useCallback(
    (stepId: string) => {
      setSelectedStepId(stepId);
      setSelectedRoleId(findWorkflowStepTargetRole(stepRows, stepId));
    },
    [stepRows]
  );

  const handleRoleStepsFocus = useCallback((role: WorkflowCatalogRole) => {
    setSelectedRoleId(role.id);
    setSelectedStepId("");
    setActiveDetailTab("steps");
  }, []);

  const handleInspectRoleFromStep = useCallback((roleId: string) => {
    setSelectedRoleId(roleId);
    setSelectedStepId("");
    setActiveDetailTab("roles");
  }, []);

  const scrollGraphPanelIntoView = useCallback(() => {
    const scrollTarget = graphPanelRef.current;
    if (!scrollTarget) {
      return false;
    }

    scrollTarget.scrollIntoView?.({
      behavior: "smooth",
      block: "nearest",
    });
    pendingGraphScrollRef.current = false;
    return true;
  }, []);

  const handleGraphPanelRef = useCallback(
    (node: HTMLDivElement | null) => {
      graphPanelRef.current = node;

      if (node && pendingGraphScrollRef.current) {
        scrollGraphPanelIntoView();
      }
    },
    [scrollGraphPanelIntoView]
  );

  const focusRoleGraph = useCallback((roleId: string) => {
    setSelectedRoleId(roleId);
    setSelectedStepId("");
    setSelectedGraphNodeId(`role:${roleId}`);
    pendingGraphScrollRef.current = true;
    setActiveDetailTab("graph");
    setGraphFocusSequence((current) => current + 1);
  }, []);

  const focusStepGraph = useCallback(
    (stepId: string, targetRole?: string) => {
      setSelectedStepId(stepId);
      setSelectedRoleId(
        targetRole || findWorkflowStepTargetRole(stepRows, stepId)
      );
      setSelectedGraphNodeId(stepId);
      pendingGraphScrollRef.current = true;
      setActiveDetailTab("graph");
      setGraphFocusSequence((current) => current + 1);
    },
    [stepRows]
  );

  const graphInteractionValue = useMemo<WorkflowGraphInteractionContextValue>(
    () => ({
      focusRoleGraph,
      focusStepGraph,
    }),
    [focusRoleGraph, focusStepGraph]
  );

  const handleGraphNodeSelect = useCallback(
    (nodeId: string) => {
      setSelectedGraphNodeId(nodeId);
      if (nodeId.startsWith("role:")) {
        const roleId = nodeId.slice("role:".length);
        setSelectedRoleId(roleId);
        setSelectedStepId("");
        return;
      }

      setSelectedStepId(nodeId);
      setSelectedRoleId(findWorkflowStepTargetRole(stepRows, nodeId));
    },
    [stepRows]
  );

  useLayoutEffect(() => {
    if (
      activeDetailTab !== "graph" ||
      graphFocusSequence === 0 ||
      !pendingGraphScrollRef.current
    ) {
      return;
    }

    scrollGraphPanelIntoView();
  }, [activeDetailTab, graphFocusSequence, scrollGraphPanelIntoView]);

  const applyFilters = () => {
    setFilters({
      keyword: filterDraft.keyword.trim(),
      groups: filterDraft.groups,
      sources: filterDraft.sources,
      llmRequirement: filterDraft.llmRequirement,
      primitives: filterDraft.primitives,
    });
  };

  const resetFilters = () => {
    setFilterDraft(defaultWorkflowLibraryFilter);
    setFilters(defaultWorkflowLibraryFilter);
    setShowAdvancedFilters(false);
  };

  const toggleLibraryCollapsed = useCallback(() => {
    setIsLibraryCollapsed((current) => !current);
  }, []);

  return (
    <PageContainer
      title="Runtime Workflows"
      content="Browse the runtime workflow catalog, inspect roles and steps, and move directly into the run console for the selected workflow."
    >
      <Row gutter={[16, 16]} align="stretch">
        <Col xs={24} xl={isLibraryCollapsed ? 5 : 9} style={stretchColumnStyle}>
          <ProCard
            title="Runtime workflow catalog"
            {...moduleCardProps}
            style={fillCardStyle}
            extra={
              <Button
                aria-label={
                  isLibraryCollapsed
                    ? "Expand workflow library"
                    : "Collapse workflow library"
                }
                icon={
                  isLibraryCollapsed ? (
                    <MenuUnfoldOutlined />
                  ) : (
                    <MenuFoldOutlined />
                  )
                }
                onClick={toggleLibraryCollapsed}
              >
                {isLibraryCollapsed ? "Expand" : "Collapse"}
              </Button>
            }
          >
            {isLibraryCollapsed ? (
              <div style={collapsedLibraryBodyStyle}>
                <div style={cardStackStyle}>
                  <Typography.Text strong>
                    Library panel is collapsed.
                  </Typography.Text>
                  <Typography.Text type="secondary">
                    Reopen the panel to browse filters, catalog rows, and
                    workflow actions.
                  </Typography.Text>
                </div>
                <div style={cardStackStyle}>
                  <div style={workflowSummaryCardStyle}>
                    <Statistic
                      title="Visible workflows"
                      value={filteredCatalog.length}
                    />
                  </div>
                  <div style={workflowSummaryCardStyle}>
                    <Typography.Text type="secondary">
                      Current selection
                    </Typography.Text>
                    <Typography.Paragraph
                      ellipsis={{
                        rows: 2,
                        tooltip: selectedWorkflow || "No workflow selected",
                      }}
                      style={{ margin: "8px 0 0" }}
                    >
                      {selectedWorkflow || "No workflow selected"}
                    </Typography.Paragraph>
                  </div>
                </div>
              </div>
            ) : (
              <div style={cardStackStyle}>
                <div style={cardStackStyle}>
                  <Row gutter={[16, 16]}>
                    <Col flex="auto">
                      <Input
                        allowClear
                        prefix={<SearchOutlined />}
                        value={filterDraft.keyword}
                        placeholder="Filter by name, description, group, category, or primitive"
                        onChange={(event) =>
                          setFilterDraft((current) => ({
                            ...current,
                            keyword: event.target.value,
                          }))
                        }
                        onPressEnter={applyFilters}
                      />
                    </Col>
                    <Col>
                      <Space wrap size={[8, 8]}>
                        <Button
                          aria-label="Advanced filters"
                          icon={<FilterOutlined />}
                          onClick={() =>
                            setShowAdvancedFilters((current) => !current)
                          }
                        >
                          {showAdvancedFilters
                            ? "Hide advanced filters"
                            : `Advanced filters${
                                advancedFilterCount > 0
                                  ? ` (${advancedFilterCount})`
                                  : ""
                              }`}
                        </Button>
                        <Button
                          type="primary"
                          disabled={!hasPendingFilterChanges}
                          onClick={applyFilters}
                        >
                          Apply
                        </Button>
                        <Button onClick={resetFilters}>Reset</Button>
                      </Space>
                    </Col>
                  </Row>
                  {showAdvancedFilters ? (
                    <div style={compactFilterPanelStyle}>
                      <Row gutter={[12, 12]}>
                        <Col xs={24} md={12}>
                          <Select<string[]>
                            mode="multiple"
                            allowClear
                            value={filterDraft.groups}
                            options={groupOptions}
                            placeholder="Groups"
                            style={{ width: "100%" }}
                            onChange={(value) =>
                              setFilterDraft((current) => ({
                                ...current,
                                groups: value,
                              }))
                            }
                            aria-label="Groups"
                          />
                        </Col>
                        <Col xs={24} md={12}>
                          <Select<string[]>
                            mode="multiple"
                            allowClear
                            value={filterDraft.sources}
                            options={sourceOptions}
                            placeholder="Sources"
                            style={{ width: "100%" }}
                            onChange={(value) =>
                              setFilterDraft((current) => ({
                                ...current,
                                sources: value,
                              }))
                            }
                            aria-label="Sources"
                          />
                        </Col>
                        <Col xs={24} md={12}>
                          <Select<WorkflowLibraryFilter["llmRequirement"]>
                            value={filterDraft.llmRequirement}
                            options={
                              llmFilterOptions as unknown as Array<{
                                label: string;
                                value: WorkflowLibraryFilter["llmRequirement"];
                              }>
                            }
                            placeholder="LLM requirement"
                            style={{ width: "100%" }}
                            onChange={(value) =>
                              setFilterDraft((current) => ({
                                ...current,
                                llmRequirement: value,
                              }))
                            }
                            aria-label="LLM requirement"
                          />
                        </Col>
                        <Col xs={24} md={12}>
                          <Select<string[]>
                            mode="multiple"
                            allowClear
                            value={filterDraft.primitives}
                            options={primitiveOptions}
                            placeholder="Primitives"
                            style={{ width: "100%" }}
                            onChange={(value) =>
                              setFilterDraft((current) => ({
                                ...current,
                                primitives: value,
                              }))
                            }
                            aria-label="Primitives"
                          />
                        </Col>
                      </Row>
                    </div>
                  ) : null}
                  <Typography.Text type="secondary">
                    {filteredCatalog.length} workflow(s) shown ·{" "}
                    {appliedFilterSummary}
                  </Typography.Text>
                </div>

                {catalogQuery.isError ? (
                  <Alert
                    showIcon
                    type="error"
                    title="Failed to load workflow catalog"
                    description={String(catalogQuery.error)}
                  />
                ) : null}

                <ProTable<WorkflowLibraryRow>
                  className="workflow-library-table"
                  rowKey="name"
                  search={false}
                  options={false}
                  bordered={false}
                  cardBordered={false}
                  size="middle"
                  pagination={{ pageSize: 8, showSizeChanger: false }}
                  columns={workflowColumns}
                  dataSource={filteredCatalog}
                  loading={catalogQuery.isLoading}
                  cardProps={compactTableCardProps}
                  scroll={{ x: 940, y: 560 }}
                  onRow={(record) => ({
                    onClick: () => setSelectedWorkflow(record.name),
                  })}
                  rowClassName={(record) =>
                    record.name === selectedWorkflow
                      ? "ant-table-row-selected"
                      : ""
                  }
                  locale={{
                    emptyText: (
                      <Empty
                        image={Empty.PRESENTED_IMAGE_SIMPLE}
                        description="No workflows match the current filter."
                      />
                    ),
                  }}
                />
              </div>
            )}
          </ProCard>
        </Col>

        <Col
          xs={24}
          xl={isLibraryCollapsed ? 19 : 15}
          style={stretchColumnStyle}
        >
          <ProCard
            title="Runtime definition"
            {...moduleCardProps}
            style={fillCardStyle}
            loading={detailQuery.isLoading && Boolean(selectedWorkflow)}
          >
            {detailQuery.isError ? (
              <Alert
                showIcon
                type="error"
                title="Failed to load workflow detail"
                description={String(detailQuery.error)}
              />
            ) : detailQuery.data ? (
              <div style={cardStackStyle}>
                <div style={workflowDetailHeaderStyle}>
                  <div style={workflowDetailHeaderMainStyle}>
                    <div style={cardStackStyle}>
                      <div>
                        <Typography.Text type="secondary">
                          Workflow ID
                        </Typography.Text>
                        <Typography.Title
                          level={4}
                          style={{ margin: "4px 0 0" }}
                        >
                          {detailQuery.data.catalog.name}
                        </Typography.Title>
                      </div>
                      <Space wrap size={[8, 8]}>
                        <Tag color="processing">
                          {detailQuery.data.catalog.groupLabel}
                        </Tag>
                        <Tag>{detailQuery.data.catalog.sourceLabel}</Tag>
                        {!detailQuery.data.catalog.showInLibrary ? (
                          <Tag color="warning">Hidden from library</Tag>
                        ) : null}
                        {detailQuery.data.catalog.primitives
                          .slice(0, 3)
                          .map((primitive) => (
                            <Tag key={primitive} color="blue">
                              {primitive}
                            </Tag>
                          ))}
                        {detailQuery.data.catalog.primitives.length > 3 ? (
                          <Tag>
                            +{detailQuery.data.catalog.primitives.length - 3}{" "}
                            more
                          </Tag>
                        ) : null}
                      </Space>
                    </div>
                    <Typography.Paragraph
                      type="secondary"
                      style={workflowDetailDescriptionStyle}
                      ellipsis={{
                        rows: 2,
                        tooltip: detailQuery.data.catalog.description,
                      }}
                    >
                      {detailQuery.data.catalog.description ||
                        "No description provided."}
                    </Typography.Paragraph>
                  </div>
                  <div style={workflowDetailActionGroupStyle}>
                    <Button
                      type="primary"
                      onClick={() =>
                        history.push(
                          `/runs?workflow=${encodeURIComponent(
                            detailQuery.data.catalog.name
                          )}`
                        )
                      }
                    >
                      Run workflow
                    </Button>
                    <Button onClick={() => history.push("/scopes/workflows")}>
                      Open scope workflows
                    </Button>
                  </div>
                </div>

                <Row gutter={[12, 12]}>
                  {workflowSummaryMetrics.map((item) => (
                    <Col key={item.id} xs={12} md={8}>
                      <div style={workflowSummaryCardStyle}>
                        <Statistic title={item.title} value={item.value} />
                      </div>
                    </Col>
                  ))}
                </Row>

                {focusRecord ? (
                  <div style={embeddedPanelStyle}>
                    <Space wrap style={{ marginBottom: 12 }}>
                      <Tag color="purple">Focused item</Tag>
                      <Typography.Text strong>
                        {focusRecord.focusId}
                      </Typography.Text>
                      {focusRecord.relatedRole ? (
                        <Tag>{focusRecord.relatedRole}</Tag>
                      ) : null}
                    </Space>
                    <ProDescriptions<WorkflowFocusRecord>
                      column={2}
                      dataSource={focusRecord}
                      columns={focusColumns}
                    />
                  </div>
                ) : null}

                <WorkflowGraphInteractionContext.Provider
                  value={graphInteractionValue}
                >
                  <Tabs
                    activeKey={activeDetailTab}
                    onChange={(value) =>
                      setActiveDetailTab(value as WorkflowDetailTab)
                    }
                    items={[
                      {
                        key: "yaml",
                        label: "YAML",
                        children: (
                          <WorkflowYamlViewer yaml={detailQuery.data.yaml} />
                        ),
                      },
                      {
                        key: "roles",
                        label: `Roles (${detailQuery.data.definition.roles.length})`,
                        children: (
                          <WorkflowRoleSplitPanel
                            roles={detailQuery.data.definition.roles}
                            selectedRoleId={selectedRoleId}
                            onOpenRoleSteps={handleRoleStepsFocus}
                            onSelectRole={handleSelectRole}
                          />
                        ),
                      },
                      {
                        key: "steps",
                        label: `Steps (${detailQuery.data.definition.steps.length})`,
                        children: (
                          <WorkflowStepSplitPanel
                            onInspectRole={handleInspectRoleFromStep}
                            onRunWorkflow={openRunsPage}
                            onSelectStep={handleSelectStep}
                            selectedStepId={selectedStepId}
                            steps={stepRows}
                          />
                        ),
                      },
                      {
                        key: "graph",
                        label: graphTabLabel,
                        children: (
                          <div
                            ref={handleGraphPanelRef}
                            style={graphPanelShellStyle}
                          >
                            <div style={graphPanelHeaderStyle}>
                              <div>
                                <Typography.Text strong>
                                  Workflow graph
                                </Typography.Text>
                                <Typography.Paragraph
                                  type="secondary"
                                  style={{ margin: "4px 0 0" }}
                                >
                                  Node highlights follow the latest focus action
                                  from Roles or Steps.
                                </Typography.Paragraph>
                              </div>
                              <Space wrap size={[8, 8]}>
                                {selectedGraphNodeId ? (
                                  <Tag color="blue">
                                    Selected: {selectedGraphNodeId}
                                  </Tag>
                                ) : (
                                  <Tag>No node selected</Tag>
                                )}
                                <Button
                                  aria-label="Open graph fullscreen"
                                  icon={<FullscreenOutlined />}
                                  onClick={() => setIsGraphFullscreenOpen(true)}
                                >
                                  Open fullscreen
                                </Button>
                              </Space>
                            </div>
                            <GraphCanvas
                              nodes={graphElements.nodes}
                              edges={graphElements.edges}
                              selectedNodeId={selectedGraphNodeId}
                              onNodeSelect={handleGraphNodeSelect}
                              height={560}
                            />
                          </div>
                        ),
                      },
                    ]}
                  />
                  <Modal
                    centered={false}
                    destroyOnHidden
                    footer={null}
                    onCancel={() => setIsGraphFullscreenOpen(false)}
                    open={isGraphFullscreenOpen}
                    style={{ maxWidth: "100vw", paddingBottom: 0, top: 0 }}
                    styles={{
                      body: {
                        flex: 1,
                        minHeight: 0,
                        padding: 0,
                      },
                      container: {
                        borderRadius: 0,
                        display: "flex",
                        flexDirection: "column",
                        height: "100vh",
                        padding: 24,
                      },
                      header: {
                        marginBottom: 16,
                      },
                    }}
                    title={`Workflow graph · ${detailQuery.data.catalog.name}`}
                    width="100vw"
                  >
                    <div style={graphModalBodyStyle}>
                      <div style={graphPanelHeaderStyle}>
                        <div>
                          <Typography.Text strong>
                            Fullscreen graph view
                          </Typography.Text>
                          <Typography.Paragraph
                            type="secondary"
                            style={{ margin: "4px 0 0" }}
                          >
                            Explore the workflow topology with more canvas space
                            while keeping node focus in sync with the detail
                            tabs.
                          </Typography.Paragraph>
                        </div>
                        <Space wrap size={[8, 8]}>
                          {selectedGraphNodeId ? (
                            <Tag color="blue">
                              Selected: {selectedGraphNodeId}
                            </Tag>
                          ) : (
                            <Tag>No node selected</Tag>
                          )}
                          <Button
                            aria-label="Close graph fullscreen"
                            icon={<FullscreenExitOutlined />}
                            onClick={() => setIsGraphFullscreenOpen(false)}
                          >
                            Exit fullscreen
                          </Button>
                        </Space>
                      </div>
                      <GraphCanvas
                        nodes={graphElements.nodes}
                        edges={graphElements.edges}
                        selectedNodeId={selectedGraphNodeId}
                        onNodeSelect={handleGraphNodeSelect}
                        height={fullscreenGraphHeight}
                      />
                    </div>
                  </Modal>
                </WorkflowGraphInteractionContext.Provider>
              </div>
            ) : (
              <Empty
                image={Empty.PRESENTED_IMAGE_SIMPLE}
                description="Select a runtime workflow to inspect the definition."
              />
            )}
          </ProCard>
        </Col>
      </Row>
    </PageContainer>
  );
};

export default WorkflowsPage;
