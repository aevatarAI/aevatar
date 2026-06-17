import { FileTextOutlined } from "@ant-design/icons";
import { Alert, Button, Space } from "antd";
import React from "react";
import { t } from "@/shared/i18n/messages";
import WorkflowStudioSidePanel from "./WorkflowStudioSidePanel";

type WorkflowStudioPasteYamlPanelProps = {
  readonly error: string;
  readonly onClose: () => void;
  readonly onImport: (yaml: string) => Promise<void>;
  readonly open: boolean;
  readonly pending: boolean;
  readonly width: number;
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
    <WorkflowStudioSidePanel
      ariaLabel={t(
        "teamMemberWorkflowStudio.yamlImportPanel.sectionAria",
        "Paste workflow YAML panel",
      )}
      bodyStyle={{
        gridTemplateRows: error
          ? "auto minmax(0, 1fr) auto"
          : "minmax(0, 1fr) auto",
      }}
      closeAriaLabel={t(
        "teamMemberWorkflowStudio.yamlImportPanel.closeAria",
        "Close paste YAML panel",
      )}
      closeDisabled={pending}
      onClose={onClose}
      subtitle={t(
        "teamMemberWorkflowStudio.yamlImportPanel.subtitle",
        "Import into the current draft",
      )}
      title={t("teamMemberWorkflowStudio.yamlImportPanel.title", "Paste YAML")}
      width={width}
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
    </WorkflowStudioSidePanel>
  );
};

export default WorkflowStudioPasteYamlPanel;
