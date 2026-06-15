import { CopyOutlined } from "@ant-design/icons";
import { Alert, Button, Empty, Space, Spin, message } from "antd";
import React from "react";
import { t } from "@/shared/i18n/messages";
import WorkflowStudioSidePanel from "./WorkflowStudioSidePanel";

type WorkflowStudioYamlPanelProps = {
  readonly error: string;
  readonly loading: boolean;
  readonly onClose: () => void;
  readonly onRetry: () => void;
  readonly open: boolean;
  readonly width: number;
  readonly yaml: string;
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
    <WorkflowStudioSidePanel
      ariaLabel={t(
        "teamMemberWorkflowStudio.yamlPanel.sectionAria",
        "Workflow YAML panel",
      )}
      bodyStyle={{
        gridTemplateRows: error
          ? "auto auto minmax(0, 1fr)"
          : "auto minmax(0, 1fr)",
      }}
      closeAriaLabel={t(
        "teamMemberWorkflowStudio.yamlPanel.closeAria",
        "Close YAML panel",
      )}
      onClose={onClose}
      subtitle={t(
        "teamMemberWorkflowStudio.yamlPanel.subtitle",
        "Current draft source",
      )}
      title={t("teamMemberWorkflowStudio.yamlPanel.title", "Workflow YAML")}
      width={width}
    >
      {contextHolder}
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
    </WorkflowStudioSidePanel>
  );
};

export default WorkflowStudioYamlPanel;
