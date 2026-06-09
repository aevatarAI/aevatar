import { CloseOutlined } from "@ant-design/icons";
import { Alert, Button, Input, Typography } from "antd";
import React from "react";
import { t } from "@/shared/i18n/messages";
import type { StudioStepInspectorDraft } from "@/shared/studio/document";
import { formatStudioStepTypeLabel } from "@/shared/studio/graph";

type WorkflowStudioNodeDetailPanelProps = {
  readonly error?: string;
  readonly onClose: () => void;
  readonly onParametersChange: (parametersText: string) => void;
  readonly stepDraft: StudioStepInspectorDraft | null;
};

const DEFAULT_PANEL_WIDTH = 420;
const MIN_PANEL_WIDTH = 360;
const MAX_PANEL_WIDTH = 500;

const INSPECTOR_CSS = `
.workflow-studio-node-inspector {
  color: #111827;
}

.workflow-studio-node-inspector__resize {
  align-items: center;
  background: transparent;
  border: 0;
  bottom: 0;
  cursor: ew-resize;
  display: flex;
  justify-content: center;
  left: -8px;
  padding: 0;
  position: absolute;
  top: 0;
  width: 16px;
}

.workflow-studio-node-inspector__resize::after {
  background: #d1d5db;
  border-radius: 999px;
  content: "";
  height: 52px;
  width: 3px;
}

.workflow-studio-node-inspector__resize:hover::after,
.workflow-studio-node-inspector__resize:focus-visible::after {
  background: #4f46e5;
}

.workflow-studio-node-inspector__body::-webkit-scrollbar {
  width: 10px;
}

.workflow-studio-node-inspector__body::-webkit-scrollbar-thumb {
  background: #cbd5e1;
  border: 3px solid #ffffff;
  border-radius: 999px;
}

@media (max-width: 720px) {
  .workflow-studio-node-inspector {
    border-left: 0 !important;
    border-radius: 16px 16px 0 0 !important;
    border-top: 1px solid #e5e7eb !important;
    bottom: 0 !important;
    left: 0 !important;
    max-height: 74vh !important;
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
          color: "#6b7280",
          fontSize: 12,
          fontWeight: 600,
          lineHeight: 1.4,
        }}
      >
        {label}
      </dt>
      <dd
        style={{
          color: "#111827",
          lineHeight: 1.5,
          margin: "4px 0 0",
          overflowWrap: "anywhere",
        }}
      >
        {value}
      </dd>
    </div>
  );
}

const WorkflowStudioNodeDetailPanel: React.FC<WorkflowStudioNodeDetailPanelProps> = ({
  error,
  onClose,
  onParametersChange,
  stepDraft,
}) => {
  const [panelWidth, setPanelWidth] = React.useState(DEFAULT_PANEL_WIDTH);
  const [parametersText, setParametersText] = React.useState(
    stepDraft?.parametersText ?? "",
  );
  const [resizing, setResizing] = React.useState(false);
  const resizeStartRef = React.useRef<{
    readonly startWidth: number;
    readonly startX: number;
  } | null>(null);

  React.useEffect(() => {
    setParametersText(stepDraft?.parametersText ?? "");
  }, [stepDraft?.id, stepDraft?.parametersText]);

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

  if (!stepDraft) {
    return null;
  }

  const stepTypeLabel = formatStudioStepTypeLabel(stepDraft.type);
  const branchesSummary = summarizeBranches(stepDraft.branchesText);

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

  return (
    <>
      <style>{INSPECTOR_CSS}</style>
      <aside
        aria-label={t(
          "teamMemberWorkflowStudio.nodeInspector.sectionAria",
          "Node inspector",
        )}
        className="workflow-studio-node-inspector"
        data-testid="workflow-node-inspector"
        style={{
          background: "#ffffff",
          border: "1px solid #e5e7eb",
          borderLeft: "1px solid #d1d5db",
          borderRadius: 14,
          bottom: 16,
          boxShadow: "0 24px 60px rgba(15, 23, 42, 0.18)",
          display: "flex",
          flexDirection: "column",
          maxWidth: "calc(100% - 32px)",
          minHeight: 0,
          overflow: "hidden",
          position: "absolute",
          right: 16,
          top: 16,
          width: panelWidth,
          zIndex: 20,
        }}
      >
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
            borderBottom: "1px solid #e5e7eb",
            display: "flex",
            gap: 12,
            justifyContent: "space-between",
            padding: "16px 18px",
          }}
        >
          <div style={{ minWidth: 0 }}>
            <Typography.Text strong style={{ color: "#111827", fontSize: 16 }}>
              {stepDraft.id}
            </Typography.Text>
            <Typography.Paragraph
              style={{ color: "#6b7280", margin: "2px 0 0" }}
            >
              {stepTypeLabel}
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
            type="text"
          />
        </header>
        <div
          className="workflow-studio-node-inspector__body"
          style={{
            display: "grid",
            gap: 18,
            overflow: "auto",
            padding: 18,
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
                gap: 12,
                margin: "12px 0 0",
              }}
            >
              <InspectorField
                label={t("teamMemberWorkflowStudio.nodeInspector.stepId", "Step ID")}
                value={stepDraft.id}
              />
              <InspectorField
                label={t("teamMemberWorkflowStudio.nodeInspector.type", "Type")}
                value={stepTypeLabel}
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
                gap: 12,
                margin: "12px 0 0",
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
          <section aria-labelledby="workflow-node-inspector-parameters-heading">
            <div
              style={{
                alignItems: "center",
                display: "flex",
                gap: 12,
                justifyContent: "space-between",
              }}
            >
              <Typography.Text
                id="workflow-node-inspector-parameters-heading"
                strong
              >
                {t("teamMemberWorkflowStudio.nodeDetail.parameters", "Parameters")}
              </Typography.Text>
              <Button
                onClick={() => onParametersChange(parametersText)}
                size="small"
                type="primary"
              >
                {t("teamMemberWorkflowStudio.nodeDetail.apply", "Apply")}
              </Button>
            </div>
            <Input.TextArea
              aria-label={t(
                "teamMemberWorkflowStudio.nodeDetail.parametersAria",
                "Node parameters",
              )}
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
        </div>
      </aside>
    </>
  );
};

export default WorkflowStudioNodeDetailPanel;
