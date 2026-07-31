import { CheckCircleOutlined, SafetyCertificateOutlined } from "@ant-design/icons";
import { Alert, Space, Tag, Typography } from "antd";
import { useIntl } from "@umijs/max";
import React from "react";
import type { TeamAutomationPermissionReview } from "@/shared/api/teamAutomationApi";

type Props = {
  readonly review: TeamAutomationPermissionReview;
};

export default function TeamAutomationAuthorizationReview({
  review,
}: Props) {
  const intl = useIntl();
  const selection = review.ownerLLMSelection;
  const exactAccessOccurrences = new Map<string, number>();
  const exactAccessKey = (semanticKey: string) => {
    const occurrence = exactAccessOccurrences.get(semanticKey) ?? 0;
    exactAccessOccurrences.set(semanticKey, occurrence + 1);
    return `${semanticKey}:${occurrence}`;
  };
  const disclosureLabel = (disclosure: string) =>
    intl.formatMessage({
      id: `teams.automations.authorization.disclosure.${disclosure}`,
      defaultMessage: disclosure.replaceAll("_", " "),
    });
  return (
    <div>
      <Alert
        icon={<SafetyCertificateOutlined />}
        message={intl.formatMessage({
          id: "teams.automations.authorization.title",
          defaultMessage: "Dedicated Agent Key",
        })}
        description={(
          <span id="team-automation-authorization-description">
            {intl.formatMessage({
              id: "teams.automations.authorization.description",
              defaultMessage:
                "Aevatar keeps this schedule's restricted key in its Vault. The browser never receives the key. Pausing preserves it; deleting revokes it.",
            })}
          </span>
        )}
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
            {selection
              ? `${selection.serviceSlugSnapshot || selection.routeKind} / ${selection.model}`
              : review.serviceGrants.length === 0
                ? intl.formatMessage({
                    id: "teams.automations.authorization.noExternalGrants",
                    defaultMessage:
                      "No external NyxID service or owner LLM model grant is required.",
                  })
                : intl.formatMessage({
                    id: "teams.automations.authorization.noOwnerLLMGrant",
                    defaultMessage: "No owner LLM model grant is required for this workflow.",
                  })}
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
            {review.credentialPlan.scopes.map((scope) => (
              <Tag key={exactAccessKey(`scope:${scope}`)}>{scope}</Tag>
            ))}
            {review.serviceGrants.map((grant) => (
              <Tag key={exactAccessKey(`service:${grant.grantId}`)}>{grant.displayName}</Tag>
            ))}
            {review.serviceGrants.flatMap((grant) =>
              grant.nodeIds.map((nodeId) => (
                <Tag key={exactAccessKey(`node:${grant.grantId}:${nodeId}`)}>
                  {nodeId}
                </Tag>
              )),
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
        <div>
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
    </div>
  );
}
