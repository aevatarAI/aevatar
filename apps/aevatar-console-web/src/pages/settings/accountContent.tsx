import {
  LogoutOutlined,
  PlusOutlined,
  UserOutlined,
} from "@ant-design/icons";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  Alert,
  Avatar,
  Button,
  Empty,
  Input,
  Modal,
  Space,
  Typography,
  message,
  theme,
} from "antd";
import React, { useMemo } from "react";
import {
  automationApiKeysApi,
  type AutomationApiKeyCreateResult,
  type AutomationApiKeyListResult,
  type AutomationApiKeyMetadata,
} from "@/shared/api/automationApiKeys";
import {
  clearStoredAuthSession,
  loadRestorableAuthSession,
} from "@/shared/auth/session";
import { studioApi } from "@/shared/studio/api";
import { AevatarCompactText } from "@/shared/ui/compactText";
import { AevatarPanel } from "@/shared/ui/aevatarPageShells";
import {
  summaryFieldGridStyle,
  summaryMetricGridStyle,
} from "@/shared/ui/proComponents";
import { buildSettingsPanelStyle, SummaryField, SummaryMetric } from "./shared";
import { t } from "@/shared/i18n/messages";

function trimText(value: string | null | undefined): string {
  return value?.trim() ?? "";
}

function formatSessionExpiry(value?: number): string {
  if (!value) {
    return "Unavailable";
  }

  return new Intl.DateTimeFormat("zh-CN", {
    dateStyle: "medium",
    timeStyle: "short",
  }).format(value);
}

function formatIsoSessionExpiry(value?: string | null): string {
  if (!value) {
    return "Unavailable";
  }

  const parsed = Date.parse(value);
  if (Number.isNaN(parsed)) {
    return "Unavailable";
  }

  return formatSessionExpiry(parsed);
}

function formatNullableDate(value?: string | null): string {
  return value ? formatIsoSessionExpiry(value) : t("pages.settings.accountcontent.never", "Never");
}

function describeAutomationKeyStatus(key: AutomationApiKeyMetadata): string {
  switch (key.status) {
    case "active":
      return t("pages.settings.accountcontent.key.status.active", "Active");
    case "expired":
      return t("pages.settings.accountcontent.key.status.expired", "Expired");
    case "revoked":
      return t("pages.settings.accountcontent.key.status.revoked", "Revoked");
    default:
      return t("pages.settings.accountcontent.key.status.missing", "Missing");
  }
}

type AccountSettingsContentProps = {
  readonly showInlineSignOut?: boolean;
};

