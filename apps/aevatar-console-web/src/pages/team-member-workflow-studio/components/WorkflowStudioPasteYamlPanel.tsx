import { CloseOutlined, FileTextOutlined } from "@ant-design/icons";
import { Alert, Button, Space, Typography } from "antd";
import React from "react";
import { t } from "@/shared/i18n/messages";

type WorkflowStudioPasteYamlPanelProps = {
  readonly error: string;
  readonly onClose: () => void;
  readonly onImport: (yaml: string) => Promise<void>;
  readonly open: boolean;
  readonly pending: boolean;
  readonly width: number;
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

const WorkflowStudioPasteYamlPanel: React.FC<WorkflowStudioPasteYamlPanelProps> = ({
  error,
  onClose,
  onImport,
  open,
  pending,
  width,
}) => {
  const [yamlText, setYamlText] = React.useState("");

  React.useEffect(() => {
    if (!open) {
      setYamlText("");
    }
  }, [open]);

  const submitYaml = React.useCallback(async () => {
    try {
      await onImport(yamlText);
      setYamlText("");
    } catch {
      // The hook owns the user-facing parse error. Keep the pasted YAML intact.
    }
  }, [onImport, yamlText]);

  if (!open) {
    return null;
  }

  return (
    <aside
      aria-label={t(
        "teamMemberWorkflowStudio.yamlImportPanel.sectionAria",
        "Paste workflow YAML panel",
      )}
      style={{
        ...panelStyle,
        flex: `0 0 ${width}px`,
        width,
      }}
    >
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
            {t("teamMemberWorkflowStudio.yamlImportPanel.title", "Paste YAML")}
          </Typography.Text>
          <Typography.Text type="secondary">
            {t(
              "teamMemberWorkflowStudio.yamlImportPanel.subtitle",
              "Import into the current draft",
            )}
          </Typography.Text>
        </div>
        <Button
          aria-label={t(
            "teamMemberWorkflowStudio.yamlImportPanel.closeAria",
            "Close paste YAML panel",
          )}
          disabled={pending}
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
            ? "auto minmax(0, 1fr) auto"
            : "minmax(0, 1fr) auto",
        }}
      >
        {error ? <Alert message={error} showIcon type="error" /> : null}
        <div
          style={{
            display: "flex",
            flex: 1,
            minHeight: 0,
            overflow: "hidden",
          }}
        >
          <textarea
            aria-label={t(
              "teamMemberWorkflowStudio.yamlImportPanel.textareaAria",
              "Workflow YAML",
            )}
            autoFocus
            onChange={(event) => setYamlText(event.target.value)}
            placeholder={t(
              "teamMemberWorkflowStudio.yamlImportPanel.placeholder",
              "name: Untitled workflow\nsteps:\n  - id: triage\n    type: llm_call",
            )}
            style={{
              border: "1px solid #d9d9d9",
              borderRadius: 6,
              boxSizing: "border-box",
              color: "#111827",
              flex: 1,
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
              width: "100%",
            }}
            value={yamlText}
            wrap="soft"
          />
        </div>
        <Space align="center" style={{ justifyContent: "flex-end" }}>
          <Button disabled={pending} onClick={onClose}>
            {t("teamMemberWorkflowStudio.yamlImportPanel.cancel", "Cancel")}
          </Button>
          <Button
            disabled={pending || !yamlText.trim()}
            icon={<FileTextOutlined />}
            loading={pending}
            onClick={() => void submitYaml()}
            type="primary"
          >
            {t("teamMemberWorkflowStudio.yamlImportPanel.import", "Import")}
          </Button>
        </Space>
      </div>
    </aside>
  );
};

export default WorkflowStudioPasteYamlPanel;
