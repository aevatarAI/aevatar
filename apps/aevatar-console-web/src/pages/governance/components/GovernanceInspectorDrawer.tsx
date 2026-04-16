import {
  ApiOutlined,
  LinkOutlined,
  SafetyCertificateOutlined,
} from "@ant-design/icons";
import {
  Alert,
  Button,
  Divider,
  Drawer,
  Form,
  Input,
  Select,
  Space,
  Switch,
  Typography,
  theme,
} from "antd";
import React, { useEffect } from "react";
import type {
  ActivationCapabilityView,
  ServiceBindingInput,
  GovernanceIdentityInput,
  ServiceBindingSnapshot,
  ServiceEndpointCatalogSnapshot,
  ServiceEndpointExposureInput,
  ServiceEndpointExposureSnapshot,
  ServicePolicyInput,
  ServicePolicySnapshot,
} from "@/shared/models/governance";
import {
  aevatarDrawerBodyStyle,
  aevatarDrawerScrollStyle,
  buildAevatarMetricCardStyle,
  buildAevatarPanelStyle,
  buildAevatarTagStyle,
  formatAevatarStatusLabel,
  resolveAevatarMetricVisual,
  type AevatarThemeSurfaceToken,
} from "@/shared/ui/aevatarWorkbench";
import type { GovernanceAuditEvent } from "./GovernanceAuditTimeline";

export type GovernanceInspectorTarget =
  | {
      kind: "policy";
      mode: "create" | "edit";
      record: ServicePolicySnapshot;
    }
  | {
      kind: "binding";
      mode: "create" | "edit";
      record: ServiceBindingSnapshot;
    }
  | {
      kind: "endpoint";
      mode: "create" | "edit";
      record: ServiceEndpointExposureSnapshot;
    }
  | {
      kind: "activation";
      record: ActivationCapabilityView;
    }
  | {
      kind: "audit";
      event: GovernanceAuditEvent;
    };

type GovernanceInspectorDrawerProps = {
  open: boolean;
  target: GovernanceInspectorTarget | null;
  identity: GovernanceIdentityInput | null;
  serviceId: string;
  endpointCatalog: ServiceEndpointCatalogSnapshot | null;
  policyOptions: string[];
  busyAction?: string | null;
  onClose: () => void;
  onCreateBinding: (input: ServiceBindingInput) => Promise<void>;
  onUpdateBinding: (
    bindingId: string,
    input: ServiceBindingInput,
  ) => Promise<void>;
  onCreatePolicy: (input: ServicePolicyInput) => Promise<void>;
  onUpdatePolicy: (policyId: string, input: ServicePolicyInput) => Promise<void>;
  onRetirePolicy: (policyId: string) => Promise<void>;
  onRetireBinding: (bindingId: string) => Promise<void>;
  onCreateEndpoint: (input: ServiceEndpointExposureInput) => Promise<void>;
  onUpdateEndpoint: (
    endpointId: string,
    input: ServiceEndpointExposureInput,
  ) => Promise<void>;
  onSetEndpointExposure: (
    endpointId: string,
    exposureKind: string,
  ) => Promise<void>;
};

type PolicyFormValues = {
  policyId: string;
  displayName: string;
  activationRequiredBindingIds: string;
  invokeAllowedCallerServiceKeys: string;
  invokeRequiresActiveDeployment: boolean;
};

type BindingFormValues = {
  bindingId: string;
  displayName: string;
  bindingKind: string;
  policyIds: string[];
  serviceTenantId: string;
  serviceAppId: string;
  serviceNamespace: string;
  serviceId: string;
  endpointId: string;
  connectorType: string;
  connectorId: string;
  secretName: string;
};

type EndpointFormValues = {
  endpointId: string;
  displayName: string;
  kind: string;
  requestTypeUrl: string;
  responseTypeUrl: string;
  description: string;
  exposureKind: string;
  policyIds: string[];
};

function joinLines(values: string[]) {
  return values.join("\n");
}

