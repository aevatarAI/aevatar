import { CloseOutlined } from "@ant-design/icons";
import {
  Alert,
  Button,
  Collapse,
  Input,
  Select,
  Space,
  Switch,
  Typography,
  theme,
} from "antd";
import React from "react";
import {
  applyRawStudioNodeConfiguration,
  applyStudioNodeConfigurationValuesWithValidation,
  formatRawStudioNodeConfiguration,
  getStudioNodeConfigurationSchema,
  readStudioNodeConfigurationValues,
  shouldShowRawStudioNodeConfiguration,
  type StudioStructuredNodeConfigField,
} from "@/shared/studio/nodeConfigFields";
import { formatConsoleMessage, t } from "@/shared/i18n/messages";
import type { StudioStepInspectorDraft } from "@/shared/studio/document";
import { parseInspectorParameters } from "@/shared/studio/document";
import { formatStudioStepTypeLabel } from "@/shared/studio/graph";

type WorkflowStudioNodeDetailPanelProps = {
  readonly error?: string;
  readonly onClose: () => void;
  readonly onConfigurationChange: (parametersText: string) => void;
  readonly onConfigurationErrorChange: (error: string) => void;
  readonly stepDraft: StudioStepInspectorDraft | null;
};

const DEFAULT_PANEL_WIDTH = 420;
const MIN_PANEL_WIDTH = 360;
const MAX_PANEL_WIDTH = 500;

function buildInspectorCss(screenMd: number): string {
  return `
.workflow-studio-node-inspector {
  color: var(--workflow-node-inspector-text);
}

.workflow-studio-node-inspector__resize {
  align-items: center;
  background: transparent;
  border: 0;
  bottom: 0;
  cursor: ew-resize;
  display: flex;
  justify-content: center;
  left: calc(var(--workflow-node-inspector-resize-hit-width) / -2);
  padding: 0;
  position: absolute;
  top: 0;
  width: var(--workflow-node-inspector-resize-hit-width);
}

.workflow-studio-node-inspector__resize::after {
  background: var(--workflow-node-inspector-resize);
  border-radius: var(--workflow-node-inspector-pill-radius);
  content: "";
  height: var(--workflow-node-inspector-resize-grip-height);
  width: var(--workflow-node-inspector-resize-grip-width);
}

.workflow-studio-node-inspector__resize:hover::after,
.workflow-studio-node-inspector__resize:focus-visible::after {
  background: var(--workflow-node-inspector-resize-active);
}

.workflow-studio-node-inspector__body::-webkit-scrollbar {
  width: var(--workflow-node-inspector-scrollbar-width);
}

.workflow-studio-node-inspector__body::-webkit-scrollbar-thumb {
  background: var(--workflow-node-inspector-scroll-thumb);
  border: 3px solid var(--workflow-node-inspector-surface);
  border-radius: var(--workflow-node-inspector-pill-radius);
}

@media (max-width: ${screenMd}px) {
  .workflow-studio-node-inspector {
    border-left: 0 !important;
    border-radius: var(--workflow-node-inspector-mobile-radius) var(--workflow-node-inspector-mobile-radius) 0 0 !important;
    border-top: 1px solid var(--workflow-node-inspector-border) !important;
    bottom: 0 !important;
    left: 0 !important;
    max-height: calc(100vh - var(--workflow-node-inspector-mobile-offset)) !important;
    max-width: none !important;
    right: 0 !important;
    top: auto !important;
    width: auto !important;
  }

  .workflow-studio-node-inspector__resize {
    display: none;
  }
}
`;
}

const fieldStackStyle: React.CSSProperties = {
  display: "grid",
  gap: "var(--workflow-node-inspector-field-gap)",
};

type InspectorCssVariables = React.CSSProperties & Record<`--${string}`, string>;

function toPx(value: number | string): string {
  return typeof value === "number" ? `${value}px` : value;
}

function doubledPx(value: number | string): string {
  return typeof value === "number" ? `${value * 2}px` : `calc(${value} * 2)`;
}

function clampPanelWidth(width: number): number {
  return Math.min(MAX_PANEL_WIDTH, Math.max(MIN_PANEL_WIDTH, width));
}

