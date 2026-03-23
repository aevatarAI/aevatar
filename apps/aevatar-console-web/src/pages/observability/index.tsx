import {
  PageContainer,
  ProCard,
  ProDescriptions,
  ProForm,
  ProFormText,
  ProList,
} from "@ant-design/pro-components";
import type {
  ProDescriptionsItemProps,
  ProFormInstance,
} from "@ant-design/pro-components";
import { history } from "@/shared/navigation/history";
import { Alert, Button, Col, Empty, Row, Space, Tag, Typography } from "antd";
import React, { useEffect, useMemo, useRef, useState } from "react";
import {
  buildObservabilityTargets,
  type ObservabilityContext,
  type ObservabilityTarget,
} from "@/shared/observability/observabilityLinks";
import { loadConsolePreferences } from "@/shared/preferences/consolePreferences";
import {
  fillCardStyle,
  moduleCardProps,
  stretchColumnStyle,
} from "@/shared/ui/proComponents";

type ObservabilityContextForm = ObservabilityContext;

type ObservabilitySummaryRecord = {
  configuredCount: number;
  missingCount: number;
  workflow: string;
  actorId: string;
  commandId: string;
};

type InternalJumpItem = {
  id: string;
  title: string;
  description: string;
  href: string;
  enabled: boolean;
};

const targetStatusValueEnum = {
  configured: { text: "Configured", status: "Success" },
  missing: { text: "Missing", status: "Default" },
} as const;

const summaryColumns: ProDescriptionsItemProps<ObservabilitySummaryRecord>[] = [
  {
    title: "Configured targets",
    dataIndex: "configuredCount",
    valueType: "digit",
  },
  {
    title: "Missing targets",
    dataIndex: "missingCount",
    valueType: "digit",
  },
  {
    title: "Workflow context",
    dataIndex: "workflow",
    render: (_, record) => record.workflow || "n/a",
  },
  {
    title: "Actor context",
    dataIndex: "actorId",
    render: (_, record) => record.actorId || "n/a",
  },
  {
    title: "Command context",
    dataIndex: "commandId",
    render: (_, record) => record.commandId || "n/a",
  },
];

function readContextFromUrl(): ObservabilityContext {
  if (typeof window === "undefined") {
    return {
      workflow: "",
      actorId: "",
      commandId: "",
      runId: "",
      stepId: "",
    };
  }

  const params = new URLSearchParams(window.location.search);
  return {
    workflow: params.get("workflow") ?? "",
    actorId: params.get("actorId") ?? "",
    commandId: params.get("commandId") ?? "",
    runId: params.get("runId") ?? "",
    stepId: params.get("stepId") ?? "",
  };
}

