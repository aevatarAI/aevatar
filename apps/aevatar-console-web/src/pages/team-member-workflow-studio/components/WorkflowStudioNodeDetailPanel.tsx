import { Alert, Button, Input, Space, Typography } from "antd";
import React from "react";
import type { StudioStepInspectorDraft } from "@/shared/studio/document";

type WorkflowStudioNodeDetailPanelProps = {
  readonly error?: string;
  readonly onClose: () => void;
  readonly onParametersChange: (parametersText: string) => void;
  readonly stepDraft: StudioStepInspectorDraft | null;
};

const WorkflowStudioNodeDetailPanel: React.FC<WorkflowStudioNodeDetailPanelProps> = ({
  error,
  onClose,
  onParametersChange,
  stepDraft,
}) => {
  const [parametersText, setParametersText] = React.useState(
    stepDraft?.parametersText ?? "",
  );

  React.useEffect(() => {
    setParametersText(stepDraft?.parametersText ?? "");
  }, [stepDraft?.id, stepDraft?.parametersText]);

  if (!stepDraft) {
    return null;
  }

  return (
    <aside
      aria-label="Node detail"
      style={{
        background: "#ffffff",
        borderLeft: "1px solid #e5e7eb",
        display: "flex",
        flexDirection: "column",
        flexShrink: 0,
        minHeight: 0,
        width: 420,
      }}
    >
      <header
        style={{
          borderBottom: "1px solid #e5e7eb",
          padding: "16px 18px",
        }}
      >
        <Space align="start" style={{ justifyContent: "space-between", width: "100%" }}>
          <div style={{ minWidth: 0 }}>
            <Typography.Text strong style={{ color: "#111827", fontSize: 16 }}>
              {stepDraft.id}
            </Typography.Text>
            <Typography.Paragraph
              style={{ color: "#6b7280", margin: "2px 0 0" }}
            >
              {stepDraft.type}
            </Typography.Paragraph>
          </div>
          <Button onClick={onClose} size="small">
            Close
          </Button>
        </Space>
      </header>
      <div
        style={{
          display: "grid",
          gap: 14,
          overflow: "auto",
          padding: 18,
        }}
      >
        <section>
          <Typography.Text strong>Input</Typography.Text>
          <Typography.Paragraph style={{ color: "#6b7280", marginTop: 8 }}>
            Input preview will appear here when workflow execution is wired.
          </Typography.Paragraph>
        </section>
        <section>
          <Space
            align="center"
            style={{ justifyContent: "space-between", width: "100%" }}
          >
            <Typography.Text strong>Parameters</Typography.Text>
            <Button
              onClick={() => onParametersChange(parametersText)}
              size="small"
              type="primary"
            >
              Apply
            </Button>
          </Space>
          <Input.TextArea
            aria-label="Node parameters"
            autoSize={{ minRows: 9, maxRows: 18 }}
            onChange={(event) => setParametersText(event.target.value)}
            spellCheck={false}
            style={{
              fontFamily:
                "SFMono-Regular, Consolas, Liberation Mono, Menlo, monospace",
              marginTop: 8,
            }}
            value={parametersText}
          />
          {error ? (
            <Alert
              message={error}
              showIcon
              style={{ marginTop: 10 }}
              type="error"
            />
          ) : null}
        </section>
        <section>
          <Typography.Text strong>Output</Typography.Text>
          <Typography.Paragraph style={{ color: "#6b7280", marginTop: 8 }}>
            Output remains empty until a real workflow execution result is available.
          </Typography.Paragraph>
        </section>
      </div>
    </aside>
  );
};

export default WorkflowStudioNodeDetailPanel;
