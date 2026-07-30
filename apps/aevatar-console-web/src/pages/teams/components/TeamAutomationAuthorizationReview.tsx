import { CheckCircleOutlined, SafetyCertificateOutlined } from "@ant-design/icons";
import { Alert, Button, Space, Tag, Typography } from "antd";
import { useIntl } from "@umijs/max";
import React from "react";
import type { TeamAutomationPermissionReview } from "@/shared/api/teamAutomationApi";

type Props = {
  readonly busy: boolean;
  readonly onCancel: () => void;
  readonly onConfirm: () => void;
  readonly review: TeamAutomationPermissionReview;
};

export default function TeamAutomationAuthorizationReview({
  busy,
  onCancel,
  onConfirm,
  review,
}: Props) {
  const intl = useIntl();
  const selection = review.ownerLLMSelection;
  const disclosureLabel = (disclosure: string) =>
    intl.formatMessage({
      id: `teams.automations.authorization.disclosure.${disclosure}`,
      defaultMessage: disclosure.replaceAll("_", " "),
    });
  return (
    <div aria-describedby="team-automation-authorization-description">
      <Alert
        icon={<SafetyCertificateOutlined />}
        message={intl.formatMessage({
          id: "teams.automations.authorization.title",
          defaultMessage: "Dedicated Agent Key",
        })}
        description={intl.formatMessage({
          id: "teams.automations.authorization.description",
          defaultMessage:
            "Aevatar keeps this schedule's restricted key in its Vault. The browser never receives the key. Pausing preserves it; deleting revokes it.",
        })}
        showIcon
        type="info"
      />
      <div style={{ display: "grid", gap: 14, marginTop: 18 }}>
        <div>
          <Typography.Text type="secondary">
            {intl.formatMessage({
              id: "teams.automations.authorization.serviceModel",
              defaultMessage: "Service and model",
            })}
          </Typography.Text>
          <Typography.Paragraph strong style={{ margin: "4px 0 0" }}>
            {selection.serviceSlugSnapshot || selection.routeKind} / {selection.model}
          </Typography.Paragraph>
        </div>
        <div>
          <Typography.Text type="secondary">
            {intl.formatMessage({
              id: "teams.automations.authorization.exactAccess",
              defaultMessage: "Exact access",
            })}
          </Typography.Text>
          <Space size={[6, 6]} wrap style={{ display: "flex", marginTop: 6 }}>
            {review.credentialPlan.scopes.map((scope) => <Tag key={scope}>{scope}</Tag>)}
            {review.serviceGrants.flatMap((grant) =>
              grant.nodeIds.map((nodeId) => <Tag key={`${grant.targetId}:${nodeId}`}>{nodeId}</Tag>),
            )}
          </Space>
        </div>
        <div>
          <Typography.Text type="secondary">
            {intl.formatMessage({
              id: "teams.automations.authorization.expiry",
              defaultMessage: "Credential expiry",
            })}
          </Typography.Text>
          <Typography.Paragraph style={{ margin: "4px 0 0" }}>
            {new Date(review.credentialPlan.expiresAt).toLocaleString()}
          </Typography.Paragraph>
        </div>
        <div id="team-automation-authorization-description">
          {review.disclosures.map((disclosure) => (
            <div key={disclosure} style={{ alignItems: "center", display: "flex", gap: 8, marginTop: 6 }}>
              <CheckCircleOutlined style={{ color: "var(--ant-color-success)" }} />
              <Typography.Text>{disclosureLabel(disclosure)}</Typography.Text>
            </div>
          ))}
        </div>
        <details>
          <summary>
            {intl.formatMessage({
              id: "teams.automations.authorization.diagnostics",
              defaultMessage: "Authorization diagnostics",
            })}
          </summary>
          <Typography.Paragraph copyable style={{ marginTop: 8, overflowWrap: "anywhere" }}>
            {review.permissionDigest}
          </Typography.Paragraph>
        </details>
      </div>
      <Space style={{ display: "flex", justifyContent: "flex-end", marginTop: 20 }}>
        <Button disabled={busy} onClick={onCancel}>
          {intl.formatMessage({
            id: "teams.automations.authorization.back",
            defaultMessage: "Back",
          })}
        </Button>
        <Button loading={busy} onClick={onConfirm} type="primary">
          {intl.formatMessage({
            id: "teams.automations.authorization.confirm",
            defaultMessage: "Authorize and continue",
          })}
        </Button>
      </Space>
    </div>
  );
}
