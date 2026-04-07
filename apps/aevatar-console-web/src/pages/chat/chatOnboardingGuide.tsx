import { Alert, Button, Input, Space, Tag, Typography } from "antd";
import React from "react";
import type { StudioProviderType } from "@/shared/studio/models";
import type { OnboardingState } from "./onboarding";

type ChatOnboardingGuideProps = {
  loading?: boolean;
  onChooseEndpointMode: (mode: "custom" | "default") => void;
  onRestart: () => void;
  onSelectProvider: (providerTypeId: string) => void;
  onSubmitApiKey: (value: string) => void;
  onSubmitCustomEndpoint: (value: string) => void;
  onSwitchToChat: () => void;
  providerTypes: readonly StudioProviderType[];
  state: OnboardingState | null;
};

const shellStyle: React.CSSProperties = {
  background: "#ffffff",
  border: "1px solid #ece8e1",
  borderRadius: 24,
  boxShadow: "0 20px 50px rgba(15, 23, 42, 0.08)",
  display: "flex",
  flexDirection: "column",
  gap: 18,
  padding: "24px 24px 22px",
};

const sectionLabelStyle: React.CSSProperties = {
  color: "#9ca3af",
  fontSize: 11,
  fontWeight: 700,
  letterSpacing: "0.12em",
  textTransform: "uppercase",
};

const helperTextStyle: React.CSSProperties = {
  color: "#6b7280",
  fontSize: 13,
  lineHeight: 1.6,
};

const providerCardStyle: React.CSSProperties = {
  background: "#ffffff",
  border: "1px solid #ece8e1",
  borderRadius: 16,
  cursor: "pointer",
  display: "flex",
  flexDirection: "column",
  gap: 8,
  minHeight: 132,
  padding: 16,
  textAlign: "left",
};

function getStepTitle(step: OnboardingState["step"] | undefined): string {
  switch (step) {
    case "select_endpoint_mode":
      return "Choose how to connect the provider";
    case "ask_custom_endpoint":
      return "Provide a custom endpoint";
    case "ask_api_key":
      return "Add the provider credential";
    case "creating":
      return "Saving provider configuration";
    case "done":
      return "Provider connected";
    case "select_provider":
    default:
      return "Connect a provider for NyxID Chat";
  }
}

function getStepDescription(
  state: OnboardingState | null,
  selectedProvider: StudioProviderType | null
): string {
  switch (state?.step) {
    case "select_endpoint_mode":
      return `Choose whether ${selectedProvider?.displayName || "this provider"} should use its default endpoint or a custom route.`;
    case "ask_custom_endpoint":
      return "Use a custom endpoint when traffic should go through your own gateway or provider proxy.";
    case "ask_api_key":
      return "The key will be saved into Studio Settings and redacted from the chat transcript.";
    case "creating":
      return "Persisting the provider in Studio Settings and preparing NyxID Chat.";
    case "done":
      return "NyxID Chat can use this provider immediately. You can switch back to the assistant or start over.";
    case "select_provider":
    default:
      return "Pick the provider you want NyxID Chat to use by default for this scope.";
  }
}