function displayValue(value: string): string {
  return value.trim() || t("teamMemberWorkflowStudio.nodeInspector.notSet", "Not set");
}

function summarizeBranches(branchesText: string): string {
  try {
    const parsed = JSON.parse(branchesText) as unknown;
    if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) {
      return t("teamMemberWorkflowStudio.nodeInspector.noBranches", "No branches");
    }

    const entries = Object.entries(parsed)
      .map(([label, target]) => [label.trim(), String(target ?? "").trim()])
      .filter(([label, target]) => Boolean(label) && Boolean(target));

    if (entries.length === 0) {
      return t("teamMemberWorkflowStudio.nodeInspector.noBranches", "No branches");
    }

    return entries.map(([label, target]) => `${label} -> ${target}`).join(", ");
  } catch {
    return t(
      "teamMemberWorkflowStudio.nodeInspector.branchesUnavailable",
      "Branches unavailable",
    );
  }
}

function InspectorField({
  label,
  value,
}: {
  readonly label: string;
  readonly value: string;
}) {
  return (
    <div style={{ minWidth: 0 }}>
      <dt
        style={{
          color: "var(--workflow-node-inspector-muted)",
          fontSize: "var(--workflow-node-inspector-caption-size)",
          fontWeight: 600,
          lineHeight: 1.4,
        }}
      >
        {label}
      </dt>
      <dd
        style={{
          color: "var(--workflow-node-inspector-text)",
          lineHeight: 1.5,
          margin: "var(--workflow-node-inspector-field-gap) 0 0",
          overflowWrap: "anywhere",
        }}
      >
        {value}
      </dd>
    </div>
  );
}

function readDraftParameters(stepDraft: StudioStepInspectorDraft): Record<string, unknown> {
  try {
    return parseInspectorParameters(stepDraft.parametersText);
  } catch {
    return {};
  }
}

function readCurrentParameters(
  stepDraft: StudioStepInspectorDraft,
  rawConfigurationText: string,
): Record<string, unknown> {
  try {
    return parseInspectorParameters(rawConfigurationText);
  } catch {
    return readDraftParameters(stepDraft);
  }
}

function mergeSchemaParameters(
  current: Record<string, unknown>,
  next: Record<string, unknown>,
): Record<string, unknown> {
  return {
    ...current,
    ...next,
  };
}

function useConfigurationState(stepDraft: StudioStepInspectorDraft | null) {
  const [configurationValues, setConfigurationValues] = React.useState<
    Record<string, string>
  >({});
  const [rawConfigurationText, setRawConfigurationText] = React.useState("");
  const [schemaParameters, setSchemaParameters] = React.useState<
    Record<string, unknown>
  >({});
  const [structuredError, setStructuredError] = React.useState("");
  const [rawError, setRawError] = React.useState("");
  const schemaParametersRef = React.useRef<Record<string, unknown>>({});
  const stepKeyRef = React.useRef("");

  const rememberSchemaParameters = React.useCallback(
    (parameters: Record<string, unknown>): Record<string, unknown> => {
      const nextSchemaParameters = mergeSchemaParameters(
        schemaParametersRef.current,
        parameters,
      );
      schemaParametersRef.current = nextSchemaParameters;
      setSchemaParameters(nextSchemaParameters);
      return nextSchemaParameters;
    },
    [],
  );

  React.useEffect(() => {
    if (!stepDraft) {
      setConfigurationValues({});
      setRawConfigurationText("");
      schemaParametersRef.current = {};
      stepKeyRef.current = "";
      setSchemaParameters({});
      setRawError("");
      setStructuredError("");
      return;
    }

    const parameters = readDraftParameters(stepDraft);
    const stepKey = `${stepDraft.id}\u0000${stepDraft.type}`;
    const nextSchemaParameters =
      stepKeyRef.current === stepKey
        ? mergeSchemaParameters(schemaParametersRef.current, parameters)
        : parameters;
    schemaParametersRef.current = nextSchemaParameters;
    stepKeyRef.current = stepKey;
    setConfigurationValues(
      readStudioNodeConfigurationValues(
        stepDraft.type,
        parameters,
        nextSchemaParameters,
      ),
    );
    setRawConfigurationText(formatRawStudioNodeConfiguration(parameters));
    setSchemaParameters(nextSchemaParameters);
    setRawError("");
    setStructuredError("");
  }, [stepDraft?.id, stepDraft?.parametersText, stepDraft?.type]);

  return {
    configurationValues,
    rawConfigurationText,
    rawError,
    schemaParameters,
    setConfigurationValues,
    setRawConfigurationText,
    setRawError,
    rememberSchemaParameters,
    setStructuredError,
    structuredError,
  };
}

