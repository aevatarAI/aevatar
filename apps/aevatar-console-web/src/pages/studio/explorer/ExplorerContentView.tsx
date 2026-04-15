import { Typography } from "antd";
import React from "react";
import {
  parseMarkdownBlocks,
  sanitizeAssistantMessageContent,
  tokenizeInlineContent,
  type MarkdownBlock,
} from "@/pages/chat/chatContent";
import {
  buildExplorerContentModel,
  type ExplorerChatMessage,
  type ExplorerScriptFile,
} from "./explorerContent";

type ExplorerContentViewProps = {
  content: string | null;
  fileType: string;
};

const scrollAreaStyle: React.CSSProperties = {
  display: "flex",
  flexDirection: "column",
  gap: 16,
  maxHeight: "70vh",
  minHeight: 0,
  overflowY: "auto",
  padding: 4,
};

const emptyPanelStyle: React.CSSProperties = {
  alignItems: "center",
  background: "rgba(15, 23, 42, 0.03)",
  borderRadius: 16,
  color: "var(--ant-color-text-tertiary)",
  display: "flex",
  justifyContent: "center",
  minHeight: 280,
  padding: "32px 24px",
  textAlign: "center",
};

const infoCardStyle: React.CSSProperties = {
  background: "rgba(15, 23, 42, 0.03)",
  border: "1px solid var(--ant-color-border-secondary)",
  borderRadius: 16,
  padding: "14px 16px",
};

const preStyle: React.CSSProperties = {
  background: "rgba(15, 23, 42, 0.03)",
  border: "1px solid var(--ant-color-border-secondary)",
  borderRadius: 16,
  margin: 0,
  minHeight: 280,
  overflowX: "auto",
  padding: "16px 18px",
  whiteSpace: "pre-wrap",
  wordBreak: "break-word",
};

export default function ExplorerContentView({
  content,
  fileType,
}: ExplorerContentViewProps): React.ReactElement {
  const model = React.useMemo(
    () => buildExplorerContentModel(fileType, content ?? ""),
    [content, fileType]
  );

  if (content === null) {
    return <div style={emptyPanelStyle}>Could not load file.</div>;
  }

  if (model.kind === "chat-history") {
    return <ChatHistoryPreview messages={model.messages} />;
  }

  if (model.kind === "script-package") {
    return <ScriptPackagePreview scriptPackage={model.package} />;
  }

  if (!model.formattedText.trim()) {
    return <div style={emptyPanelStyle}>Empty file.</div>;
  }

  return (
    <div style={scrollAreaStyle}>
      <pre aria-label="Explorer file preview" style={preStyle}>
        {model.formattedText}
      </pre>
    </div>
  );
}

function ChatHistoryPreview({
  messages,
}: {
  messages: ExplorerChatMessage[];
}): React.ReactElement {
  return (
    <div aria-label="Explorer chat history preview" style={scrollAreaStyle}>
      <div style={infoCardStyle}>
        <Typography.Text type="secondary" style={{ fontSize: 12 }}>
          Conversation
        </Typography.Text>
        <div style={{ marginTop: 6 }}>
          <Typography.Text strong>
            {messages.length} message{messages.length === 1 ? "" : "s"}
          </Typography.Text>
        </div>
      </div>

      {messages.length === 0 ? (
        <div style={emptyPanelStyle}>Empty conversation.</div>
      ) : (
        messages.map((message, index) => (
          <ChatHistoryBubble
            key={message.id || `${message.timestamp}-${index}`}
            message={message}
          />
        ))
      )}
    </div>
  );
}

