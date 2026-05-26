import { BuildOutlined, EyeOutlined } from "@ant-design/icons";
import { useQuery } from "@tanstack/react-query";
import { Button, Empty, Input, Pagination, Select, Space, Typography } from "antd";
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
  cardListStyle,
  summaryFieldLabelStyle,
  summaryMetricStyle,
  summaryMetricValueStyle,
} from "@/shared/ui/proComponents";

const primitiveCatalogPageSize = 8;

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
    ? `别名：${primitive.aliases.join(", ")}`
    : "已就绪，可继续查看参数契约和示例行为定义。";
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
      aria-label={`查看连接器 ${primitive.name}`}
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
        <PrimitiveSummaryMetric label="分类" value={primitive.category} />
        <PrimitiveSummaryMetric
          label="参数"
          value={`${primitive.parameters.length} 个`}
        />
        <PrimitiveSummaryMetric
          label="示例"
          value={`${primitive.exampleWorkflows.length} 个`}
        />
      </div>

      <div style={cardListActionStyle}>
        <Button
          aria-label="查看"
          icon={<EyeOutlined />}
          onClick={(event) => {
            event.stopPropagation();
            onInspect();
          }}
        >
          查看
        </Button>
        <Button
          aria-label="示例行为定义"
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
          示例行为定义
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
  const [currentPage, setCurrentPage] = useState(1);

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

  const pagedRows = useMemo(() => {
    const startIndex = (currentPage - 1) * primitiveCatalogPageSize;
    return filteredRows.slice(startIndex, startIndex + primitiveCatalogPageSize);
  }, [currentPage, filteredRows]);

  const selectedPrimitive =
    filteredRows.find((item) => item.name === selectedPrimitiveName) ??
    primitiveRows.find((item) => item.name === selectedPrimitiveName) ??
    null;

  useEffect(() => {
    history.replace(buildPrimitivesHref(selectedPrimitiveName));
  }, [selectedPrimitiveName]);

  return (
    <AevatarPageShell
      layoutMode="document"
      title="连接器目录"
      titleHelp="这里继续复用 runtime primitive 数据，但对外展示成团队可复用的连接器能力目录。"
    >
      <AevatarWorkbenchLayout
        layoutMode="document"
        rail={
          <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
            <AevatarPanel
              layoutMode="document"
              title="筛选连接器"
              titleHelp="按分类或关键字过滤连接器能力，不离开当前视口。"
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
                  placeholder="搜索连接器、分类或别名"
                  style={{ width: "100%" }}
                  value={keyword}
                />
                <Select
                  mode="multiple"
                  onChange={setSelectedCategories}
                  options={categoryOptions}
                  placeholder="筛选分类"
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
                  重置筛选
                </Button>
              </div>
            </AevatarPanel>

            <AevatarPanel layoutMode="document" title="目录摘要">
              <Space orientation="vertical" size={6}>
                <Typography.Text strong>
                  {filteredRows.length} 个连接器能力
                </Typography.Text>
                <Typography.Text type="secondary">
                  {categoryOptions.length} 个分类 ·{" "}
                  {filteredRows.reduce(
                    (count, primitive) => count + primitive.parameters.length,
                    0,
                  )}{" "}
                  个已暴露参数
                </Typography.Text>
              </Space>
            </AevatarPanel>
          </div>
        }
        stage={
          <AevatarPanel
            layoutMode="document"
            title="可用连接器"
            titleHelp="卡片流目录帮助你快速浏览能力分类、参数契约和示例行为定义。"
          >
            {filteredRows.length === 0 ? (
              <Empty
                description="当前筛选条件下没有匹配的连接器。"
                image={Empty.PRESENTED_IMAGE_SIMPLE}
              />
            ) : (
              <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
                <div style={cardListStyle}>
                  {pagedRows.map((primitive) => (
                    <PrimitiveCatalogCard
                      key={primitive.name}
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
                  ))}
                </div>
                <Pagination
                  align="end"
                  current={currentPage}
                  onChange={setCurrentPage}
                  pageSize={primitiveCatalogPageSize}
                  showSizeChanger={false}
                  total={filteredRows.length}
                />
              </div>
            )}
          </AevatarPanel>
        }
      />

      <AevatarContextDrawer
        onClose={() => setSelectedPrimitiveName("")}
        open={Boolean(selectedPrimitiveName)}
        subtitle="连接器契约"
        title={selectedPrimitive?.name || selectedPrimitiveName || "连接器"}
      >
        {!selectedPrimitive ? (
          <AevatarInspectorEmpty description="选择一个连接器以查看它的参数契约和示例行为定义。" />
        ) : (
          <>
            <AevatarPanel
              title="定义"
              titleHelp="连接器描述和别名保持精简，方便快速决策。"
            >
              <Space orientation="vertical" size={8}>
                <Space wrap size={[8, 8]}>
                  <AevatarStatusTag domain="governance" status="ready" />
                  <Typography.Text type="secondary">
                    {selectedPrimitive.category}
                  </Typography.Text>
                </Space>
                <Typography.Text>
                  {selectedPrimitive.description || "当前连接器还没有描述。"}
                </Typography.Text>
                <Typography.Text type="secondary">
                  别名：
                  {selectedPrimitive.aliases.length > 0
                    ? selectedPrimitive.aliases.join(", ")
                    : "无"}
                </Typography.Text>
              </Space>
            </AevatarPanel>

            <AevatarPanel
              title="参数"
              titleHelp="参数契约收进右侧抽屉，保持主目录轻量。"
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
                          label={parameter.required ? "必填" : "可选"}
                          status={parameter.required ? "ready" : "draft"}
                        />
                        <Typography.Text type="secondary">
                          {parameter.type}
                        </Typography.Text>
                      </Space>
                      <Typography.Text type="secondary">
                        {parameter.description || "当前参数还没有描述。"}
                      </Typography.Text>
                      <Typography.Text type="secondary">
                        默认值：{parameter.default || "n/a"}
                      </Typography.Text>
                    </div>
                  ))}
                </div>
              ) : (
                <Empty
                  description="当前连接器没有声明参数。"
                  image={Empty.PRESENTED_IMAGE_SIMPLE}
                />
              )}
            </AevatarPanel>

            <AevatarPanel
              title="示例覆盖"
              titleHelp="示例行为定义会把连接器目录和行为设计串起来。"
            >
              {selectedPrimitive.exampleWorkflows.length > 0 ? (
                <Space orientation="vertical" size={8} style={{ width: "100%" }}>
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
                        打开行为定义
                      </Button>
                    </div>
                  ))}
                </Space>
              ) : (
                <Empty
                  description="当前还没有关联示例行为定义。"
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
