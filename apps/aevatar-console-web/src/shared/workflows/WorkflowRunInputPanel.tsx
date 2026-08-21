import {
  CloseOutlined,
  FileImageOutlined,
  FileOutlined,
  PaperClipOutlined,
  PlayCircleOutlined,
} from '@ant-design/icons';
import { Button, Input, Typography } from 'antd';
import React from 'react';
import AevatarTooltip from '@/shared/ui/AevatarTooltip';
import { t } from '@/shared/i18n/messages';
import WorkflowSidePanel from './WorkflowSidePanel';

type DraftRunVariant = {
  readonly kind: 'draft';
  readonly acceptedFileTypes?: string;
  readonly files: readonly File[];
  readonly onFilesAdd: (files: readonly File[]) => void;
  readonly onFileRemove: (index: number) => void;
};

type PublishedRunVariant = {
  readonly acceptedFileTypes?: string;
  readonly files: readonly File[];
  readonly inputError?: string;
  readonly kind: 'published';
  readonly onFilesAdd: (files: readonly File[]) => void;
  readonly onFileRemove: (index: number) => void;
};

type WorkflowRunInputPanelProps = {
  readonly canRun: boolean;
  readonly disabledReason?: string;
  readonly height?: React.CSSProperties['height'];
  readonly inputDisabled?: boolean;
  readonly onClose: () => void;
  readonly onRun: () => void;
  readonly onRunMessageChange: (message: string) => void;
  readonly open: boolean;
  readonly pending: boolean;
  readonly runMessage: string;
  readonly variant: DraftRunVariant | PublishedRunVariant;
  readonly width?: number;
};

const DEFAULT_ACCEPTED_FILE_TYPES = [
  'image/png',
  'image/jpeg',
  'image/webp',
  'audio/mpeg',
  'audio/wav',
  'audio/wave',
  'audio/x-wav',
  'video/mp4',
  'application/pdf',
  'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
  'text/csv',
  'text/plain',
  'text/markdown',
  'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
].join(',');

const attachmentToolbarStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  flexWrap: 'wrap',
  gap: 8,
  minWidth: 0,
};

const attachmentListStyle: React.CSSProperties = {
  alignItems: 'center',
  display: 'flex',
  flex: '1 1 220px',
  flexWrap: 'wrap',
  gap: 6,
  minWidth: 0,
};

const attachmentChipStyle: React.CSSProperties = {
  alignItems: 'center',
  background: '#f8fafc',
  border: '1px solid #dbe3ee',
  borderRadius: 8,
  color: '#334155',
  display: 'inline-flex',
  gap: 6,
  maxWidth: '100%',
  minHeight: 28,
  minWidth: 0,
  padding: '3px 5px 3px 8px',
};

const attachmentNameStyle: React.CSSProperties = {
  fontSize: 12,
  fontWeight: 700,
  lineHeight: '16px',
  maxWidth: 168,
  overflow: 'hidden',
  textOverflow: 'ellipsis',
  whiteSpace: 'nowrap',
};

const attachmentMetaStyle: React.CSSProperties = {
  color: '#64748b',
  flex: '0 0 auto',
  fontSize: 11,
  lineHeight: '14px',
};

const attachmentEmptyStyle: React.CSSProperties = {
  color: '#64748b',
  fontSize: 12,
  lineHeight: '18px',
};

const fileDropZoneStyle: React.CSSProperties = {
  alignItems: 'center',
  background: '#fbfcfe',
  border: '1px dashed #cbd5e1',
  borderRadius: 8,
  color: '#475569',
  display: 'grid',
  gap: 10,
  justifyItems: 'start',
  minHeight: 84,
  padding: '12px',
};

function formatFileSize(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes <= 0) {
    return '0 B';
  }

  if (bytes < 1024) {
    return `${bytes} B`;
  }

  const kib = bytes / 1024;
  if (kib < 1024) {
    return `${kib.toFixed(kib >= 10 ? 0 : 1)} KB`;
  }

  const mib = kib / 1024;
  return `${mib.toFixed(mib >= 10 ? 0 : 1)} MB`;
}

function getFileIcon(file: File): React.ReactNode {
  return file.type.startsWith('image/') ? (
    <FileImageOutlined />
  ) : (
    <FileOutlined />
  );
}

function getAttachmentKey(file: File): string {
  return [file.name, file.size, file.lastModified, file.type || 'file'].join(
    ':',
  );
}

