import {
  DeleteOutlined,
  InfoCircleOutlined,
  PaperClipOutlined,
  PlayCircleOutlined,
  PlusOutlined,
  UploadOutlined,
} from "@ant-design/icons";
import { Button, Input, Typography } from "antd";
import React from "react";
import { t } from "@/shared/i18n/messages";
import WorkflowStudioSidePanel from "./WorkflowStudioSidePanel";

const DRAFT_RUN_FILE_ACCEPT =
  "image/png,image/jpeg,image/webp,audio/mpeg,audio/wav,audio/wave,audio/x-wav,video/mp4,application/pdf,application/vnd.openxmlformats-officedocument.wordprocessingml.document,text/csv,text/plain,text/markdown,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet,.md,.csv,.txt,.docx,.xlsx";

type WorkflowStudioDraftRunPanelProps = {
  readonly canRun: boolean;
  readonly draftRunFiles: readonly File[];
  readonly disabledReason?: string;
  readonly onAddFiles: (files: readonly File[]) => void;
  readonly onClearFiles: () => void;
  readonly onClose: () => void;
  readonly onRemoveFile: (index: number) => void;
  readonly onRun: () => void;
  readonly onRunMessageChange: (message: string) => void;
  readonly open: boolean;
  readonly pending: boolean;
  readonly runMessage: string;
  readonly width?: number;
};