function splitLines(value: string): string[] {
  return value
    .split(/[\n,]/g)
    .map((entry) => entry.trim())
    .filter(Boolean);
}

function buildPolicyStatus(record: ServicePolicySnapshot): string {
  return record.retired ? "retired" : "active";
}

function buildBindingStatus(record: ServiceBindingSnapshot): string {
  return record.retired ? "retired" : "active";
}

function buildEndpointStatus(record: ServiceEndpointExposureSnapshot): string {
  return record.exposureKind.trim() || "internal";
}

function renderMetric(
  token: AevatarThemeSurfaceToken,
  label: string,
  value: string,
  tone: "default" | "info" | "success" | "warning" = "default",
) {
  const visual = resolveAevatarMetricVisual(token, tone);

  return (
    <div style={buildAevatarMetricCardStyle(token, tone)}>
      <Typography.Text style={{ color: visual.labelColor }}>{label}</Typography.Text>
      <Typography.Text strong style={{ color: visual.valueColor }}>
        {value}
      </Typography.Text>
    </div>
  );
}

function renderList(values: string[]) {
  if (values.length === 0) {
    return <Typography.Text type="secondary">暂无</Typography.Text>;
  }

  return (
    <Space orientation="vertical" size={6} style={{ display: "flex" }}>
      {values.map((value) => (
        <Typography.Text key={value}>{value}</Typography.Text>
      ))}
    </Space>
  );
}

