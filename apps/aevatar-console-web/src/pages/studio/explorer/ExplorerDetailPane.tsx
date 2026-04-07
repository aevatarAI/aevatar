import { Alert, Button, Input, Space, Spin, Tag, Typography } from "antd";
import React from "react";
import type { ExplorerManifestEntry } from "@/shared/api/explorerApi";
import ExplorerContentView from "./ExplorerContentView";

type ExplorerDetailPaneProps = {
  content: string | null;
  contentErrorMessage: string | null;
  contentLoading: boolean;
  onDirtyStateChange?: (dirty: boolean) => void;
  onDeleteFile?: (key: string) => Promise<void>;
  errorMessage: string | null;
  onOpenScriptInStudio: (scriptId: string) => void;
  onOpenWorkflowInStudio: (workflowId: string) => void;
  onSaveFile?: (key: string, content: string) => Promise<void>;
  scopeId: string;
  selectedEntry: ExplorerManifestEntry | null;
};

type ExplorerEditorMode = "edit" | "preview";

const emptyShellStyle: React.CSSProperties = {
  alignItems: "center",
  display: "flex",
  flex: 1,
  justifyContent: "center",
  minHeight: 0,
  padding: "24px 16px",
};

const detailShellStyle: React.CSSProperties = {
  display: "flex",
  flex: 1,
  flexDirection: "column",
  gap: 16,
  minHeight: 0,
};

const detailHeaderStyle: React.CSSProperties = {
  display: "flex",
  flexDirection: "column",
  gap: 12,
};

const actionRowStyle: React.CSSProperties = {
  alignItems: "center",
  display: "flex",
  flexWrap: "wrap",
  gap: 8,
  justifyContent: "space-between",
};

const editorShellStyle: React.CSSProperties = {
  display: "flex",
  flex: 1,
  flexDirection: "column",
  gap: 12,
  minHeight: 0,
};

const editorStyle: React.CSSProperties = {
  fontFamily:
    'ui-monospace, SFMono-Regular, SF Mono, Menlo, Consolas, "Liberation Mono", monospace',
  fontSize: 13,
  minHeight: 360,
};

function inferStudioResourceId(key: string, extension: string): string {
  const fileName = key.split("/").pop() ?? key;
  return fileName.replace(new RegExp(`${extension}$`, "i"), "");
}

function formatUpdatedAt(updatedAt?: string): string | null {
  if (!updatedAt) {
    return null;
  }

  const value = Date.parse(updatedAt);
  if (!Number.isFinite(value)) {
    return updatedAt;
  }

  return new Date(value).toLocaleString();
}

function describeType(type: string): string {
  switch (type) {
    case "config":
      return "Config";
    case "roles":
      return "Roles";
    case "connectors":
      return "Connectors";
    case "workflow":
      return "Workflow";
    case "script":
      return "Script";
    case "chat-history":
      return "Chat history";
    default:
      return type || "File";
  }
}