const WorkflowStudioNodeDetailPanel: React.FC<WorkflowStudioNodeDetailPanelProps> = ({
  error,
  onClose,
  onConfigurationChange,
  onConfigurationErrorChange,
  stepDraft,
}) => {
  const { token } = theme.useToken();
  const [panelWidth, setPanelWidth] = React.useState(DEFAULT_PANEL_WIDTH);
  const [resizing, setResizing] = React.useState(false);
  const resizeStartRef = React.useRef<{
    readonly startWidth: number;
    readonly startX: number;
  } | null>(null);
  const {
    configurationValues,
    rawConfigurationText,
    rawError,
    schemaParameters,
    setConfigurationValues,
    setRawConfigurationText,
    setRawError,
    rememberSchemaParameters,
    setStructuredError,
    structuredError,
  } = useConfigurationState(stepDraft);

  React.useEffect(() => {
    if (!resizing) {
      return;
    }

    const previousCursor = document.body.style.cursor;
    const previousUserSelect = document.body.style.userSelect;
    document.body.style.cursor = "ew-resize";
    document.body.style.userSelect = "none";

    return () => {
      document.body.style.cursor = previousCursor;
      document.body.style.userSelect = previousUserSelect;
    };
  }, [resizing]);

  React.useEffect(() => {
    onConfigurationErrorChange(structuredError || rawError);
  }, [onConfigurationErrorChange, rawError, structuredError]);

  if (!stepDraft) {
    return null;
  }

  const overlayInset = token.padding;
  const inspectorCss = buildInspectorCss(token.screenMD);
  const inspectorVariables: InspectorCssVariables = {
    "--workflow-node-inspector-border": token.colorBorderSecondary,
    "--workflow-node-inspector-border-strong": token.colorBorder,
    "--workflow-node-inspector-caption-size": toPx(token.fontSizeSM),
    "--workflow-node-inspector-field-gap": toPx(token.paddingXXS),
    "--workflow-node-inspector-header-gap": toPx(token.paddingSM),
    "--workflow-node-inspector-mobile-offset": token.sizeXXL
      ? toPx(token.sizeXXL)
      : token.controlHeightLG
        ? doubledPx(token.controlHeightLG)
        : doubledPx(token.paddingXL),
    "--workflow-node-inspector-mobile-radius": toPx(token.borderRadiusLG),
    "--workflow-node-inspector-muted": token.colorTextSecondary,
    "--workflow-node-inspector-panel-radius": toPx(token.borderRadiusLG),
    "--workflow-node-inspector-pill-radius": toPx(token.borderRadiusSM),
    "--workflow-node-inspector-resize": token.colorBorder,
    "--workflow-node-inspector-resize-active": token.colorPrimary,
    "--workflow-node-inspector-resize-grip-height": token.controlHeightLG
      ? toPx(token.controlHeightLG)
      : doubledPx(token.paddingLG),
    "--workflow-node-inspector-resize-grip-width": toPx(token.lineWidthBold),
    "--workflow-node-inspector-resize-hit-width": toPx(token.controlHeightXS),
    "--workflow-node-inspector-scroll-thumb": token.colorTextQuaternary,
    "--workflow-node-inspector-scrollbar-width": toPx(token.controlHeightXS),
    "--workflow-node-inspector-section-gap": toPx(token.paddingLG),
    "--workflow-node-inspector-section-radius": toPx(token.borderRadius),
    "--workflow-node-inspector-surface": token.colorBgElevated,
    "--workflow-node-inspector-surface-muted": token.colorFillAlter,
    "--workflow-node-inspector-text": token.colorText,
    "--workflow-node-inspector-text-strong": token.colorTextHeading,
  };
  const parameters = readCurrentParameters(stepDraft, rawConfigurationText);
  const schema = getStudioNodeConfigurationSchema(stepDraft.type, schemaParameters);
  const hasSemanticFields = schema.fields.length > 0;
  const showRawConfiguration = shouldShowRawStudioNodeConfiguration(
    stepDraft.type,
    schemaParameters,
  );
  const nodeTypeLabel = formatStudioStepTypeLabel(stepDraft.type);
  const branchesSummary = summarizeBranches(stepDraft.branchesText);
  const activeError = structuredError || rawError || error || "";

  const startResize = (event: React.PointerEvent<HTMLDivElement>) => {
    event.preventDefault();
    resizeStartRef.current = {
      startWidth: panelWidth,
      startX: event.clientX,
    };
    setResizing(true);
    event.currentTarget.setPointerCapture(event.pointerId);
  };

  const updateResize = (event: React.PointerEvent<HTMLDivElement>) => {
    if (!resizeStartRef.current) {
      return;
    }

    const delta = resizeStartRef.current.startX - event.clientX;
    setPanelWidth(clampPanelWidth(resizeStartRef.current.startWidth + delta));
  };

  const stopResize = (event: React.PointerEvent<HTMLDivElement>) => {
    if (!resizeStartRef.current) {
      return;
    }

    resizeStartRef.current = null;
    setResizing(false);
    if (event.currentTarget.hasPointerCapture(event.pointerId)) {
      event.currentTarget.releasePointerCapture(event.pointerId);
    }
  };

  const resizeWithKeyboard = (event: React.KeyboardEvent<HTMLDivElement>) => {
    if (event.key !== "ArrowLeft" && event.key !== "ArrowRight") {
      return;
    }

    event.preventDefault();
    const direction = event.key === "ArrowLeft" ? 1 : -1;
    setPanelWidth((currentWidth) => clampPanelWidth(currentWidth + direction * 16));
  };

  const updateFieldValue = (fieldName: string, value: string) => {
    const nextValues = {
      ...configurationValues,
      [fieldName]: value,
    };
    const result = applyStudioNodeConfigurationValuesWithValidation(
      stepDraft.type,
      parameters,
      nextValues,
      schemaParameters,
    );

    setConfigurationValues(nextValues);
    setStructuredError(result.errors[0] ?? "");
    setRawError("");
    if (result.valid) {
      setRawConfigurationText(formatRawStudioNodeConfiguration(result.parameters));
      rememberSchemaParameters(result.parameters);
    }
  };

  const applyConfigurationToDraft = () => {
    const result = applyStudioNodeConfigurationValuesWithValidation(
      stepDraft.type,
      parameters,
      configurationValues,
      schemaParameters,
    );
    const nextError = result.errors[0] ?? "";
    setStructuredError(nextError);
    setRawError("");
    if (!result.valid) {
      return;
    }

    const nextRawText = formatRawStudioNodeConfiguration(result.parameters);
    setRawConfigurationText(nextRawText);
    rememberSchemaParameters(result.parameters);
    onConfigurationChange(nextRawText);
  };

  const applyRawConfigurationToDraft = () => {
    try {
      const nextParameters = applyRawStudioNodeConfiguration(
        stepDraft.type,
        rawConfigurationText,
      );
      const nextRawText = formatRawStudioNodeConfiguration(nextParameters);
      setRawError("");
      setStructuredError("");
      setRawConfigurationText(nextRawText);
      const nextSchemaParameters = rememberSchemaParameters(nextParameters);
      setConfigurationValues(
        readStudioNodeConfigurationValues(
          stepDraft.type,
          nextParameters,
          nextSchemaParameters,
        ),
      );
      onConfigurationChange(nextRawText);
    } catch (error) {
      setRawError(
        error instanceof Error
          ? error.message
          : t(
              "teamMemberWorkflowStudio.nodeDetail.rawConfigurationError",
              "Raw node configuration must be a JSON object.",
            ),
      );
    }
  };

  const updateRawConfigurationText = (value: string) => {
    setRawConfigurationText(value);
    try {
      const nextParameters = applyRawStudioNodeConfiguration(stepDraft.type, value);
      const nextSchemaParameters = rememberSchemaParameters(nextParameters);
      setRawError("");
      setStructuredError("");
      setConfigurationValues(
        readStudioNodeConfigurationValues(
          stepDraft.type,
          nextParameters,
          nextSchemaParameters,
        ),
      );
    } catch (error) {
      setRawError(
        error instanceof Error
          ? error.message
          : t(
              "teamMemberWorkflowStudio.nodeDetail.rawConfigurationError",
              "Raw node configuration must be a JSON object.",
            ),
      );
    }
  };

  const renderFieldControl = (field: StudioStructuredNodeConfigField) => {
    const value = configurationValues[field.name] ?? "";
    const control = field.control ?? field.kind;
    if (control === "select") {
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

    if (control === "boolean") {
      return (
        <Switch
          aria-label={formatConsoleMessage(field.label)}
          checked={value === "true"}
          onChange={(checked) => updateFieldValue(field.name, String(checked))}
        />
      );
    }

    if (control === "number") {
      return (
        <Input
          aria-label={formatConsoleMessage(field.label)}
          inputMode="decimal"
          onChange={(event) => updateFieldValue(field.name, event.target.value)}
          placeholder={
            field.placeholder ? formatConsoleMessage(field.placeholder) : undefined
          }
          status={structuredError ? "error" : undefined}
          value={value}
        />
      );
    }

    if (
      control === "array" ||
      control === "json" ||
      control === "object" ||
      control === "multi-line" ||
      control === "textarea"
    ) {
      return (
        <Input.TextArea
          aria-label={formatConsoleMessage(field.label)}
          autoSize={{ minRows: 4, maxRows: 10 }}
          onChange={(event) => updateFieldValue(field.name, event.target.value)}
          placeholder={
            field.placeholder ? formatConsoleMessage(field.placeholder) : undefined
          }
          status={structuredError ? "error" : undefined}
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
        status={structuredError ? "error" : undefined}
        value={value}
      />
    );
  };

  return (
    <>
      <style>{inspectorCss}</style>
      <aside
        aria-label={t(
          "teamMemberWorkflowStudio.nodeInspector.sectionAria",
          "Node inspector",
        )}
        className="workflow-studio-node-inspector"
        data-testid="workflow-node-inspector"
        style={{
          ...inspectorVariables,
          background: token.colorBgElevated,
          border: `${token.lineWidth}px ${token.lineType} ${token.colorBorderSecondary}`,
          borderLeft: `${token.lineWidth}px ${token.lineType} ${token.colorBorder}`,
          borderRadius: token.borderRadiusLG,
          bottom: overlayInset,
          boxShadow: token.boxShadowSecondary,
          display: "flex",
          flexDirection: "column",
          maxWidth: `calc(100% - ${doubledPx(overlayInset)})`,
          minHeight: 0,
          overflow: "hidden",
          position: "absolute",
          right: overlayInset,
          top: overlayInset,
          width: panelWidth,
          zIndex: token.zIndexPopupBase,
        }}
      >
        {/* biome-ignore lint/a11y/useSemanticElements: The separator is an interactive keyboard and pointer resize handle. */}
        <div
          aria-label={t(
            "teamMemberWorkflowStudio.nodeInspector.resizeHandle",
            "Resize node inspector",
          )}
          aria-orientation="vertical"
          aria-valuemax={MAX_PANEL_WIDTH}
          aria-valuemin={MIN_PANEL_WIDTH}
          aria-valuenow={panelWidth}
          className="workflow-studio-node-inspector__resize"
          onKeyDown={resizeWithKeyboard}
          onPointerCancel={stopResize}
          onPointerDown={startResize}
          onPointerMove={updateResize}
          onPointerUp={stopResize}
          role="separator"
          tabIndex={0}
        />
        <header
          style={{
            alignItems: "flex-start",
            borderBottom: `${token.lineWidth}px ${token.lineType} ${token.colorBorderSecondary}`,
            display: "flex",
            gap: token.paddingSM,
            justifyContent: "space-between",
            padding: "16px 20px 14px",
          }}
        >
          <div style={{ minWidth: 0 }}>
            <Typography.Text strong style={{ color: token.colorTextHeading }}>
              {nodeTypeLabel}
            </Typography.Text>
            <Typography.Paragraph
              style={{ color: token.colorTextSecondary, margin: `${token.marginXXS}px 0 0` }}
            >
              {t("teamMemberWorkflowStudio.nodeInspector.selectedNode", "Selected node")}
            </Typography.Paragraph>
          </div>
          <Button
            aria-label={t(
              "teamMemberWorkflowStudio.nodeInspector.closeAria",
              "Close node inspector",
            )}
            icon={<CloseOutlined />}
            onClick={onClose}
            size="small"
            style={{ height: 28, width: 28 }}
            type="text"
          />
        </header>
        <div
          className="workflow-studio-node-inspector__body"
          style={{
            display: "grid",
            gap: token.paddingLG,
            overflow: "auto",
            padding: token.paddingLG,
          }}
        >
          <section aria-labelledby="workflow-node-inspector-basics-heading">
            <Typography.Text
              id="workflow-node-inspector-basics-heading"
              strong
            >
              {t("teamMemberWorkflowStudio.nodeInspector.basics", "Basics")}
            </Typography.Text>
            <dl
              style={{
                display: "grid",
                gap: token.paddingSM,
                margin: `${token.marginSM}px 0 0`,
              }}
            >
              <InspectorField
                label={t("teamMemberWorkflowStudio.nodeInspector.type", "Type")}
                value={nodeTypeLabel}
              />
              <InspectorField
                label={t(
                  "teamMemberWorkflowStudio.nodeInspector.targetRole",
                  "Target role",
                )}
                value={displayValue(stepDraft.targetRole)}
              />
            </dl>
          </section>
          <section aria-labelledby="workflow-node-inspector-flow-heading">
            <Typography.Text id="workflow-node-inspector-flow-heading" strong>
              {t("teamMemberWorkflowStudio.nodeInspector.flow", "Flow")}
            </Typography.Text>
            <dl
              style={{
                display: "grid",
                gap: token.paddingSM,
                margin: `${token.marginSM}px 0 0`,
              }}
            >
              <InspectorField
                label={t(
                  "teamMemberWorkflowStudio.nodeInspector.nextStep",
                  "Next step",
                )}
                value={displayValue(stepDraft.next)}
              />
              <InspectorField
                label={t(
                  "teamMemberWorkflowStudio.nodeInspector.branches",
                  "Branches",
                )}
                value={branchesSummary}
              />
            </dl>
          </section>
        <section style={{ display: "grid", gap: token.padding }}>
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
                style={{ color: token.colorTextSecondary, margin: `${token.marginXXS}px 0 0` }}
              >
                {t(
                  "teamMemberWorkflowStudio.nodeDetail.configurationDescription",
                  "Edit the fields this node uses when the draft runs.",
                )}
              </Typography.Paragraph>
            </div>
            <Button
              disabled={Boolean(structuredError)}
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
                <Typography.Text strong style={{ color: token.colorText }}>
                  {formatConsoleMessage(field.label)}
                </Typography.Text>
                {renderFieldControl(field)}
                {field.description ? (
                  <Typography.Text style={{ color: token.colorTextSecondary }}>
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

          {activeError ? (
            <Alert
              message={activeError}
              showIcon
              type="error"
            />
          ) : null}
        </section>

        {showRawConfiguration ? (
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
                  <div style={{ display: "grid", gap: token.paddingXS }}>
                    <Typography.Paragraph style={{ color: token.colorTextSecondary, margin: 0 }}>
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
                      onChange={(event) =>
                        updateRawConfigurationText(event.target.value)
                      }
                      spellCheck={false}
                      status={rawError ? "error" : undefined}
                      style={{
                        fontFamily:
                          "SFMono-Regular, Consolas, Liberation Mono, Menlo, monospace",
                      }}
                      value={rawConfigurationText}
                    />
                    <Button
                      disabled={Boolean(rawError)}
                      onClick={applyRawConfigurationToDraft}
                      size="small"
                    >
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
              background: token.colorFillAlter,
              border: `${token.lineWidth}px ${token.lineType} ${token.colorBorderSecondary}`,
              borderRadius: token.borderRadius,
            }}
          />
        ) : null}
        </div>
      </aside>
    </>
  );
};

export default WorkflowStudioNodeDetailPanel;
