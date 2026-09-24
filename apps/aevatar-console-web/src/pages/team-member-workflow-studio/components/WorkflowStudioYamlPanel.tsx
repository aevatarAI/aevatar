import { CheckOutlined, CopyOutlined } from '@ant-design/icons';
import { Alert, Button, Space, Spin, Tag } from 'antd';
import React from 'react';
import { t } from '@/shared/i18n/messages';
import type { StudioValidationFinding } from '@/shared/studio/models';
import { useConsoleToast } from '@/shared/ui/ConsoleToast';
import WorkflowSidePanel from '@/shared/workflows/WorkflowSidePanel';

type WorkflowStudioYamlPanelProps = {
  readonly applying: boolean;
  readonly buffer: string;
  readonly diagnostics: readonly StudioValidationFinding[];
  readonly error: string;
  readonly hasBlockingFindings: boolean;
  readonly hasConflict: boolean;
  readonly hasUnappliedChanges: boolean;
  readonly editorLoading: boolean;
  readonly loading: boolean;
  readonly onApply: () => Promise<void>;
  readonly onBufferChange: (yaml: string) => void;
  readonly onClose: () => void;
  readonly open: boolean;
  readonly width: number;
};

type DiagnosticLevel = 'error' | 'warning' | 'info';

function fallbackCopy(text: string): boolean {
  const textarea = document.createElement('textarea');
  textarea.value = text;
  textarea.setAttribute('readonly', 'true');
  textarea.style.position = 'fixed';
  textarea.style.opacity = '0';
  document.body.appendChild(textarea);
  textarea.select();

  try {
    return document.execCommand('copy');
  } finally {
    document.body.removeChild(textarea);
  }
}

function normalizeDiagnosticLevel(
  level: StudioValidationFinding['level'],
): DiagnosticLevel {
  if (typeof level === 'number') {
    return level >= 2 ? 'error' : level === 1 ? 'warning' : 'info';
  }

  const normalized = String(level || '')
    .trim()
    .toLowerCase();
  if (normalized === '2' || normalized === 'error') {
    return 'error';
  }

  if (normalized === '1' || normalized === 'warning' || normalized === 'warn') {
    return 'warning';
  }

  return 'info';
}

function formatDiagnosticLevel(level: DiagnosticLevel): string {
  switch (level) {
    case 'error':
      return t('teamMemberWorkflowStudio.yamlPanel.error', 'Error');
    case 'warning':
      return t('teamMemberWorkflowStudio.yamlPanel.warning', 'Warning');
    default:
      return t('teamMemberWorkflowStudio.yamlPanel.info', 'Info');
  }
}

function findSequenceItemLine(
  lines: readonly string[],
  sectionName: 'roles' | 'steps',
  itemIndex: number,
): number | null {
  const sectionLineIndex = lines.findIndex((line) =>
    new RegExp(`^\\s*${sectionName}\\s*:`).test(line),
  );
  if (sectionLineIndex < 0) {
    return null;
  }

  let seenItems = -1;
  for (let index = sectionLineIndex + 1; index < lines.length; index += 1) {
    const line = lines[index];
    if (/^\S/.test(line) && !line.trimStart().startsWith('-')) {
      break;
    }

    if (/^\s*-\s+/.test(line)) {
      seenItems += 1;
      if (seenItems === itemIndex) {
        return index + 1;
      }
    }
  }

  return null;
}

function resolveDiagnosticLine(
  yaml: string,
  finding: StudioValidationFinding,
): number | null {
  const path = finding.path?.trim() ?? '';
  if (!path || path === '/') {
    return null;
  }

  const lines = yaml.split('\n');
  if (path === '/name') {
    const nameLineIndex = lines.findIndex((line) => /^\s*name\s*:/.test(line));
    return nameLineIndex >= 0 ? nameLineIndex + 1 : null;
  }

  const sequenceMatch = /^\/(roles|steps)\/(\d+)/.exec(path);
  if (sequenceMatch) {
    return findSequenceItemLine(
      lines,
      sequenceMatch[1] as 'roles' | 'steps',
      Number(sequenceMatch[2]),
    );
  }

  return null;
}