const ExplorerDetailPane: React.FC<ExplorerDetailPaneProps> = ({
  content,
  contentErrorMessage,
  contentLoading,
  onDirtyStateChange,
  onDeleteFile,
  errorMessage,
  onOpenScriptInStudio,
  onOpenWorkflowInStudio,
  onSaveFile,
  scopeId,
  selectedEntry,
}) => {
  const [draft, setDraft] = React.useState("");
  const [saveError, setSaveError] = React.useState<string | null>(null);
  const [deleteError, setDeleteError] = React.useState<string | null>(null);
  const [isSaving, setIsSaving] = React.useState(false);
  const [isDeleting, setIsDeleting] = React.useState(false);
  const [mode, setMode] = React.useState<ExplorerEditorMode>("edit");

  const isReadOnly =
    selectedEntry?.type === "workflow" || selectedEntry?.type === "script";
  const isEditable =
    Boolean(selectedEntry) &&
    !isReadOnly &&
    !contentLoading &&
    !contentErrorMessage &&
    content !== null;
  const isDirty = isEditable && draft !== (content ?? "");

  React.useEffect(() => {
    onDirtyStateChange?.(Boolean(isDirty));
  }, [isDirty, onDirtyStateChange]);

  React.useEffect(() => {
    setSaveError(null);
    setDeleteError(null);
    setDraft(content ?? "");
    if (selectedEntry?.type === "chat-history") {
      setMode("preview");
      return;
    }

    setMode("edit");
  }, [content, selectedEntry?.key, selectedEntry?.type]);

  if (!scopeId) {
    return (
      <div style={emptyShellStyle}>
        <Alert
          type="info"
          showIcon
          message="Resolve a project scope to browse explorer storage."
        />
      </div>
    );
  }

  if (errorMessage && !selectedEntry) {
    return (
      <div style={detailShellStyle}>
        <Alert
          type="warning"
          showIcon
          message="Explorer is unavailable"
          description={errorMessage}
        />
      </div>
    );
  }

  if (!selectedEntry) {
    return (
      <div style={emptyShellStyle}>
        <Alert type="info" showIcon message="Select a file from Explorer." />
      </div>
    );
  }

  const updatedAt = formatUpdatedAt(selectedEntry.updatedAt);
  const canOpenWorkflow = selectedEntry.type === "workflow";
  const canOpenScript = selectedEntry.type === "script";

  return (
    <div style={detailShellStyle}>
      <div style={detailHeaderStyle}>
        <Space wrap size={[8, 8]}>
          <Typography.Title level={4} style={{ margin: 0 }}>
            {selectedEntry.name || selectedEntry.key}
          </Typography.Title>
          <Tag color="blue">{describeType(selectedEntry.type)}</Tag>
        </Space>
        <Typography.Text type="secondary">
          {selectedEntry.key}
          {updatedAt ? ` · Updated ${updatedAt}` : ""}
        </Typography.Text>
        {isDirty ? (
          <Tag color="gold">Unsaved changes</Tag>
        ) : null}
        {canOpenWorkflow || canOpenScript ? (
          <Alert
            type="info"
            showIcon
            message="Read-only in Explorer"
            description="Workflow and script files are edited through Studio. Use Explorer to inspect the stored content, then jump back into Studio to change it."
            action={
              canOpenWorkflow ? (
                <Button
                  type="primary"
                  size="small"
                  onClick={() =>
                    onOpenWorkflowInStudio(
                      inferStudioResourceId(selectedEntry.key, "\\.ya?ml")
                    )
                  }
                >
                  Open in Studio
                </Button>
              ) : (
                <Button
                  type="primary"
                  size="small"
                  onClick={() =>
                    onOpenScriptInStudio(
                      inferStudioResourceId(selectedEntry.key, "\\.cs")
                    )
                  }
                >
                  Open in Studio
                </Button>
              )
            }
          />
        ) : null}
        {isEditable ? (
          <div style={actionRowStyle}>
            <Space.Compact>
              <Button
                type={mode === "edit" ? "primary" : "default"}
                onClick={() => setMode("edit")}
              >
                Edit
              </Button>
              <Button
                type={mode === "preview" ? "primary" : "default"}
                onClick={() => setMode("preview")}
              >
                Preview
              </Button>
            </Space.Compact>
            <Space wrap size={[8, 8]}>
              <Button
                onClick={() => {
                  setDraft(content ?? "");
                  setSaveError(null);
                }}
                disabled={!isDirty || isSaving || isDeleting}
              >
                Reset
              </Button>
              <Button
                type="primary"
                loading={isSaving}
                disabled={!isDirty || isDeleting || !onSaveFile}
                onClick={async () => {
                  if (!onSaveFile) {
                    return;
                  }

                  setSaveError(null);
                  setIsSaving(true);
                  try {
                    await onSaveFile(selectedEntry.key, draft);
                  } catch (error) {
                    setSaveError(
                      error instanceof Error ? error.message : "Failed to save explorer file."
                    );
                  } finally {
                    setIsSaving(false);
                  }
                }}
              >
                Save
              </Button>
              <Button
                danger
                loading={isDeleting}
                disabled={isSaving || !onDeleteFile}
                onClick={async () => {
                  if (!onDeleteFile) {
                    return;
                  }

                  if (!window.confirm(`Delete ${selectedEntry.key}? This cannot be undone.`)) {
                    return;
                  }

                  setDeleteError(null);
                  setIsDeleting(true);
                  try {
                    await onDeleteFile(selectedEntry.key);
                  } catch (error) {
                    setDeleteError(
                      error instanceof Error ? error.message : "Failed to delete explorer file."
                    );
                  } finally {
                    setIsDeleting(false);
                  }
                }}
              >
                Delete
              </Button>
            </Space>
          </div>
        ) : null}
        {saveError ? (
          <Alert type="error" showIcon message="Could not save file" description={saveError} />
        ) : null}
        {deleteError ? (
          <Alert
            type="error"
            showIcon
            message="Could not delete file"
            description={deleteError}
          />
        ) : null}
      </div>

      {contentLoading ? (
        <div style={emptyShellStyle}>
          <Spin description="Loading file..." />
        </div>
      ) : contentErrorMessage ? (
        <Alert
          type="error"
          showIcon
          message="Could not load file"
          description={contentErrorMessage}
        />
      ) : (
        <div style={editorShellStyle}>
          {isEditable && mode === "edit" ? (
            <Input.TextArea
              aria-label="Explorer file editor"
              autoSize={false}
              spellCheck={false}
              style={editorStyle}
              value={draft}
              onChange={(event) => {
                setDraft(event.target.value);
                setSaveError(null);
              }}
            />
          ) : (
            <ExplorerContentView
              content={isEditable ? draft : content}
              fileType={selectedEntry.type}
            />
          )}
        </div>
      )}
    </div>
  );
};

export default ExplorerDetailPane;
