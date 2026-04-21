import {
  CodeOutlined,
  EyeOutlined,
  PlayCircleOutlined,
} from "@ant-design/icons";
import { useQuery } from "@tanstack/react-query";
import {
  Alert,
  Button,
  Empty,
  Input,
  Select,
  Space,
  Table,
  Tabs,
  Tag,
  Typography,
} from "antd";
import type { ColumnsType } from "antd/es/table";
import React, { useEffect, useMemo, useState } from "react";
import { runtimeCatalogApi } from "@/shared/api/runtimeCatalogApi";
import { history } from "@/shared/navigation/history";
import {
  buildRuntimeRunsHref,
  buildRuntimeWorkflowsHref,
} from "@/shared/navigation/runtimeRoutes";
import { buildStudioWorkflowEditorRoute } from "@/shared/studio/navigation";
import type {
  WorkflowCatalogItem,
  WorkflowCatalogItemDetail,
  WorkflowCatalogRole,
} from "@/shared/models/runtime/catalog";
import {
  AevatarContextDrawer,
  AevatarInspectorEmpty,
  AevatarPageShell,
  AevatarPanel,
  AevatarStatusTag,
} from "@/shared/ui/aevatarPageShells";
import { AevatarCompactText } from "@/shared/ui/compactText";
import {
  codeBlockStyle,
  summaryFieldLabelStyle,
  summaryMetricStyle,
  summaryMetricValueStyle,
} from "@/shared/ui/proComponents";
import { listVisibleWorkflowCatalogItems } from "@/shared/workflows/catalogVisibility";
import {
  buildStepRows,
  buildStringOptions,
  buildWorkflowRows,
  defaultWorkflowLibraryFilter,
  filterWorkflowRows,
  type WorkflowLibraryFilter,
  type WorkflowLibraryRow,
  type WorkflowStepRow,
} from "./workflowPresentation";

const tableHeaderCellStyle: React.CSSProperties = {
  background: "var(--ant-color-fill-alter)",
  borderBottom: "1px solid var(--ant-color-border-secondary)",
  color: "var(--ant-color-text-secondary)",
  fontSize: 11,
  fontWeight: 700,
  letterSpacing: 0.24,
  padding: "12px 14px",
  textAlign: "left",
  textTransform: "uppercase",
  whiteSpace: "nowrap",
};

const tableCellStyle: React.CSSProperties = {
  borderBottom: "1px solid var(--ant-color-border-secondary)",
  padding: "12px 14px",
  verticalAlign: "top",
};

const workflowRunTextButtonStyle: React.CSSProperties = {
  color: "var(--ant-color-primary)",
  paddingInline: 8,
};

const workflowSurfaceShadow = "0 12px 28px rgba(15, 23, 42, 0.05)";

function readWorkflowSelection(): string {
  if (typeof window === "undefined") {
    return "";
  }

  return new URLSearchParams(window.location.search).get("workflow")?.trim() ?? "";
}

function buildWorkflowHref(workflowName: string): string {
  return buildRuntimeWorkflowsHref({
    workflow: workflowName.trim() || undefined,
  });
}

function buildWorkflowSummary(workflow: WorkflowCatalogItem): string {
  const description = workflow.description.trim();
  if (description) {
    return description;
  }

  return workflow.requiresLlmProvider
    ? "This workflow depends on an LLM provider before runtime handoff can start."
    : "Closed-world definition ready for direct runtime invocation.";
}

function formatListPreview(values: readonly string[], emptyLabel = "None"): string {
  if (values.length === 0) {
    return emptyLabel;
  }

  if (values.length <= 3) {
    return values.join(", ");
  }

  return `${values.slice(0, 3).join(", ")} +${values.length - 3}`;
}

function buildLibraryMetrics(rows: readonly WorkflowLibraryRow[]) {
  return {
    workflows: rows.length,
    groups: new Set(rows.map((row) => row.groupLabel)).size,
    llmRequired: rows.filter((row) => row.requiresLlmProvider).length,
    yourWorkflows: rows.filter((row) => row.group === "your-workflows").length,
  };
}

