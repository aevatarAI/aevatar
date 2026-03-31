import { BuildOutlined, EyeOutlined } from "@ant-design/icons";
import { ProList } from "@ant-design/pro-components";
import { useQuery } from "@tanstack/react-query";
import { Button, Empty, Input, List, Select, Space, Typography } from "antd";
import React, { useEffect, useMemo, useState } from "react";
import { runtimeQueryApi } from "@/shared/api/runtimeQueryApi";
import { history } from "@/shared/navigation/history";
import { buildRuntimeWorkflowsHref } from "@/shared/navigation/runtimeRoutes";
import type { WorkflowPrimitiveDescriptor } from "@/shared/models/runtime/query";
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

function readPrimitiveSelection(): string {
  if (typeof window === "undefined") {
    return "";
  }

  return (
    new URLSearchParams(window.location.search).get("primitive")?.trim() ?? ""
  );
}

function buildPrimitivesHref(primitiveName: string): string {
  const params = new URLSearchParams();
  if (primitiveName.trim()) {
    params.set("primitive", primitiveName.trim());
  }
  const query = params.toString();
  return query ? `/runtime/primitives?${query}` : "/runtime/primitives";
}

function buildPrimitiveSummary(primitive: WorkflowPrimitiveDescriptor): string {
  const description = primitive.description.trim();
  if (description) {
    return description;
  }

  return primitive.aliases.length > 0
    ? `Aliases: ${primitive.aliases.join(", ")}`
    : "Ready to inspect parameter contracts and example workflow coverage.";
}

const PrimitiveSummaryMetric: React.FC<{
  label: string;
  value: React.ReactNode;
}> = ({ label, value }) => (
  <div style={summaryMetricStyle}>
    <Typography.Text style={summaryFieldLabelStyle}>{label}</Typography.Text>
    <Typography.Text style={summaryMetricValueStyle}>{value}</Typography.Text>
  </div>
);

const PrimitiveCatalogCard: React.FC<{
  onInspect: () => void;
  onOpenExample: () => void;
  primitive: WorkflowPrimitiveDescriptor;
}> = ({ onInspect, onOpenExample, primitive }) => {
  const summary = buildPrimitiveSummary(primitive);
  const hasExampleWorkflow = primitive.exampleWorkflows.length > 0;

  return (
    <div
      aria-label={`Inspect primitive ${primitive.name}`}
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
          <AevatarStatusTag domain="governance" status="ready" />
          <Typography.Text style={{ color: "var(--ant-color-text-tertiary)" }}>
            {primitive.category}
          </Typography.Text>
        </Space>
        <Typography.Text style={{ color: "var(--ant-color-text-tertiary)" }}>
          {primitive.aliases.length} aliases
        </Typography.Text>
      </div>

      <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
        <Typography.Text strong style={{ fontSize: 16, lineHeight: 1.4 }}>
          {primitive.name}
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
        <PrimitiveSummaryMetric label="Category" value={primitive.category} />
        <PrimitiveSummaryMetric
          label="Parameters"
          value={`${primitive.parameters.length} defined`}
        />
        <PrimitiveSummaryMetric
          label="Examples"
          value={`${primitive.exampleWorkflows.length} linked`}
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
          disabled={!hasExampleWorkflow}
          icon={<BuildOutlined />}
          onClick={(event) => {
            event.stopPropagation();
            if (hasExampleWorkflow) {
              onOpenExample();
            }
          }}
          type="primary"
        >
          Example workflow
        </Button>
      </div>
    </div>
  );
};