const WorkflowStudioDraftRunPanel: React.FC<WorkflowStudioDraftRunPanelProps> = ({
  canRun,
  draftRunFiles,
  disabledReason,
  onAddFiles,
  onClearFiles,
  onClose,
  onRemoveFile,
  onRun,
  onRunMessageChange,
  open,
  pending,
  runMessage,
  width = 420,
}) => {
  const fileInputRef = React.useRef<HTMLInputElement | null>(null);
  const [dragActive, setDragActive] = React.useState(false);

  if (!open) {
    return null;
  }

  const openFilePicker = () => {
    if (!pending) {
      fileInputRef.current?.click();
    }
  };

  const handleFileInputChange = (
    event: React.ChangeEvent<HTMLInputElement>,
  ) => {
    const files = Array.from(event.target.files ?? []);
    if (files.length > 0) {
      onAddFiles(files);
    }
    event.target.value = "";
  };

  const handleDropZoneKeyDown = (
    event: React.KeyboardEvent<HTMLDivElement>,
  ) => {
    if (event.key !== "Enter" && event.key !== " ") {
      return;
    }

    event.preventDefault();
    openFilePicker();
  };

  const handleDragOver = (event: React.DragEvent<HTMLDivElement>) => {
    event.preventDefault();
    if (!pending) {
      setDragActive(true);
    }
  };

  const handleDragLeave = (event: React.DragEvent<HTMLDivElement>) => {
    const relatedTarget = event.relatedTarget;
    if (!(relatedTarget instanceof Node) || !event.currentTarget.contains(relatedTarget)) {
      setDragActive(false);
    }
  };

  const handleDrop = (event: React.DragEvent<HTMLDivElement>) => {
    event.preventDefault();
    setDragActive(false);
    if (pending) {
      return;
    }

    const files = Array.from(event.dataTransfer.files ?? []);
    if (files.length > 0) {
      onAddFiles(files);
    }
  };

  return (
    <WorkflowStudioSidePanel
      ariaLabel={t(
        "teamMemberWorkflowStudio.draftRunPanel.sectionAria",
        "Draft run panel",
      )}
      bodyStyle={{
        display: "flex",
        flexDirection: "column",
        gap: 0,
        overflow: "hidden",
        padding: 0,
      }}
      closeAriaLabel={t(
        "teamMemberWorkflowStudio.draftRunPanel.closeAria",
        "Close draft run panel",
      )}
      onClose={onClose}
      title={
        <span style={{ alignItems: "center", display: "inline-flex", gap: 8 }}>
          <PlayCircleOutlined />
          <span>{t("teamMemberWorkflowStudio.draftRunPanel.title", "Draft run")}</span>
        </span>
      }
      width={width}
    >
      <div
        style={{
          alignContent: "start",
          display: "grid",
          flex: "1 1 auto",
          gap: 28,
          minHeight: 0,
          overflow: "auto",
          padding: "28px 24px 24px",
        }}
      >
        <section
          style={{
            display: "grid",
            gap: 12,
          }}
        >
          <div style={{ display: "grid", gap: 4 }}>
            <Typography.Text strong>
              {t(
                "teamMemberWorkflowStudio.draftRunPanel.messageLabel",
                "Draft run input",
              )}
            </Typography.Text>
            <Typography.Text style={{ color: "#64748b" }}>
              {t(
                "teamMemberWorkflowStudio.draftRunPanel.emptyInputHint",
                "Leave blank to run this draft without user input.",
              )}
            </Typography.Text>
          </div>
          <Input.TextArea
            aria-label={t(
              "teamMemberWorkflowStudio.draftRunPanel.messageLabel",
              "Draft run input",
            )}
            autoSize={{ minRows: 7, maxRows: 10 }}
            onChange={(event) => onRunMessageChange(event.target.value)}
            placeholder={t(
              "teamMemberWorkflowStudio.draftRunPanel.messagePlaceholder",
              "Optional input sent to this workflow draft run",
            )}
            style={{ fontSize: 15 }}
            value={runMessage}
          />
        </section>

        <section
          aria-label={t(
            "teamMemberWorkflowStudio.draftRunPanel.filesSectionAria",
            "Run input files",
          )}
          style={{
            display: "grid",
            gap: 14,
          }}
        >
          <Typography.Text strong>
            {t(
              "teamMemberWorkflowStudio.draftRunPanel.filesTitle",
              "Run input files",
            )}
          </Typography.Text>
          <input
            accept={DRAFT_RUN_FILE_ACCEPT}
            aria-label={t(
              "teamMemberWorkflowStudio.draftRunPanel.filesSectionAria",
              "Run input files",
            )}
            data-testid="draft-run-file-input"
            disabled={pending}
            multiple
            onChange={handleFileInputChange}
            ref={fileInputRef}
            style={{ display: "none" }}
            tabIndex={-1}
            type="file"
          />
          <div
            aria-disabled={pending}
            data-testid="draft-run-file-drop-zone"
            onClick={openFilePicker}
            onDragLeave={handleDragLeave}
            onDragOver={handleDragOver}
            onDrop={handleDrop}
            onKeyDown={handleDropZoneKeyDown}
            role="button"
            style={{
              alignItems: "center",
              background: dragActive ? "#f8fafc" : "#ffffff",
              border: "1px dashed #cbd5e1",
              borderRadius: 6,
              cursor: pending ? "not-allowed" : "pointer",
              display: "grid",
              gap: 14,
              justifyItems: "center",
              minHeight: 230,
              padding: "28px 24px",
              textAlign: "center",
              transition: "background 120ms ease, border-color 120ms ease",
            }}
            tabIndex={pending ? -1 : 0}
          >
            <span
              aria-hidden="true"
              style={{
                alignItems: "center",
                background: "#f1f5f9",
                borderRadius: 12,
                display: "inline-flex",
                height: 48,
                justifyContent: "center",
                width: 48,
              }}
            >
              <UploadOutlined style={{ fontSize: 20 }} />
            </span>
            <div style={{ display: "grid", gap: 6 }}>
              <Typography.Text strong>
                {t(
                  "teamMemberWorkflowStudio.draftRunPanel.filesDropPrompt",
                  "Click to upload or drag and drop",
                )}
              </Typography.Text>
              <Typography.Text style={{ color: "#64748b" }}>
                {t(
                  "teamMemberWorkflowStudio.draftRunPanel.filesHint",
                  "Files are sent only with this draft run.",
                )}
              </Typography.Text>
            </div>
            <Typography.Text
              style={{
                color: "#64748b",
                fontSize: 12,
                letterSpacing: 0.8,
                textTransform: "uppercase",
              }}
            >
              {t(
                "teamMemberWorkflowStudio.draftRunPanel.filesSoftLimitCompact",
                "Max size: 10 MB per file",
              )}
            </Typography.Text>
            <Button
              disabled={pending}
              icon={<PlusOutlined />}
              onClick={(event) => {
                event.stopPropagation();
                openFilePicker();
              }}
              size="large"
            >
              {t(
                "teamMemberWorkflowStudio.draftRunPanel.addFiles",
                "Add files",
              )}
            </Button>
          </div>

          {draftRunFiles.length > 0 ? (
            <div style={{ display: "grid", gap: 10 }}>
              {draftRunFiles.map((file, index) => {
                const fileType =
                  file.type ||
                  t(
                    "teamMemberWorkflowStudio.draftRunPanel.unknownFileType",
                    "unknown type",
                  );
                const removeLabel = t(
                  "teamMemberWorkflowStudio.draftRunPanel.removeFile",
                  "Remove {fileName}",
                  { fileName: file.name },
                );
                return (
                  <div
                    key={`${file.name}:${file.size}:${file.lastModified}:${index}`}
                    style={{
                      alignItems: "center",
                      background: "#f8fafc",
                      border: "1px solid #e5e7eb",
                      borderRadius: 6,
                      display: "grid",
                      gap: 8,
                      gridTemplateColumns: "minmax(0, 1fr) auto",
                      padding: "8px 10px",
                    }}
                  >
                    <div
                      style={{
                        alignItems: "center",
                        display: "grid",
                        gap: 8,
                        gridTemplateColumns: "auto minmax(0, 1fr)",
                        minWidth: 0,
                      }}
                    >
                      <PaperClipOutlined style={{ color: "#64748b" }} />
                      <div style={{ minWidth: 0 }}>
                        <Typography.Text
                          ellipsis={{ tooltip: file.name }}
                          style={{ display: "block" }}
                        >
                          {file.name}
                        </Typography.Text>
                        <Typography.Text
                          style={{
                            color: "#64748b",
                            display: "block",
                            fontSize: 12,
                          }}
                        >
                          {t(
                            "teamMemberWorkflowStudio.draftRunPanel.fileMeta",
                            "{size} · {type}",
                            {
                              size: formatDraftRunFileSize(file.size),
                              type: fileType,
                            },
                          )}
                        </Typography.Text>
                      </div>
                    </div>
                    <Button
                      aria-label={removeLabel}
                      disabled={pending}
                      icon={<DeleteOutlined />}
                      onClick={() => onRemoveFile(index)}
                      title={removeLabel}
                      type="text"
                    />
                  </div>
                );
              })}
              <div>
                <Button disabled={pending} onClick={onClearFiles} size="small">
                  {t(
                    "teamMemberWorkflowStudio.draftRunPanel.clearFiles",
                    "Clear files",
                  )}
                </Button>
              </div>
            </div>
          ) : null}

          <div
            style={{
              alignItems: "center",
              background: "#f8fafc",
              border: "1px solid #e5e7eb",
              borderRadius: 4,
              color: "#475569",
              display: "grid",
              fontSize: 12,
              gap: 8,
              gridTemplateColumns: "auto minmax(0, 1fr)",
              padding: "10px 12px",
            }}
          >
            <InfoCircleOutlined />
            <Typography.Text style={{ color: "inherit", fontSize: 12 }}>
              {draftRunFiles.length > 0
                ? t(
                    "teamMemberWorkflowStudio.draftRunPanel.filesSelectedNotice",
                    "{count} selected file(s) will be temporarily stored for this run session.",
                    { count: draftRunFiles.length },
                  )
                : t(
                    "teamMemberWorkflowStudio.draftRunPanel.filesTemporaryNotice",
                    "Selected files will be temporarily stored for this run session.",
                  )}
            </Typography.Text>
          </div>
        </section>
      </div>

      <div
        style={{
          borderTop: "1px solid #e5e7eb",
          display: "grid",
          gap: 10,
          padding: "20px 24px 24px",
        }}
      >
        <Button
          disabled={!canRun}
          icon={<PlayCircleOutlined />}
          loading={pending}
          onClick={onRun}
          size="large"
          style={{
            boxShadow: canRun ? "0 12px 24px rgba(15, 23, 42, 0.14)" : undefined,
            height: 54,
            width: "100%",
          }}
          title={canRun ? undefined : disabledReason}
          type="primary"
        >
          {t(
            "teamMemberWorkflowStudio.draftRunPanel.startDraftRun",
            "Start draft run",
          )}
        </Button>
        {!canRun && disabledReason ? (
          <Typography.Text style={{ color: "#6b7280", fontSize: 12 }}>
            {disabledReason}
          </Typography.Text>
        ) : null}
      </div>
    </WorkflowStudioSidePanel>
  );
};

function formatDraftRunFileSize(size: number): string {
  if (!Number.isFinite(size) || size <= 0) {
    return t("teamMemberWorkflowStudio.draftRunPanel.fileSizeZero", "0 B");
  }

  if (size < 1024) {
    return t("teamMemberWorkflowStudio.draftRunPanel.fileSizeBytes", "{size} B", {
      size,
    });
  }

  if (size < 1024 * 1024) {
    return t("teamMemberWorkflowStudio.draftRunPanel.fileSizeKb", "{size} KB", {
      size: Math.round((size / 1024) * 10) / 10,
    });
  }

  return t("teamMemberWorkflowStudio.draftRunPanel.fileSizeMb", "{size} MB", {
    size: Math.round((size / (1024 * 1024)) * 10) / 10,
  });
}

export default WorkflowStudioDraftRunPanel;