export const ChatOnboardingGuide: React.FC<ChatOnboardingGuideProps> = ({
  loading = false,
  onChooseEndpointMode,
  onRestart,
  onSelectProvider,
  onSubmitApiKey,
  onSubmitCustomEndpoint,
  onSwitchToChat,
  providerTypes,
  state,
}) => {
  const [customEndpoint, setCustomEndpoint] = React.useState("");
  const [apiKey, setApiKey] = React.useState("");

  const selectedProvider = React.useMemo(
    () =>
      state?.providerTypeId
        ? providerTypes.find((item) => item.id === state.providerTypeId) || null
        : null,
    [providerTypes, state?.providerTypeId]
  );

  React.useEffect(() => {
    setCustomEndpoint(state?.step === "ask_custom_endpoint" ? state.endpointUrl || "" : "");
  }, [state?.endpointUrl, state?.step]);

  React.useEffect(() => {
    setApiKey("");
  }, [state?.providerTypeId, state?.step]);

  return (
    <div style={shellStyle}>
      <div style={{ display: "flex", flexDirection: "column", gap: 10 }}>
        <div style={sectionLabelStyle}>Provider Setup</div>
        <div style={{ color: "#111827", fontSize: 22, fontWeight: 700 }}>
          {getStepTitle(state?.step)}
        </div>
        <div style={helperTextStyle}>
          {getStepDescription(state, selectedProvider)}
        </div>
        {selectedProvider ? (
          <Space wrap size={[8, 8]}>
            <Tag color="blue">{selectedProvider.displayName}</Tag>
            {state?.endpointUrl ? <Tag>{state.endpointUrl}</Tag> : null}
          </Space>
        ) : null}
      </div>

      {loading ? (
        <Alert
          description="Loading provider types from Studio Settings..."
          message="Preparing onboarding"
          showIcon
          type="info"
        />
      ) : null}

      {!loading && providerTypes.length === 0 ? (
        <Alert
          description="Open Studio Settings to refresh provider types, then return to onboarding."
          message="No provider types are currently available"
          showIcon
          type="warning"
        />
      ) : null}

      {!loading && providerTypes.length > 0 ? (
        <>
          {state?.step === "select_provider" || !state ? (
            <div
              style={{
                display: "grid",
                gap: 12,
                gridTemplateColumns: "repeat(auto-fit, minmax(220px, 1fr))",
              }}
            >
              {providerTypes.map((providerType) => (
                <button
                  aria-label={providerType.displayName}
                  key={providerType.id}
                  onClick={() => onSelectProvider(providerType.id)}
                  style={providerCardStyle}
                  type="button"
                >
                  <div
                    style={{
                      alignItems: "center",
                      display: "flex",
                      gap: 8,
                      justifyContent: "space-between",
                    }}
                  >
                    <span
                      style={{ color: "#111827", fontSize: 15, fontWeight: 700 }}
                    >
                      {providerType.displayName}
                    </span>
                    {providerType.recommended ? (
                      <Tag color="gold">Recommended</Tag>
                    ) : null}
                  </div>
                  <div style={helperTextStyle}>
                    {providerType.description || "Provider configuration"}
                  </div>
                  <div style={{ color: "#9ca3af", fontSize: 12 }}>
                    Default endpoint: {providerType.defaultEndpoint || "Custom endpoint required"}
                  </div>
                </button>
              ))}
            </div>
          ) : null}

          {state?.step === "select_endpoint_mode" && selectedProvider ? (
            <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
              <Alert
                description={
                  selectedProvider.defaultEndpoint
                    ? `Default endpoint: ${selectedProvider.defaultEndpoint}`
                    : "This provider requires a custom endpoint."
                }
                message={`${selectedProvider.displayName} routing`}
                showIcon
                type="info"
              />
              <Space wrap>
                <Button
                  type="primary"
                  onClick={() => onChooseEndpointMode("default")}
                >
                  Use default endpoint
                </Button>
                <Button onClick={() => onChooseEndpointMode("custom")}>
                  Set custom endpoint
                </Button>
              </Space>
            </div>
          ) : null}

          {state?.step === "ask_custom_endpoint" ? (
            <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
              <label style={{ display: "flex", flexDirection: "column", gap: 8 }}>
                <span style={sectionLabelStyle}>Custom Endpoint</span>
                <Input
                  aria-label="Onboarding custom endpoint"
                  onChange={(event) => setCustomEndpoint(event.target.value)}
                  placeholder="https://proxy.example.com/v1"
                  value={customEndpoint}
                />
              </label>
              <Space wrap>
                <Button
                  disabled={!customEndpoint.trim()}
                  onClick={() => onSubmitCustomEndpoint(customEndpoint)}
                  type="primary"
                >
                  Continue
                </Button>
                <Button onClick={onRestart}>Start over</Button>
              </Space>
            </div>
          ) : null}

          {state?.step === "ask_api_key" ? (
            <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
              <label style={{ display: "flex", flexDirection: "column", gap: 8 }}>
                <span style={sectionLabelStyle}>API Key</span>
                <Input.Password
                  aria-label="Onboarding API key"
                  onChange={(event) => setApiKey(event.target.value)}
                  placeholder="Paste the provider API key"
                  value={apiKey}
                />
              </label>
              <Typography.Text type="secondary">
                The saved key stays in Studio Settings. The chat transcript only stores a
                redacted confirmation.
              </Typography.Text>
              <Space wrap>
                <Button
                  disabled={!apiKey.trim()}
                  onClick={() => onSubmitApiKey(apiKey)}
                  type="primary"
                >
                  Save provider
                </Button>
                <Button onClick={onRestart}>Start over</Button>
              </Space>
            </div>
          ) : null}

          {state?.step === "creating" ? (
            <Alert
              description="Studio Settings is being updated with the selected provider and endpoint."
              message="Saving provider"
              showIcon
              type="info"
            />
          ) : null}

          {state?.step === "done" ? (
            <div style={{ display: "flex", flexDirection: "column", gap: 12 }}>
              <Alert
                description="The provider has been saved successfully. Switch back to NyxID Chat to continue the conversation."
                message="Onboarding complete"
                showIcon
                type="success"
              />
              <Space wrap>
                <Button onClick={onSwitchToChat} type="primary">
                  Open NyxID Chat
                </Button>
                <Button onClick={onRestart}>Configure another provider</Button>
              </Space>
            </div>
          ) : null}
        </>
      ) : null}
    </div>
  );
};
