import {
  CodeOutlined,
  EyeOutlined,
  PlayCircleOutlined,
} from "@ant-design/icons";
import { ProList } from "@ant-design/pro-components";
import { useQuery } from "@tanstack/react-query";
import { Alert, Button, Empty, Input, List, Select, Space, Typography } from "antd";
import React, { useEffect, useMemo, useState } from "react";
import { runtimeCatalogApi } from "@/shared/api/runtimeCatalogApi";
import { history } from "@/shared/navigation/history";
import {
  buildRuntimeRunsHref,
  buildRuntimeWorkflowsHref,
} from "@/shared/navigation/runtimeRoutes";
import { buildStudioWorkflowEditorRoute } from "@/shared/studio/navigation";
import type { WorkflowCatalogItem } from "@/shared/models/runtime/catalog";
import {
  AevatarContextDrawer,
  AevatarInspectorEmpty,
  AevatarPageShell,
  AevatarPanel,
  AevatarStatusTag,
  AevatarWorkbenchLayout,
} from "@/shared/ui/aevatarPageShells";
import {
  cardListActionStyle,
  summaryFieldLabelStyle,
  summaryMetricStyle,
  summaryMetricValueStyle,
} from "@/shared/ui/proComponents";
import { listVisibleWorkflowCatalogItems } from "@/shared/workflows/catalogVisibility";

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
    ? "Requires an LLM provider before the runtime handoff can execute."
    : "Closed-world definition ready for inspection and direct run handoff.";
}

const WorkflowSummaryMetric: React.FC<{
  label: string;
  value: React.ReactNode;
}> = ({ label, value }) => (
  <div style={summaryMetricStyle}>
    <Typography.Text style={summaryFieldLabelStyle}>{label}</Typography.Text>
    <Typography.Text style={summaryMetricValueStyle}>{value}</Typography.Text>
  </div>
);

const WorkflowCatalogCard: React.FC<{
  workflow: WorkflowCatalogItem;
  onInspect: () => void;
  onRun: () => void;
}> = ({ workflow, onInspect, onRun }) => {
  const summary = buildWorkflowSummary(workflow);

  return (
    <div
      aria-label={`Inspect workflow ${workflow.name}`}
      onClick={onInspect}
      onKeyDown={(event) => {
        if (event.key === "Enter" || event.key === " ") {
          event.preventDefault();
          onInspect();
        }
      }}
      role="button"
      style={{
        background: "var(--ant-color-bg-container)",
        border: "1px solid var(--ant-color-border-secondary)",
        borderRadius: 14,
        boxShadow: "0 12px 28px rgba(15, 23, 42, 0.06)",
        cursor: "pointer",
        display: "flex",
        flexDirection: "column",
        gap: 16,
        padding: 18,
        width: "100%",
      }}
      tabIndex={0}
    >
      <div
        style={{
          alignItems: "flex-start",
          display: "flex",
          flexWrap: "wrap",
          gap: 8,
          justifyContent: "space-between",
        }}
      >
        <Space wrap size={[8, 8]}>
          <AevatarStatusTag
            domain="governance"
            status={workflow.requiresLlmProvider ? "active" : "ready"}
            label={workflow.requiresLlmProvider ? "LLM required" : "Closed-world ready"}
          />
          {workflow.isPrimitiveExample ? (
            <AevatarStatusTag
              domain="observation"
              label="Primitive example"
              status="snapshot_available"
            />
          ) : null}
        </Space>
        <Typography.Text style={{ color: "var(--ant-color-text-tertiary)" }}>
          {workflow.category}
        </Typography.Text>
      </div>

      <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
        <Typography.Text strong style={{ fontSize: 16, lineHeight: 1.4 }}>
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

      <div
        style={{
          display: "grid",
          gap: 10,
          gridTemplateColumns: "repeat(auto-fit, minmax(140px, 1fr))",
          width: "100%",
        }}
      >
        <WorkflowSummaryMetric label="Group" value={workflow.groupLabel} />
        <WorkflowSummaryMetric label="Source" value={workflow.sourceLabel} />
        <WorkflowSummaryMetric
          label="Primitives"
          value={`${workflow.primitives.length} linked`}
        />
      </div>

      <div style={cardListActionStyle}>
        <Button
          icon={<EyeOutlined />}
          onClick={(event) => {
            event.stopPropagation();
            onInspect();
          }}
        >
          Inspect
        </Button>
        <Button
          icon={<PlayCircleOutlined />}
          onClick={(event) => {
            event.stopPropagation();
            onRun();
          }}
          type="primary"
        >
          Run
        </Button>
      </div>
    </div>
  );
};

