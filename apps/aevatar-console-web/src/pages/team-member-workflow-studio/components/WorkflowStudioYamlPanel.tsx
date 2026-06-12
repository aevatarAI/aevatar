import {
  CloseOutlined,
  CopyOutlined,
} from "@ant-design/icons";
import { Alert, Button, Empty, Space, Spin, Typography, message } from "antd";
import React from "react";
import { t } from "@/shared/i18n/messages";

type WorkflowStudioYamlPanelProps = {
  readonly error: string;
  readonly loading: boolean;
  readonly onClose: () => void;
  readonly onRetry: () => void;
  readonly open: boolean;
  readonly width: number;
  readonly yaml: string;
};

const panelStyle: React.CSSProperties = {
  background: "#ffffff",
  borderLeft: "1px solid #e5e7eb",
  boxShadow: "-12px 0 28px rgba(15, 23, 42, 0.08)",
  display: "flex",
  flexDirection: "column",
  minHeight: 0,
  overflow: "hidden",
  position: "relative",
  zIndex: 2,
};

const bodyStyle: React.CSSProperties = {
  display: "grid",
  flex: 1,
  gap: 12,
  minHeight: 0,
  padding: "14px 16px 16px",
};

function fallbackCopy(text: string): boolean {
  const textarea = document.createElement("textarea");
  textarea.value = text;
  textarea.setAttribute("readonly", "true");
  textarea.style.position = "fixed";
  textarea.style.opacity = "0";
  document.body.appendChild(textarea);
  textarea.select();

  try {
    return document.execCommand("copy");
  } finally {
    document.body.removeChild(textarea);
  }
}

const WorkflowStudioYamlPanel: React.FC<WorkflowStudioYamlPanelProps> = ({
  error,
  loading,
  onClose,
  onRetry,
  open,
  width,
  yaml,
}) => {
  const [messageApi, contextHolder] = message.useMessage();

  const copyYaml = React.useCallback(async () => {
    if (!yaml.trim()) {
      return;
    }

    try {
      if (navigator.clipboard?.writeText) {
        await navigator.clipboard.writeText(yaml);
      } else if (!fallbackCopy(yaml)) {
        throw new Error("Clipboard is unavailable.");
      }
      messageApi.success(
        t("teamMemberWorkflowStudio.yamlPanel.copySuccess", "YAML copied."),
      );
    } catch (copyError) {
      messageApi.error(
        copyError instanceof Error
          ? copyError.message
          : t("teamMemberWorkflowStudio.yamlPanel.copyFailed", "Failed to copy YAML."),
      );
    }
  }, [messageApi, yaml]);

  if (!open) {
    return null;
  }

  return (
    <aside
      aria-label={t(
        "teamMemberWorkflowStudio.yamlPanel.sectionAria",
        "Workflow YAML panel",
      )}
      style={{
        ...panelStyle,
        flex: `0 0 ${width}px`,
        width,
      }}
    >
      {contextHolder}
      <header
        style={{
          alignItems: "flex-start",
          borderBottom: "1px solid #eef2f7",
          display: "flex",
          gap: 12,
          justifyContent: "space-between",
          padding: "16px 16px 14px",
        }}
      >
        <div style={{ display: "grid", gap: 4, minWidth: 0 }}>
          <Typography.Text strong>
            {t("teamMemberWorkflowStudio.yamlPanel.title", "Workflow YAML")}
          </Typography.Text>
          <Typography.Text type="secondary">
            {t(
              "teamMemberWorkflowStudio.yamlPanel.subtitle",
              "Current draft source",
            )}
          </Typography.Text>
        </div>
        <Button
          aria-label={t(
            "teamMemberWorkflowStudio.yamlPanel.closeAria",
            "Close YAML panel",
          )}
          icon={<CloseOutlined />}
          onClick={onClose}
          size="small"
          type="text"
        />
      </header>
      <div
        style={{
          ...bodyStyle,
          gridTemplateRows: error
            ? "auto auto minmax(0, 1fr)"
            : "auto minmax(0, 1fr)",
        }}
      >
        <Space align="center" size={8} wrap>
          <Button
            disabled={!yaml.trim()}
            icon={<CopyOutlined />}
            onClick={() => void copyYaml()}
            size="small"
          >
            {t("teamMemberWorkflowStudio.yamlPanel.copy", "Copy")}
          </Button>
        </Space>
        {error ? (
          <Alert
            action={
              <Button loading={loading} onClick={onRetry} size="small">
                {t("teamMemberWorkflowStudio.yamlPanel.retry", "Retry")}
              </Button>
            }
            message={error}
            showIcon
            type="error"
          />
        ) : null}
        <div
          style={{
            border: "1px solid #e5e7eb",
            borderRadius: 6,
            display: "flex",
            flex: 1,
            minHeight: 0,
            overflow: "hidden",
          }}
        >
          {loading && !yaml ? (
            <div
              style={{
                alignItems: "center",
                display: "flex",
                flex: 1,
                justifyContent: "center",
                minHeight: 0,
              }}
            >
              <Spin />
            </div>
          ) : yaml.trim() ? (
            <textarea
              aria-label={t(
                "teamMemberWorkflowStudio.yamlPanel.textareaAria",
                "Current workflow YAML",
              )}
              readOnly
              style={{
                border: 0,
                borderRadius: 0,
                boxSizing: "border-box",
                color: "#111827",
                fontFamily:
                  "ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace",
                fontSize: 12,
                height: "100%",
                lineHeight: 1.55,
                minHeight: 0,
                outline: "none",
                overflow: "auto",
                padding: "8px 11px",
                resize: "none",
                whiteSpace: "pre-wrap",
                width: "100%",
                wordBreak: "break-word",
              }}
              value={yaml}
              wrap="soft"
            />
          ) : (
            <div
              style={{
                alignItems: "center",
                display: "flex",
                flex: 1,
                justifyContent: "center",
                minHeight: 0,
                padding: 16,
              }}
            >
              <Empty
                description={t(
                  "teamMemberWorkflowStudio.yamlPanel.empty",
                  "No YAML is available for this draft.",
                )}
                image={Empty.PRESENTED_IMAGE_SIMPLE}
              />
            </div>
          )}
        </div>
      </div>
    </aside>
  );
};

export default WorkflowStudioYamlPanel;
