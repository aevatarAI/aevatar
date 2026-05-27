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
    ? "此 Workflow 需要 LLM provider 才能开始运行时移交。"
    : "闭环定义已就绪，可直接运行。";
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

function formatWorkflowLoadError(error: unknown): string {
  if (error instanceof Error) {
    return error.message.includes("streamBufferCapacity")
      ? "Workflow 详情使用了旧版运行时字段，当前前端已按空值处理；请刷新后重试。"
      : error.message;
  }

  return "加载 Workflow 详情失败。";
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
      label={workflow.requiresLlmProvider ? "需要 LLM" : "闭环就绪"}
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
        title: "集合",
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
        title: "运行适配",
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
              {formatListPreview(workflow.primitives, "暂无 primitive 数据")}
            </Typography.Text>
          </div>
        ),
      },
      {
        title: "操作",
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
              查看
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
              运行
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
        title: "角色",
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
            <Typography.Text>{role.provider || "无 provider"}</Typography.Text>
            <Typography.Text type="secondary">{role.model || "无 model"}</Typography.Text>
          </div>
        ),
      },
      {
        title: "连接器",
        key: "connectors",
        width: "24%",
        render: (_value, role) => (
          <Typography.Text type="secondary">
            {formatListPreview(role.connectors, "无连接器")}
          </Typography.Text>
        ),
      },
      {
        title: "运行限制",
        key: "limits",
        render: (_value, role) => (
          <Typography.Text type="secondary">
            {`工具轮次 ${role.maxToolRounds ?? "n/a"} · 历史消息 ${
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
        title: "步骤",
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
        title: "类型",
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
        title: "目标角色",
        dataIndex: "targetRole",
        key: "targetRole",
        width: "16%",
        render: (value: string) => (
          <Typography.Text>{value || "n/a"}</Typography.Text>
        ),
      },
      {
        title: "流程",
        key: "flow",
        width: "24%",
        render: (_value, step) => (
          <Typography.Text type="secondary">
            {step.next
              ? `下一步：${step.next}`
              : step.branchCount > 0
                ? `${step.branchCount} 条分支路由`
                : "没有显式下一步"}
          </Typography.Text>
        ),
      },
      {
        title: "参数",
        key: "parameters",
        render: (_value, step) => (
          <Typography.Text type="secondary">
            {step.parameterCount} 个参数 · {step.childCount} 个子步骤
          </Typography.Text>
        ),
      },
    ],
    [],
  );

  return (
    <AevatarPageShell
      layoutMode="document"
      title="Workflow 库"
      titleHelp="浏览运行时暴露的 Workflow 定义，查看连接方式，再从同一个目录进入运行或编辑。"
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
          <WorkflowSummaryMetric label="库内 Workflow" value={metrics.workflows} />
          <WorkflowSummaryMetric label="分组" value={metrics.groups} />
          <WorkflowSummaryMetric label="需要 LLM" value={metrics.llmRequired} />
          <WorkflowSummaryMetric label="你的 Workflow" value={metrics.yourWorkflows} />
        </div>

        <AevatarPanel
          description="运行时目录已加载。筛选会立即生效，方便你专注选择可运行的定义。"
          extra={
            <Button
              onClick={() => setFilters(defaultWorkflowLibraryFilter)}
              type="default"
            >
              清除筛选
            </Button>
          }
          title="查找 Workflow"
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
              placeholder="搜索 Workflow、描述、分组或 primitive"
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
              placeholder="分组"
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
              placeholder="来源"
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
                { label: "全部 Workflow", value: "all" },
                { label: "需要 LLM", value: "required" },
                { label: "闭环就绪", value: "optional" },
              ]}
              value={filters.llmRequirement}
            />
          </div>
        </AevatarPanel>

        <AevatarPanel
          description="这是运行时目录，不是草稿工作区。先在这里选择定义，再查看、运行或打开编辑器。"
          title="Workflow 目录"
        >
          {catalogQuery.isLoading ? (
            <Typography.Text type="secondary">正在加载 Workflow 目录...</Typography.Text>
          ) : filteredRows.length === 0 ? (
            <Empty
              description="没有 Workflow 匹配当前筛选。"
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
                打开 Workflow 编辑器
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
                运行 Workflow
              </Button>
            </Space>
          ) : null
        }
        onClose={() => setSelectedWorkflow("")}
        open={Boolean(selectedWorkflow)}
        subtitle="运行时 Workflow 详情"
        title={selectedWorkflowDetail?.catalog.name || selectedWorkflow || "Workflow"}
        width={920}
      >
        {!selectedWorkflow ? null : selectedWorkflowQuery.isLoading ? (
          <Typography.Text type="secondary">正在加载 Workflow 详情...</Typography.Text>
        ) : selectedWorkflowQuery.error ? (
          <Alert
            showIcon
            title={
              selectedWorkflowQuery.error instanceof Error
                ? formatWorkflowLoadError(selectedWorkflowQuery.error)
                : "加载 Workflow 详情失败。"
            }
            type="error"
          />
        ) : !selectedWorkflowDetail ? (
          <AevatarInspectorEmpty description="选择一个 Workflow 查看运行时连线、角色模型和源 YAML。" />
        ) : (
          <Tabs
            defaultActiveKey="overview"
            items={[
              {
                key: "overview",
                label: "概览",
                children: (
                  <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
                    <AevatarPanel
                      description="运行或编辑定义前，先确认运行适配、角色数量、步骤数量和连接器。"
                      title="定义摘要"
                    >
                      <div
                        style={{
                          display: "grid",
                          gap: 14,
                          gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
                        }}
                      >
                        <WorkflowField
                          label="集合"
                          value={selectedWorkflowDetail.catalog.groupLabel}
                        />
                        <WorkflowField
                          label="来源"
                          value={selectedWorkflowDetail.catalog.sourceLabel}
                        />
                        <WorkflowField
                          label="闭环模式"
                          value={
                            selectedWorkflowDetail.definition.closedWorldMode
                              ? "已启用"
                              : "已禁用"
                          }
                        />
                        <WorkflowField
                          label="需要 LLM provider"
                          value={
                            selectedWorkflowDetail.catalog.requiresLlmProvider
                              ? "是"
                              : "否"
                          }
                        />
                        <WorkflowField
                          label="角色"
                          value={selectedWorkflowDetail.definition.roles.length}
                        />
                        <WorkflowField
                          label="步骤"
                          value={selectedWorkflowDetail.definition.steps.length}
                        />
                        <WorkflowField
                          label="拓扑边"
                          value={selectedWorkflowDetail.edges.length}
                        />
                        <WorkflowField
                          label="连接器"
                          value={formatListPreview(connectorSummary, "无连接器")}
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
                          描述
                        </Typography.Text>
                        <Typography.Text>
                          {selectedWorkflowDetail.catalog.description ||
                            "暂无描述。"}
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
                label: `角色 (${selectedWorkflowDetail.definition.roles.length})`,
                children: (
                  <AevatarPanel
                    description="这些是 Workflow 定义声明的运行时角色，包括 provider/model 提示和已挂载连接器。"
                    title="角色模型"
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
                label: `步骤 (${stepRows.length})`,
                children: (
                  <AevatarPanel
                    description="打开完整编辑器前，可先在这里理解执行路径。"
                    title="执行步骤"
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
                    description="源码保留在独立视图里，方便检查，同时不打断目录浏览。"
                    title="定义源码"
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