function ChatHistoryBubble({
  message,
}: {
  message: ExplorerChatMessage;
}): React.ReactElement {
  const isUser = message.role === "user";
  const label =
    isUser ? "User" : message.role === "assistant" ? "Assistant" : message.role;
  const sanitizedContent = isUser
    ? message.content
    : sanitizeAssistantMessageContent(message.content);

  return (
    <div
      style={{
        alignSelf: isUser ? "flex-end" : "stretch",
        background: isUser ? "rgba(22, 119, 255, 0.1)" : "white",
        border: `1px solid ${
          isUser
            ? "rgba(22, 119, 255, 0.2)"
            : "var(--ant-color-border-secondary)"
        }`,
        borderRadius: 18,
        marginLeft: isUser ? "14%" : 0,
        marginRight: isUser ? 0 : "8%",
        padding: "14px 16px",
      }}
    >
      <div
        style={{
          alignItems: "center",
          color: "var(--ant-color-text-secondary)",
          display: "flex",
          flexWrap: "wrap",
          fontSize: 12,
          gap: 8,
          marginBottom: 10,
        }}
      >
        <Typography.Text strong>{label}</Typography.Text>
        <span>{formatTimestamp(message.timestamp)}</span>
        {message.status && message.status !== "complete" ? (
          <span
            style={{
              background: "rgba(15, 23, 42, 0.05)",
              borderRadius: 999,
              padding: "2px 8px",
            }}
          >
            {message.status}
          </span>
        ) : null}
      </div>

      {!isUser && message.thinking ? (
        <ThinkingBlock text={message.thinking} />
      ) : null}

      <div>{renderMarkdownContent(sanitizedContent)}</div>

      {message.error ? (
        <div
          style={{
            background: "rgba(255, 77, 79, 0.08)",
            border: "1px solid rgba(255, 77, 79, 0.2)",
            borderRadius: 12,
            color: "var(--ant-color-error)",
            marginTop: 12,
            padding: "10px 12px",
          }}
        >
          {message.error}
        </div>
      ) : null}
    </div>
  );
}

function ThinkingBlock({ text }: { text: string }): React.ReactElement {
  const [open, setOpen] = React.useState(false);

  return (
    <div style={{ marginBottom: 10 }}>
      <button
        type="button"
        onClick={() => setOpen((current) => !current)}
        style={{
          background: "transparent",
          border: "none",
          color: "var(--ant-color-text-secondary)",
          cursor: "pointer",
          padding: 0,
        }}
      >
        Thinking
      </button>
      {open ? (
        <div
          style={{
            background: "rgba(114, 46, 209, 0.06)",
            borderRadius: 12,
            color: "var(--ant-color-text-secondary)",
            marginTop: 8,
            padding: "10px 12px",
            whiteSpace: "pre-wrap",
          }}
        >
          {text}
        </div>
      ) : null}
    </div>
  );
}

function ScriptPackagePreview({
  scriptPackage,
}: {
  scriptPackage: {
    format: string;
    entryBehaviorTypeName: string;
    entrySourcePath: string;
    csharpSources: ExplorerScriptFile[];
    protoFiles: ExplorerScriptFile[];
  };
}): React.ReactElement {
  const files = [
    ...scriptPackage.csharpSources.map((file) => ({ ...file, label: "C#" })),
    ...scriptPackage.protoFiles.map((file) => ({ ...file, label: "Proto" })),
  ];

  return (
    <div aria-label="Explorer script package preview" style={scrollAreaStyle}>
      <div style={infoCardStyle}>
        <Typography.Text type="secondary" style={{ fontSize: 12 }}>
          Script package
        </Typography.Text>
        <div style={{ marginTop: 8 }}>
          <Typography.Text>
            {files.length} file{files.length === 1 ? "" : "s"}
          </Typography.Text>
        </div>
        {scriptPackage.entrySourcePath ? (
          <div style={{ marginTop: 6 }}>
            <Typography.Text type="secondary">
              Entry: {scriptPackage.entrySourcePath}
            </Typography.Text>
          </div>
        ) : null}
      </div>

      {files.length === 0 ? (
        <div style={emptyPanelStyle}>No files in package.</div>
      ) : (
        files.map((file) => (
          <div
            key={`${file.label}-${file.path}`}
            style={{
              background: "white",
              border: "1px solid var(--ant-color-border-secondary)",
              borderRadius: 16,
              overflow: "hidden",
            }}
          >
            <div
              style={{
                alignItems: "center",
                background: "rgba(15, 23, 42, 0.03)",
                borderBottom: "1px solid var(--ant-color-border-secondary)",
                display: "flex",
                justifyContent: "space-between",
                padding: "12px 16px",
              }}
            >
              <div>
                <Typography.Text strong>{file.path}</Typography.Text>
                <div>
                  <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                    {file.label}
                  </Typography.Text>
                </div>
              </div>
              {file.path === scriptPackage.entrySourcePath ? (
                <span
                  style={{
                    background: "rgba(22, 119, 255, 0.1)",
                    borderRadius: 999,
                    color: "var(--ant-color-primary)",
                    fontSize: 12,
                    padding: "2px 8px",
                  }}
                >
                  Entry
                </span>
              ) : null}
            </div>
            <pre style={{ ...preStyle, border: "none", borderRadius: 0, minHeight: 0 }}>
              {file.content}
            </pre>
          </div>
        ))
      )}
    </div>
  );
}

