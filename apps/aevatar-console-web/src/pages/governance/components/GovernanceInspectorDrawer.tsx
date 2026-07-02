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
import { AevatarCompactText } from "@/shared/ui/compactText";
import { getUserFacingIdentifierLabel } from "@/shared/ui/userFacingIdentifiers";
import type { GovernanceAuditEvent } from "./GovernanceAuditTimeline";
import {
  resolveBindingAffordance,
  resolveEndpointAffordance,
  resolveEndpointExposureAction,
  resolvePolicyAffordance,
} from "./governanceAffordance";
import { t } from "@/shared/i18n/messages";

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
    return <Typography.Text type="secondary">{t("pages.governance.governanceinspectordrawer.none.yet.2", "None yet")}</Typography.Text>;
  }

  return (
    <Space orientation="vertical" size={6} style={{ display: "flex" }}>
      {values.map((value) => (
        <AevatarCompactText key={value} monospace value={value} />
      ))}
    </Space>
  );
}

function renderFactNotice(summary: string) {
  return (
    <Alert
      message={summary}
      showIcon
      type="info"
    />
  );
}

function formatInspectorResourceLabel(
  value: string | null | undefined,
  fallback: string,
): string {
  return getUserFacingIdentifierLabel(value, fallback);
}

function buildInspectorTitle(target: GovernanceInspectorTarget | null): React.ReactNode {
  if (!target) {
    return t("pages.governance.governanceinspectordrawer.governance.details.3", "Governance details");
  }

  if (target.kind === "policy") {
    return target.mode === "create" ? (
      t("pages.governance.governanceinspectordrawer.new.strategy.2", "New strategy")
    ) : (
      formatInspectorResourceLabel(
        target.record.displayName || target.record.policyId,
        t("pages.governance.governanceinspectordrawer.strategy", "Strategy"),
      )
    );
  }

  if (target.kind === "binding") {
    return target.mode === "create" ? (
      t("pages.governance.governanceinspectordrawer.new.binding.2", "New binding")
    ) : (
      formatInspectorResourceLabel(
        target.record.displayName || target.record.bindingId,
        t("pages.governance.governanceinspectordrawer.binding", "Binding"),
      )
    );
  }

  if (target.kind === "endpoint") {
    return target.mode === "create" ? (
      t("pages.governance.governanceinspectordrawer.new.entrance.2", "New entrance")
    ) : (
      formatInspectorResourceLabel(
        target.record.displayName || target.record.endpointId,
        t("pages.governance.governanceinspectordrawer.entrance", "Entrance"),
      )
    );
  }

  if (target.kind === "activation") {
    return t("pages.governance.governanceinspectordrawer.activation.verification.2", "activation verification");
  }

  if (target.kind === "audit") {
    return t("pages.governance.governanceinspectordrawer.change.history.2", "Change history");
  }

  return t("pages.governance.governanceinspectordrawer.governance.details.4", "Governance details");
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
  const policyAffordance =
    target?.kind === "policy" ? resolvePolicyAffordance(target.record) : null;
  const bindingAffordance =
    target?.kind === "binding" ? resolveBindingAffordance(target.record) : null;
  const endpointAffordance =
    target?.kind === "endpoint" && target.mode === "edit"
      ? resolveEndpointAffordance(target.record, endpointCatalog)
      : null;
  const endpointExposureAction =
    target?.kind === "endpoint" && target.mode === "edit"
      ? resolveEndpointExposureAction(target.record, endpointCatalog)
      : null;
  const canEditPolicy =
    canManage && (target?.kind !== "policy" || policyAffordance?.editMode === "writable");
  const canEditBinding =
    canManage &&
    (target?.kind !== "binding" || bindingAffordance?.editMode === "writable");
  const canEditEndpoint =
    canManage &&
    (target?.kind !== "endpoint" ||
      target.mode === "create" ||
      endpointAffordance?.editMode === "writable");

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
      title={buildInspectorTitle(target)}
    >
      <div style={aevatarDrawerScrollStyle}>
        {!canManage ? (
          <Alert
            message={t("pages.governance.governanceinspectordrawer.please.select.service.first.2", "Please select a service first")}
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
	                    ? t("pages.governance.governanceinspectordrawer.create.new.governance.policy.2", "Create a new governance policy")
	                    : formatInspectorResourceLabel(
	                        target.record.displayName || target.record.policyId,
	                        t("pages.governance.governanceinspectordrawer.strategy", "Strategy"),
	                      )}
	                </Typography.Text>
                {target.mode === "edit" ? (
                  <span
                    style={buildAevatarTagStyle(
                      surfaceToken,
                      "governance",
                      policyAffordance?.status ?? buildPolicyStatus(target.record),
                    )}
                  >
                    {policyAffordance?.statusLabel ??
                      formatAevatarStatusLabel(buildPolicyStatus(target.record))}
                  </span>
                ) : null}
              </Space>

              {policyAffordance ? renderFactNotice(policyAffordance.summary) : null}

              <Form<PolicyFormValues>
                form={policyForm}
                layout="vertical"
                disabled={!canEditPolicy}
              >
                <Form.Item
                  label={t("pages.governance.governanceinspectordrawer.policy.id.2", "Policy ID")}
                  name="policyId"
                  rules={[{ required: true, message: t("pages.governance.governanceinspectordrawer.please.fill.in.the.9", "Please fill in the policy ID.") }]}
                >
                  <Input disabled={target.mode === "edit"} />
                </Form.Item>
                <Form.Item
                  label={t("pages.governance.governanceinspectordrawer.display.name.4", "display name")}
                  name="displayName"
                  rules={[{ required: true, message: t("pages.governance.governanceinspectordrawer.please.enter.display.name.4", "Please enter a display name.") }]}
                >
                  <Input />
                </Form.Item>
                <Form.Item
                  label={t("pages.governance.governanceinspectordrawer.activate.dependency.binding.2", "Activate dependency binding")}
                  name="activationRequiredBindingIds"
                >
                  <Input.TextArea
                    autoSize={{ minRows: 3, maxRows: 6 }}
                    placeholder={t("pages.governance.governanceinspectordrawer.one.binding.id.per.2", "One binding ID per line")}
                  />
                </Form.Item>
                <Form.Item
                  label={t("pages.governance.governanceinspectordrawer.service.key.allowed.to.2", "Service Key allowed to be called")}
                  name="invokeAllowedCallerServiceKeys"
                >
                  <Input.TextArea
                    autoSize={{ minRows: 3, maxRows: 6 }}
                    placeholder={t("pages.governance.governanceinspectordrawer.team.app.namespace.service.2", "team/app/namespace/service")}
                  />
                </Form.Item>
                <Form.Item
                  label={t("pages.governance.governanceinspectordrawer.requires.deployment.to.be.2", "Requires deployment to be activated")}
                  name="invokeRequiresActiveDeployment"
                  valuePropName="checked"
                >
                  <Switch />
                </Form.Item>
              </Form>

              <Space wrap>
                {canEditPolicy ? (
                  <Button
                    loading={busyAction === policyAction}
                    onClick={() => void submitPolicy()}
                    type="primary"
                  >
                    {target.mode === "create" ? t("pages.governance.governanceinspectordrawer.create.policy.2", "Create policy") : t("pages.governance.governanceinspectordrawer.save.strategy.2", "Save strategy")}
                  </Button>
                ) : null}
                {target.mode === "edit" && canEditPolicy ? (
                  <Button
                    danger
                    loading={busyAction === "retire-policy"}
                    onClick={() => void onRetirePolicy(target.record.policyId)}
                  >
                    {t("pages.governance.governanceinspectordrawer.offline.strategy.2", "Offline strategy")}</Button>
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
                          ? t("pages.governance.governanceinspectordrawer.create.new.governance.binding.2", "Create a new governance binding")
                          : formatInspectorResourceLabel(
                              target.record.displayName || target.record.bindingId,
                              t("pages.governance.governanceinspectordrawer.binding", "Binding"),
                            )}
                      </Typography.Text>
                {target.mode === "edit" ? (
                  <span
                    style={buildAevatarTagStyle(
                      surfaceToken,
                      "governance",
                      bindingAffordance?.status ?? buildBindingStatus(target.record),
                    )}
                  >
                    {bindingAffordance?.statusLabel ??
                      formatAevatarStatusLabel(buildBindingStatus(target.record))}
                  </span>
                ) : null}
              </Space>

              {bindingAffordance ? renderFactNotice(bindingAffordance.summary) : null}

              <Form<BindingFormValues>
                form={bindingForm}
                layout="vertical"
                disabled={!canEditBinding}
              >
                <div
                  style={{
                    display: "grid",
                    gap: 12,
                    gridTemplateColumns: "repeat(2, minmax(0, 1fr))",
                  }}
                >
                  <Form.Item
                    label={t("pages.governance.governanceinspectordrawer.binding.id.2", "Binding ID")}
                    name="bindingId"
                    rules={[{ required: true, message: t("pages.governance.governanceinspectordrawer.please.fill.in.the.10", "Please fill in the binding ID.") }]}
                  >
                    <Input disabled={target.mode === "edit"} />
                  </Form.Item>
                  <Form.Item
                    label={t("pages.governance.governanceinspectordrawer.display.name.5", "display name")}
                    name="displayName"
                    rules={[{ required: true, message: t("pages.governance.governanceinspectordrawer.please.enter.display.name.5", "Please enter a display name.") }]}
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
                    label={t("pages.governance.governanceinspectordrawer.binding.type.2", "binding type")}
                    name="bindingKind"
                    rules={[{ required: true, message: t("pages.governance.governanceinspectordrawer.please.select.binding.type.2", "Please select a binding type.") }]}
                  >
                    <Select
                      options={[
                        { label: "Service", value: "service" },
                        { label: "Connector", value: "connector" },
                        { label: "Secret", value: "secret" },
                      ]}
                    />
                  </Form.Item>
                  <Form.Item label={t("pages.governance.governanceinspectordrawer.mount.strategy.3", "Mount strategy")} name="policyIds">
                    <Select
                      mode="tags"
                      options={policyOptions.map((policyId) => ({
                        label: policyId,
                        value: policyId,
                      }))}
                      placeholder={t("pages.governance.governanceinspectordrawer.select.or.enter.policy.3", "Select or enter policy ID")}
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
                        label={t("pages.governance.governanceinspectordrawer.target.service.id.2", "Target service ID")}
                        name="serviceId"
                        rules={[{ required: true, message: t("pages.governance.governanceinspectordrawer.please.fill.in.the.11", "Please fill in the target service ID.") }]}
                      >
                        <Input placeholder="dependency-service" />
                      </Form.Item>
                      <Form.Item label={t("pages.governance.governanceinspectordrawer.target.endpoint.2", "target endpoint")} name="endpointId">
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
                        label={t("pages.governance.governanceinspectordrawer.target.tenant.2", "Target tenant")}
                        name="serviceTenantId"
                        extra={t("pages.governance.governanceinspectordrawer.leave.blank.to.reuse.4", "Leave blank to reuse the tenant of the current service.")}
                      >
                        <Input placeholder={identity?.tenantId ?? ""} />
                      </Form.Item>
                      <Form.Item
                        label={t("pages.governance.governanceinspectordrawer.target.app.2", "target app")}
                        name="serviceAppId"
                        extra={t("pages.governance.governanceinspectordrawer.leave.blank.to.reuse.5", "Leave blank to reuse the current serving app.")}
                      >
                        <Input placeholder={identity?.appId ?? ""} />
                      </Form.Item>
                      <Form.Item
                        label={t("pages.governance.governanceinspectordrawer.target.namespace.2", "target namespace")}
                        name="serviceNamespace"
                        extra={t("pages.governance.governanceinspectordrawer.leave.blank.to.reuse.6", "Leave blank to reuse the namespace of the current service.")}
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
                      label={t("pages.governance.governanceinspectordrawer.connector.type.2", "Connector type")}
                      name="connectorType"
                      rules={[{ required: true, message: t("pages.governance.governanceinspectordrawer.please.fill.in.the.12", "Please fill in the connector type.") }]}
                    >
                      <Input placeholder="mcp" />
                    </Form.Item>
                    <Form.Item
                      label={t("pages.governance.governanceinspectordrawer.connector.id.2", "Connector ID")}
                      name="connectorId"
                      rules={[{ required: true, message: t("pages.governance.governanceinspectordrawer.please.fill.in.the.13", "Please fill in the connector ID.") }]}
                    >
                      <Input placeholder="connector-1" />
                    </Form.Item>
                  </div>
                ) : null}

                {bindingKind === "secret" ? (
                  <Form.Item
                    label={t("pages.governance.governanceinspectordrawer.secret.name.2", "Secret name")}
                    name="secretName"
                    rules={[{ required: true, message: t("pages.governance.governanceinspectordrawer.please.fill.in.the.14", "Please fill in the secret name.") }]}
                  >
                    <Input placeholder="api-key" />
                  </Form.Item>
                ) : null}
              </Form>

              <Space wrap>
                {canEditBinding ? (
                  <Button
                    loading={busyAction === bindingAction}
                    onClick={() => void submitBinding()}
                    type="primary"
                  >
                    {target.mode === "create" ? t("pages.governance.governanceinspectordrawer.create.binding.2", "Create binding") : t("pages.governance.governanceinspectordrawer.save.binding.2", "save binding")}
                  </Button>
                ) : null}
                {target.mode === "edit" && canEditBinding ? (
                  <Button
                    danger
                    loading={busyAction === "retire-binding"}
                    onClick={() => void onRetireBinding(target.record.bindingId)}
                  >
                    {t("pages.governance.governanceinspectordrawer.offline.binding.2", "Offline binding")}</Button>
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
                        ? t("pages.governance.governanceinspectordrawer.add.new.management.entrance.2", "Add a new management entrance")
                        : formatInspectorResourceLabel(
                            target.record.displayName || target.record.endpointId,
                            t("pages.governance.governanceinspectordrawer.entrance", "Entrance"),
                          )}
                    </Typography.Text>
                {target.mode === "edit" ? (
                  <span
                    style={buildAevatarTagStyle(
                      surfaceToken,
                      "governance",
                      endpointAffordance?.status ?? buildEndpointStatus(target.record),
                    )}
                  >
                    {endpointAffordance?.statusLabel ??
                      formatAevatarStatusLabel(buildEndpointStatus(target.record))}
                  </span>
                ) : null}
              </Space>

              {endpointAffordance ? renderFactNotice(endpointAffordance.summary) : null}

              <Form<EndpointFormValues>
                form={endpointForm}
                layout="vertical"
                disabled={!canEditEndpoint}
              >
                <div
                  style={{
                    display: "grid",
                    gap: 12,
                    gridTemplateColumns: "repeat(2, minmax(0, 1fr))",
                  }}
                >
                  <Form.Item
                    label={t("pages.governance.governanceinspectordrawer.portal.id.2", "Portal ID")}
                    name="endpointId"
                    rules={[{ required: true, message: t("pages.governance.governanceinspectordrawer.please.fill.in.the.15", "Please fill in the portal ID.") }]}
                  >
                    <Input disabled={target.mode === "edit"} />
                  </Form.Item>
                  <Form.Item
                    label={t("pages.governance.governanceinspectordrawer.display.name.6", "display name")}
                    name="displayName"
                    rules={[{ required: true, message: t("pages.governance.governanceinspectordrawer.please.enter.display.name.6", "Please enter a display name.") }]}
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
                    label={t("pages.governance.governanceinspectordrawer.entrance.type.2", "Entrance type")}
                    name="kind"
                    rules={[{ required: true, message: t("pages.governance.governanceinspectordrawer.please.select.an.entrance.2", "Please select an entrance type.") }]}
                  >
                    <Select
                      options={[
                        { label: "Command", value: "command" },
                        { label: "Chat", value: "chat" },
                      ]}
                    />
                  </Form.Item>
                  <Form.Item
                    label={t("pages.governance.governanceinspectordrawer.exposure.status.2", "exposure status")}
                    name="exposureKind"
                    rules={[{ required: true, message: t("pages.governance.governanceinspectordrawer.please.select.an.exposure.2", "Please select an exposure status.") }]}
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
                  label={t("pages.governance.governanceinspectordrawer.request.type.2", "Request type")}
                  name="requestTypeUrl"
                  rules={[{ required: true, message: t("pages.governance.governanceinspectordrawer.please.fill.in.the.16", "Please fill in the request type.") }]}
                >
                  <Input />
                </Form.Item>
                <Form.Item label={t("pages.governance.governanceinspectordrawer.response.type.2", "response type")} name="responseTypeUrl">
                  <Input />
                </Form.Item>
                <Form.Item label={t("pages.governance.governanceinspectordrawer.describe.2", "describe")} name="description">
                  <Input.TextArea autoSize={{ minRows: 2, maxRows: 4 }} />
                </Form.Item>
                <Form.Item label={t("pages.governance.governanceinspectordrawer.mount.strategy.4", "Mount strategy")} name="policyIds">
                  <Select
                    mode="tags"
                    options={policyOptions.map((policyId) => ({
                      label: policyId,
                      value: policyId,
                    }))}
                    placeholder={t("pages.governance.governanceinspectordrawer.select.or.enter.policy.4", "Select or enter policy ID")}
                  />
                </Form.Item>
              </Form>

              {!endpointCatalog ? (
                <Alert
                  message={
                    target.mode === "create"
                      ? t("pages.governance.governanceinspectordrawer.there.is.currently.no.2", "There is currently no entry catalog, and the first endpoint catalog will be created after saving.")
                      : t("pages.governance.governanceinspectordrawer.the.entry.directory.cannot.2", "The entry directory cannot be read currently, and the exposure status cannot be modified at the moment.")
                  }
                  type={target.mode === "create" ? "info" : "warning"}
                />
              ) : null}

              <Space wrap>
                <Button
                  disabled={!canEditEndpoint}
                  loading={busyAction === endpointAction}
                  onClick={() => void submitEndpoint()}
                  type="primary"
                >
                  {target.mode === "create" ? t("pages.governance.governanceinspectordrawer.create.portal.2", "Create portal") : t("pages.governance.governanceinspectordrawer.save.entry.2", "Save entry")}
                </Button>
                {target.mode === "edit" && endpointExposureAction ? (
                  <Button
                    disabled={endpointExposureAction.disabled}
                    loading={busyAction === "set-endpoint-exposure:public"}
                    onClick={() =>
                      void onSetEndpointExposure(
                        target.record.endpointId,
                        endpointExposureAction.nextExposureKind,
                      )
                    }
                  >
                    {endpointExposureAction.label}
                  </Button>
                ) : null}
              </Space>
              {target.mode === "edit" && endpointExposureAction ? (
                <Typography.Text type="secondary">
                  {endpointExposureAction.reason}
                </Typography.Text>
              ) : null}
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
              {renderFactNotice(
                t("pages.governance.governanceinspectordrawer.activation.verification.is.read", "Activation verification is a read-only diagnostic fact of the current revision and governance directory; configuration changes are not committed here."),
              )}
              <Space size={8} wrap>
                <Typography.Text strong>{t("pages.governance.governanceinspectordrawer.version.2", "Version")}</Typography.Text>
                {target.record.revisionId ? (
                  <Typography.Text>
                    {t("pages.governance.governanceinspectordrawer.version.ready", "Version ready")}
                  </Typography.Text>
                ) : (
                  <Typography.Text type="secondary">{t("pages.governance.governanceinspectordrawer.unresolved.2", "Unresolved")}</Typography.Text>
                )}
                <Typography.Text strong>{t("pages.governance.governanceinspectordrawer.activation.check.2", "activation check")}</Typography.Text>
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
                  t("pages.governance.governanceinspectordrawer.binding.2", "binding"),
                  String(target.record.bindings.length),
                )}
                {renderMetric(
                  surfaceToken,
                  t("pages.governance.governanceinspectordrawer.strategy.2", "Strategy"),
                  String(target.record.policies.length),
                )}
                {renderMetric(
                  surfaceToken,
                  t("pages.governance.governanceinspectordrawer.entrance.2", "Entrance"),
                  String(target.record.endpoints.length),
                )}
                {renderMetric(
                  surfaceToken,
                  t("pages.governance.governanceinspectordrawer.missing.strategy.3", "missing strategy"),
                  String(target.record.missingPolicyIds.length),
                  target.record.missingPolicyIds.length > 0
                    ? "warning"
                    : "success",
                )}
              </div>

              <div>
                <Typography.Text type="secondary">{t("pages.governance.governanceinspectordrawer.missing.strategy.4", "missing strategy")}</Typography.Text>
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
                  {t("pages.governance.governanceinspectordrawer.source.2", "source:")}{target.event.actor}
                </Typography.Text>
                <Space size={6} wrap>
                  <Typography.Text type="secondary">{t("pages.governance.governanceinspectordrawer.object.2", "Object:")}</Typography.Text>
                  <AevatarCompactText value={target.event.targetLabel} />
                </Space>
                <Typography.Text type="secondary">
                  {t("pages.governance.governanceinspectordrawer.time.2", "time:")}{target.event.at}
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
