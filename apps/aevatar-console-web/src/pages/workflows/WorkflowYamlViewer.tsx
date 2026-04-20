import {
  CopyOutlined,
  FullscreenExitOutlined,
  FullscreenOutlined,
} from '@ant-design/icons';
import { Button, Empty, Modal, message, Space, Tag, Typography } from 'antd';
import React, { useCallback, useEffect, useMemo, useState } from 'react';
import { cardStackStyle, embeddedPanelStyle } from '@/shared/ui/proComponents';

type WorkflowYamlViewerProps = {
  yaml: string;
};

type YamlLineTokens = {
  comment?: string;
  indent: string;
  key?: string;
  listMarker?: string;
  raw: string;
  separator?: string;
  value?: string;
};

const codeViewerStyle = {
  background: '#f8fafc',
  overflow: 'auto',
  padding: 0,
} as const;

const editorShellStyle = {
  ...embeddedPanelStyle,
  background: 'linear-gradient(180deg, #fbfdff 0%, #f4f7fb 100%)',
  border: '1px solid var(--ant-color-border-secondary)',
  borderRadius: 18,
  boxShadow: '0 20px 45px rgba(15, 23, 42, 0.06)',
  overflow: 'hidden',
  padding: 0,
} as const;

const editorHeaderStyle = {
  alignItems: 'flex-start',
  background: 'rgba(248, 250, 252, 0.96)',
  borderBottom: '1px solid var(--ant-color-border-secondary)',
  display: 'flex',
  flexWrap: 'wrap',
  gap: 16,
  justifyContent: 'space-between',
  padding: '16px 18px',
} as const;

const editorMetaStyle = {
  display: 'flex',
  flex: '1 1 320px',
  flexDirection: 'column',
  gap: 8,
  minWidth: 0,
} as const;

const editorActionsStyle = {
  alignItems: 'flex-start',
  display: 'flex',
  flexWrap: 'wrap',
  gap: 8,
  justifyContent: 'flex-end',
} as const;

const editorChromeStyle = {
  alignItems: 'center',
  display: 'flex',
  gap: 8,
} as const;

const codeLineStyle = {
  display: 'grid',
  gridTemplateColumns: '64px minmax(0, 1fr)',
} as const;

const lineNumberStyle = {
  background: 'rgba(226, 232, 240, 0.42)',
  borderRight: '1px solid rgba(148, 163, 184, 0.14)',
  color: 'var(--ant-color-text-tertiary)',
  fontFamily:
    "'SFMono-Regular', 'SF Mono', Consolas, 'Liberation Mono', Menlo, monospace",
  fontSize: 12,
  padding: '4px 12px',
  textAlign: 'right',
  userSelect: 'none',
} as const;

const codeContentStyle = {
  color: '#0f172a',
  fontFamily:
    "'SFMono-Regular', 'SF Mono', Consolas, 'Liberation Mono', Menlo, monospace",
  fontSize: 13,
  minHeight: 22,
  padding: '4px 16px',
  wordBreak: 'break-word',
} as const;

const editorViewportPaddingStyle = {
  paddingBlock: 12,
} as const;

function renderChromeDot(color: string): React.ReactNode {
  return (
    <span
      aria-hidden
      style={{
        background: color,
        borderRadius: '50%',
        display: 'inline-block',
        height: 10,
        width: 10,
      }}
    />
  );
}

function fallbackCopy(text: string): boolean {
  if (typeof document === 'undefined') {
    return false;
  }

  const textarea = document.createElement('textarea');
  textarea.value = text;
  textarea.setAttribute('readonly', 'true');
  textarea.style.position = 'absolute';
  textarea.style.left = '-9999px';
  document.body.append(textarea);
  textarea.select();

  try {
    return document.execCommand('copy');
  } finally {
    textarea.remove();
  }
}