const WorkflowsPage: React.FC = () => {
  const [keyword, setKeyword] = useState("");
  const [selectedGroups, setSelectedGroups] = useState<string[]>([]);
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

  const visibleItems = useMemo(
    () => listVisibleWorkflowCatalogItems(catalogQuery.data ?? []),
    [catalogQuery.data],
  );

  useEffect(() => {
    history.replace(buildWorkflowHref(selectedWorkflow));
  }, [selectedWorkflow]);

  const groupOptions = useMemo(
    () =>
      Array.from(new Set(visibleItems.map((item) => item.groupLabel)))
        .sort((left, right) => left.localeCompare(right))
        .map((group) => ({
          label: group,
          value: group,
        })),
    [visibleItems],
  );

  const filteredItems = useMemo(() => {
    const normalizedKeyword = keyword.trim().toLowerCase();

    return visibleItems.filter((item) => {
      if (
        selectedGroups.length > 0 &&
        !selectedGroups.includes(item.groupLabel)
      ) {
        return false;
      }

      if (!normalizedKeyword) {
        return true;
      }

      return [item.name, item.groupLabel, item.description, item.primitives.join(" ")]
        .join(" ")
        .toLowerCase()
        .includes(normalizedKeyword);
    });
  }, [keyword, selectedGroups, visibleItems]);

  const selectedWorkflowDetail = selectedWorkflowQuery.data;

  return (
    <AevatarPageShell
      content="Workflow definitions now live in a card-flow library. Definition detail, role topology, and YAML inspection sit in a drawer so the library stage stays focused."
      title="Workflow Library"
    >
      <AevatarWorkbenchLayout
        rail={
          <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
            <AevatarPanel
              description="Search and narrow the runtime library without losing the center stage."
              title="Filter Library"
            >
              <div
                style={{
                  display: "flex",
                  flexDirection: "column",
                  gap: 12,
                  width: "100%",
                }}
              >
                <Input
                  onChange={(event) => setKeyword(event.target.value)}
                  placeholder="Search workflow, group, or primitive"
                  style={{ width: "100%" }}
                  value={keyword}
                />
                <Select
                  mode="multiple"
                  onChange={setSelectedGroups}
                  options={groupOptions}
                  placeholder="Filter groups"
                  style={{ width: "100%" }}
                  value={selectedGroups}
                />
                <Button
                  onClick={() => {
                    setKeyword("");
                    setSelectedGroups([]);
                  }}
                >
                  Reset filters
                </Button>
              </div>
            </AevatarPanel>

            <AevatarPanel title="Library Digest">
              <Space direction="vertical" size={6}>
                <Typography.Text strong>
                  {filteredItems.length} workflows in view
                </Typography.Text>
                <Typography.Text type="secondary">
                  {groupOptions.length} groups ·{" "}
                  {filteredItems.filter((item) => item.requiresLlmProvider).length}{" "}
                  require LLM
                </Typography.Text>
              </Space>
            </AevatarPanel>
          </div>
        }
        stage={
          <AevatarPanel
            description="The library remains scannable because the expensive definition detail now opens contextually."
            title="Workflow Catalog"
          >
            {catalogQuery.error ? (
              <Alert
                title={
                  catalogQuery.error instanceof Error
                    ? catalogQuery.error.message
                    : "Failed to load workflow catalog."
                }
                showIcon
                type="error"
              />
            ) : null}

            <ProList<WorkflowCatalogItem>
              dataSource={filteredItems}
              grid={{ gutter: 16, column: 1 }}
              locale={{
                emptyText: (
                  <Empty
                    description="No workflows matched the current filter."
                    image={Empty.PRESENTED_IMAGE_SIMPLE}
                  />
                ),
              }}
              pagination={{ defaultPageSize: 8, showSizeChanger: false }}
              renderItem={(workflow) => (
                <List.Item style={{ border: "none", padding: 0 }}>
                  <WorkflowCatalogCard
                    onInspect={() => setSelectedWorkflow(workflow.name)}
                    onRun={() =>
                      history.push(
                        buildRuntimeRunsHref({
                          workflow: workflow.name,
                        }),
                      )
                    }
                    workflow={workflow}
                  />
                </List.Item>
              )}
              rowKey="name"
              split={false}
            />
          </AevatarPanel>
        }
      />

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
              >
                Run workflow
              </Button>
            </Space>
          ) : null
        }
        onClose={() => setSelectedWorkflow("")}
        open={Boolean(selectedWorkflow)}
        subtitle="Definition inspector"
        title={selectedWorkflowDetail?.catalog.name || selectedWorkflow || "Workflow"}
      >
        {!selectedWorkflowDetail ? (
          <AevatarInspectorEmpty description="Choose a workflow to inspect its role topology, step count, and source definition." />
        ) : (
          <>
            <AevatarPanel
              description="Definition-level signals the operator needs before opening the workflow editor or Runs."
              title="Definition Summary"
            >
              <Space direction="vertical" size={8}>
                <Space wrap size={[8, 8]}>
                  <AevatarStatusTag
                    domain="governance"
                    label={
                      selectedWorkflowDetail.catalog.requiresLlmProvider
                        ? "LLM required"
                        : "Closed-world ready"
                    }
                    status={
                      selectedWorkflowDetail.catalog.requiresLlmProvider
                        ? "active"
                        : "ready"
                    }
                  />
                  <Typography.Text type="secondary">
                    {selectedWorkflowDetail.catalog.groupLabel}
                  </Typography.Text>
                </Space>
                <Typography.Text>
                  {selectedWorkflowDetail.catalog.description || "No description provided."}
                </Typography.Text>
                <Typography.Text type="secondary">
                  Source: {selectedWorkflowDetail.catalog.sourceLabel} · Roles{" "}
                  {selectedWorkflowDetail.definition.roles.length} · Steps{" "}
                  {selectedWorkflowDetail.definition.steps.length}
                </Typography.Text>
              </Space>
            </AevatarPanel>

            <AevatarPanel
              description="Roles and step topology are condensed here so you can assess complexity without leaving the library."
              title="Topology Signals"
            >
              <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
                {selectedWorkflowDetail.definition.roles.map((role) => (
                  <div
                    key={role.id}
                    style={{
                      border: "1px solid var(--ant-color-border-secondary)",
                      borderRadius: 12,
                      display: "flex",
                      flexDirection: "column",
                      gap: 6,
                      padding: 12,
                    }}
                  >
                    <Space wrap size={[8, 8]}>
                      <Typography.Text strong>{role.name || role.id}</Typography.Text>
                      <Typography.Text type="secondary">
                        {role.provider || "No provider"}
                      </Typography.Text>
                    </Space>
                    <Typography.Text type="secondary">
                      Connectors: {role.connectors.join(", ") || "None"}
                    </Typography.Text>
                  </div>
                ))}
              </div>
            </AevatarPanel>

            <AevatarPanel
              description="YAML stays available, but only when you intentionally pull it into view."
              title="Definition Source"
            >
              <pre
                style={{
                  background: "var(--ant-color-fill-quaternary)",
                  border: "1px solid var(--ant-color-border-secondary)",
                  borderRadius: 12,
                  margin: 0,
                  maxHeight: 320,
                  overflow: "auto",
                  padding: 12,
                  whiteSpace: "pre-wrap",
                }}
              >
                {selectedWorkflowDetail.yaml}
              </pre>
            </AevatarPanel>
          </>
        )}
      </AevatarContextDrawer>
    </AevatarPageShell>
  );
};

export default WorkflowsPage;