const PrimitivesPage: React.FC = () => {
  const [keyword, setKeyword] = useState("");
  const [selectedCategories, setSelectedCategories] = useState<string[]>([]);
  const [selectedPrimitiveName, setSelectedPrimitiveName] = useState(
    readPrimitiveSelection(),
  );

  const primitivesQuery = useQuery({
    queryKey: ["primitive-library"],
    queryFn: () => runtimeQueryApi.listPrimitives(),
  });

  const primitiveRows = primitivesQuery.data ?? [];
  const categoryOptions = useMemo(
    () =>
      Array.from(new Set(primitiveRows.map((item) => item.category)))
        .sort((left, right) => left.localeCompare(right))
        .map((category) => ({
          label: category,
          value: category,
        })),
    [primitiveRows],
  );

  const filteredRows = useMemo(() => {
    const normalizedKeyword = keyword.trim().toLowerCase();

    return primitiveRows.filter((item) => {
      if (
        selectedCategories.length > 0 &&
        !selectedCategories.includes(item.category)
      ) {
        return false;
      }

      if (!normalizedKeyword) {
        return true;
      }

      return [item.name, item.category, item.description, item.aliases.join(" ")]
        .join(" ")
        .toLowerCase()
        .includes(normalizedKeyword);
    });
  }, [keyword, primitiveRows, selectedCategories]);

  const selectedPrimitive =
    filteredRows.find((item) => item.name === selectedPrimitiveName) ??
    primitiveRows.find((item) => item.name === selectedPrimitiveName) ??
    null;

  useEffect(() => {
    history.replace(buildPrimitivesHref(selectedPrimitiveName));
  }, [selectedPrimitiveName]);

  return (
    <AevatarPageShell
      content="Primitive definitions are now managed as a runtime library workbench. The main stage stays dedicated to discovery while parameter contracts and example workflows live in the inspector."
      title="Primitive Library"
    >
      <AevatarWorkbenchLayout
        rail={
          <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
            <AevatarPanel
              description="Filter by category or keyword without leaving the viewport."
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
                  placeholder="Search primitive, category, or alias"
                  style={{ width: "100%" }}
                  value={keyword}
                />
                <Select
                  mode="multiple"
                  onChange={setSelectedCategories}
                  options={categoryOptions}
                  placeholder="Filter categories"
                  style={{ width: "100%" }}
                  value={selectedCategories}
                />
                <Button
                  onClick={() => {
                    setKeyword("");
                    setSelectedCategories([]);
                    setSelectedPrimitiveName("");
                  }}
                >
                  Reset filters
                </Button>
              </div>
            </AevatarPanel>

            <AevatarPanel title="Library Digest">
              <Space direction="vertical" size={6}>
                <Typography.Text strong>
                  {filteredRows.length} primitives in view
                </Typography.Text>
                <Typography.Text type="secondary">
                  {categoryOptions.length} categories ·{" "}
                  {filteredRows.reduce(
                    (count, primitive) => count + primitive.parameters.length,
                    0,
                  )}{" "}
                  parameters surfaced
                </Typography.Text>
              </Space>
            </AevatarPanel>
          </div>
        }
        stage={
          <AevatarPanel
            description="A card-flow library lets you scan categories and examples without collapsing into parameter tables."
            title="Runtime Primitives"
          >
            <ProList<WorkflowPrimitiveDescriptor>
              dataSource={filteredRows}
              grid={{ gutter: 16, column: 1 }}
              locale={{
                emptyText: (
                  <Empty
                    description="No primitives matched the current filter."
                    image={Empty.PRESENTED_IMAGE_SIMPLE}
                  />
                ),
              }}
              pagination={{ defaultPageSize: 8, showSizeChanger: false }}
              renderItem={(primitive) => (
                <List.Item style={{ border: "none", padding: 0 }}>
                  <PrimitiveCatalogCard
                    onInspect={() => setSelectedPrimitiveName(primitive.name)}
                    onOpenExample={() =>
                      history.push(
                        buildRuntimeWorkflowsHref({
                          workflow: primitive.exampleWorkflows[0],
                        }),
                      )
                    }
                    primitive={primitive}
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
        onClose={() => setSelectedPrimitiveName("")}
        open={Boolean(selectedPrimitiveName)}
        subtitle="Primitive contract"
        title={selectedPrimitive?.name || selectedPrimitiveName || "Primitive"}
      >
        {!selectedPrimitive ? (
          <AevatarInspectorEmpty description="Choose a primitive to inspect its parameter contract and example workflow coverage." />
        ) : (
          <>
            <AevatarPanel
              description="Primitive description and aliases remain concise so the drawer stays decision-oriented."
              title="Definition"
            >
              <Space direction="vertical" size={8}>
                <Space wrap size={[8, 8]}>
                  <AevatarStatusTag domain="governance" status="ready" />
                  <Typography.Text type="secondary">
                    {selectedPrimitive.category}
                  </Typography.Text>
                </Space>
                <Typography.Text>
                  {selectedPrimitive.description || "No primitive description."}
                </Typography.Text>
                <Typography.Text type="secondary">
                  Aliases:{" "}
                  {selectedPrimitive.aliases.length > 0
                    ? selectedPrimitive.aliases.join(", ")
                    : "None"}
                </Typography.Text>
              </Space>
            </AevatarPanel>

            <AevatarPanel
              description="Parameter contracts move here so the library stage can stay lightweight."
              title="Parameters"
            >
              {selectedPrimitive.parameters.length > 0 ? (
                <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
                  {selectedPrimitive.parameters.map((parameter) => (
                    <div
                      key={parameter.name}
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
                        <Typography.Text strong>{parameter.name}</Typography.Text>
                        <AevatarStatusTag
                          domain="governance"
                          label={parameter.required ? "Required" : "Optional"}
                          status={parameter.required ? "ready" : "draft"}
                        />
                        <Typography.Text type="secondary">
                          {parameter.type}
                        </Typography.Text>
                      </Space>
                      <Typography.Text type="secondary">
                        {parameter.description || "No parameter description."}
                      </Typography.Text>
                      <Typography.Text type="secondary">
                        Default: {parameter.default || "n/a"}
                      </Typography.Text>
                    </div>
                  ))}
                </div>
              ) : (
                <Empty
                  description="This primitive does not declare parameters."
                  image={Empty.PRESENTED_IMAGE_SIMPLE}
                />
              )}
            </AevatarPanel>

            <AevatarPanel
              description="Examples form the bridge from primitive library to workflow design."
              title="Example Coverage"
            >
              {selectedPrimitive.exampleWorkflows.length > 0 ? (
                <Space direction="vertical" size={8} style={{ width: "100%" }}>
                  {selectedPrimitive.exampleWorkflows.map((workflowName) => (
                    <div
                      key={workflowName}
                      style={{
                        border: "1px solid var(--ant-color-border-secondary)",
                        borderRadius: 12,
                        display: "flex",
                        justifyContent: "space-between",
                        gap: 12,
                        padding: 12,
                      }}
                    >
                      <Typography.Text strong>{workflowName}</Typography.Text>
                      <Button
                        onClick={() =>
                          history.push(
                            buildRuntimeWorkflowsHref({
                              workflow: workflowName,
                            }),
                          )
                        }
                      >
                        Open workflow
                      </Button>
                    </div>
                  ))}
                </Space>
              ) : (
                <Empty
                  description="No example workflows were attached."
                  image={Empty.PRESENTED_IMAGE_SIMPLE}
                />
              )}
            </AevatarPanel>
          </>
        )}
      </AevatarContextDrawer>
    </AevatarPageShell>
  );
};

export default PrimitivesPage;
