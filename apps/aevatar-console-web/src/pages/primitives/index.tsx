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
  type AevatarBreadcrumbItem,
  AevatarInspectorEmpty,
  AevatarPageShell,
  AevatarPanel,
  AevatarStatusTag,
  AevatarWorkbenchLayout,
} from "@/shared/ui/aevatarPageShells";
import AevatarContentSkeleton from "@/shared/ui/AevatarContentSkeleton";
import AevatarTooltip from "@/shared/ui/AevatarTooltip";
import {
  cardListActionStyle,
  cardListStyle,
  summaryFieldLabelStyle,
  summaryMetricStyle,
  summaryMetricValueStyle,
} from "@/shared/ui/proComponents";
import { t } from "@/shared/i18n/messages";

const primitiveCatalogPageSize = 8;
const breadcrumbItems: AevatarBreadcrumbItem[] = [
  {
    title: "Platform",
  },
  {
    current: true,
    title: "Connectors",
  },
];

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
    ? t("pages.primitives.index.alias", "Alias: {value1}", { value1: primitive.aliases.join(", ") })
    : t("pages.primitives.index.you.re.ready.to", "You're ready to continue looking at the parameter contracts and sample behavior definitions.");
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
      aria-label={t("pages.primitives.index.view.connector", "View connector {value1}", { value1: primitive.name })}
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
          {primitive.aliases.length} {t("pages.primitives.index.aliases", "aliases")}</Typography.Text>
      </div>

      <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
        <Typography.Text strong style={{ fontSize: 16, lineHeight: 1.4 }}>
          {primitive.name}
        </Typography.Text>
        <AevatarTooltip title={summary}>
          <Typography.Paragraph
            ellipsis={{ rows: 2 }}
            style={{
              color: "var(--ant-color-text-secondary)",
              margin: 0,
            }}
          >
            {summary}
          </Typography.Paragraph>
        </AevatarTooltip>
      </div>

      <div
        style={{
          display: "grid",
          gap: 10,
          gridTemplateColumns: "repeat(auto-fit, minmax(140px, 1fr))",
          width: "100%",
        }}
      >
        <PrimitiveSummaryMetric label={t("pages.primitives.index.classification", "Classification")} value={primitive.category} />
        <PrimitiveSummaryMetric
          label={t("pages.primitives.index.parameter", "parameter")}
          value={t("pages.primitives.index.copy", "{value1}", { value1: primitive.parameters.length })}
        />
        <PrimitiveSummaryMetric
          label={t("pages.primitives.index.example", "Example")}
          value={t("pages.primitives.index.copy.2", "{value1}", { value1: primitive.exampleWorkflows.length })}
        />
      </div>

      <div style={cardListActionStyle}>
        <Button
          aria-label={t("pages.primitives.index.check", "Check")}
          icon={<EyeOutlined />}
          onClick={(event) => {
            event.stopPropagation();
            onInspect();
          }}
        >
          {t("pages.primitives.index.check.2", "Check")}</Button>
        <Button
          aria-label={t("pages.primitives.index.example.behavior.definition", "Example behavior definition")}
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
          {t("pages.primitives.index.example.behavior.definition.2", "Example behavior definition")}</Button>
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
      breadcrumbItems={breadcrumbItems}
      layoutMode="document"
      title={t("pages.primitives.index.connector.catalog", "Connector catalog")}
      titleHelp={t("pages.primitives.index.the.runtime.primitive.data", "The runtime primitive data continues to be reused here, but is displayed externally as a connector capability directory that can be reused by the team.")}
    >
      <AevatarWorkbenchLayout
        layoutMode="document"
        rail={
          <div style={{ display: "flex", flexDirection: "column", gap: 16 }}>
            <AevatarPanel
              layoutMode="document"
              title={t("pages.primitives.index.filter.connector", "filter connector")}
              titleHelp={t("pages.primitives.index.ability.to.filter.connectors", "Ability to filter connectors by category or keyword without leaving the current viewport.")}
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
                  placeholder={t("pages.primitives.index.search.for.connectors.categories", "Search for connectors, categories or aliases")}
                  style={{ width: "100%" }}
                  value={keyword}
                />
                <Select
                  mode="multiple"
                  onChange={setSelectedCategories}
                  options={categoryOptions}
                  placeholder={t("pages.primitives.index.filter.categories", "Filter categories")}
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
                  {t("pages.primitives.index.reset.filter", "Reset filter")}</Button>
              </div>
            </AevatarPanel>

            <AevatarPanel layoutMode="document" title={t("pages.primitives.index.table.of.contents.summary", "Table of Contents Summary")}>
              <Space orientation="vertical" size={6}>
                <Typography.Text strong>
                  {filteredRows.length} {t("pages.primitives.index.connector.capabilities", "connector capabilities")}</Typography.Text>
                <Typography.Text type="secondary">
                  {categoryOptions.length} {t("pages.primitives.index.categories", "categories ·")}{" "}
                  {filteredRows.reduce(
                    (count, primitive) => count + primitive.parameters.length,
                    0,
                  )}{" "}
                  {t("pages.primitives.index.exposed.parameters", "exposed parameters")}</Typography.Text>
              </Space>
            </AevatarPanel>
          </div>
        }
        stage={
          <AevatarPanel
            layoutMode="document"
            title={t("pages.primitives.index.available.connectors", "Available connectors")}
            titleHelp={t("pages.primitives.index.the.card.flow.catalog", "The card flow catalog helps you quickly browse capability categories, parameter contracts, and sample behavior definitions.")}
          >
            {primitivesQuery.isLoading ? (
              <AevatarContentSkeleton
                ariaLabel={t("pages.primitives.index.loading.connectors", "Loading connectors")}
                listLayout="grid"
                rows={4}
                variant="list"
              />
            ) : filteredRows.length === 0 ? (
              <Empty
                description={t("pages.primitives.index.there.are.no.matching", "There are no matching connectors under the current filter criteria.")}
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
        subtitle={t("pages.primitives.index.connector.contract", "Connector contract")}
        title={selectedPrimitive?.name || selectedPrimitiveName || t("pages.primitives.index.connector", "connector")}
      >
        {!selectedPrimitive ? (
          <AevatarInspectorEmpty description={t("pages.primitives.index.select.connector.to.view", "Select a connector to view its parameter contracts and sample behavior definitions.")} />
        ) : (
          <>
            <AevatarPanel
              title={t("pages.primitives.index.definition", "definition")}
              titleHelp={t("pages.primitives.index.connector.descriptions.and.aliases", "Connector descriptions and aliases are kept simple to facilitate quick decision-making.")}
            >
              <Space orientation="vertical" size={8}>
                <Space wrap size={[8, 8]}>
                  <AevatarStatusTag domain="governance" status="ready" />
                  <Typography.Text type="secondary">
                    {selectedPrimitive.category}
                  </Typography.Text>
                </Space>
                <Typography.Text>
                  {selectedPrimitive.description || t("pages.primitives.index.the.current.connector.has", "The current connector has no description yet.")}
                </Typography.Text>
                <Typography.Text type="secondary">
                  {t("pages.primitives.index.alias.2", "Alias:")}{selectedPrimitive.aliases.length > 0
                    ? selectedPrimitive.aliases.join(", ")
                    : t("pages.primitives.index.none", "none")}
                </Typography.Text>
              </Space>
            </AevatarPanel>

            <AevatarPanel
              title={t("pages.primitives.index.parameter.2", "parameter")}
              titleHelp={t("pages.primitives.index.the.parameter.contract.is", "The parameter contract is stored in the right drawer to keep the main directory lightweight.")}
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
                          label={parameter.required ? t("pages.primitives.index.required", "Required") : t("pages.primitives.index.optional", "Optional")}
                          status={parameter.required ? "ready" : "draft"}
                        />
                        <Typography.Text type="secondary">
                          {parameter.type}
                        </Typography.Text>
                      </Space>
                      <Typography.Text type="secondary">
                        {parameter.description || t("pages.primitives.index.the.current.parameter.has", "The current parameter has no description yet.")}
                      </Typography.Text>
                      <Typography.Text type="secondary">
                        {t("pages.primitives.index.default.value", "default value:")}{parameter.default || "n/a"}
                      </Typography.Text>
                    </div>
                  ))}
                </div>
              ) : (
                <Empty
                  description={t("pages.primitives.index.the.current.connector.has.2", "The current connector has no parameters declared.")}
                  image={Empty.PRESENTED_IMAGE_SIMPLE}
                />
              )}
            </AevatarPanel>

            <AevatarPanel
              title={t("pages.primitives.index.example.coverage", "Example coverage")}
              titleHelp={t("pages.primitives.index.sample.behavior.definitions.link", "Sample behavior definitions link the connector catalog with the behavior design.")}
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
                        {t("pages.primitives.index.open.behavior.definition", "Open behavior definition")}</Button>
                    </div>
                  ))}
                </Space>
              ) : (
                <Empty
                  description={t("pages.primitives.index.there.is.currently.no", "There is currently no associated sample behavior definition.")}
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