const editorShellStyle: React.CSSProperties = {
  border: '1px solid #d9dce3',
  borderRadius: 6,
  display: 'grid',
  flex: '1 1 0%',
  gridTemplateColumns: '44px minmax(0, 1fr)',
  minHeight: 0,
  overflow: 'hidden',
  width: '100%',
};

const lineNumberGutterStyle: React.CSSProperties = {
  background: '#f8fafc',
  borderRight: '1px solid #e5e7eb',
  color: '#64748b',
  fontFamily:
    'ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace',
  fontSize: 12,
  lineHeight: '20px',
  minHeight: 0,
  overflow: 'hidden',
  padding: '8px 8px 8px 0',
  textAlign: 'right',
  userSelect: 'none',
};

const textareaStyle: React.CSSProperties = {
  border: 0,
  borderRadius: 0,
  boxSizing: 'border-box',
  color: '#111827',
  fontFamily:
    'ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace',
  fontSize: 12,
  height: '100%',
  lineHeight: '20px',
  minHeight: 0,
  outline: 'none',
  overflow: 'auto',
  padding: '8px 11px',
  resize: 'none',
  tabSize: 2,
  whiteSpace: 'pre',
  width: '100%',
};

const diagnosticsListStyle: React.CSSProperties = {
  border: '1px solid #e5e7eb',
  borderRadius: 6,
  display: 'grid',
  gap: 6,
  maxHeight: 116,
  overflow: 'auto',
  padding: 8,
};