function formatTimestamp(timestamp: number): string {
  if (!Number.isFinite(timestamp) || timestamp <= 0) {
    return "";
  }

  return new Date(timestamp).toLocaleString();
}

function renderMarkdownContent(text: string): React.ReactNode {
  if (!text) {
    return null;
  }

  return parseMarkdownBlocks(text).map((block, index) => renderBlock(block, index));
}

function renderBlock(block: MarkdownBlock, key: number): React.ReactElement {
  switch (block.kind) {
    case "code":
      return (
        <pre
          key={key}
          style={{
            ...preStyle,
            borderRadius: 14,
            fontSize: 13,
            marginBottom: 12,
            minHeight: 0,
            padding: "12px 14px",
          }}
        >
          {block.code}
        </pre>
      );
    case "heading": {
      const fontSize = Math.max(16, 26 - block.level * 2);
      return (
        <div
          key={key}
          style={{ fontSize, fontWeight: 600, lineHeight: 1.35, marginBottom: 10 }}
        >
          {block.text}
        </div>
      );
    }
    case "blockquote":
      return (
        <blockquote
          key={key}
          style={{
            borderLeft: "3px solid var(--ant-color-border-secondary)",
            color: "var(--ant-color-text-secondary)",
            margin: "0 0 12px",
            padding: "2px 0 2px 12px",
          }}
        >
          {block.lines.map((line, index) => (
            <div key={`${key}-${index}`}>{renderInlineContent(line)}</div>
          ))}
        </blockquote>
      );
    case "unordered-list":
      return (
        <ul key={key} style={{ margin: "0 0 12px", paddingLeft: 20 }}>
          {block.items.map((item, index) => (
            <li key={`${key}-${index}`}>{renderInlineContent(item)}</li>
          ))}
        </ul>
      );
    case "ordered-list":
      return (
        <ol key={key} style={{ margin: "0 0 12px", paddingLeft: 20 }}>
          {block.items.map((item, index) => (
            <li key={`${key}-${index}`}>{renderInlineContent(item)}</li>
          ))}
        </ol>
      );
    case "thematic-break":
      return (
        <div
          key={key}
          style={{
            borderTop: "1px solid var(--ant-color-border-secondary)",
            margin: "14px 0",
          }}
        />
      );
    case "paragraph":
    default:
      return (
        <div key={key} style={{ lineHeight: 1.7, marginBottom: 12 }}>
          {block.lines.map((line, index) => (
            <React.Fragment key={`${key}-${index}`}>
              {index > 0 ? <br /> : null}
              {renderInlineContent(line)}
            </React.Fragment>
          ))}
        </div>
      );
  }
}

function renderInlineContent(text: string): React.ReactNode {
  return tokenizeInlineContent(text).map((token, index) => {
    if (token.kind === "code") {
      return (
        <code
          key={index}
          style={{
            background: "rgba(15, 23, 42, 0.06)",
            borderRadius: 6,
            padding: "1px 6px",
          }}
        >
          {token.text}
        </code>
      );
    }

    if (token.kind === "link") {
      return (
        <a
          key={index}
          href={token.href}
          rel="noreferrer"
          style={{
            color: "var(--ant-color-link)",
            fontWeight: token.bold ? 600 : 400,
          }}
          target="_blank"
        >
          {token.text}
        </a>
      );
    }

    return (
      <span key={index} style={{ fontWeight: token.bold ? 600 : 400 }}>
        {token.text}
      </span>
    );
  });
}
