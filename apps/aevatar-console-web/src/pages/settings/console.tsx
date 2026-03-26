import type { ProFormInstance } from "@ant-design/pro-components";
import {
  PageContainer,
  ProCard,
  ProForm,
  ProFormDigit,
  ProFormSelect,
  ProFormText,
} from "@ant-design/pro-components";
import { useQuery } from "@tanstack/react-query";
import { history } from "@/shared/navigation/history";
import {
  buildRuntimeObservabilityHref,
  buildRuntimeRunsHref,
} from "@/shared/navigation/runtimeRoutes";
import { Button, Col, Empty, Row, Space, Tag, Typography, message } from "antd";
import React, { useMemo, useRef, useState } from "react";
import { runtimeCatalogApi } from "@/shared/api/runtimeCatalogApi";
import {
  buildObservabilityTargets,
  type ObservabilityTarget,
} from "@/shared/observability/observabilityLinks";
import {
  type ActorGraphDirection,
  type ConsolePreferences,
  loadConsolePreferences,
  resetConsolePreferences,
  saveConsolePreferences,
} from "@/shared/preferences/consolePreferences";
import { buildWorkflowCatalogOptions } from "@/shared/workflows/catalogVisibility";
import {
  cardListActionStyle,
  cardListHeaderStyle,
  cardListItemStyle,
  cardListMainStyle,
  cardListStyle,
  cardListUrlStyle,
  cardStackStyle,
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

type ConsoleSettingsSummaryRecord = {
  preferredWorkflow: string;
  graphDirection: ActorGraphDirection;
  observabilityTargetsConfigured: number;
};

const consoleUsageNotes = [
  {
    id: "console-defaults",
    text: "These settings are stored locally in the browser and apply to console navigation, runtime explorer defaults, and outbound observability links.",
  },
  {
    id: "console-observability",
    text: "Grafana, Jaeger, and Loki URLs are not proxied. The console only builds outbound links and preserves current workflow context.",
  },
];

type SummaryFieldProps = {
  label: string;
  value: React.ReactNode;
};

type SummaryMetricProps = {
  label: string;
  value: React.ReactNode;
};

const SummaryField: React.FC<SummaryFieldProps> = ({ label, value }) => (
  <div style={summaryFieldStyle}>
    <Typography.Text style={summaryFieldLabelStyle}>{label}</Typography.Text>
    <Typography.Text>{value}</Typography.Text>
  </div>
);

const SummaryMetric: React.FC<SummaryMetricProps> = ({ label, value }) => (
  <div style={summaryMetricStyle}>
    <Typography.Text style={summaryFieldLabelStyle}>{label}</Typography.Text>
    <Typography.Text style={summaryMetricValueStyle}>{value}</Typography.Text>
  </div>
);

function renderObservabilityEndpointCards(
  observabilityTargets: ObservabilityTarget[],
): React.ReactNode {
  if (observabilityTargets.length === 0) {
    return (
      <Typography.Text type="secondary">
        No observability targets configured.
      </Typography.Text>
    );
  }

  return (
    <div style={cardListStyle}>
      {observabilityTargets.map((record) => (
        <div key={record.id} style={cardListItemStyle}>
          <div style={cardListHeaderStyle}>
            <div style={cardListMainStyle}>
              <Space wrap size={[8, 8]}>
                <Typography.Text strong>{record.label}</Typography.Text>
                <Tag color={record.status === "configured" ? "success" : "default"}>
                  {record.status}
                </Tag>
              </Space>
              <Typography.Paragraph style={{ margin: 0 }} type="secondary">
                {record.description}
              </Typography.Paragraph>
            </div>
          </div>

          {record.homeUrl ? (
            <Typography.Paragraph
              copyable={{ text: record.homeUrl }}
              ellipsis={{ rows: 2, expandable: true, symbol: "more" }}
              style={cardListUrlStyle}
              type="secondary"
            >
              {record.homeUrl}
            </Typography.Paragraph>
          ) : (
            <Typography.Text type="secondary">No URL configured.</Typography.Text>
          )}

          <div style={cardListActionStyle}>
            <Button
              type="link"
              disabled={record.status !== "configured"}
              href={record.homeUrl || undefined}
              target="_blank"
              rel="noreferrer"
            >
              Open
            </Button>
            <Button
              type="link"
              disabled={record.status !== "configured"}
              href={record.exploreUrl || undefined}
              target="_blank"
              rel="noreferrer"
            >
              Explore
            </Button>
          </div>
        </div>
      ))}
    </div>
  );
}

const ConsoleSettingsPage: React.FC = () => {
  const formRef = useRef<ProFormInstance<ConsolePreferences> | undefined>(
    undefined
  );
  const [messageApi, messageContextHolder] = message.useMessage();
  const [preferences, setPreferences] = useState<ConsolePreferences>(
    loadConsolePreferences()
  );

  const workflowCatalogQuery = useQuery({
    queryKey: ["settings-console", "workflow-catalog"],
    queryFn: () => runtimeCatalogApi.listWorkflowCatalog(),
  });

  const workflowOptions = useMemo(
    () =>
      buildWorkflowCatalogOptions(
        workflowCatalogQuery.data ?? [],
        preferences.preferredWorkflow
      ),
    [preferences.preferredWorkflow, workflowCatalogQuery.data]
  );

  const observabilityTargets = useMemo<ObservabilityTarget[]>(
    () =>
      buildObservabilityTargets(preferences, {
        workflow: preferences.preferredWorkflow,
        actorId: "",
        commandId: "",
        runId: "",
        stepId: "",
      }),
    [preferences]
  );

  const summaryRecord = useMemo<ConsoleSettingsSummaryRecord>(
    () => ({
      preferredWorkflow: preferences.preferredWorkflow,
      graphDirection: preferences.actorGraphDirection,
      observabilityTargetsConfigured: observabilityTargets.filter(
        (target) => target.status === "configured"
      ).length,
    }),
    [observabilityTargets, preferences]
  );

  const handleSavePreferences = async (values: ConsolePreferences) => {
    const next = saveConsolePreferences(values);
    setPreferences(next);
    messageApi.success("Console preferences saved.");
    return true;
  };

  const handleResetPreferences = () => {
    const next = resetConsolePreferences();
    setPreferences(next);
    formRef.current?.setFieldsValue(next);
    messageApi.success("Console preferences reset to defaults.");
  };

  return (
    <PageContainer
      title="Console Settings"
      content="Manage local console preferences, default workflow selection, runtime explorer defaults, and observability URLs."
      onBack={() => history.push("/overview")}
      extra={[
        <Button
          key="observability"
          onClick={() => history.push(buildRuntimeObservabilityHref())}
        >
          Open observability hub
        </Button>,
      ]}
    >
      {messageContextHolder}
      <Row gutter={[16, 16]} align="stretch">
        <Col xs={24} xxl={15} style={stretchColumnStyle}>
          <ProCard
            title="Console preferences"
            {...moduleCardProps}
            style={fillCardStyle}
          >
            <ProForm<ConsolePreferences>
              formRef={formRef}
              layout="vertical"
              initialValues={preferences}
              onFinish={handleSavePreferences}
              submitter={{
                render: (props) => (
                  <Space wrap>
                    <Button
                      type="primary"
                      onClick={() => props.form?.submit?.()}
                    >
                      Save preferences
                    </Button>
                    <Button onClick={handleResetPreferences}>
                      Reset defaults
                    </Button>
                    <Button
                      onClick={() =>
                        history.push(
                          buildRuntimeRunsHref({
                            workflow:
                              formRef.current?.getFieldValue(
                                "preferredWorkflow"
                              ) ?? preferences.preferredWorkflow,
                          })
                        )
                      }
                    >
                      Open preferred workflow
                    </Button>
                  </Space>
                ),
              }}
            >
              <Space direction="vertical" style={{ width: "100%" }} size={16}>
                <ProCard title="Workflow defaults" ghost>
                  <Row gutter={[16, 0]}>
                    <Col xs={24}>
                      <ProFormSelect
                        name="preferredWorkflow"
                        label="Preferred workflow"
                        options={workflowOptions}
                        placeholder="Select a default workflow"
                        rules={[
                          {
                            required: true,
                            message: "Preferred workflow is required.",
                          },
                        ]}
                        fieldProps={{
                          showSearch: true,
                          optionFilterProp: "label",
                          notFoundContent: workflowCatalogQuery.isLoading ? (
                            <Typography.Text type="secondary">
                              Loading workflows...
                            </Typography.Text>
                          ) : (
                            <Empty
                              image={Empty.PRESENTED_IMAGE_SIMPLE}
                              description="No workflows available."
                            />
                          ),
                        }}
                      />
                    </Col>
                  </Row>
                </ProCard>

                <ProCard title="Observability URLs" ghost>
                  <Row gutter={[16, 0]}>
                    <Col xs={24} md={12}>
                      <ProFormText
                        name="grafanaBaseUrl"
                        label="Grafana base URL"
                        placeholder="https://grafana.example.com"
                      />
                    </Col>
                    <Col xs={24} md={12}>
                      <ProFormText
                        name="jaegerBaseUrl"
                        label="Jaeger base URL"
                        placeholder="https://jaeger.example.com"
                      />
                    </Col>
                    <Col xs={24}>
                      <ProFormText
                        name="lokiBaseUrl"
                        label="Loki base URL"
                        placeholder="https://loki.example.com"
                      />
                    </Col>
                  </Row>
                </ProCard>

                <ProCard title="Runtime explorer defaults" ghost>
                  <Row gutter={[16, 0]}>
                    <Col xs={24} md={12}>
                      <ProFormDigit
                        name="actorTimelineTake"
                        label="Actor timeline take"
                        min={10}
                        max={500}
                        fieldProps={{ precision: 0 }}
                        rules={[
                          {
                            required: true,
                            message: "Timeline take is required.",
                          },
                        ]}
                      />
                    </Col>
                    <Col xs={24} md={12}>
                      <ProFormDigit
                        name="actorGraphDepth"
                        label="Actor graph depth"
                        min={1}
                        max={8}
                        fieldProps={{ precision: 0 }}
                        rules={[
                          {
                            required: true,
                            message: "Graph depth is required.",
                          },
                        ]}
                      />
                    </Col>
                    <Col xs={24} md={12}>
                      <ProFormDigit
                        name="actorGraphTake"
                        label="Actor graph take"
                        min={10}
                        max={500}
                        fieldProps={{ precision: 0 }}
                        rules={[
                          {
                            required: true,
                            message: "Graph take is required.",
                          },
                        ]}
                      />
                    </Col>
                    <Col xs={24} md={12}>
                      <ProFormSelect<ActorGraphDirection>
                        name="actorGraphDirection"
                        label="Actor graph direction"
                        options={[
                          { label: "Both", value: "Both" },
                          { label: "Outbound", value: "Outbound" },
                          { label: "Inbound", value: "Inbound" },
                        ]}
                        rules={[
                          {
                            required: true,
                            message: "Graph direction is required.",
                          },
                        ]}
                      />
                    </Col>
                  </Row>
                </ProCard>
              </Space>
            </ProForm>
          </ProCard>
        </Col>

        <Col xs={24} xxl={9} style={stretchColumnStyle}>
          <Space direction="vertical" style={{ width: "100%" }} size={16}>
            <ProCard
              title="Console summary"
              {...moduleCardProps}
              style={fillCardStyle}
            >
              <div style={cardStackStyle}>
                <Space wrap size={[8, 8]}>
                  <Tag color="processing">{summaryRecord.preferredWorkflow}</Tag>
                  <Tag>{summaryRecord.graphDirection}</Tag>
                </Space>

                <div style={summaryMetricGridStyle}>
                  <SummaryMetric
                    label="Configured targets"
                    value={`${summaryRecord.observabilityTargetsConfigured}/${observabilityTargets.length}`}
                  />
                  <SummaryMetric
                    label="Graph direction"
                    value={summaryRecord.graphDirection}
                  />
                </div>

                <div style={summaryFieldGridStyle}>
                  <SummaryField
                    label="Preferred workflow"
                    value={summaryRecord.preferredWorkflow}
                  />
                  <SummaryField
                    label="Grafana"
                    value={preferences.grafanaBaseUrl || "Not configured"}
                  />
                  <SummaryField
                    label="Jaeger"
                    value={preferences.jaegerBaseUrl || "Not configured"}
                  />
                  <SummaryField
                    label="Loki"
                    value={preferences.lokiBaseUrl || "Not configured"}
                  />
                </div>
              </div>
            </ProCard>
            <ProCard
              title="Observability endpoints"
              {...moduleCardProps}
              style={fillCardStyle}
            >
              {renderObservabilityEndpointCards(observabilityTargets)}
            </ProCard>
            <ProCard
              title="Usage notes"
              {...moduleCardProps}
              style={fillCardStyle}
            >
              <Space direction="vertical" style={{ width: "100%" }} size={12}>
                {consoleUsageNotes.map((item) => (
                  <Typography.Text key={item.id}>{item.text}</Typography.Text>
                ))}
              </Space>
            </ProCard>
          </Space>
        </Col>
      </Row>
    </PageContainer>
  );
};

export default ConsoleSettingsPage;
