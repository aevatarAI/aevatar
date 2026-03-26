import {
  PageContainer,
  ProCard,
  ProTable,
} from "@ant-design/pro-components";
import type { ProColumns } from "@ant-design/pro-components";
import { useQuery } from "@tanstack/react-query";
import { history } from "@/shared/navigation/history";
import {
  buildRuntimeRunsHref,
  buildRuntimeWorkflowsHref,
} from "@/shared/navigation/runtimeRoutes";
import {
  Alert,
  Button,
  Col,
  Empty,
  Input,
  Row,
  Select,
  Space,
  Tag,
  Typography,
} from "antd";
import React, { useEffect, useMemo, useState } from "react";
import { runtimeQueryApi } from "@/shared/api/runtimeQueryApi";
import type {
  WorkflowPrimitiveDescriptor,
  WorkflowPrimitiveParameterDescriptor,
} from "@/shared/models/runtime/query";
import {
  cardListActionStyle,
  cardListHeaderStyle,
  cardListItemStyle,
  cardListMainStyle,
  cardListStyle,
  cardStackStyle,
  compactPanelHeight,
  compactTableCardProps,
  embeddedPanelStyle,
  fillCardStyle,
  moduleCardProps,
  summaryFieldGridStyle,
  summaryFieldLabelStyle,
  summaryFieldStyle,
  summaryMetricGridStyle,
  summaryMetricStyle,
  summaryMetricValueStyle,
  stretchColumnStyle,
} from "@/shared/ui/proComponents";

type PrimitiveLibraryRow = WorkflowPrimitiveDescriptor & {
  key: string;
  aliasSummary: string;
  parameterCount: number;
  exampleWorkflowCount: number;
};

type PrimitiveSummaryRecord = {
  category: string;
  aliasCount: number;
  parameterCount: number;
  exampleWorkflowCount: number;
};

type SummaryFieldProps = {
  label: string;
  value: React.ReactNode;
};

type SummaryMetricProps = {
  label: string;
  value: React.ReactNode;
};

function readInitialPrimitiveSelection(): string {
  if (typeof window === "undefined") {
    return "";
  }

  return (
    new URLSearchParams(window.location.search).get("primitive")?.trim() ?? ""
  );
}

const parameterColumns: ProColumns<WorkflowPrimitiveParameterDescriptor>[] = [
  {
    title: "Name",
    dataIndex: "name",
    width: 180,
  },
  {
    title: "Type",
    dataIndex: "type",
    width: 120,
  },
  {
    title: "Required",
    dataIndex: "required",
    width: 120,
    render: (_, record) => (
      <Tag color={record.required ? "error" : "default"}>
        {record.required ? "Required" : "Optional"}
      </Tag>
    ),
  },
  {
    title: "Default",
    dataIndex: "default",
    width: 180,
    render: (_, record) => record.default || "n/a",
  },
  {
    title: "Enum",
    dataIndex: "enumValues",
    width: 180,
    render: (_, record) =>
      record.enumValues.length > 0 ? record.enumValues.join(", ") : "n/a",
  },
  {
    title: "Description",
    dataIndex: "description",
    ellipsis: true,
  },
];

const SummaryField: React.FC<SummaryFieldProps> = ({ label, value }) => (
  <div style={summaryFieldStyle}>
    <Typography.Text style={summaryFieldLabelStyle}>{label}</Typography.Text>
    {typeof value === "string" || typeof value === "number" ? (
      <Typography.Text>{value}</Typography.Text>
    ) : (
      value
    )}
  </div>
);

const SummaryMetric: React.FC<SummaryMetricProps> = ({ label, value }) => (
  <div style={summaryMetricStyle}>
    <Typography.Text style={summaryFieldLabelStyle}>{label}</Typography.Text>
    <Typography.Text style={summaryMetricValueStyle}>{value}</Typography.Text>
  </div>
);