function buildRoleConnectorSummary(roles: readonly WorkflowCatalogRole[]): string[] {
  return Array.from(
    new Set(
      roles.flatMap((role) => role.connectors.filter((connector) => connector.trim().length > 0)),
    ),
  ).sort((left, right) => left.localeCompare(right));
}

const WorkflowSummaryMetric: React.FC<{
  label: string;
  value: React.ReactNode;
}> = ({ label, value }) => (
  <div
    style={{
      ...summaryMetricStyle,
      background: "var(--ant-color-bg-container)",
      border: "1px solid var(--ant-color-border-secondary)",
      borderRadius: 18,
      boxShadow: "0 1px 2px rgba(15, 23, 42, 0.04)",
      minHeight: 0,
      padding: "12px 14px",
      position: "relative",
    }}
  >
    <div
      aria-hidden
      style={{
        background: "var(--ant-color-primary-border)",
        borderRadius: 999,
        height: 3,
        left: 14,
        position: "absolute",
        right: 14,
        top: 0,
      }}
    />
    <Typography.Text style={summaryFieldLabelStyle}>{label}</Typography.Text>
    <Typography.Text style={summaryMetricValueStyle}>{value}</Typography.Text>
  </div>
);

const WorkflowField: React.FC<{
  label: string;
  value: React.ReactNode;
}> = ({ label, value }) => (
  <div style={{ display: "flex", flexDirection: "column", gap: 4, minWidth: 0 }}>
    <Typography.Text style={summaryFieldLabelStyle}>{label}</Typography.Text>
    <div
      style={{
        color: "var(--ant-color-text)",
        fontWeight: 600,
        minWidth: 0,
        overflowWrap: "anywhere",
      }}
    >
      {value}
    </div>
  </div>
);

const WorkflowCatalogStatusTags: React.FC<{
  workflow: WorkflowCatalogItem;
}> = ({ workflow }) => (
  <Space size={[8, 8]} wrap>
    <AevatarStatusTag
      domain="governance"
      label={workflow.requiresLlmProvider ? "LLM required" : "Closed-world ready"}
      status={workflow.requiresLlmProvider ? "active" : "ready"}
    />
    <Tag
      style={{
        borderRadius: 999,
        fontWeight: 600,
        marginInlineEnd: 0,
      }}
    >
      {workflow.sourceLabel}
    </Tag>
  </Space>
);

