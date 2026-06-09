import { Alert, Button, Collapse, Input, Select, Space, Typography } from "antd";
import React from "react";
import {
  applyStudioNodeConfigurationValues,
  formatRawStudioNodeConfiguration,
  getStudioNodeConfigurationSchema,
  readStudioNodeConfigurationValues,
  type StudioNodeConfigurationField,
} from "@/shared/studio/nodeConfiguration";
import { formatConsoleMessage, t } from "@/shared/i18n/messages";
import type { StudioStepInspectorDraft } from "@/shared/studio/document";
import { parseInspectorParameters } from "@/shared/studio/document";
import { formatStudioStepTypeLabel } from "@/shared/studio/graph";

type WorkflowStudioNodeDetailPanelProps = {
  readonly error?: string;
  readonly onClose: () => void;
  readonly onConfigurationChange: (parametersText: string) => void;
  readonly stepDraft: StudioStepInspectorDraft | null;
  readonly width?: number;
};

const fieldStackStyle: React.CSSProperties = {
  display: "grid",
  gap: 6,
};

function readDraftParameters(stepDraft: StudioStepInspectorDraft): Record<string, unknown> {
  try {
    return parseInspectorParameters(stepDraft.parametersText);
  } catch {
    return {};
  }
}

function useConfigurationState(stepDraft: StudioStepInspectorDraft | null) {
  const [configurationValues, setConfigurationValues] = React.useState<
    Record<string, string>
  >({});
  const [rawConfigurationText, setRawConfigurationText] = React.useState("");

  React.useEffect(() => {
    if (!stepDraft) {
      setConfigurationValues({});
      setRawConfigurationText("");
      return;
    }

    const parameters = readDraftParameters(stepDraft);
    setConfigurationValues(
      readStudioNodeConfigurationValues(stepDraft.type, parameters),
    );
    setRawConfigurationText(formatRawStudioNodeConfiguration(parameters));
  }, [stepDraft?.id, stepDraft?.parametersText, stepDraft?.type]);

  return {
    configurationValues,
    rawConfigurationText,
    setConfigurationValues,
    setRawConfigurationText,
  };
}