const PrimitivesPage: React.FC = () => {
  const [keyword, setKeyword] = useState("");
  const [selectedCategories, setSelectedCategories] = useState<string[]>([]);
  const [selectedPrimitiveName, setSelectedPrimitiveName] = useState(
    readInitialPrimitiveSelection()
  );

  const primitivesQuery = useQuery({
    queryKey: ["primitive-library"],
    queryFn: () => runtimeQueryApi.listPrimitives(),
  });

  const primitiveRows = useMemo<PrimitiveLibraryRow[]>(
    () =>
      (primitivesQuery.data ?? []).map((primitive) => ({
        ...primitive,
        key: primitive.name,
        aliasSummary:
          primitive.aliases.length > 0 ? primitive.aliases.join(", ") : "n/a",
        parameterCount: primitive.parameters.length,
        exampleWorkflowCount: primitive.exampleWorkflows.length,
      })),
    [primitivesQuery.data]
  );

  const categoryOptions = useMemo(
    () =>
      Array.from(new Set(primitiveRows.map((item) => item.category)))
        .sort((left, right) => left.localeCompare(right))
        .map((category) => ({ label: category, value: category })),
    [primitiveRows]
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

      return [item.name, item.category, item.description, item.aliasSummary]
        .join(" ")
        .toLowerCase()
        .includes(normalizedKeyword);
    });
  }, [keyword, primitiveRows, selectedCategories]);

  useEffect(() => {
    if (filteredRows.length === 0) {
      setSelectedPrimitiveName("");
      return;
    }

    if (
      !selectedPrimitiveName ||
      !filteredRows.some((item) => item.name === selectedPrimitiveName)
    ) {
      setSelectedPrimitiveName(filteredRows[0].name);
    }
  }, [filteredRows, selectedPrimitiveName]);

  useEffect(() => {
    if (typeof window === "undefined") {
      return;
    }

    const url = new URL(window.location.href);
    if (selectedPrimitiveName) {
      url.searchParams.set("primitive", selectedPrimitiveName);
    } else {
      url.searchParams.delete("primitive");
    }
    window.history.replaceState(null, "", `${url.pathname}${url.search}`);
  }, [selectedPrimitiveName]);

  const selectedPrimitive = useMemo(
    () => primitiveRows.find((item) => item.name === selectedPrimitiveName),
    [primitiveRows, selectedPrimitiveName]
  );

  const summaryRecord = useMemo<PrimitiveSummaryRecord | undefined>(() => {
    if (!selectedPrimitive) {
      return undefined;
    }

    return {
      category: selectedPrimitive.category,
      aliasCount: selectedPrimitive.aliases.length,
      parameterCount: selectedPrimitive.parameters.length,
      exampleWorkflowCount: selectedPrimitive.exampleWorkflows.length,
    };
  }, [selectedPrimitive]);

  const libraryColumns = useMemo<ProColumns<PrimitiveLibraryRow>[]>(
    () => [
      {
        title: "Primitive",
        dataIndex: "name",
        width: 180,
      },
      {
        title: "Category",
        dataIndex: "category",
        width: 140,
      },
      {
        title: "Aliases",
        dataIndex: "aliasSummary",
        ellipsis: true,
      },
      {
        title: "Params",
        dataIndex: "parameterCount",
        width: 100,
        valueType: "digit",
      },
      {
        title: "Examples",
        dataIndex: "exampleWorkflowCount",
        width: 100,
        valueType: "digit",
      },
    ],
    []
  );

  return (
    <PageContainer
      title="Runtime Primitives"
      content="Browse the backend-authored runtime primitive view, including normalized parameters and example workflow references."
    >
      <Row gutter={[16, 16]} align="stretch">
        <Col xs={24} xl={10} style={stretchColumnStyle}>
          <ProCard
            title="Primitive library"
            {...moduleCardProps}
            style={fillCardStyle}
          >
            <div style={cardStackStyle}>
              <div
                style={{
                  ...embeddedPanelStyle,
                  background: "var(--ant-color-fill-quaternary)",
                }}
              >
                <Typography.Text strong>Runtime-backed browser</Typography.Text>
                <Typography.Paragraph
                  style={{ margin: "8px 0 0" }}
                  type="secondary"
                >
                  This page is powered by the formal `/api/primitives` view
                  instead of stitching capability and catalog data in the
                  browser.
                </Typography.Paragraph>
              </div>

              <Input.Search
                allowClear
                placeholder="Filter by name, alias, category, or description"
                value={keyword}
                onChange={(event) => setKeyword(event.target.value)}
              />

              <Select
                mode="multiple"
                allowClear
                value={selectedCategories}
                options={categoryOptions}
                style={{ width: "100%" }}
                placeholder="Filter categories"
                onChange={(value) => setSelectedCategories(value)}
              />

              <ProTable<PrimitiveLibraryRow>
                rowKey="key"
                search={false}
                options={false}
                pagination={{ pageSize: 8, showSizeChanger: false }}
                columns={libraryColumns}
                dataSource={filteredRows}
                loading={primitivesQuery.isLoading}
                cardProps={compactTableCardProps}
                scroll={{ x: 860, y: compactPanelHeight }}
                onRow={(record) => ({
                  onClick: () => setSelectedPrimitiveName(record.name),
                })}
                rowClassName={(record) =>
                  record.name === selectedPrimitiveName
                    ? "ant-table-row-selected"
                    : ""
                }
                locale={{
                  emptyText: (
                    <Empty
                      image={Empty.PRESENTED_IMAGE_SIMPLE}
                      description="No primitives match the current filters."
                    />
                  ),
                }}
              />
            </div>
          </ProCard>
        </Col>

        <Col xs={24} xl={14} style={stretchColumnStyle}>
          <ProCard
            title={
              selectedPrimitive
                ? `Primitive detail · ${selectedPrimitive.name}`
                : "Primitive detail"
            }
            {...moduleCardProps}
            style={fillCardStyle}
          >
            {primitivesQuery.isError ? (
              <Alert
                showIcon
                type="error"
                title="Failed to load primitive library"
                description={String(primitivesQuery.error)}
              />
            ) : !selectedPrimitive ? (
              <Empty
                image={Empty.PRESENTED_IMAGE_SIMPLE}
                description="Select a primitive to inspect its details."
              />
            ) : (
              <div style={cardStackStyle}>
                <Typography.Paragraph style={{ marginBottom: 0 }}>
                  {selectedPrimitive.description}
                </Typography.Paragraph>

                <Space wrap size={[8, 8]}>
                  <Tag color="processing">{selectedPrimitive.category}</Tag>
                  {selectedPrimitive.aliases.map((alias) => (
                    <Tag key={alias}>{alias}</Tag>
                  ))}
                </Space>

                {summaryRecord ? (
                  <div style={cardStackStyle}>
                    <div style={summaryFieldGridStyle}>
                      <SummaryField
                        label="Category"
                        value={summaryRecord.category}
                      />
                    </div>
                    <div style={summaryMetricGridStyle}>
                      <SummaryMetric
                        label="Aliases"
                        value={summaryRecord.aliasCount}
                      />
                      <SummaryMetric
                        label="Parameters"
                        value={summaryRecord.parameterCount}
                      />
                      <SummaryMetric
                        label="Example workflows"
                        value={summaryRecord.exampleWorkflowCount}
                      />
                    </div>
                  </div>
                ) : null}

                <div style={embeddedPanelStyle}>
                  <Typography.Text strong>Parameters</Typography.Text>
                  <div style={{ marginTop: 12 }}>
                    <ProTable<WorkflowPrimitiveParameterDescriptor>
                      rowKey="name"
                      search={false}
                      options={false}
                      pagination={false}
                      columns={parameterColumns}
                      dataSource={selectedPrimitive.parameters}
                      cardProps={compactTableCardProps}
                      scroll={{ x: 820 }}
                      locale={{
                        emptyText: (
                          <Empty
                            image={Empty.PRESENTED_IMAGE_SIMPLE}
                            description="This primitive does not declare parameters."
                          />
                        ),
                      }}
                    />
                  </div>
                </div>

                <div style={embeddedPanelStyle}>
                  <Typography.Text strong>Example workflows</Typography.Text>
                  <div style={{ marginTop: 12 }}>
                    {selectedPrimitive.exampleWorkflows.length > 0 ? (
                      <div style={cardListStyle}>
                        {selectedPrimitive.exampleWorkflows.map((name) => (
                          <div key={name} style={cardListItemStyle}>
                            <div style={cardListHeaderStyle}>
                              <div style={cardListMainStyle}>
                                <Typography.Text strong>{name}</Typography.Text>
                                <Typography.Paragraph
                                  style={{ margin: 0 }}
                                  type="secondary"
                                >
                                  Open {name} in the runtime workflow library,
                                  launch a run, or jump to scope-owned
                                  published assets.
                                </Typography.Paragraph>
                              </div>
                            </div>
                            <div style={cardListActionStyle}>
                              <Button
                                type="link"
                                onClick={() =>
                                  history.push(
                                    buildRuntimeWorkflowsHref({
                                      workflow: name,
                                      tab: "yaml",
                                    })
                                  )
                                }
                              >
                                Inspect runtime
                              </Button>
                              <Button
                                type="link"
                                onClick={() =>
                                  history.push(
                                    buildRuntimeRunsHref({
                                      workflow: name,
                                    })
                                  )
                                }
                              >
                                Run
                              </Button>
                              <Button
                                type="link"
                                onClick={() => history.push("/scopes/workflows")}
                              >
                                Scope assets
                              </Button>
                            </div>
                          </div>
                        ))}
                      </div>
                    ) : (
                      <Empty
                        image={Empty.PRESENTED_IMAGE_SIMPLE}
                        description="No example workflows are currently advertised for this primitive."
                      />
                    )}
                  </div>
                </div>
              </div>
            )}
          </ProCard>
        </Col>
      </Row>
    </PageContainer>
  );
};

export default PrimitivesPage;