function tokenizeYamlLine(line: string): YamlLineTokens {
  const commentMatch = line.match(/^(\s*)(#.*)$/);
  if (commentMatch) {
    return {
      indent: commentMatch[1],
      comment: commentMatch[2],
      raw: line,
    };
  }

  const keyValueMatch = line.match(
    /^(\s*)(-\s+)?([A-Za-z0-9_.-]+)(\s*:\s*)(.*)?$/,
  );
  if (keyValueMatch) {
    return {
      indent: keyValueMatch[1] ?? '',
      listMarker: keyValueMatch[2],
      key: keyValueMatch[3],
      raw: line,
      separator: keyValueMatch[4],
      value: keyValueMatch[5] ?? '',
    };
  }

  const listMatch = line.match(/^(\s*)(-\s+)(.*)$/);
  if (listMatch) {
    return {
      indent: listMatch[1] ?? '',
      listMarker: listMatch[2],
      raw: line,
      value: listMatch[3] ?? '',
    };
  }

  return {
    indent: '',
    raw: line,
    value: line,
  };
}

function renderYamlValue(value: string | undefined): React.ReactNode {
  if (!value) {
    return null;
  }

  if (/^#.*$/.test(value.trim())) {
    return <span style={{ color: '#64748b' }}>{value}</span>;
  }

  if (/^["'].*["']$/.test(value.trim())) {
    return <span style={{ color: '#047857' }}>{value}</span>;
  }

  if (/^(true|false|null|yes|no)$/i.test(value.trim())) {
    return <span style={{ color: '#c2410c' }}>{value}</span>;
  }

  if (/^-?\d+(\.\d+)?$/.test(value.trim())) {
    return <span style={{ color: '#7c3aed' }}>{value}</span>;
  }

  if (/[{}[\]]/.test(value)) {
    return <span style={{ color: '#0369a1' }}>{value}</span>;
  }

  return <span>{value}</span>;
}

const WorkflowYamlViewer: React.FC<WorkflowYamlViewerProps> = ({ yaml }) => {
  const [messageApi, contextHolder] = message.useMessage();
  const [wrapLines, setWrapLines] = useState(true);
  const [isFullscreenOpen, setIsFullscreenOpen] = useState(false);
  const [viewportHeight, setViewportHeight] = useState(() =>
    typeof window === 'undefined' ? 960 : window.innerHeight,
  );

  useEffect(() => {
    if (typeof window === 'undefined') {
      return;
    }

    const handleResize = () => setViewportHeight(window.innerHeight);
    window.addEventListener('resize', handleResize);
    return () => window.removeEventListener('resize', handleResize);
  }, []);

  const keyedLines = useMemo(() => {
    const seen = new Map<string, number>();

    return yaml.split('\n').map((line) => {
      const occurrence = (seen.get(line) ?? 0) + 1;
      seen.set(line, occurrence);

      return {
        key: `${line}-${occurrence}`,
        line,
      };
    });
  }, [yaml]);
  const yamlStats = useMemo(() => {
    const longestLine = keyedLines.reduce(
      (current, item) => Math.max(current, item.line.length),
      0,
    );

    return {
      charCount: yaml.length,
      lineCount: keyedLines.length,
      longestLine,
    };
  }, [keyedLines, yaml]);
  const fullscreenHeight = useMemo(
    () => Math.max(viewportHeight - 250, 520),
    [viewportHeight],
  );

  const handleCopy = useCallback(async () => {
    if (!yaml.trim()) {
      return;
    }

    try {
      if (navigator.clipboard?.writeText) {
        await navigator.clipboard.writeText(yaml);
      } else if (!fallbackCopy(yaml)) {
        throw new Error('Clipboard is unavailable.');
      }

      messageApi.success('YAML copied to clipboard.');
    } catch (error) {
      messageApi.error(
        error instanceof Error ? error.message : 'Failed to copy YAML.',
      );
    }
  }, [messageApi, yaml]);

  const renderYamlPanel = (
    viewerHeight: number,
    fullscreenMode: boolean,
  ): React.ReactNode => (
    <div style={editorShellStyle}>
      <div style={editorHeaderStyle}>
        <div style={editorMetaStyle}>
          <div style={editorChromeStyle}>
            {renderChromeDot('#ef4444')}
            {renderChromeDot('#f59e0b')}
            {renderChromeDot('#10b981')}
            <Typography.Text strong>
              {fullscreenMode ? 'Fullscreen YAML inspector' : 'Workflow YAML'}
            </Typography.Text>
          </div>
          <Typography.Paragraph
            type="secondary"
            style={{ margin: 0, maxWidth: 720 }}
          >
            Read-only workflow YAML with syntax highlighting, inline copy, and
            editor-style controls.
          </Typography.Paragraph>
          <Space wrap size={[8, 8]}>
            <Tag color="blue">Read only</Tag>
            <Tag>{yamlStats.lineCount} lines</Tag>
            <Tag>{yamlStats.charCount} chars</Tag>
            <Tag>{yamlStats.longestLine} max columns</Tag>
          </Space>
        </div>

        <div style={editorActionsStyle}>
          <Button
            aria-label="Toggle YAML line wrap"
            type={wrapLines ? 'primary' : 'default'}
            onClick={() => setWrapLines((current) => !current)}
          >
            {wrapLines ? 'Wrapped' : 'No wrap'}
          </Button>
          <Button
            aria-label={
              fullscreenMode ? 'Close YAML fullscreen' : 'Open YAML fullscreen'
            }
            icon={
              fullscreenMode ? (
                <FullscreenExitOutlined />
              ) : (
                <FullscreenOutlined />
              )
            }
            onClick={() =>
              fullscreenMode
                ? setIsFullscreenOpen(false)
                : setIsFullscreenOpen(true)
            }
          >
            {fullscreenMode ? 'Exit fullscreen' : 'Open fullscreen'}
          </Button>
          <Button
            aria-label="Copy workflow YAML"
            icon={<CopyOutlined />}
            onClick={() => void handleCopy()}
          >
            Copy YAML
          </Button>
        </div>
      </div>

      <div style={{ ...codeViewerStyle, maxHeight: viewerHeight }}>
        <div
          style={{
            ...editorViewportPaddingStyle,
            minWidth: wrapLines ? 0 : 960,
          }}
        >
          {keyedLines.map(({ key, line }, index) => {
            const tokens = tokenizeYamlLine(line);

            return (
              <div key={key} style={codeLineStyle}>
                <div style={lineNumberStyle}>{index + 1}</div>
                <div
                  style={{
                    ...codeContentStyle,
                    whiteSpace: wrapLines ? 'pre-wrap' : 'pre',
                  }}
                >
                  {tokens.indent}
                  {tokens.listMarker ? (
                    <span style={{ color: '#94a3b8' }}>
                      {tokens.listMarker}
                    </span>
                  ) : null}
                  {tokens.key ? (
                    <span style={{ color: '#2563eb' }}>{tokens.key}</span>
                  ) : null}
                  {tokens.separator ? (
                    <span style={{ color: '#94a3b8' }}>{tokens.separator}</span>
                  ) : null}
                  {tokens.value !== undefined
                    ? renderYamlValue(tokens.value)
                    : null}
                  {tokens.comment ? (
                    <span style={{ color: '#64748b' }}>{tokens.comment}</span>
                  ) : null}
                  {!tokens.key &&
                  !tokens.listMarker &&
                  !tokens.comment &&
                  !tokens.value
                    ? ' '
                    : null}
                </div>
              </div>
            );
          })}
        </div>
      </div>
    </div>
  );

  if (!yaml.trim()) {
    return (
      <>
        {contextHolder}
        <Empty
          image={Empty.PRESENTED_IMAGE_SIMPLE}
          description="No YAML is available for this workflow."
        />
      </>
    );
  }

  return (
    <div style={cardStackStyle}>
      {contextHolder}
      {renderYamlPanel(620, false)}
      <Modal
        centered={false}
        destroyOnHidden
        footer={null}
        onCancel={() => setIsFullscreenOpen(false)}
        open={isFullscreenOpen}
        style={{ maxWidth: '100vw', paddingBottom: 0, top: 0 }}
        styles={{
          body: {
            minHeight: 0,
            padding: 0,
          },
          container: {
            borderRadius: 0,
            display: 'flex',
            flexDirection: 'column',
            height: '100vh',
            padding: 24,
          },
          header: {
            marginBottom: 16,
          },
        }}
        title="Workflow YAML"
        width="100vw"
      >
        {renderYamlPanel(fullscreenHeight, true)}
      </Modal>
    </div>
  );
};

export default WorkflowYamlViewer;