const AccountSettingsContent: React.FC<AccountSettingsContentProps> = ({
  showInlineSignOut = true,
}) => {
  const { token } = theme.useToken();
  const queryClient = useQueryClient();
  const settingsPanelStyle = buildSettingsPanelStyle(token);
  const authSession = useMemo(() => loadRestorableAuthSession(), []);
  const [apiKeyModalOpen, setApiKeyModalOpen] = React.useState(false);
  const [apiKeyDisplayName, setApiKeyDisplayName] = React.useState(
    t(
      "pages.settings.accountcontent.default.api.key.name",
      "Workflow automation key",
    ),
  );
  const [oneTimeCreateResult, setOneTimeCreateResult] =
    React.useState<AutomationApiKeyCreateResult | null>(null);
  const [apiKeyCreating, setApiKeyCreating] = React.useState(false);
  const authMeQuery = useQuery({
    queryKey: ["settings", "auth-me"],
    queryFn: () => studioApi.getAuthSession(),
  });
  const backendProfile = authMeQuery.data?.profile;
  const backendSession = authMeQuery.data?.session;
  const authenticated = Boolean(authMeQuery.data?.authenticated || authSession);
  const automationScopeId =
    trimText(authMeQuery.data?.scopeId) || trimText(backendSession?.scopeId);
  const automationKeysQueryKey = React.useMemo(
    () => ["settings", "automation-api-keys", automationScopeId] as const,
    [automationScopeId],
  );
  const automationKeysQuery = useQuery({
    enabled: authenticated && automationScopeId.length > 0,
    queryKey: automationKeysQueryKey,
    queryFn: () => automationApiKeysApi.list(automationScopeId),
  });

  const accountDisplayName = useMemo(
    () =>
      backendProfile?.name ||
      backendProfile?.email ||
      backendProfile?.subject ||
      authSession?.user.name ||
      authSession?.user.email ||
      authSession?.user.sub ||
      "No active session",
    [authSession, backendProfile],
  );
  const accountSecondaryText = useMemo(() => {
    if (!authenticated) {
      return "This browser does not have a restorable sign-in session.";
    }

    if (backendProfile?.email || backendProfile?.subject) {
      return backendProfile.email || backendProfile.subject;
    }

    return authSession?.user.email || authSession?.user.sub || "Unavailable";
  }, [authSession, authenticated, backendProfile]);
  const rolesLabel =
    (backendProfile?.roles && backendProfile.roles.length > 0
      ? backendProfile.roles.join(", ")
      : authSession?.user.roles?.join(", ")) || "No roles";
  const groupsLabel =
    (backendProfile?.groups && backendProfile.groups.length > 0
      ? backendProfile.groups.join(", ")
      : authSession?.user.groups?.join(", ")) || "No groups";
  const userId = backendProfile?.subject || authSession?.user.sub || "";
  const picture = backendProfile?.picture || authSession?.user.picture;
  const emailVerified =
    backendProfile?.emailVerified ?? authSession?.user.email_verified ?? null;
  const revokeAutomationKeyMutation = useMutation({
    mutationFn: (apiKeyId: string) =>
      automationApiKeysApi.revoke({
        scopeId: automationScopeId,
        apiKeyId,
      }),
    onError: (error) => {
      void message.error(
        t(
          "pages.settings.accountcontent.api.key.revoke.failed",
          "API key was not revoked: {message}",
          { message: error instanceof Error ? error.message : String(error) },
        ),
      );
    },
    onSuccess: (_result, apiKeyId) => {
      queryClient.setQueryData<AutomationApiKeyListResult>(
        automationKeysQueryKey,
        (current) =>
          current
            ? {
                ...current,
                items: current.items.filter((key) => key.apiKeyId !== apiKeyId),
                totalCount:
                  typeof current.totalCount === "number"
                    ? Math.max(0, current.totalCount - 1)
                    : current.totalCount,
              }
            : current,
      );
      void message.success(
        t(
          "pages.settings.accountcontent.api.key.revoke.success",
          "Automation API key revoked.",
        ),
      );
    },
  });

  const handleSignOut = () => {
    clearStoredAuthSession();
    window.location.replace("/login");
  };
  const handleCreateAutomationKey = async () => {
    if (!automationScopeId) {
      void message.error(
        t(
          "pages.settings.accountcontent.api.key.scope.missing",
          "A scope is required before creating an automation API key.",
        ),
      );
      return;
    }

    setApiKeyCreating(true);
    try {
      const result = await automationApiKeysApi.create({
        scopeId: automationScopeId,
        displayName: apiKeyDisplayName,
        scopes: ["proxy"],
      });
      setOneTimeCreateResult(result);
      queryClient.setQueryData<AutomationApiKeyListResult>(
        automationKeysQueryKey,
        (current) => ({
          items: [
            result.apiKey,
            ...(current?.items.filter(
              (key) => key.apiKeyId !== result.apiKey.apiKeyId,
            ) ?? []),
          ],
          totalCount:
            typeof current?.totalCount === "number"
              ? current.totalCount + 1
              : current?.totalCount ?? null,
        }),
      );
      void message.success(
        t(
          "pages.settings.accountcontent.api.key.create.success",
          "Automation API key created.",
        ),
      );
    } catch (error) {
      void message.error(
        t(
          "pages.settings.accountcontent.api.key.create.failed",
          "API key was not created: {message}",
          { message: error instanceof Error ? error.message : String(error) },
        ),
      );
    } finally {
      setApiKeyCreating(false);
    }
  };
  const openApiKeyModal = () => {
    setOneTimeCreateResult(null);
    setApiKeyDisplayName(
      t(
        "pages.settings.accountcontent.default.api.key.name",
        "Workflow automation key",
      ),
    );
    setApiKeyModalOpen(true);
  };
  const closeApiKeyModal = () => {
    if (apiKeyCreating) {
      return;
    }

    setApiKeyModalOpen(false);
    setOneTimeCreateResult(null);
  };

  return (
    <>
      <AevatarPanel
        extra={
          authenticated && showInlineSignOut ? (
            <Button danger icon={<LogoutOutlined />} onClick={handleSignOut}>
              {t("pages.settings.accountcontent.sign.out", "Sign out")}</Button>
          ) : null
        }
        style={settingsPanelStyle}
        title="Profile"
      >
        {authenticated ? (
          <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
            <Space align="start" size={14}>
              <Avatar
                icon={<UserOutlined />}
                size={52}
                src={picture}
              />
              <div style={{ minWidth: 0 }}>
                <Typography.Text
                  strong
                  style={{ display: "block", fontSize: 18 }}
                >
                  {accountDisplayName}
                </Typography.Text>
                <Typography.Text type="secondary">
                  {accountSecondaryText}
                </Typography.Text>
              </div>
            </Space>

            <div style={summaryMetricGridStyle}>
              <SummaryMetric
                label="Session"
                tone={backendSession?.authenticated || authSession ? "success" : "warning"}
                value={backendSession?.authenticated || authSession ? "Active" : "Browser only"}
              />
              <SummaryMetric
                label="Email"
                tone={emailVerified ? "success" : "warning"}
                value={
                  emailVerified ? "Verified" : "Needs review"
                }
              />
            </div>

            <div style={summaryFieldGridStyle}>
              <SummaryField
                label={t("pages.settings.accountcontent.user.id", "User ID")}
                value={
                  <AevatarCompactText
                    copyable
                    head={8}
                    maxWidth="100%"
                    monospace
                    tail={6}
                    value={userId}
                  />
                }
              />
              <SummaryField label="Roles" value={rolesLabel} />
              <SummaryField label="Groups" value={groupsLabel} />
            </div>
          </div>
        ) : (
          <Empty
            description={t("pages.settings.accountcontent.this.browser.does.not.have", "This browser does not have a restorable sign-in session.")}
            image={Empty.PRESENTED_IMAGE_SIMPLE}
          >
            <Button
              type="primary"
              onClick={() => window.location.replace("/login")}
            >
              {t("pages.settings.accountcontent.sign.in", "Sign in")}</Button>
          </Empty>
        )}
      </AevatarPanel>

      <AevatarPanel style={settingsPanelStyle} title="Authentication">
        <div style={{ display: "grid", gap: 12 }}>
          <Typography.Text type="secondary">
            {t(
              "pages.settings.accountcontent.oauth.browser.only",
              "OAuth tokens stay in this browser for interactive Console requests.",
            )}
          </Typography.Text>
          <div style={summaryFieldGridStyle}>
            <SummaryField
              label={t("pages.settings.accountcontent.session.expires", "Session expires")}
              value={
                backendSession?.expiresAtUtc
                  ? formatIsoSessionExpiry(backendSession.expiresAtUtc)
                  : formatSessionExpiry(authSession?.tokens.expiresAt)
              }
            />
            <SummaryField
              label={t("pages.settings.accountcontent.provider", "Provider")}
              value={authMeQuery.data?.providerDisplayName || "Unavailable"}
            />
            <SummaryField
              label={t("pages.settings.accountcontent.scope", "Scope")}
              value={automationScopeId || "Unavailable"}
            />
            <SummaryField
              label={t("pages.settings.accountcontent.browser.token.refresh", "Browser token refresh")}
              value={t("pages.settings.accountcontent.browser.token.refresh.disabled", "Disabled")}
            />
          </div>
        </div>
      </AevatarPanel>

      <AevatarPanel
        extra={
          <Button
            disabled={!automationScopeId}
            icon={<PlusOutlined />}
            onClick={openApiKeyModal}
            type="primary"
          >
            {t(
              "pages.settings.accountcontent.create.api.key",
              "Create API key",
            )}
          </Button>
        }
        style={settingsPanelStyle}
        title={t(
          "pages.settings.accountcontent.automation.api.keys",
          "Automation API keys",
        )}
      >
        <div style={{ display: "grid", gap: 12 }}>
          <Typography.Text type="secondary">
            {t(
              "pages.settings.accountcontent.automation.api.keys.description",
              "User-created keys authorize scheduled workflows after this browser session closes.",
            )}
          </Typography.Text>
          {!automationScopeId ? (
            <Empty
              description={t(
                "pages.settings.accountcontent.api.key.no.scope",
                "Sign in with a resolved scope before creating automation API keys.",
              )}
              image={Empty.PRESENTED_IMAGE_SIMPLE}
            />
          ) : automationKeysQuery.isLoading ? (
            <Typography.Text type="secondary">
              {t(
                "pages.settings.accountcontent.api.key.loading",
                "Loading automation API keys...",
              )}
            </Typography.Text>
          ) : (automationKeysQuery.data?.items.length ?? 0) === 0 ? (
            <Empty
              description={t(
                "pages.settings.accountcontent.api.key.empty",
                "No automation API keys yet.",
              )}
              image={Empty.PRESENTED_IMAGE_SIMPLE}
            />
          ) : (
            <div style={{ display: "grid", gap: 10 }}>
              {(automationKeysQuery.data?.items ?? []).map((key) => (
                <div
                  key={key.apiKeyId}
                  style={{
                    border: `1px solid ${token.colorBorderSecondary}`,
                    borderRadius: 8,
                    display: "grid",
                    gap: 10,
                    padding: 12,
                  }}
                >
                  <Space
                    align="start"
                    style={{
                      justifyContent: "space-between",
                      width: "100%",
                    }}
                  >
                    <div style={{ display: "grid", gap: 2, minWidth: 0 }}>
                      <Typography.Text strong>{key.displayName}</Typography.Text>
                      <Typography.Text style={{ fontSize: 12 }} type="secondary">
                        {describeAutomationKeyStatus(key)}
                      </Typography.Text>
                    </div>
                    <Button
                      danger
                      loading={
                        revokeAutomationKeyMutation.isPending &&
                        revokeAutomationKeyMutation.variables === key.apiKeyId
                      }
                      onClick={() => revokeAutomationKeyMutation.mutate(key.apiKeyId)}
                      size="small"
                    >
                      {t("pages.settings.accountcontent.revoke", "Revoke")}
                    </Button>
                  </Space>
                  <div style={summaryFieldGridStyle}>
                    <SummaryField
                      label={t("pages.settings.accountcontent.api.key.id", "Key ID")}
                      value={
                        <AevatarCompactText
                          copyable
                          head={8}
                          monospace
                          tail={6}
                          value={key.apiKeyId}
                        />
                      }
                    />
                    <SummaryField
                      label={t("pages.settings.accountcontent.api.key.suffix", "Suffix")}
                      value={key.keySuffix}
                    />
                    <SummaryField
                      label={t("pages.settings.accountcontent.api.key.last.used", "Last used")}
                      value={formatNullableDate(key.lastUsedAt)}
                    />
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>
      </AevatarPanel>

      <Modal
        confirmLoading={apiKeyCreating}
        okButtonProps={{
          disabled: !apiKeyDisplayName.trim() || Boolean(oneTimeCreateResult),
        }}
        okText={t("pages.settings.accountcontent.create.key", "Create key")}
        onCancel={closeApiKeyModal}
        onOk={handleCreateAutomationKey}
        open={apiKeyModalOpen}
        title={t(
          "pages.settings.accountcontent.create.api.key.title",
          "Create automation API key",
        )}
      >
        <div style={{ display: "grid", gap: 14 }}>
          <div style={{ display: "grid", gap: 8 }}>
            <label
              htmlFor="automation-api-key-name"
              style={{ fontWeight: 700 }}
            >
              {t("pages.settings.accountcontent.key.name", "Key name")}
            </label>
            <Input
              aria-label={t("pages.settings.accountcontent.key.name", "Key name")}
              id="automation-api-key-name"
              disabled={apiKeyCreating || Boolean(oneTimeCreateResult)}
              onChange={(event) => setApiKeyDisplayName(event.target.value)}
              value={apiKeyDisplayName}
            />
          </div>
          {oneTimeCreateResult ? (
            <Alert
              description={
                <div style={{ display: "grid", gap: 8 }}>
                  <Typography.Text copyable={{ text: oneTimeCreateResult.rawKey }}>
                    {oneTimeCreateResult.rawKey}
                  </Typography.Text>
                  <Typography.Text>
                    {t(
                      "pages.settings.accountcontent.raw.key.shown.once",
                      "Shown once. Store it outside Aevatar if you need the raw key.",
                    )}
                  </Typography.Text>
                </div>
              }
              message={t(
                "pages.settings.accountcontent.raw.key.ready",
                "Automation API key created",
              )}
              showIcon
              type="success"
            />
          ) : (
            <Typography.Text type="secondary">
              {t(
                "pages.settings.accountcontent.raw.key.description",
                "Aevatar stores key metadata only. The raw key is shown once after creation.",
              )}
            </Typography.Text>
          )}
        </div>
      </Modal>
    </>
  );
};

export default AccountSettingsContent;