const GovernanceInspectorDrawer: React.FC<GovernanceInspectorDrawerProps> = ({
  open,
  target,
  identity,
  serviceId,
  endpointCatalog,
  policyOptions,
  busyAction = null,
  onClose,
  onCreateBinding,
  onUpdateBinding,
  onCreatePolicy,
  onUpdatePolicy,
  onRetirePolicy,
  onRetireBinding,
  onCreateEndpoint,
  onUpdateEndpoint,
  onSetEndpointExposure,
}) => {
  const { token } = theme.useToken();
  const surfaceToken = token as AevatarThemeSurfaceToken;
  const [policyForm] = Form.useForm<PolicyFormValues>();
  const [bindingForm] = Form.useForm<BindingFormValues>();
  const [endpointForm] = Form.useForm<EndpointFormValues>();

  useEffect(() => {
    if (!open || target?.kind !== "policy") {
      return;
    }

    policyForm.setFieldsValue({
      policyId: target.record.policyId,
      displayName: target.record.displayName,
      activationRequiredBindingIds: joinLines(
        target.record.activationRequiredBindingIds,
      ),
      invokeAllowedCallerServiceKeys: joinLines(
        target.record.invokeAllowedCallerServiceKeys,
      ),
      invokeRequiresActiveDeployment:
        target.record.invokeRequiresActiveDeployment,
    });
  }, [open, policyForm, target]);

  useEffect(() => {
    if (!open || target?.kind !== "binding") {
      return;
    }

    bindingForm.resetFields();
    bindingForm.setFieldsValue({
      bindingId: target.record.bindingId,
      displayName: target.record.displayName,
      bindingKind: target.record.bindingKind || "service",
      policyIds: target.record.policyIds,
      serviceTenantId: target.record.serviceRef?.identity.tenantId ?? "",
      serviceAppId: target.record.serviceRef?.identity.appId ?? "",
      serviceNamespace: target.record.serviceRef?.identity.namespace ?? "",
      serviceId: target.record.serviceRef?.identity.serviceId ?? "",
      endpointId: target.record.serviceRef?.endpointId ?? "",
      connectorType: target.record.connectorRef?.connectorType ?? "",
      connectorId: target.record.connectorRef?.connectorId ?? "",
      secretName: target.record.secretRef?.secretName ?? "",
    });
  }, [bindingForm, open, target]);

  useEffect(() => {
    if (!open || target?.kind !== "endpoint") {
      return;
    }

    endpointForm.resetFields();
    endpointForm.setFieldsValue({
      endpointId: target.record.endpointId,
      displayName: target.record.displayName,
      kind: target.record.kind || "command",
      requestTypeUrl: target.record.requestTypeUrl,
      responseTypeUrl: target.record.responseTypeUrl,
      description: target.record.description,
      exposureKind: target.record.exposureKind || "internal",
      policyIds: target.record.policyIds,
    });
  }, [endpointForm, open, target]);

  const canManage = Boolean(identity && serviceId.trim());
  const bindingKind = Form.useWatch("bindingKind", bindingForm) ?? "service";

  const policyAction =
    target?.kind === "policy" && target.mode === "create"
      ? "create-policy"
      : "save-policy";
  const bindingAction =
    target?.kind === "binding" && target.mode === "create"
      ? "create-binding"
      : "save-binding";
  const endpointAction =
    target?.kind === "endpoint" && target.mode === "create"
      ? "create-endpoint"
      : "save-endpoint";

  async function submitPolicy() {
    if (!identity || target?.kind !== "policy") {
      return;
    }

    const values = await policyForm.validateFields();
    const payload: ServicePolicyInput = {
      ...identity,
      policyId: values.policyId.trim(),
      displayName: values.displayName.trim(),
      activationRequiredBindingIds: splitLines(
        values.activationRequiredBindingIds,
      ),
      invokeAllowedCallerServiceKeys: splitLines(
        values.invokeAllowedCallerServiceKeys,
      ),
      invokeRequiresActiveDeployment: values.invokeRequiresActiveDeployment,
    };

    if (target.mode === "create") {
      await onCreatePolicy(payload);
      return;
    }

    await onUpdatePolicy(target.record.policyId, payload);
  }

  async function submitBinding() {
    if (!identity || target?.kind !== "binding") {
      return;
    }

    const values = await bindingForm.validateFields();
    const normalizedKind = values.bindingKind.trim() || "service";
    const payload: ServiceBindingInput = {
      ...identity,
      bindingId: values.bindingId.trim(),
      bindingKind: normalizedKind,
      displayName: values.displayName.trim(),
      policyIds: (values.policyIds ?? []).map((entry) => entry.trim()).filter(Boolean),
    };

    if (normalizedKind === "service") {
      payload.service = {
        tenantId: values.serviceTenantId.trim() || identity.tenantId,
        appId: values.serviceAppId.trim() || identity.appId,
        namespace: values.serviceNamespace.trim() || identity.namespace,
        serviceId: values.serviceId.trim(),
        endpointId: values.endpointId.trim() || undefined,
      };
    }

    if (normalizedKind === "connector") {
      payload.connector = {
        connectorType: values.connectorType.trim(),
        connectorId: values.connectorId.trim(),
      };
    }

    if (normalizedKind === "secret") {
      payload.secret = {
        secretName: values.secretName.trim(),
      };
    }

    if (target.mode === "create") {
      await onCreateBinding(payload);
      return;
    }

    await onUpdateBinding(target.record.bindingId, payload);
  }

  async function submitEndpoint() {
    if (target?.kind !== "endpoint") {
      return;
    }

    const values = await endpointForm.validateFields();
    const payload: ServiceEndpointExposureInput = {
      endpointId: values.endpointId.trim(),
      displayName: values.displayName.trim(),
      kind: values.kind.trim(),
      requestTypeUrl: values.requestTypeUrl.trim(),
      responseTypeUrl: values.responseTypeUrl.trim(),
      description: values.description.trim(),
      exposureKind: values.exposureKind.trim(),
      policyIds: (values.policyIds ?? []).map((entry) => entry.trim()).filter(Boolean),
    };

    if (target.mode === "create") {
      await onCreateEndpoint(payload);
      return;
    }

    await onUpdateEndpoint(target.record.endpointId, payload);
  }

  return (
    <Drawer
      destroyOnClose={false}
      onClose={onClose}
      open={open}
      size="large"
      styles={{
        body: aevatarDrawerBodyStyle,
        wrapper: {
          width: 760,
        },
      }}
      title={
        target?.kind === "policy"
          ? target.mode === "create"
            ? "新建策略"
            : target.record.policyId
          : target?.kind === "binding"
            ? target.mode === "create"
              ? "新建绑定"
              : target.record.bindingId
          : target?.kind === "endpoint"
              ? target.mode === "create"
                ? "新建入口"
                : target.record.endpointId
              : target?.kind === "activation"
                ? "激活校验"
                : target?.kind === "audit"
                  ? "变更记录"
                  : "治理详情"
      }
    >
      <div style={aevatarDrawerScrollStyle}>
        {!canManage ? (
          <Alert
            message="请先选择服务"
            type="info"
          />
        ) : null}

        {target?.kind === "policy" ? (
          <div
            style={{
              ...buildAevatarPanelStyle(surfaceToken, {
                background: surfaceToken.colorFillAlter,
                padding: 16,
              }),
              boxShadow: "none",
            }}
          >
            <Space orientation="vertical" size={16} style={{ display: "flex" }}>
              <Space align="center" size={[8, 8]} wrap>
                <SafetyCertificateOutlined />
                <Typography.Text strong>
                  {target.mode === "create"
                    ? "新建一条治理策略"
                    : target.record.displayName || target.record.policyId}
                </Typography.Text>
                {target.mode === "edit" ? (
                  <span
                    style={buildAevatarTagStyle(
                      surfaceToken,
                      "governance",
                      buildPolicyStatus(target.record),
                    )}
                  >
                    {formatAevatarStatusLabel(buildPolicyStatus(target.record))}
                  </span>
                ) : null}
              </Space>

              <Form<PolicyFormValues>
                form={policyForm}
                layout="vertical"
                disabled={!canManage}
              >
                <Form.Item
                  label="策略 ID"
                  name="policyId"
                  rules={[{ required: true, message: "请填写策略 ID。" }]}
                >
                  <Input disabled={target.mode === "edit"} />
                </Form.Item>
                <Form.Item
                  label="显示名称"
                  name="displayName"
                  rules={[{ required: true, message: "请填写显示名称。" }]}
                >
                  <Input />
                </Form.Item>
                <Form.Item
                  label="激活依赖绑定"
                  name="activationRequiredBindingIds"
                >
                  <Input.TextArea
                    autoSize={{ minRows: 3, maxRows: 6 }}
                    placeholder="每行一个绑定 ID"
                  />
                </Form.Item>
                <Form.Item
                  label="允许调用的服务 Key"
                  name="invokeAllowedCallerServiceKeys"
                >
                  <Input.TextArea
                    autoSize={{ minRows: 3, maxRows: 6 }}
                    placeholder="团队/应用/命名空间/服务"
                  />
                </Form.Item>
                <Form.Item
                  label="要求已激活部署"
                  name="invokeRequiresActiveDeployment"
                  valuePropName="checked"
                >
                  <Switch />
                </Form.Item>
              </Form>

              <Space wrap>
                <Button
                  loading={busyAction === policyAction}
                  onClick={() => void submitPolicy()}
                  type="primary"
                >
                  {target.mode === "create" ? "创建策略" : "保存策略"}
                </Button>
                {target.mode === "edit" ? (
                  <Button
                    danger
                    loading={busyAction === "retire-policy"}
                    onClick={() => void onRetirePolicy(target.record.policyId)}
                  >
                    下线策略
                  </Button>
                ) : null}
              </Space>
            </Space>
          </div>
        ) : null}

        {target?.kind === "binding" ? (
          <div
            style={{
              ...buildAevatarPanelStyle(surfaceToken, {
                background: surfaceToken.colorFillAlter,
                padding: 16,
              }),
              boxShadow: "none",
            }}
          >
            <Space orientation="vertical" size={16} style={{ display: "flex" }}>
              <Space align="center" size={[8, 8]} wrap>
                <LinkOutlined />
                <Typography.Text strong>
                  {target.mode === "create"
                    ? "新建一条治理绑定"
                    : target.record.displayName || target.record.bindingId}
                </Typography.Text>
                {target.mode === "edit" ? (
                  <span
                    style={buildAevatarTagStyle(
                      surfaceToken,
                      "governance",
                      buildBindingStatus(target.record),
                    )}
                  >
                    {formatAevatarStatusLabel(buildBindingStatus(target.record))}
                  </span>
                ) : null}
              </Space>

              <Form<BindingFormValues>
                form={bindingForm}
                layout="vertical"
                disabled={!canManage}
              >
                <div
                  style={{
                    display: "grid",
                    gap: 12,
                    gridTemplateColumns: "repeat(2, minmax(0, 1fr))",
                  }}
                >
                  <Form.Item
                    label="绑定 ID"
                    name="bindingId"
                    rules={[{ required: true, message: "请填写绑定 ID。" }]}
                  >
                    <Input disabled={target.mode === "edit"} />
                  </Form.Item>
                  <Form.Item
                    label="显示名称"
                    name="displayName"
                    rules={[{ required: true, message: "请填写显示名称。" }]}
                  >
                    <Input />
                  </Form.Item>
                </div>

                <div
                  style={{
                    display: "grid",
                    gap: 12,
                    gridTemplateColumns: "repeat(2, minmax(0, 1fr))",
                  }}
                >
                  <Form.Item
                    label="绑定类型"
                    name="bindingKind"
                    rules={[{ required: true, message: "请选择绑定类型。" }]}
                  >
                    <Select
                      options={[
                        { label: "Service", value: "service" },
                        { label: "Connector", value: "connector" },
                        { label: "Secret", value: "secret" },
                      ]}
                    />
                  </Form.Item>
                  <Form.Item label="挂载策略" name="policyIds">
                    <Select
                      mode="tags"
                      options={policyOptions.map((policyId) => ({
                        label: policyId,
                        value: policyId,
                      }))}
                      placeholder="选择或输入 policy ID"
                    />
                  </Form.Item>
                </div>

                {bindingKind === "service" ? (
                  <>
                    <div
                      style={{
                        display: "grid",
                        gap: 12,
                        gridTemplateColumns: "repeat(2, minmax(0, 1fr))",
                      }}
                    >
                      <Form.Item
                        label="目标服务 ID"
                        name="serviceId"
                        rules={[{ required: true, message: "请填写目标服务 ID。" }]}
                      >
                        <Input placeholder="dependency-service" />
                      </Form.Item>
                      <Form.Item label="目标 endpoint" name="endpointId">
                        <Input placeholder="chat" />
                      </Form.Item>
                    </div>
                    <div
                      style={{
                        display: "grid",
                        gap: 12,
                        gridTemplateColumns: "repeat(3, minmax(0, 1fr))",
                      }}
                    >
                      <Form.Item
                        label="目标 tenant"
                        name="serviceTenantId"
                        extra="留空则复用当前服务的 tenant。"
                      >
                        <Input placeholder={identity?.tenantId ?? ""} />
                      </Form.Item>
                      <Form.Item
                        label="目标 app"
                        name="serviceAppId"
                        extra="留空则复用当前服务的 app。"
                      >
                        <Input placeholder={identity?.appId ?? ""} />
                      </Form.Item>
                      <Form.Item
                        label="目标 namespace"
                        name="serviceNamespace"
                        extra="留空则复用当前服务的 namespace。"
                      >
                        <Input placeholder={identity?.namespace ?? ""} />
                      </Form.Item>
                    </div>
                  </>
                ) : null}

                {bindingKind === "connector" ? (
                  <div
                    style={{
                      display: "grid",
                      gap: 12,
                      gridTemplateColumns: "repeat(2, minmax(0, 1fr))",
                    }}
                  >
                    <Form.Item
                      label="Connector 类型"
                      name="connectorType"
                      rules={[{ required: true, message: "请填写 connector 类型。" }]}
                    >
                      <Input placeholder="mcp" />
                    </Form.Item>
                    <Form.Item
                      label="Connector ID"
                      name="connectorId"
                      rules={[{ required: true, message: "请填写 connector ID。" }]}
                    >
                      <Input placeholder="connector-1" />
                    </Form.Item>
                  </div>
                ) : null}

                {bindingKind === "secret" ? (
                  <Form.Item
                    label="Secret 名称"
                    name="secretName"
                    rules={[{ required: true, message: "请填写 secret 名称。" }]}
                  >
                    <Input placeholder="api-key" />
                  </Form.Item>
                ) : null}
              </Form>

              <Space wrap>
                <Button
                  loading={busyAction === bindingAction}
                  onClick={() => void submitBinding()}
                  type="primary"
                >
                  {target.mode === "create" ? "创建绑定" : "保存绑定"}
                </Button>
                {target.mode === "edit" ? (
                  <Button
                    danger
                    loading={busyAction === "retire-binding"}
                    onClick={() => void onRetireBinding(target.record.bindingId)}
                  >
                    下线绑定
                  </Button>
                ) : null}
              </Space>
            </Space>
          </div>
        ) : null}

        {target?.kind === "endpoint" ? (
          <div
            style={{
              ...buildAevatarPanelStyle(surfaceToken, {
                background: surfaceToken.colorFillAlter,
                padding: 16,
              }),
              boxShadow: "none",
            }}
          >
            <Space orientation="vertical" size={16} style={{ display: "flex" }}>
              <Space align="center" size={[8, 8]} wrap>
                <ApiOutlined />
                <Typography.Text strong>
                  {target.mode === "create"
                    ? "新增一条治理入口"
                    : target.record.displayName || target.record.endpointId}
                </Typography.Text>
                {target.mode === "edit" ? (
                  <span
                    style={buildAevatarTagStyle(
                      surfaceToken,
                      "governance",
                      buildEndpointStatus(target.record),
                    )}
                  >
                    {formatAevatarStatusLabel(buildEndpointStatus(target.record))}
                  </span>
                ) : null}
              </Space>

              <Form<EndpointFormValues>
                form={endpointForm}
                layout="vertical"
                disabled={!canManage || (target.mode === "edit" && !endpointCatalog)}
              >
                <div
                  style={{
                    display: "grid",
                    gap: 12,
                    gridTemplateColumns: "repeat(2, minmax(0, 1fr))",
                  }}
                >
                  <Form.Item
                    label="入口 ID"
                    name="endpointId"
                    rules={[{ required: true, message: "请填写入口 ID。" }]}
                  >
                    <Input disabled={target.mode === "edit"} />
                  </Form.Item>
                  <Form.Item
                    label="显示名称"
                    name="displayName"
                    rules={[{ required: true, message: "请填写显示名称。" }]}
                  >
                    <Input />
                  </Form.Item>
                </div>

                <div
                  style={{
                    display: "grid",
                    gap: 12,
                    gridTemplateColumns: "repeat(2, minmax(0, 1fr))",
                  }}
                >
                  <Form.Item
                    label="入口类型"
                    name="kind"
                    rules={[{ required: true, message: "请选择入口类型。" }]}
                  >
                    <Select
                      options={[
                        { label: "Command", value: "command" },
                        { label: "Chat", value: "chat" },
                      ]}
                    />
                  </Form.Item>
                  <Form.Item
                    label="暴露状态"
                    name="exposureKind"
                    rules={[{ required: true, message: "请选择暴露状态。" }]}
                  >
                    <Select
                      options={[
                        { label: "Public", value: "public" },
                        { label: "Internal", value: "internal" },
                        { label: "Disabled", value: "disabled" },
                      ]}
                    />
                  </Form.Item>
                </div>

                <Form.Item
                  label="请求类型"
                  name="requestTypeUrl"
                  rules={[{ required: true, message: "请填写请求类型。" }]}
                >
                  <Input />
                </Form.Item>
                <Form.Item label="响应类型" name="responseTypeUrl">
                  <Input />
                </Form.Item>
                <Form.Item label="描述" name="description">
                  <Input.TextArea autoSize={{ minRows: 2, maxRows: 4 }} />
                </Form.Item>
                <Form.Item label="挂载策略" name="policyIds">
                  <Select
                    mode="tags"
                    options={policyOptions.map((policyId) => ({
                      label: policyId,
                      value: policyId,
                    }))}
                    placeholder="选择或输入 policy ID"
                  />
                </Form.Item>
              </Form>

              {!endpointCatalog ? (
                <Alert
                  message={
                    target.mode === "create"
                      ? "当前还没有入口目录，保存后会创建第一份 endpoint catalog。"
                      : "当前无法读取入口目录，暂时不能修改暴露状态。"
                  }
                  type={target.mode === "create" ? "info" : "warning"}
                />
              ) : null}

              <Space wrap>
                <Button
                  disabled={!canManage || (target.mode === "edit" && !endpointCatalog)}
                  loading={busyAction === endpointAction}
                  onClick={() => void submitEndpoint()}
                  type="primary"
                >
                  {target.mode === "create" ? "创建入口" : "保存入口"}
                </Button>
                {target.mode === "edit" ? (
                  <Button
                    loading={busyAction === "set-endpoint-exposure:public"}
                    onClick={() =>
                      void onSetEndpointExposure(target.record.endpointId, "public")
                    }
                  >
                    快速公开
                  </Button>
                ) : null}
              </Space>
            </Space>
          </div>
        ) : null}

        {target?.kind === "activation" ? (
          <div
            style={{
              ...buildAevatarPanelStyle(surfaceToken, {
                background: surfaceToken.colorFillAlter,
                padding: 16,
              }),
              boxShadow: "none",
            }}
          >
            <Space orientation="vertical" size={16} style={{ display: "flex" }}>
              <Typography.Text strong>
                版本 {target.record.revisionId || "未解析"} 的激活校验
              </Typography.Text>
              <div
                style={{
                  display: "grid",
                  gap: 12,
                  gridTemplateColumns: "repeat(2, minmax(0, 1fr))",
                }}
              >
                {renderMetric(
                  surfaceToken,
                  "绑定",
                  String(target.record.bindings.length),
                )}
                {renderMetric(
                  surfaceToken,
                  "策略",
                  String(target.record.policies.length),
                )}
                {renderMetric(
                  surfaceToken,
                  "入口",
                  String(target.record.endpoints.length),
                )}
                {renderMetric(
                  surfaceToken,
                  "缺失策略",
                  String(target.record.missingPolicyIds.length),
                  target.record.missingPolicyIds.length > 0
                    ? "warning"
                    : "success",
                )}
              </div>

              <div>
                <Typography.Text type="secondary">缺失策略</Typography.Text>
                <div style={{ marginTop: 8 }}>
                  {renderList(target.record.missingPolicyIds)}
                </div>
              </div>
            </Space>
          </div>
        ) : null}

        {target?.kind === "audit" ? (
          <div
            style={{
              ...buildAevatarPanelStyle(surfaceToken, {
                background: surfaceToken.colorFillAlter,
                padding: 16,
              }),
              boxShadow: "none",
            }}
          >
            <Space orientation="vertical" size={16} style={{ display: "flex" }}>
              <Space align="center" size={[8, 8]} wrap>
                <Typography.Text strong>{target.event.action}</Typography.Text>
                <span
                  style={buildAevatarTagStyle(
                    surfaceToken,
                    "governance",
                    target.event.status,
                  )}
                >
                  {formatAevatarStatusLabel(target.event.status)}
                </span>
              </Space>

              <Typography.Paragraph style={{ margin: 0 }}>
                {target.event.summary}
              </Typography.Paragraph>

              <Divider style={{ margin: 0 }} />

              <Space orientation="vertical" size={8} style={{ display: "flex" }}>
                <Typography.Text type="secondary">
                  来源: {target.event.actor}
                </Typography.Text>
                <Typography.Text type="secondary">
                  对象: {target.event.targetLabel}
                </Typography.Text>
                <Typography.Text type="secondary">
                  时间: {target.event.at}
                </Typography.Text>
              </Space>
            </Space>
          </div>
        ) : null}
      </div>
    </Drawer>
  );
};

export default GovernanceInspectorDrawer;
