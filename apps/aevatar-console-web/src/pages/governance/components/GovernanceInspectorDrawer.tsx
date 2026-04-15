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
  Space,
  Switch,
  Typography,
  theme,
} from "antd";
import React, { useEffect } from "react";
import type {
  ActivationCapabilityView,
  GovernanceIdentityInput,
  ServiceBindingSnapshot,
  ServiceEndpointCatalogSnapshot,
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
      record: ServiceBindingSnapshot;
    }
  | {
      kind: "endpoint";
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
  busyAction?: string | null;
  onClose: () => void;
  onCreatePolicy: (input: ServicePolicyInput) => Promise<void>;
  onUpdatePolicy: (policyId: string, input: ServicePolicyInput) => Promise<void>;
  onRetirePolicy: (policyId: string) => Promise<void>;
  onRetireBinding: (bindingId: string) => Promise<void>;
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
  busyAction = null,
  onClose,
  onCreatePolicy,
  onUpdatePolicy,
  onRetirePolicy,
  onRetireBinding,
  onSetEndpointExposure,
}) => {
  const { token } = theme.useToken();
  const surfaceToken = token as AevatarThemeSurfaceToken;
  const [policyForm] = Form.useForm<PolicyFormValues>();

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

  const canManage = Boolean(identity && serviceId.trim());

  const policyAction =
    target?.kind === "policy" && target.mode === "create"
      ? "create-policy"
      : "save-policy";

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
            ? target.record.bindingId
          : target?.kind === "endpoint"
              ? target.record.endpointId
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
                  {target.record.displayName || target.record.bindingId}
                </Typography.Text>
                <span
                  style={buildAevatarTagStyle(
                    surfaceToken,
                    "governance",
                    buildBindingStatus(target.record),
                  )}
                >
                  {formatAevatarStatusLabel(buildBindingStatus(target.record))}
                </span>
              </Space>

              <div
                style={{
                  display: "grid",
                  gap: 12,
                  gridTemplateColumns: "repeat(2, minmax(0, 1fr))",
                }}
              >
                {renderMetric(
                  surfaceToken,
                  "绑定 ID",
                  target.record.bindingId,
                )}
                {renderMetric(
                  surfaceToken,
                  "类型",
                  formatAevatarStatusLabel(target.record.bindingKind),
                  "info",
                )}
              </div>

              <div>
                <Typography.Text type="secondary">策略</Typography.Text>
                <div style={{ marginTop: 8 }}>{renderList(target.record.policyIds)}</div>
              </div>

              <div>
                <Typography.Text type="secondary">目标</Typography.Text>
                <Typography.Paragraph style={{ margin: "8px 0 0" }}>
                  {target.record.serviceRef
                    ? `${target.record.serviceRef.identity.serviceId}:${target.record.serviceRef.endpointId || "*"}`
                    : target.record.connectorRef
                      ? `${target.record.connectorRef.connectorType}:${target.record.connectorRef.connectorId}`
                      : target.record.secretRef?.secretName || "暂无"}
                </Typography.Paragraph>
              </div>

              <Space wrap>
                <Button
                  danger
                  loading={busyAction === "retire-binding"}
                  onClick={() => void onRetireBinding(target.record.bindingId)}
                >
                  下线绑定
                </Button>
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
                  {target.record.displayName || target.record.endpointId}
                </Typography.Text>
                <span
                  style={buildAevatarTagStyle(
                    surfaceToken,
                    "governance",
                    buildEndpointStatus(target.record),
                  )}
                >
                  {formatAevatarStatusLabel(buildEndpointStatus(target.record))}
                </span>
              </Space>

              <div
                style={{
                  display: "grid",
                  gap: 12,
                  gridTemplateColumns: "repeat(2, minmax(0, 1fr))",
                }}
              >
                {renderMetric(
                  surfaceToken,
                  "入口",
                  target.record.endpointId,
                )}
                {renderMetric(
                  surfaceToken,
                  "类型",
                  formatAevatarStatusLabel(target.record.kind),
                  "info",
                )}
              </div>

              <div>
                <Typography.Text type="secondary">请求类型</Typography.Text>
                <Typography.Paragraph style={{ margin: "8px 0 0" }}>
                  {target.record.requestTypeUrl || "暂无"}
                </Typography.Paragraph>
              </div>

              <div>
                <Typography.Text type="secondary">策略</Typography.Text>
                <div style={{ marginTop: 8 }}>{renderList(target.record.policyIds)}</div>
              </div>

              {!endpointCatalog ? (
                <Alert
                  message="当前无法读取入口目录，暂时不能修改暴露状态。"
                  type="warning"
                />
              ) : null}

              <Space wrap>
                <Button
                  loading={busyAction === "set-endpoint-exposure:public"}
                  onClick={() =>
                    void onSetEndpointExposure(target.record.endpointId, "public")
                  }
                  type="primary"
                >
                  公开入口
                </Button>
                <Button
                  loading={busyAction === "set-endpoint-exposure:internal"}
                  onClick={() =>
                    void onSetEndpointExposure(target.record.endpointId, "internal")
                  }
                >
                  设为内部
                </Button>
                <Button
                  danger
                  loading={busyAction === "set-endpoint-exposure:disabled"}
                  onClick={() =>
                    void onSetEndpointExposure(target.record.endpointId, "disabled")
                  }
                >
                  停用入口
                </Button>
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