const WorkflowStudioYamlPanel: React.FC<WorkflowStudioYamlPanelProps> = ({
  applying,
  buffer,
  diagnostics,
  error,
  hasBlockingFindings,
  hasConflict,
  hasUnappliedChanges,
  editorLoading,
  loading,
  onApply,
  onBufferChange,
  onClose,
  open,
  width,
}) => {
  const toast = useConsoleToast();
  const gutterRef = React.useRef<HTMLDivElement | null>(null);
  const lineCount = Math.max(1, buffer.split('\n').length);
  const lineNumbers = React.useMemo(
    () => Array.from({ length: lineCount }, (_, lineIndex) => lineIndex + 1),
    [lineCount],
  );
  const showEditorLoading = Boolean(editorLoading && !buffer.trim() && !error);
  const applyDisabled = Boolean(
    applying ||
      loading ||
      hasConflict ||
      hasBlockingFindings ||
      !hasUnappliedChanges ||
      !buffer.trim(),
  );

  const copyYaml = React.useCallback(async () => {
    if (!buffer.trim()) {
      return;
    }

    try {
      if (navigator.clipboard?.writeText) {
        await navigator.clipboard.writeText(buffer);
      } else if (!fallbackCopy(buffer)) {
        throw new Error('Clipboard is unavailable.');
      }
      toast.success(
        t('teamMemberWorkflowStudio.yamlPanel.copySuccess', 'YAML copied.'),
      );
    } catch {
      toast.error(
        t(
          'teamMemberWorkflowStudio.yamlPanel.copyFailed',
          'Failed to copy YAML.',
        ),
      );
    }
  }, [buffer, toast]);

  if (!open) {
    return null;
  }

  return (
    <WorkflowSidePanel
      ariaLabel={t(
        'teamMemberWorkflowStudio.yamlPanel.sectionAria',
        'Workflow YAML panel',
      )}
      bodyStyle={{
        display: 'flex',
        flexDirection: 'column',
        overflow: 'hidden',
      }}
      closeAriaLabel={t(
        'teamMemberWorkflowStudio.yamlPanel.closeAria',
        'Close YAML editor',
      )}
      closeDisabled={applying}
      onClose={onClose}
      subtitle={t(
        'teamMemberWorkflowStudio.yamlPanel.subtitle',
        'Draft source buffer',
      )}
      title={t('teamMemberWorkflowStudio.yamlPanel.title', 'Edit YAML')}
      width={width}
    >
      <Space align="center" size={8} wrap>
        <Button
          disabled={!buffer.trim()}
          icon={<CopyOutlined />}
          onClick={() => void copyYaml()}
          size="small"
        >
          {t('teamMemberWorkflowStudio.yamlPanel.copy', 'Copy')}
        </Button>
        {loading ? <Spin size="small" /> : null}
        {hasUnappliedChanges ? (
          <Tag color="gold">
            {t('teamMemberWorkflowStudio.yamlPanel.unapplied', 'Unapplied')}
          </Tag>
        ) : null}
      </Space>
      {error ? <Alert message={error} showIcon type="error" /> : null}
      {diagnostics.length > 0 ? (
        <ul
          aria-label={t(
            'teamMemberWorkflowStudio.yamlPanel.diagnosticsAria',
            'YAML diagnostics',
          )}
          style={{ ...diagnosticsListStyle, listStyle: 'none', margin: 0 }}
        >
          {diagnostics.map((finding) => {
            const level = normalizeDiagnosticLevel(finding.level);
            const lineNumber = resolveDiagnosticLine(buffer, finding);
            return (
              <li
                key={`${finding.path ?? '/'}:${finding.code ?? ''}:${finding.message}:${lineNumber ?? ''}`}
                style={{
                  alignItems: 'start',
                  display: 'grid',
                  gap: 6,
                  gridTemplateColumns: 'auto minmax(0, 1fr)',
                }}
              >
                <Tag
                  color={
                    level === 'error'
                      ? 'red'
                      : level === 'warning'
                        ? 'gold'
                        : 'blue'
                  }
                >
                  {formatDiagnosticLevel(level)}
                </Tag>
                <span style={{ color: '#111827', fontSize: 12, minWidth: 0 }}>
                  {lineNumber
                    ? t(
                        'teamMemberWorkflowStudio.yamlPanel.line',
                        'Line {line}',
                        {
                          line: lineNumber,
                        },
                      )
                    : finding.path || '/'}{' '}
                  {finding.message}
                </span>
              </li>
            );
          })}
        </ul>
      ) : null}
      {showEditorLoading ? (
        <div
          style={{
            ...editorShellStyle,
            alignItems: 'center',
            justifyContent: 'center',
          }}
        >
          <Spin />
        </div>
      ) : (
        <div style={editorShellStyle}>
          <div aria-hidden="true" ref={gutterRef} style={lineNumberGutterStyle}>
            {lineNumbers.map((lineNumber) => (
              <div key={lineNumber}>{lineNumber}</div>
            ))}
          </div>
          <textarea
            aria-label={t(
              'teamMemberWorkflowStudio.yamlPanel.editorAria',
              'Workflow YAML editor',
            )}
            onChange={(event) => {
              if (!applying) {
                onBufferChange(event.target.value);
              }
            }}
            onScroll={(event) => {
              if (gutterRef.current) {
                gutterRef.current.scrollTop = event.currentTarget.scrollTop;
              }
            }}
            readOnly={applying}
            spellCheck={false}
            style={textareaStyle}
            value={buffer}
            wrap="off"
          />
        </div>
      )}
      <footer style={{ flex: '0 0 auto' }}>
        <Space
          align="center"
          style={{ justifyContent: 'flex-end', width: '100%' }}
        >
          <Button disabled={applying} onClick={onClose}>
            {t('teamMemberWorkflowStudio.yamlPanel.cancel', 'Cancel')}
          </Button>
          <Button
            disabled={applyDisabled}
            icon={<CheckOutlined />}
            loading={applying}
            onClick={() => void onApply()}
            title={
              hasConflict
                ? t(
                    'teamMemberWorkflowStudio.yamlPanel.conflictTitle',
                    'Reopen Edit YAML from the current canvas before applying.',
                  )
                : hasBlockingFindings
                  ? t(
                      'teamMemberWorkflowStudio.yamlPanel.fixErrors',
                      'Resolve error-level diagnostics before applying.',
                    )
                  : undefined
            }
            type="primary"
          >
            {t('teamMemberWorkflowStudio.yamlPanel.apply', 'Apply to draft')}
          </Button>
        </Space>
      </footer>
    </WorkflowSidePanel>
  );
};

export default WorkflowStudioYamlPanel;