const WorkflowStudioNodeDetailPanel: React.FC<WorkflowStudioNodeDetailPanelProps> = ({
  error,
  onClose,
  onConfigurationChange,
  stepDraft,
  width = 420,
}) => {
  const {
    configurationValues,
    rawConfigurationText,
    setConfigurationValues,
    setRawConfigurationText,
  } = useConfigurationState(stepDraft);

  if (!stepDraft) {
    return null;
  }

  const schema = getStudioNodeConfigurationSchema(stepDraft.type);
  const hasSemanticFields = schema.fields.length > 0;
  const nodeTypeLabel = formatStudioStepTypeLabel(stepDraft.type);

  const updateFieldValue = (fieldName: string, value: string) => {
    setConfigurationValues((current) => ({
      ...current,
      [fieldName]: value,
    }));
  };

  const applyConfigurationToDraft = () => {
    const nextParameters = applyStudioNodeConfigurationValues(
      stepDraft.type,
      readDraftParameters(stepDraft),
      configurationValues,
    );
    onConfigurationChange(formatRawStudioNodeConfiguration(nextParameters));
  };

  const applyRawConfigurationToDraft = () => {
    onConfigurationChange(rawConfigurationText);
  };

  const renderFieldControl = (field: StudioNodeConfigurationField) => {
    const value = configurationValues[field.name] ?? "";
    if (field.kind === "select") {
      return (
        <Select
          aria-label={formatConsoleMessage(field.label)}
          onChange={(nextValue) => updateFieldValue(field.name, nextValue)}
          options={(field.options ?? []).map((option) => ({
            label: formatConsoleMessage(option.label),
            value: option.value,
          }))}
          value={value || undefined}
        />
      );
    }

    if (field.kind === "multi-line") {
      return (
        <Input.TextArea
          aria-label={formatConsoleMessage(field.label)}
          autoSize={{ minRows: 4, maxRows: 10 }}
          onChange={(event) => updateFieldValue(field.name, event.target.value)}
          placeholder={
            field.placeholder ? formatConsoleMessage(field.placeholder) : undefined
          }
          value={value}
        />
      );
    }

    return (
      <Input
        aria-label={formatConsoleMessage(field.label)}
        onChange={(event) => updateFieldValue(field.name, event.target.value)}
        placeholder={
          field.placeholder ? formatConsoleMessage(field.placeholder) : undefined
        }
        value={value}
      />
    );
  };

  return (
    <aside
      aria-label={t(
        "teamMemberWorkflowStudio.nodeDetail.sectionAria",
        "Node detail",
      )}
      style={{
        background: "#ffffff",
        borderLeft: "1px solid #e5e7eb",
        display: "flex",
        flexDirection: "column",
        flexShrink: 0,
        minHeight: 0,
        width,
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
              {nodeTypeLabel}
            </Typography.Text>
            <Typography.Paragraph
              style={{ color: "#6b7280", margin: "2px 0 0" }}
            >
              {t("teamMemberWorkflowStudio.nodeDetail.stepId", "Step ID: {stepId}", {
                stepId: stepDraft.id,
              })}
            </Typography.Paragraph>
          </div>
          <Button onClick={onClose} size="small">
            {t("teamMemberWorkflowStudio.common.close", "Close")}
          </Button>
        </Space>
      </header>
      <div
        style={{
          display: "grid",
          gap: 16,
          overflow: "auto",
          padding: 18,
        }}
      >
        <section style={{ display: "grid", gap: 14 }}>
          <Space
            align="start"
            style={{ justifyContent: "space-between", width: "100%" }}
          >
            <div style={{ minWidth: 0 }}>
              <Typography.Text strong>
                {t(
                  "teamMemberWorkflowStudio.nodeDetail.configuration",
                  "Configuration",
                )}
              </Typography.Text>
              <Typography.Paragraph
                style={{ color: "#6b7280", margin: "2px 0 0" }}
              >
                {t(
                  "teamMemberWorkflowStudio.nodeDetail.configurationDescription",
                  "Edit the fields this node uses when the draft runs.",
                )}
              </Typography.Paragraph>
            </div>
            <Button
              onClick={applyConfigurationToDraft}
              size="small"
              type="primary"
            >
              {t(
                "teamMemberWorkflowStudio.nodeDetail.updateNode",
                "Update node",
              )}
            </Button>
          </Space>

          {hasSemanticFields ? (
            schema.fields.map((field) => (
              <div key={field.name} style={fieldStackStyle}>
                <Typography.Text strong style={{ color: "#374151", fontSize: 13 }}>
                  {formatConsoleMessage(field.label)}
                </Typography.Text>
                {renderFieldControl(field)}
                {field.description ? (
                  <Typography.Text style={{ color: "#6b7280", fontSize: 12 }}>
                    {formatConsoleMessage(field.description)}
                  </Typography.Text>
                ) : null}
              </div>
            ))
          ) : (
            <Alert
              message={t(
                "teamMemberWorkflowStudio.nodeDetail.noSemanticFields",
                "This node type does not have guided fields yet. Use advanced raw configuration when needed.",
              )}
              showIcon
              type="info"
            />
          )}

          {error ? (
            <Alert
              message={error}
              showIcon
              type="error"
            />
          ) : null}
        </section>

        <Collapse
          bordered={false}
          items={[
            {
              key: "raw-configuration",
              label: t(
                "teamMemberWorkflowStudio.nodeDetail.advancedRawConfiguration",
                "Advanced raw configuration",
              ),
              children: (
                <div style={{ display: "grid", gap: 10 }}>
                  <Typography.Paragraph style={{ color: "#6b7280", margin: 0 }}>
                    {t(
                      "teamMemberWorkflowStudio.nodeDetail.advancedRawConfigurationDescription",
                      "Use this only when a node option is not available as a guided field.",
                    )}
                  </Typography.Paragraph>
                  <Input.TextArea
                    aria-label={t(
                      "teamMemberWorkflowStudio.nodeDetail.rawConfigurationAria",
                      "Raw node configuration",
                    )}
                    autoSize={{ minRows: 8, maxRows: 16 }}
                    onChange={(event) => setRawConfigurationText(event.target.value)}
                    spellCheck={false}
                    style={{
                      fontFamily:
                        "SFMono-Regular, Consolas, Liberation Mono, Menlo, monospace",
                    }}
                    value={rawConfigurationText}
                  />
                  <Button onClick={applyRawConfigurationToDraft} size="small">
                    {t(
                      "teamMemberWorkflowStudio.nodeDetail.applyRawConfiguration",
                      "Apply raw JSON",
                    )}
                  </Button>
                </div>
              ),
            },
          ]}
          style={{
            background: "#f9fafb",
            border: "1px solid #e5e7eb",
            borderRadius: 8,
          }}
        />
      </div>
    </aside>
  );
};

export default WorkflowStudioNodeDetailPanel;
