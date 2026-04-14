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
    return <Typography.Text type="secondary">n/a</Typography.Text>;
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
            ? "New Policy"
            : target.record.policyId
          : target?.kind === "binding"
            ? target.record.bindingId
            : target?.kind === "endpoint"
              ? target.record.endpointId
              : target?.kind === "activation"
                ? "Activation"
                : target?.kind === "audit"
                  ? "Activity"
                  : "Governance"
      }
    >
      <div style={aevatarDrawerScrollStyle}>
        {!canManage ? (
          <Alert
            message="Select a service"
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
                    ? "Promote a new governance rule"
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
                  label="Policy Id"
                  name="policyId"
                  rules={[{ required: true, message: "Policy id is required." }]}
                >
                  <Input disabled={target.mode === "edit"} />
                </Form.Item>
                <Form.Item
                  label="Display Name"
                  name="displayName"
                  rules={[
                    { required: true, message: "Display name is required." },
                  ]}
                >
                  <Input />
                </Form.Item>
                <Form.Item
                  label="Activation Required Bindings"
                  name="activationRequiredBindingIds"
                >
                  <Input.TextArea
                    autoSize={{ minRows: 3, maxRows: 6 }}
                    placeholder="One binding id per line"
                  />
                </Form.Item>
                <Form.Item
                  label="Allowed Caller Service Keys"
                  name="invokeAllowedCallerServiceKeys"
                >
                  <Input.TextArea
                    autoSize={{ minRows: 3, maxRows: 6 }}
                    placeholder="tenant/app/ns/service"
                  />
                </Form.Item>
                <Form.Item
                  label="Requires Active Deployment"
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
                  {target.mode === "create" ? "Create policy" : "Save policy"}
                </Button>
                {target.mode === "edit" ? (
                  <Button
                    danger
                    loading={busyAction === "retire-policy"}
                    onClick={() => void onRetirePolicy(target.record.policyId)}
                  >
                    Retire policy
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
                  "Binding Id",
                  target.record.bindingId,
                )}
                {renderMetric(
                  surfaceToken,
                  "Kind",
                  formatAevatarStatusLabel(target.record.bindingKind),
                  "info",
                )}
              </div>

              <div>
                <Typography.Text type="secondary">Policies</Typography.Text>
                <div style={{ marginTop: 8 }}>{renderList(target.record.policyIds)}</div>
              </div>

              <div>
                <Typography.Text type="secondary">Target</Typography.Text>
                <Typography.Paragraph style={{ margin: "8px 0 0" }}>
                  {target.record.serviceRef
                    ? `${target.record.serviceRef.identity.serviceId}:${target.record.serviceRef.endpointId || "*"}`
                    : target.record.connectorRef
                      ? `${target.record.connectorRef.connectorType}:${target.record.connectorRef.connectorId}`
                      : target.record.secretRef?.secretName || "n/a"}
                </Typography.Paragraph>
              </div>

              <Space wrap>
                <Button
                  danger
                  loading={busyAction === "retire-binding"}
                  onClick={() => void onRetireBinding(target.record.bindingId)}
                >
                  Retire binding
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
                  "Endpoint",
                  target.record.endpointId,
                )}
                {renderMetric(
                  surfaceToken,
                  "Kind",
                  formatAevatarStatusLabel(target.record.kind),
                  "info",
                )}
              </div>

              <div>
                <Typography.Text type="secondary">Request Type</Typography.Text>
                <Typography.Paragraph style={{ margin: "8px 0 0" }}>
                  {target.record.requestTypeUrl || "n/a"}
                </Typography.Paragraph>
              </div>

              <div>
                <Typography.Text type="secondary">Policies</Typography.Text>
                <div style={{ marginTop: 8 }}>{renderList(target.record.policyIds)}</div>
              </div>

              {!endpointCatalog ? (
                <Alert
                  message="The endpoint catalog is unavailable, so exposure changes are temporarily blocked."
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
                  Make public
                </Button>
                <Button
                  loading={busyAction === "set-endpoint-exposure:internal"}
                  onClick={() =>
                    void onSetEndpointExposure(target.record.endpointId, "internal")
                  }
                >
                  Set internal
                </Button>
                <Button
                  danger
                  loading={busyAction === "set-endpoint-exposure:disabled"}
                  onClick={() =>
                    void onSetEndpointExposure(target.record.endpointId, "disabled")
                  }
                >
                  Disable endpoint
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
                Revision {target.record.revisionId || "unresolved"} activation
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
                  "Bindings",
                  String(target.record.bindings.length),
                )}
                {renderMetric(
                  surfaceToken,
                  "Policies",
                  String(target.record.policies.length),
                )}
                {renderMetric(
                  surfaceToken,
                  "Endpoints",
                  String(target.record.endpoints.length),
                )}
                {renderMetric(
                  surfaceToken,
                  "Missing policies",
                  String(target.record.missingPolicyIds.length),
                  target.record.missingPolicyIds.length > 0
                    ? "warning"
                    : "success",
                )}
              </div>

              <div>
                <Typography.Text type="secondary">Missing policies</Typography.Text>
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
                  Actor: {target.event.actor}
                </Typography.Text>
                <Typography.Text type="secondary">
                  Target: {target.event.targetLabel}
                </Typography.Text>
                <Typography.Text type="secondary">
                  Timestamp: {target.event.at}
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