const WorkflowsPage: React.FC = () => {
  const [filters, setFilters] = useState<WorkflowLibraryFilter>(
    defaultWorkflowLibraryFilter,
  );
  const [selectedWorkflow, setSelectedWorkflow] = useState(readWorkflowSelection());

  const catalogQuery = useQuery({
    queryKey: ["workflow-catalog"],
    queryFn: () => runtimeCatalogApi.listWorkflowCatalog(),
  });

  const selectedWorkflowQuery = useQuery({
    enabled: selectedWorkflow.trim().length > 0,
    queryKey: ["workflow-detail", selectedWorkflow],
    queryFn: () => runtimeCatalogApi.getWorkflowDetail(selectedWorkflow),
  });

  useEffect(() => {
    history.replace(buildWorkflowHref(selectedWorkflow));
  }, [selectedWorkflow]);

  const visibleItems = useMemo(
    () => listVisibleWorkflowCatalogItems(catalogQuery.data ?? []),
    [catalogQuery.data],
  );

  const allRows = useMemo(() => buildWorkflowRows(visibleItems), [visibleItems]);
  const filteredRows = useMemo(
    () => filterWorkflowRows(allRows, filters),
    [allRows, filters],
  );
  const metrics = useMemo(() => buildLibraryMetrics(allRows), [allRows]);

  const groupOptions = useMemo(
    () => buildStringOptions(allRows.map((row) => row.groupLabel)),
    [allRows],
  );

  const sourceOptions = useMemo(
    () => buildStringOptions(allRows.map((row) => row.sourceLabel)),
    [allRows],
  );

  const selectedWorkflowDetail = selectedWorkflowQuery.data;
  const stepRows = useMemo(
    () => buildStepRows(selectedWorkflowDetail?.definition.steps ?? []),
    [selectedWorkflowDetail],
  );
  const connectorSummary = useMemo(
    () => buildRoleConnectorSummary(selectedWorkflowDetail?.definition.roles ?? []),
    [selectedWorkflowDetail],
  );

  const workflowColumns = useMemo<ColumnsType<WorkflowLibraryRow>>(
    () => [
      {
        title: "Workflow",
        dataIndex: "name",
        key: "workflow",
        width: "32%",
        render: (_value, workflow) => {
          const summary = buildWorkflowSummary(workflow);

          return (
            <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
              <Typography.Text strong style={{ fontSize: 15 }}>
                {workflow.name}
              </Typography.Text>
              <Typography.Paragraph
                ellipsis={{ rows: 2, tooltip: summary }}
                style={{
                  color: "var(--ant-color-text-secondary)",
                  margin: 0,
                }}
              >
                {summary}
              </Typography.Paragraph>
            </div>
          );
        },
      },
      {
        title: "Collection",
        key: "collection",
        width: "18%",
        render: (_value, workflow) => (
          <div style={{ display: "flex", flexDirection: "column", gap: 4 }}>
            <Typography.Text strong>{workflow.groupLabel}</Typography.Text>
            <Typography.Text type="secondary">{workflow.sourceLabel}</Typography.Text>
          </div>
        ),
      },
      {
        title: "Runtime fit",
        key: "runtime-fit",
        width: "18%",
        render: (_value, workflow) => <WorkflowCatalogStatusTags workflow={workflow} />,
      },
      {
        title: "Primitives",
        key: "primitives",
        width: "18%",
        render: (_value, workflow) => (
          <div style={{ display: "flex", flexDirection: "column", gap: 4 }}>
            <Typography.Text strong>{workflow.primitives.length}</Typography.Text>
            <Typography.Text type="secondary">
              {formatListPreview(workflow.primitives, "No primitive data")}
            </Typography.Text>
          </div>
        ),
      },
      {
        title: "Actions",
        key: "actions",
        width: 240,
        render: (_value, workflow) => (
          <Space wrap size={[8, 8]}>
            <Button
              icon={<EyeOutlined />}
              onClick={(event) => {
                event.stopPropagation();
                setSelectedWorkflow(workflow.name);
              }}
            >
              Inspect
            </Button>
            <Button
              icon={<PlayCircleOutlined />}
              onClick={(event) => {
                event.stopPropagation();
                history.push(
                  buildRuntimeRunsHref({
                    workflow: workflow.name,
                  }),
                );
              }}
              style={workflowRunTextButtonStyle}
              type="text"
            >
              Run
            </Button>
          </Space>
        ),
      },
    ],
    [],
  );

  const roleColumns = useMemo<ColumnsType<WorkflowCatalogRole>>(
    () => [
      {
        title: "Role",
        dataIndex: "name",
        key: "role",
        width: "28%",
        render: (_value, role) => (
          <div style={{ display: "flex", flexDirection: "column", gap: 4 }}>
            <Typography.Text strong>{role.name || role.id}</Typography.Text>
            <Typography.Text type="secondary">{role.id}</Typography.Text>
          </div>
        ),
      },
      {
        title: "Provider",
        key: "provider",
        width: "24%",
        render: (_value, role) => (
          <div style={{ display: "flex", flexDirection: "column", gap: 4 }}>
            <Typography.Text>{role.provider || "No provider"}</Typography.Text>
            <Typography.Text type="secondary">{role.model || "No model"}</Typography.Text>
          </div>
        ),
      },
      {
        title: "Connectors",
        key: "connectors",
        width: "24%",
        render: (_value, role) => (
          <Typography.Text type="secondary">
            {formatListPreview(role.connectors)}
          </Typography.Text>
        ),
      },
      {
        title: "Runtime limits",
        key: "limits",
        render: (_value, role) => (
          <Typography.Text type="secondary">
            {`Tool rounds ${role.maxToolRounds ?? "n/a"} · History ${
              role.maxHistoryMessages ?? "n/a"
            }`}
          </Typography.Text>
        ),
      },
    ],
    [],
  );

  const stepColumns = useMemo<ColumnsType<WorkflowStepRow>>(
    () => [
      {
        title: "Step",
        dataIndex: "id",
        key: "step",
        width: "20%",
        render: (value: string) => (
          <AevatarCompactText
            maxChars={24}
            mode="tail"
            strong
            style={{ fontSize: 13 }}
            value={value}
          />
        ),
      },
      {
        title: "Type",
        dataIndex: "type",
        key: "type",
        width: "16%",
        render: (value: string) => (
          <Tag style={{ borderRadius: 999, fontWeight: 600, marginInlineEnd: 0 }}>
            {value}
          </Tag>
        ),
      },
      {
        title: "Target role",
        dataIndex: "targetRole",
        key: "targetRole",
        width: "16%",
        render: (value: string) => (
          <Typography.Text>{value || "n/a"}</Typography.Text>
        ),
      },
      {
        title: "Flow",
        key: "flow",
        width: "24%",
        render: (_value, step) => (
          <Typography.Text type="secondary">
            {step.next
              ? `Next: ${step.next}`
              : step.branchCount > 0
                ? `${step.branchCount} branch routes`
                : "No explicit next step"}
          </Typography.Text>
        ),
      },
      {
        title: "Parameters",
        key: "parameters",
        render: (_value, step) => (
          <Typography.Text type="secondary">
            {step.parameterCount} params · {step.childCount} child steps
          </Typography.Text>
        ),
      },
    ],
    [],
  );

  return (
    <AevatarPageShell
      layoutMode="document"
      title="Workflow Library"
      titleHelp="Browse runtime-exposed workflow definitions, inspect how they are wired, then jump into run or editor from the same catalog."
    >
      <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
        {catalogQuery.error ? (
          <Alert
            showIcon
            title={
              catalogQuery.error instanceof Error
                ? catalogQuery.error.message
                : "Failed to load workflow catalog."
            }
            type="error"
          />
        ) : null}

        <div
          style={{
            display: "grid",
            gap: 14,
            gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
          }}
        >
          <WorkflowSummaryMetric label="Workflows in library" value={metrics.workflows} />
          <WorkflowSummaryMetric label="Groups" value={metrics.groups} />
          <WorkflowSummaryMetric label="LLM required" value={metrics.llmRequired} />
          <WorkflowSummaryMetric label="Your workflows" value={metrics.yourWorkflows} />
        </div>

        <AevatarPanel
          description="The runtime catalog is already loaded. Filters apply immediately so you can stay focused on selecting a runnable definition."
          extra={
            <Button
              onClick={() => setFilters(defaultWorkflowLibraryFilter)}
              type="default"
            >
              Reset
            </Button>
          }
          title="Find workflows"
        >
          <div
            style={{
              display: "grid",
              gap: 12,
              gridTemplateColumns: "minmax(280px, 2fr) repeat(3, minmax(180px, 1fr))",
            }}
          >
            <Input
              onChange={(event) =>
                setFilters((current) => ({
                  ...current,
                  keyword: event.target.value,
                }))
              }
              placeholder="Search workflow, description, group, or primitive"
              value={filters.keyword}
            />
            <Select
              allowClear
              mode="multiple"
              onChange={(values) =>
                setFilters((current) => ({
                  ...current,
                  groups: values,
                }))
              }
              options={groupOptions}
              placeholder="Groups"
              value={filters.groups}
            />
            <Select
              allowClear
              mode="multiple"
              onChange={(values) =>
                setFilters((current) => ({
                  ...current,
                  sources: values,
                }))
              }
              options={sourceOptions}
              placeholder="Sources"
              value={filters.sources}
            />
            <Select
              onChange={(value) =>
                setFilters((current) => ({
                  ...current,
                  llmRequirement: value,
                }))
              }
              options={[
                { label: "All workflows", value: "all" },
                { label: "LLM required", value: "required" },
                { label: "Closed-world ready", value: "optional" },
              ]}
              value={filters.llmRequirement}
            />
          </div>
        </AevatarPanel>

        <AevatarPanel
          description="This page is the runtime catalog, not the draft workspace. Stay here to choose a definition, then inspect, run, or open the editor."
          title="Workflow catalog"
        >
          {catalogQuery.isLoading ? (
            <Typography.Text type="secondary">Loading workflow catalog…</Typography.Text>
          ) : filteredRows.length === 0 ? (
            <Empty
              description="No workflows matched the current filters."
              image={Empty.PRESENTED_IMAGE_SIMPLE}
            />
          ) : (
            <Table<WorkflowLibraryRow>
              columns={workflowColumns}
              dataSource={filteredRows}
              onRow={(workflow) => ({
                onClick: () => setSelectedWorkflow(workflow.name),
                style: { cursor: "pointer" },
              })}
              pagination={{
                pageSize: 10,
                showSizeChanger: false,
              }}
              rowKey={(workflow) => workflow.name}
              scroll={{ x: 1100 }}
              style={{ width: "100%" }}
            />
          )}
        </AevatarPanel>
      </div>

      <AevatarContextDrawer
        extra={
          selectedWorkflowDetail ? (
            <Space>
              <Button
                icon={<CodeOutlined />}
                onClick={() =>
                  history.push(
                    buildStudioWorkflowEditorRoute({
                      workflowId: selectedWorkflowDetail.catalog.name,
                    }),
                  )
                }
              >
                Open workflow editor
              </Button>
              <Button
                icon={<PlayCircleOutlined />}
                onClick={() =>
                  history.push(
                    buildRuntimeRunsHref({
                      workflow: selectedWorkflowDetail.catalog.name,
                    }),
                  )
                }
                style={workflowRunTextButtonStyle}
                type="text"
              >
                Run workflow
              </Button>
            </Space>
          ) : null
        }
        onClose={() => setSelectedWorkflow("")}
        open={Boolean(selectedWorkflow)}
        subtitle="Runtime workflow detail"
        title={selectedWorkflowDetail?.catalog.name || selectedWorkflow || "Workflow"}
        width={920}
      >
        {!selectedWorkflow ? null : selectedWorkflowQuery.isLoading ? (
          <Typography.Text type="secondary">Loading workflow detail…</Typography.Text>
        ) : selectedWorkflowQuery.error ? (
          <Alert
            showIcon
            title={
              selectedWorkflowQuery.error instanceof Error
                ? selectedWorkflowQuery.error.message
                : "Failed to load workflow detail."
            }
            type="error"
          />
        ) : !selectedWorkflowDetail ? (
          <AevatarInspectorEmpty description="Choose a workflow to inspect its runtime wiring, role model, and source YAML." />
        ) : (
          <Tabs
            defaultActiveKey="overview"
            items={[
              {
                key: "overview",
                label: "Overview",
                children: (
                  <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
                    <AevatarPanel
                      description="Check the runtime fit, role count, step count, and linked connectors before you decide to run or edit this definition."
                      title="Definition summary"
                    >
                      <div
                        style={{
                          display: "grid",
                          gap: 14,
                          gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
                        }}
                      >
                        <WorkflowField
                          label="Collection"
                          value={selectedWorkflowDetail.catalog.groupLabel}
                        />
                        <WorkflowField
                          label="Source"
                          value={selectedWorkflowDetail.catalog.sourceLabel}
                        />
                        <WorkflowField
                          label="Closed-world mode"
                          value={
                            selectedWorkflowDetail.definition.closedWorldMode
                              ? "Enabled"
                              : "Disabled"
                          }
                        />
                        <WorkflowField
                          label="Requires LLM provider"
                          value={
                            selectedWorkflowDetail.catalog.requiresLlmProvider
                              ? "Yes"
                              : "No"
                          }
                        />
                        <WorkflowField
                          label="Roles"
                          value={selectedWorkflowDetail.definition.roles.length}
                        />
                        <WorkflowField
                          label="Steps"
                          value={selectedWorkflowDetail.definition.steps.length}
                        />
                        <WorkflowField
                          label="Topology edges"
                          value={selectedWorkflowDetail.edges.length}
                        />
                        <WorkflowField
                          label="Connectors"
                          value={formatListPreview(connectorSummary)}
                        />
                      </div>
                      <div
                        style={{
                          background: "var(--ant-color-fill-quaternary)",
                          border: "1px solid var(--ant-color-border-secondary)",
                          borderRadius: 14,
                          display: "flex",
                          flexDirection: "column",
                          gap: 8,
                          marginTop: 16,
                          padding: 14,
                        }}
                      >
                        <Typography.Text style={summaryFieldLabelStyle}>
                          Description
                        </Typography.Text>
                        <Typography.Text>
                          {selectedWorkflowDetail.catalog.description ||
                            "No description provided."}
                        </Typography.Text>
                      </div>
                      <div
                        style={{
                          display: "flex",
                          flexWrap: "wrap",
                          gap: 8,
                          marginTop: 16,
                        }}
                      >
                        <WorkflowCatalogStatusTags workflow={selectedWorkflowDetail.catalog} />
                        {selectedWorkflowDetail.catalog.primitives.map((primitive) => (
                          <Tag
                            key={primitive}
                            style={{
                              borderRadius: 999,
                              fontWeight: 600,
                              marginInlineEnd: 0,
                            }}
                          >
                            {primitive}
                          </Tag>
                        ))}
                      </div>
                    </AevatarPanel>
                  </div>
                ),
              },
              {
                key: "roles",
                label: `Roles (${selectedWorkflowDetail.definition.roles.length})`,
                children: (
                  <AevatarPanel
                    description="These are the runtime roles the workflow definition declares, including provider/model hints and attached connectors."
                    title="Role model"
                  >
                    <Table<WorkflowCatalogRole>
                      columns={roleColumns}
                      dataSource={selectedWorkflowDetail.definition.roles}
                      pagination={false}
                      rowKey={(role) => role.id}
                      scroll={{ x: 820 }}
                    />
                  </AevatarPanel>
                ),
              },
              {
                key: "steps",
                label: `Steps (${stepRows.length})`,
                children: (
                  <AevatarPanel
                    description="Use this view to understand the execution path before opening the full editor."
                    title="Execution steps"
                  >
                    <Table<WorkflowStepRow>
                      columns={stepColumns}
                      dataSource={stepRows}
                      pagination={false}
                      rowKey={(step) => step.key}
                      scroll={{ x: 860 }}
                    />
                  </AevatarPanel>
                ),
              },
              {
                key: "yaml",
                label: "YAML",
                children: (
                  <AevatarPanel
                    description="Keep source close by for inspection, but off the main stage so the library stays scannable."
                    title="Definition source"
                  >
                    <pre
                      style={{
                        ...codeBlockStyle,
                        background: "var(--ant-color-fill-quaternary)",
                        border: "1px solid var(--ant-color-border-secondary)",
                        borderRadius: 14,
                        boxShadow: workflowSurfaceShadow,
                        margin: 0,
                        maxHeight: 480,
                        overflow: "auto",
                        padding: 14,
                        whiteSpace: "pre-wrap",
                      }}
                    >
                      {selectedWorkflowDetail.yaml}
                    </pre>
                  </AevatarPanel>
                ),
              },
            ]}
          />
        )}
      </AevatarContextDrawer>
    </AevatarPageShell>
  );
};

export default WorkflowsPage;