const WorkflowRunInputPanel: React.FC<WorkflowRunInputPanelProps> = ({
  canRun,
  disabledReason,
  height,
  inputDisabled = false,
  onClose,
  onRun,
  onRunMessageChange,
  open,
  pending,
  runMessage,
  variant,
  width = 420,
}) => {
  const fileInputRef = React.useRef<HTMLInputElement | null>(null);
  const inputLocked = pending || inputDisabled;
  const fileVariant = variant;
  const inputError = variant.kind === 'published' ? variant.inputError : '';

  if (!open) {
    return null;
  }

  const addFiles = (nextFiles: readonly File[]) => {
    if (nextFiles.length > 0) {
      fileVariant.onFilesAdd(nextFiles);
    }
  };

  return (
    <WorkflowSidePanel
      ariaLabel={t(
        variant.kind === 'draft'
          ? 'teamMemberWorkflowStudio.draftRunPanel.sectionAria'
          : 'workflowActivityVNext.editor.publishedRunPanel.sectionAria',
        variant.kind === 'draft' ? 'Draft run panel' : 'Published run panel',
      )}
      bodyStyle={{
        display: 'flex',
        flexDirection: 'column',
        gap: 0,
        overflow: 'hidden',
        padding: 0,
      }}
      closeAriaLabel={t(
        variant.kind === 'draft'
          ? 'teamMemberWorkflowStudio.draftRunPanel.closeAria'
          : 'workflowActivityVNext.editor.publishedRunPanel.closeAria',
        variant.kind === 'draft'
          ? 'Close draft run panel'
          : 'Close published run panel',
      )}
      height={height}
      onClose={onClose}
      title={
        <span style={{ alignItems: 'center', display: 'inline-flex', gap: 8 }}>
          <PlayCircleOutlined />
          <span>
            {t(
              variant.kind === 'draft'
                ? 'teamMemberWorkflowStudio.draftRunPanel.title'
                : 'workflowActivityVNext.editor.publishedRunPanel.title',
              variant.kind === 'draft' ? 'Draft run' : 'Published run',
            )}
          </span>
        </span>
      }
      width={width}
    >
      <div
        style={{
          alignContent: 'start',
          display: 'grid',
          flex: '1 1 auto',
          gap: 28,
          minHeight: 0,
          overflow: 'auto',
          padding: '28px 24px 24px',
        }}
      >
        <section
          style={{
            display: 'grid',
            gap: 12,
          }}
        >
          <div style={{ display: 'grid', gap: 4 }}>
            <Typography.Text strong>
              {t(
                variant.kind === 'draft'
                  ? 'teamMemberWorkflowStudio.draftRunPanel.messageLabel'
                  : 'workflowActivityVNext.editor.publishedRunPanel.messageLabel',
                variant.kind === 'draft'
                  ? 'Draft run input'
                  : 'Published run input',
              )}
            </Typography.Text>
            <Typography.Text style={{ color: '#64748b' }}>
              {t(
                variant.kind === 'draft'
                  ? 'teamMemberWorkflowStudio.draftRunPanel.emptyInputHint'
                  : 'workflowActivityVNext.editor.publishedRunPanel.inputHint',
                variant.kind === 'draft'
                  ? 'Leave blank to run this draft without user input.'
                  : 'Leave blank to start this published workflow without user input.',
              )}
            </Typography.Text>
          </div>
          <Input.TextArea
            aria-label={t(
              variant.kind === 'draft'
                ? 'teamMemberWorkflowStudio.draftRunPanel.messageLabel'
                : 'workflowActivityVNext.editor.publishedRunPanel.messageLabel',
              variant.kind === 'draft'
                ? 'Draft run input'
                : 'Published run input',
            )}
            aria-invalid={Boolean(inputError)}
            autoSize={{ minRows: 7, maxRows: 10 }}
            disabled={inputLocked}
            onChange={(event) => onRunMessageChange(event.target.value)}
            placeholder={t(
              variant.kind === 'draft'
                ? 'teamMemberWorkflowStudio.draftRunPanel.messagePlaceholder'
                : 'workflowActivityVNext.editor.publishedRunPanel.messagePlaceholder',
              variant.kind === 'draft'
                ? 'Optional input sent to this workflow draft run'
                : 'Optional input sent to this published workflow run',
            )}
            style={{ fontSize: 15 }}
            value={runMessage}
          />
          {inputError ? (
            <Typography.Text role="alert" type="danger">
              {inputError}
            </Typography.Text>
          ) : null}
        </section>

        <section
          style={{
            display: 'grid',
            gap: 10,
          }}
        >
          <div style={{ display: 'grid', gap: 4 }}>
            <Typography.Text strong>
              {t('teamMemberWorkflowStudio.draftRunPanel.filesLabel', 'Files')}
            </Typography.Text>
            <Typography.Text style={{ color: '#64748b' }}>
              {t(
                variant.kind === 'draft'
                  ? 'teamMemberWorkflowStudio.draftRunPanel.filesHint'
                  : 'workflowActivityVNext.editor.publishedRunPanel.filesHint',
                variant.kind === 'draft'
                  ? 'Attach files for this draft run.'
                  : 'Attach files for this published workflow run.',
              )}
            </Typography.Text>
          </div>
          <input
            ref={fileInputRef}
            aria-label={t(
              'teamMemberWorkflowStudio.draftRunPanel.attachFilesInput',
              'Attach files',
            )}
            accept={
              fileVariant.acceptedFileTypes ?? DEFAULT_ACCEPTED_FILE_TYPES
            }
            data-testid="workflow-run-file-input"
            multiple
            onChange={(event) => {
              addFiles(Array.from(event.target.files ?? []));
              event.currentTarget.value = '';
            }}
            style={{ display: 'none' }}
            type="file"
          />
          <div
            data-testid="workflow-run-file-drop-zone"
            onDragOver={(event) => {
              event.preventDefault();
              event.dataTransfer.dropEffect = inputLocked ? 'none' : 'copy';
            }}
            onDrop={(event) => {
              event.preventDefault();
              if (!inputLocked) {
                addFiles(Array.from(event.dataTransfer.files ?? []));
              }
            }}
            style={fileDropZoneStyle}
          >
            <div style={attachmentToolbarStyle}>
              <AevatarTooltip
                title={t(
                  'teamMemberWorkflowStudio.draftRunPanel.attachFiles',
                  'Attach files',
                )}
              >
                <Button
                  aria-label={t(
                    'teamMemberWorkflowStudio.draftRunPanel.attachFilesButton',
                    'Add files',
                  )}
                  disabled={inputLocked}
                  icon={<PaperClipOutlined />}
                  onClick={() => fileInputRef.current?.click()}
                >
                  {t(
                    'teamMemberWorkflowStudio.draftRunPanel.addFiles',
                    'Add files',
                  )}
                </Button>
              </AevatarTooltip>
              <Typography.Text style={{ color: '#64748b', fontSize: 12 }}>
                {t(
                  'teamMemberWorkflowStudio.draftRunPanel.dropFiles',
                  'Drop files here',
                )}
              </Typography.Text>
            </div>
            <div style={attachmentListStyle}>
              {fileVariant.files.length === 0 ? (
                <span style={attachmentEmptyStyle}>
                  {t(
                    'teamMemberWorkflowStudio.draftRunPanel.noFilesAttached',
                    'No files attached',
                  )}
                </span>
              ) : (
                fileVariant.files.map((file, index) => (
                  <span
                    key={getAttachmentKey(file)}
                    data-testid="workflow-run-file-chip"
                    style={attachmentChipStyle}
                  >
                    {getFileIcon(file)}
                    <AevatarTooltip
                      title={`${file.name} · ${file.type || 'file'} · ${formatFileSize(file.size)}`}
                    >
                      <span style={attachmentNameStyle}>{file.name}</span>
                    </AevatarTooltip>
                    <span style={attachmentMetaStyle}>
                      {formatFileSize(file.size)}
                    </span>
                    <Button
                      aria-label={t(
                        'teamMemberWorkflowStudio.draftRunPanel.removeFile',
                        'Remove {name}',
                        { name: file.name },
                      )}
                      disabled={inputLocked}
                      icon={<CloseOutlined />}
                      onClick={() => fileVariant.onFileRemove(index)}
                      size="small"
                      type="text"
                    />
                  </span>
                ))
              )}
            </div>
          </div>
          <Typography.Text style={{ color: '#64748b', fontSize: 12 }}>
            {t(
              'teamMemberWorkflowStudio.draftRunPanel.filesLimitHint',
              'Images, documents, audio, video, CSV, and text files up to 10 MB.',
            )}
          </Typography.Text>
        </section>
      </div>

      <div
        style={{
          borderTop: '1px solid #e5e7eb',
          display: 'grid',
          gap: 10,
          padding: '20px 24px 24px',
        }}
      >
        <Button
          disabled={!canRun}
          icon={<PlayCircleOutlined />}
          loading={pending}
          onClick={onRun}
          size="large"
          style={{
            boxShadow: canRun
              ? '0 12px 24px rgba(15, 23, 42, 0.14)'
              : undefined,
            height: 54,
            width: '100%',
          }}
          title={canRun ? undefined : disabledReason}
          type="primary"
        >
          {t(
            variant.kind === 'draft'
              ? 'teamMemberWorkflowStudio.draftRunPanel.startDraftRun'
              : 'workflowActivityVNext.editor.publishedRunPanel.startRun',
            variant.kind === 'draft'
              ? 'Start draft run'
              : 'Start published run',
          )}
        </Button>
        {!canRun && disabledReason ? (
          <Typography.Text style={{ color: '#6b7280', fontSize: 12 }}>
            {disabledReason}
          </Typography.Text>
        ) : null}
      </div>
    </WorkflowSidePanel>
  );
};

export default WorkflowRunInputPanel;