const ObservabilityPage: React.FC = () => {
  const preferences = useMemo(() => loadConsolePreferences(), []);
  const initialContext = useMemo(() => readContextFromUrl(), []);
  const formRef = useRef<ProFormInstance<ObservabilityContextForm> | undefined>(
    undefined
  );
  const [context, setContext] = useState<ObservabilityContext>(initialContext);

  const targets = useMemo(
    () => buildObservabilityTargets(preferences, context),
    [context, preferences]
  );

  const summaryRecord = useMemo<ObservabilitySummaryRecord>(
    () => ({
      configuredCount: targets.filter(
        (target) => target.status === "configured"
      ).length,
      missingCount: targets.filter((target) => target.status === "missing")
        .length,
      workflow: context.workflow,
      actorId: context.actorId,
      commandId: context.commandId,
    }),
    [context.actorId, context.commandId, context.workflow, targets]
  );

  const internalJumps = useMemo<InternalJumpItem[]>(
    () => [
      {
        id: "jump-runs",
        title: "Open Runs",
        description: context.workflow
          ? `Open Runs with workflow=${context.workflow}.`
          : "Open Runs and keep the current workflow selection manual.",
        href: context.workflow
          ? `/runs?workflow=${encodeURIComponent(context.workflow)}`
          : "/runs",
        enabled: true,
      },
      {
        id: "jump-actors",
        title: "Open Runtime Explorer",
        description: context.actorId
          ? `Open Runtime Explorer with actorId=${context.actorId}.`
          : "Provide actorId first to jump directly to Runtime Explorer.",
        href: context.actorId
          ? `/actors?actorId=${encodeURIComponent(context.actorId)}`
          : "/actors",
        enabled: Boolean(context.actorId),
      },
      {
        id: "jump-workflows",
        title: "Open Workflow Detail",
        description: context.workflow
          ? `Open Workflows with workflow=${context.workflow}.`
          : "Provide workflow first to jump directly to Workflow detail.",
        href: context.workflow
          ? `/workflows?workflow=${encodeURIComponent(context.workflow)}`
          : "/workflows",
        enabled: Boolean(context.workflow),
      },
      {
        id: "jump-settings",
        title: "Open Console Settings",
        description:
          "Manage observability endpoint URLs and console preferences.",
        href: "/settings/console",
        enabled: true,
      },
      {
        id: "jump-settings-runtime",
        title: "Open Runtime Settings",
        description:
          "Manage local workflows, providers, connectors, MCP servers, secrets, and raw configuration files.",
        href: "/settings/runtime",
        enabled: true,
      },
      {
        id: "jump-scopes",
        title: "Open Scopes",
        description:
          "Inspect published workflow and script assets owned by GAgentService scopes.",
        href: "/scopes",
        enabled: true,
      },
      {
        id: "jump-services",
        title: "Open Services",
        description:
          "Inspect services, revisions, serving targets, rollouts, and traffic exposure.",
        href: "/services",
        enabled: true,
      },
      {
        id: "jump-governance",
        title: "Open Governance",
        description:
          "Inspect bindings, policies, endpoint exposure, and activation capability views.",
        href: "/governance",
        enabled: true,
      },
    ],
    [context.actorId, context.workflow]
  );

  useEffect(() => {
    if (typeof window === "undefined") {
      return;
    }

    const url = new URL(window.location.href);
    const entries = Object.entries(context) as Array<
      [keyof ObservabilityContext, string]
    >;
    for (const [key, value] of entries) {
      if (value) {
        url.searchParams.set(key, value);
      } else {
        url.searchParams.delete(key);
      }
    }
    window.history.replaceState(null, "", `${url.pathname}${url.search}`);
  }, [context]);

  return (
    <PageContainer
      title="Observability"
      content="Use configured external tools as the jump hub for runtime, scopes, services, governance, and local settings without adding new backend APIs."
    >
      <Alert
        showIcon
        type="info"
        title="External observability only"
        description="This page only manages outbound links and context handoff. Grafana, Jaeger, and Loki queries are not proxied through the Aevatar backend."
        style={{ marginBottom: 16 }}
      />

      <Row gutter={[16, 16]} align="stretch">
        <Col xs={24} xl={9} style={stretchColumnStyle}>
          <ProCard
            title="Observation context"
            {...moduleCardProps}
            style={fillCardStyle}
          >
            <ProForm<ObservabilityContextForm>
              formRef={formRef}
              layout="vertical"
              initialValues={initialContext}
              onFinish={async (values) => {
                setContext({
                  workflow: values.workflow?.trim() ?? "",
                  actorId: values.actorId?.trim() ?? "",
                  commandId: values.commandId?.trim() ?? "",
                  runId: values.runId?.trim() ?? "",
                  stepId: values.stepId?.trim() ?? "",
                });
                return true;
              }}
              submitter={{
                render: (props) => (
                  <Space wrap>
                    <Button
                      type="primary"
                      onClick={() => props.form?.submit?.()}
                    >
                      Apply context
                    </Button>
                    <Button
                      onClick={() => {
                        formRef.current?.setFieldsValue(initialContext);
                        setContext(initialContext);
                      }}
                    >
                      Reset
                    </Button>
                  </Space>
                ),
              }}
            >
              <ProFormText
                name="workflow"
                label="Workflow"
                placeholder="direct"
              />
              <ProFormText
                name="actorId"
                label="ActorId"
                placeholder="Workflow:19fe1b04"
              />
              <ProFormText
                name="commandId"
                label="CommandId"
                placeholder="cmd-123"
              />
              <ProFormText name="runId" label="RunId" placeholder="run-123" />
              <ProFormText
                name="stepId"
                label="StepId"
                placeholder="approve_release"
              />
            </ProForm>
          </ProCard>
        </Col>

        <Col xs={24} xl={15} style={stretchColumnStyle}>
          <ProCard
            title="Observability summary"
            {...moduleCardProps}
            style={fillCardStyle}
          >
            <ProDescriptions<ObservabilitySummaryRecord>
              column={2}
              dataSource={summaryRecord}
              columns={summaryColumns}
            />
          </ProCard>
        </Col>
      </Row>

      <Row gutter={[16, 16]} style={{ marginTop: 16 }} align="stretch">
        <Col xs={24} xl={15} style={stretchColumnStyle}>
          <ProCard
            title="Configured targets"
            {...moduleCardProps}
            style={fillCardStyle}
          >
            <ProList<ObservabilityTarget>
              rowKey="id"
              search={false}
              split
              dataSource={targets}
              locale={{
                emptyText: (
                  <Empty
                    image={Empty.PRESENTED_IMAGE_SIMPLE}
                    description="No observability targets are available."
                  />
                ),
              }}
              metas={{
                title: {
                  dataIndex: "label",
                  render: (_, record) => (
                    <Space wrap size={[8, 8]}>
                      <Typography.Text strong>{record.label}</Typography.Text>
                      <Tag
                        color={
                          record.status === "configured" ? "success" : "default"
                        }
                      >
                        {record.status}
                      </Tag>
                    </Space>
                  ),
                },
                description: {
                  dataIndex: "description",
                },
                subTitle: {
                  render: (_, record) => (
                    <Space wrap size={[8, 8]}>
                      <Tag>{record.contextSummary}</Tag>
                      {record.status === "configured" ? (
                        <Tag color="processing">{record.homeUrl}</Tag>
                      ) : (
                        <Tag>No URL configured</Tag>
                      )}
                    </Space>
                  ),
                },
                content: {
                  render: (_, record) => (
                    <Space wrap>
                      <Button
                        type="primary"
                        disabled={record.status !== "configured"}
                        href={record.homeUrl || undefined}
                        target="_blank"
                        rel="noreferrer"
                      >
                        Open {record.label}
                      </Button>
                      <Button
                        disabled={record.status !== "configured"}
                        href={record.exploreUrl || undefined}
                        target="_blank"
                        rel="noreferrer"
                      >
                        Open explore
                      </Button>
                    </Space>
                  ),
                },
              }}
            />
          </ProCard>
        </Col>

        <Col xs={24} xl={9} style={stretchColumnStyle}>
          <ProCard
            title="Console surfaces"
            {...moduleCardProps}
            style={fillCardStyle}
          >
            <ProList<InternalJumpItem>
              rowKey="id"
              search={false}
              split
              dataSource={internalJumps}
              metas={{
                title: {
                  dataIndex: "title",
                },
                description: {
                  dataIndex: "description",
                },
                actions: {
                  render: (_, record) => [
                    <Button
                      key={`${record.id}-open`}
                      type="link"
                      disabled={!record.enabled}
                      onClick={() => history.push(record.href)}
                    >
                      Open
                    </Button>,
                  ],
                },
              }}
            />
          </ProCard>
        </Col>
      </Row>
    </PageContainer>
  );
};

export default ObservabilityPage;
