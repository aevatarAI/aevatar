import {
  CheckOutlined,
  DownOutlined,
  SearchOutlined,
} from "@ant-design/icons";
import React, {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
} from "react";
import { createPortal } from "react-dom";
import { Empty } from "antd";
import { RuntimeEventPreviewPanel } from "@/shared/agui/runtimeConversationPresentation";
import { AevatarHeaderSelect } from "@/shared/ui/AevatarHeaderSelect";
import {
  AEVATAR_INTERACTIVE_BUTTON_CLASS,
  AEVATAR_INTERACTIVE_CHIP_CLASS,
  joinInteractiveClassNames,
} from "@/shared/ui/interactionStandards";
import {
  CONVERSATION_ROUTE_DEFAULT_VALUE,
  USER_LLM_ROUTE_GATEWAY,
  decodeConversationRouteSelectValue,
  encodeConversationRouteSelectValue,
  trimConversationValue,
  type ConversationLlmModelGroup,
  type ConversationRouteOption,
} from "./chatConversationConfig";
import {
  parseMarkdownBlocks,
  sanitizeAssistantMessageContent,
  tokenizeInlineContent,
  type InlineContentToken,
  type MarkdownBlock,
} from "./chatContent";
import type {
  ChatMessage,
  ConversationMeta,
  PendingApprovalInfo,
  PendingRunInterventionInfo,
  RuntimeEvent,
  ServiceOption,
  StepInfo,
  ToolCallInfo,
} from "./chatTypes";

function renderInlineTokens(
  tokens: readonly InlineContentToken[],
  keyPrefix: string
): React.ReactNode[] {
  return tokens.map((token, index) => {
    if (token.kind === "code") {
      return (
        <code
          key={`${keyPrefix}-code-${index}`}
          style={{
            background: "#f3f4f6",
            borderRadius: 6,
            color: "#be185d",
            fontFamily:
              "SFMono-Regular, ui-monospace, SFMono-Regular, Menlo, monospace",
            fontSize: 12,
            padding: "2px 6px",
          }}
        >
          {token.text}
        </code>
      );
    }

    const content =
      token.kind === "link" ? (
        <a
          href={token.href}
          key={`${keyPrefix}-link-${index}`}
          rel="noreferrer"
          style={{
            color: "#2563eb",
            textDecoration: "underline",
            textUnderlineOffset: 3,
          }}
          target="_blank"
        >
          {token.text}
        </a>
      ) : (
        <span key={`${keyPrefix}-text-${index}`}>{token.text}</span>
      );

    return token.bold ? (
      <strong key={`${keyPrefix}-strong-${index}`}>{content}</strong>
    ) : (
      content
    );
  });
}

function renderLineCollection(
  lines: readonly string[],
  keyPrefix: string
): React.ReactNode {
  return lines.map((line, lineIndex) => (
    <span key={`${keyPrefix}-line-${lineIndex}`}>
      {renderInlineTokens(
        tokenizeInlineContent(line),
        `${keyPrefix}-inline-${lineIndex}`
      )}
      {lineIndex < lines.length - 1 ? <br /> : null}
    </span>
  ));
}

function renderMarkdownBlock(
  block: MarkdownBlock,
  blockIndex: number
): React.ReactNode {
  switch (block.kind) {
    case "heading":
      return (
        <div
          key={`block-${blockIndex}`}
          style={{
            color: "#111827",
            fontSize: Math.max(18 - (block.level - 1) * 2, 14),
            fontWeight: 700,
            lineHeight: 1.4,
            margin: blockIndex === 0 ? "0 0 10px" : "14px 0 10px",
          }}
        >
          {renderInlineTokens(
            tokenizeInlineContent(block.text),
            `heading-${blockIndex}`
          )}
        </div>
      );
    case "paragraph":
      return (
        <div key={`block-${blockIndex}`} style={{ margin: "0 0 10px" }}>
          {renderLineCollection(block.lines, `paragraph-${blockIndex}`)}
        </div>
      );
    case "blockquote":
      return (
        <div
          key={`block-${blockIndex}`}
          style={{
            background: "#f8fafc",
            borderLeft: "3px solid #cbd5e1",
            borderRadius: 10,
            color: "#475569",
            margin: "0 0 12px",
            padding: "10px 12px",
          }}
        >
          {renderLineCollection(block.lines, `blockquote-${blockIndex}`)}
        </div>
      );
    case "unordered-list":
    case "ordered-list":
      return (
        <div key={`block-${blockIndex}`} style={{ margin: "0 0 10px" }}>
          {block.items.map((item, itemIndex) => (
            <div
              key={`list-${blockIndex}-${itemIndex}`}
              style={{
                alignItems: "flex-start",
                display: "flex",
                gap: 8,
                marginTop: itemIndex === 0 ? 0 : 6,
              }}
            >
              <span
                style={{
                  color: "#6b7280",
                  flexShrink: 0,
                  fontSize: 13,
                  lineHeight: "24px",
                  width: 18,
                }}
              >
                {block.kind === "ordered-list" ? `${itemIndex + 1}.` : "•"}
              </span>
              <span style={{ minWidth: 0 }}>
                {renderInlineTokens(
                  tokenizeInlineContent(item),
                  `list-${blockIndex}-${itemIndex}`
                )}
              </span>
            </div>
          ))}
        </div>
      );
    case "code":
      return (
        <div
          key={`block-${blockIndex}`}
          style={{
            background: "#f8fafc",
            border: "1px solid #e5e7eb",
            borderRadius: 14,
            margin: "8px 0 12px",
            overflow: "hidden",
          }}
        >
          {block.lang ? (
            <div
              style={{
                background: "#f3f4f6",
                borderBottom: "1px solid #e5e7eb",
                color: "#6b7280",
                fontFamily:
                  "SFMono-Regular, ui-monospace, SFMono-Regular, Menlo, monospace",
                fontSize: 11,
                padding: "8px 12px",
              }}
            >
              {block.lang}
            </div>
          ) : null}
          <pre
            style={{
              fontFamily:
                "SFMono-Regular, ui-monospace, SFMono-Regular, Menlo, monospace",
              fontSize: 13,
              margin: 0,
              overflowX: "auto",
              padding: "12px 14px",
              whiteSpace: "pre-wrap",
            }}
          >
            {block.code}
          </pre>
        </div>
      );
    case "thematic-break":
      return (
        <div
          key={`block-${blockIndex}`}
          style={{
            borderTop: "1px solid #e5e7eb",
            margin: "14px 0",
          }}
        />
      );
    default:
      return null;
  }
}

function renderContent(text: string): React.ReactNode {
  const sanitized = sanitizeAssistantMessageContent(text);
  if (!sanitized) {
    return null;
  }

  return parseMarkdownBlocks(sanitized).map(renderMarkdownBlock);
}

function formatRelativeTime(isoString: string): string {
  if (!isoString) {
    return "";
  }

  const deltaMs = Date.now() - Date.parse(isoString);
  const minutes = Math.floor(deltaMs / 60_000);
  if (minutes < 1) {
    return "just now";
  }

  if (minutes < 60) {
    return `${minutes}m ago`;
  }

  const hours = Math.floor(minutes / 60);
  if (hours < 24) {
    return `${hours}h ago`;
  }

  const days = Math.floor(hours / 24);
  if (days < 7) {
    return `${days}d ago`;
  }

  return new Date(isoString).toLocaleDateString();
}

function StepIndicator({ step }: { step: StepInfo }): React.ReactElement {
  const isRunning = step.status === "running";
  const isError = step.status === "error";
  return (
    <div
      style={{
        alignItems: "center",
        display: "flex",
        gap: 8,
        padding: "4px 0",
      }}
    >
      <div
        style={{
          alignItems: "center",
          background: isError ? "#fee2e2" : isRunning ? "#fef3c7" : "#dcfce7",
          borderRadius: 999,
          display: "flex",
          height: 16,
          justifyContent: "center",
          width: 16,
        }}
      >
        {isRunning ? (
          <span
            style={{
              animation: "pulse 1.5s ease-in-out infinite",
              background: "#f59e0b",
              borderRadius: 999,
              display: "block",
              height: 8,
              width: 8,
            }}
          />
        ) : isError ? (
          <span
            style={{
              color: "#ef4444",
              fontSize: 11,
              fontWeight: 700,
              lineHeight: 1,
            }}
          >
            !
          </span>
        ) : (
          <svg
            fill="none"
            stroke="currentColor"
            strokeWidth={3}
            style={{ color: "#22c55e", height: 10, width: 10 }}
            viewBox="0 0 24 24"
          >
            <path
              d="M5 13l4 4L19 7"
              strokeLinecap="round"
              strokeLinejoin="round"
            />
          </svg>
        )}
      </div>
      <span
        style={{
          color: "#9ca3af",
          fontSize: 12,
          fontWeight: 500,
        }}
      >
        {step.name || "Processing"}
      </span>
      {step.stepType ? (
        <span
          style={{
            background: "#f3f4f6",
            borderRadius: 999,
            color: "#6b7280",
            fontSize: 10,
            marginLeft: 4,
            padding: "2px 8px",
          }}
        >
          {step.stepType}
        </span>
      ) : null}
      {step.finishedAt && step.startedAt ? (
        <span
          style={{
            color: "#d1d5db",
            fontSize: 11,
            marginLeft: "auto",
          }}
        >
          {((step.finishedAt - step.startedAt) / 1000).toFixed(1)}s
        </span>
      ) : null}
    </div>
  );
}

function ToolCallIndicator({ tool }: { tool: ToolCallInfo }): React.ReactElement {
  const [open, setOpen] = useState(false);
  return (
    <div style={{ padding: "4px 0" }}>
      <button
        className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
        onClick={() => tool.result && setOpen((value) => !value)}
        style={{
          alignItems: "center",
          background: "transparent",
          border: "none",
          color: "#9ca3af",
          cursor: tool.result ? "pointer" : "default",
          display: "flex",
          fontSize: 12,
          gap: 8,
          padding: 0,
        }}
        type="button"
      >
        <span
          style={{
            alignItems: "center",
            background: tool.status === "running" ? "#dbeafe" : "#f3f4f6",
            borderRadius: 999,
            display: "flex",
            height: 14,
            justifyContent: "center",
            width: 14,
          }}
        >
          {tool.status === "running" ? (
            <span
              style={{
                animation: "pulse 1.5s ease-in-out infinite",
                background: "#60a5fa",
                borderRadius: 999,
                display: "block",
                height: 6,
                width: 6,
              }}
            />
          ) : (
            <svg
              fill="none"
              stroke="currentColor"
              strokeWidth={2}
              style={{ color: "#9ca3af", height: 12, width: 12 }}
              viewBox="0 0 24 24"
            >
              <path
                d="M11.42 15.17l-5.59-5.59a1.5 1.5 0 010-2.12l.88-.88a1.5 1.5 0 012.12 0l3.59 3.59 7.59-7.59a1.5 1.5 0 012.12 0l.88.88a1.5 1.5 0 010 2.12l-9.59 9.59z"
                strokeLinecap="round"
                strokeLinejoin="round"
              />
            </svg>
          )}
        </span>
        <span
          style={{
            fontFamily:
              "SFMono-Regular, ui-monospace, SFMono-Regular, Menlo, monospace",
          }}
        >
          {tool.name || tool.id}
        </span>
      </button>
      {open && tool.result ? (
        <pre
          style={{
            background: "#f9fafb",
            borderRadius: 10,
            color: "#6b7280",
            fontFamily:
              "SFMono-Regular, ui-monospace, SFMono-Regular, Menlo, monospace",
            fontSize: 11,
            margin: "6px 0 0 22px",
            maxHeight: 120,
            overflow: "auto",
            padding: "8px 10px",
            whiteSpace: "pre-wrap",
          }}
        >
          {tool.result.slice(0, 500)}
        </pre>
      ) : null}
    </div>
  );
}

function ThinkingBlock({
  isStreaming,
  text,
}: {
  isStreaming: boolean;
  text: string;
}): React.ReactElement | null {
  const [open, setOpen] = useState(false);
  if (!text) {
    return null;
  }

  return (
    <div style={{ marginBottom: 10 }}>
      <button
        className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
        onClick={() => setOpen((value) => !value)}
        style={{
          alignItems: "center",
          background: "transparent",
          border: "none",
          color: "#9ca3af",
          cursor: "pointer",
          display: "flex",
          fontSize: 12,
          gap: 6,
          padding: "4px 0",
        }}
        type="button"
      >
        <svg
          fill="none"
          stroke="currentColor"
          strokeWidth={2}
          style={{
            height: 12,
            transform: open ? "rotate(90deg)" : "rotate(0deg)",
            transition: "transform 120ms ease",
            width: 12,
          }}
          viewBox="0 0 24 24"
        >
          <path
            d="M9 5l7 7-7 7"
            strokeLinecap="round"
            strokeLinejoin="round"
          />
        </svg>
        <span>Thinking</span>
        {isStreaming ? (
          <span
            style={{
              animation: "pulse 1.5s ease-in-out infinite",
              background: "#a855f7",
              borderRadius: 999,
              display: "block",
              height: 6,
              width: 6,
            }}
          />
        ) : null}
      </button>
      {open ? (
        <div
          style={{
            borderLeft: "2px solid #ede9fe",
            color: "#9ca3af",
            fontSize: 13,
            fontStyle: "italic",
            marginLeft: 14,
            maxHeight: 220,
            overflow: "auto",
            paddingLeft: 12,
            whiteSpace: "pre-wrap",
          }}
        >
          {text}
        </div>
      ) : null}
    </div>
  );
}

function ApprovalActionButton({
  busy,
  label,
  onClick,
  tone,
}: {
  busy: boolean;
  label: string;
  onClick: () => void;
  tone: "approve" | "reject";
}): React.ReactElement {
  const isApprove = tone === "approve";
  return (
    <button
      className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
      disabled={busy}
      onClick={onClick}
      style={{
        background: isApprove ? "#111827" : "#ffffff",
        border: `1px solid ${isApprove ? "#111827" : "#fca5a5"}`,
        borderRadius: 10,
        color: isApprove ? "#ffffff" : "#b91c1c",
        cursor: busy ? "wait" : "pointer",
        fontSize: 12,
        fontWeight: 600,
        opacity: busy ? 0.7 : 1,
        padding: "8px 12px",
      }}
      type="button"
    >
      {label}
    </button>
  );
}

function ApprovalCard({
  approval,
  busy,
  onDecision,
}: {
  approval: PendingApprovalInfo;
  busy: boolean;
  onDecision?: (requestId: string, approved: boolean) => void;
}): React.ReactElement {
  const statusLabel = approval.isDestructive
    ? "Operator decision required"
    : "Review before continuing";

  return (
    <div
      style={{
        background: "#fffaf0",
        border: "1px solid #fdba74",
        borderRadius: 16,
        boxShadow: "0 14px 30px rgba(245, 158, 11, 0.08)",
        color: "#7c2d12",
        marginBottom: 12,
        overflow: "hidden",
        padding: "0",
      }}
    >
      <div
        style={{
          alignItems: "flex-start",
          background: "linear-gradient(180deg, rgba(255,237,213,0.85) 0%, rgba(255,250,240,0.92) 100%)",
          borderBottom: "1px solid #fed7aa",
          display: "flex",
          flexWrap: "wrap",
          gap: 10,
          justifyContent: "space-between",
          padding: "14px 14px 12px",
        }}
      >
        <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
          <span
            style={{
              color: "#9a3412",
              fontSize: 12,
              fontWeight: 700,
              letterSpacing: "0.06em",
            }}
          >
            TOOL APPROVAL
          </span>
          <div
            style={{
              color: "#111827",
              fontSize: 15,
              fontWeight: 700,
            }}
          >
            {approval.toolName || approval.toolCallId || approval.requestId}
          </div>
          <span style={{ color: "#9a3412", fontSize: 12, lineHeight: 1.6 }}>
            {statusLabel}
          </span>
        </div>
        <div style={{ display: "flex", flexWrap: "wrap", gap: 8 }}>
          <span
            style={{
              background: "#ffedd5",
              borderRadius: 999,
              fontFamily:
                "SFMono-Regular, ui-monospace, SFMono-Regular, Menlo, monospace",
              fontSize: 11,
              padding: "3px 8px",
            }}
          >
            {approval.toolCallId || approval.requestId}
          </span>
          {approval.isDestructive ? (
            <span
              style={{
                background: "#fee2e2",
                borderRadius: 999,
                color: "#b91c1c",
                fontSize: 11,
                fontWeight: 700,
                padding: "3px 8px",
              }}
            >
              Destructive
            </span>
          ) : (
            <span
              style={{
                background: "#ecfdf5",
                borderRadius: 999,
                color: "#047857",
                fontSize: 11,
                fontWeight: 700,
                padding: "3px 8px",
              }}
            >
              Safe to review
            </span>
          )}
          <span
            style={{
              background: "#ffffff",
              border: "1px solid #fed7aa",
              borderRadius: 999,
              color: "#9a3412",
              fontSize: 11,
              fontWeight: 600,
              padding: "3px 8px",
            }}
          >
            Timeout {approval.timeoutSeconds}s
          </span>
        </div>
      </div>

      <div style={{ display: "flex", flexDirection: "column", gap: 12, padding: "14px" }}>
        <div style={{ fontSize: 13, lineHeight: 1.7 }}>
          Review the tool call and decide whether NyxID can continue.
        </div>

        <div
          style={{
            display: "grid",
            gap: 10,
            gridTemplateColumns: "repeat(auto-fit, minmax(140px, 1fr))",
          }}
        >
          <div
            style={{
              background: "rgba(255,255,255,0.78)",
              border: "1px solid #fed7aa",
              borderRadius: 12,
              padding: "10px 12px",
            }}
          >
            <div style={{ color: "#9a3412", fontSize: 11, fontWeight: 700 }}>
              Tool
            </div>
            <div style={{ color: "#7c2d12", fontSize: 12, marginTop: 4 }}>
              {approval.toolName || "Tool call"}
            </div>
          </div>
          <div
            style={{
              background: "rgba(255,255,255,0.78)",
              border: "1px solid #fed7aa",
              borderRadius: 12,
              padding: "10px 12px",
            }}
          >
            <div style={{ color: "#9a3412", fontSize: 11, fontWeight: 700 }}>
              Impact
            </div>
            <div style={{ color: "#7c2d12", fontSize: 12, marginTop: 4 }}>
              {approval.isDestructive
                ? "This tool may change runtime state."
                : "This tool only needs operator confirmation."}
            </div>
          </div>
        </div>

        <div
          style={{
            color: "#9a3412",
            fontSize: 12,
            fontWeight: 700,
            letterSpacing: "0.04em",
            textTransform: "uppercase",
          }}
        >
          Request payload
        </div>

        {approval.argumentsJson ? (
          <pre
            style={{
              background: "rgba(255,255,255,0.72)",
              border: "1px solid #fed7aa",
              borderRadius: 10,
              color: "#7c2d12",
              fontFamily:
                "SFMono-Regular, ui-monospace, SFMono-Regular, Menlo, monospace",
              fontSize: 12,
              margin: 0,
              maxHeight: 180,
              overflow: "auto",
              padding: "10px 12px",
              whiteSpace: "pre-wrap",
            }}
          >
            {approval.argumentsJson}
          </pre>
        ) : (
          <div
            style={{
              background: "rgba(255,255,255,0.72)",
              border: "1px dashed #fdba74",
              borderRadius: 10,
              color: "#9a3412",
              fontSize: 12,
              padding: "10px 12px",
            }}
          >
            No additional arguments were provided with this request.
          </div>
        )}

        <div
          style={{
            alignItems: "center",
            display: "flex",
            flexWrap: "wrap",
            gap: 8,
            justifyContent: "space-between",
            marginTop: 2,
          }}
        >
          <span style={{ color: "#9a3412", fontSize: 12 }}>
            Approve to let NyxID continue, or reject to stop this tool call.
          </span>
          <div
            style={{
              alignItems: "center",
              display: "flex",
              flexWrap: "wrap",
              gap: 8,
            }}
          >
            <ApprovalActionButton
              busy={busy}
              label={busy ? "Applying..." : "Approve"}
              onClick={() => onDecision?.(approval.requestId, true)}
              tone="approve"
            />
            <ApprovalActionButton
              busy={busy}
              label="Reject"
              onClick={() => onDecision?.(approval.requestId, false)}
              tone="reject"
            />
          </div>
        </div>
      </div>
    </div>
  );
}

type RunInterventionAction =
  | { kind: "resume"; value?: string }
  | { kind: "approve"; value?: string }
  | { kind: "reject"; value?: string }
  | { kind: "signal"; value?: string };

function RunInterventionCard({
  busy,
  intervention,
  onSubmit,
}: {
  busy: boolean;
  intervention: PendingRunInterventionInfo;
  onSubmit?: (action: RunInterventionAction) => void;
}): React.ReactElement {
  const [value, setValue] = useState("");

  useEffect(() => {
    setValue("");
  }, [intervention.key]);

  const isApproval = intervention.kind === "human_approval";
  const isSignal = intervention.kind === "wait_signal";
  const requiresInput = intervention.kind === "human_input";
  const primaryLabel = isSignal
    ? "Send Signal"
    : isApproval
      ? "Approve"
      : "Resume";
  const cardTone = isSignal
    ? {
        background: "#eff6ff",
        border: "#93c5fd",
        text: "#1d4ed8",
        badge: "#dbeafe",
      }
    : {
        background: "#fff7ed",
        border: "#fdba74",
        text: "#9a3412",
        badge: "#ffedd5",
      };
  const helperText = isSignal
    ? "Add an optional payload before sending the signal."
    : isApproval
      ? "Add an optional note before approving or rejecting this gate."
      : intervention.variableName
        ? `This value will be available as ${intervention.variableName}.`
        : "Provide the requested value to resume the run.";
  const placeholder = isSignal
    ? "Optional signal payload"
    : isApproval
      ? "Optional approval note"
      : intervention.variableName
        ? `Provide ${intervention.variableName}`
        : "Provide the missing input";
  const trimmedValue = value.trim();
  const statusLabel = isSignal
    ? "Waiting on an external signal"
    : isApproval
      ? "Operator approval is blocking progress"
      : "Operator input is required to continue";

  return (
    <div
      style={{
        background: "#ffffff",
        border: `1px solid ${cardTone.border}`,
        borderRadius: 16,
        boxShadow: "0 14px 28px rgba(15, 23, 42, 0.06)",
        color: cardTone.text,
        marginBottom: 12,
        overflow: "hidden",
        padding: 0,
      }}
    >
      <div
        style={{
          alignItems: "flex-start",
          background: `${cardTone.background}`,
          borderBottom: `1px solid ${cardTone.border}`,
          display: "flex",
          flexWrap: "wrap",
          gap: 10,
          justifyContent: "space-between",
          padding: "14px 14px 12px",
        }}
      >
        <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
          <span
            style={{
              fontSize: 12,
              fontWeight: 700,
              letterSpacing: "0.06em",
            }}
          >
            {isSignal
              ? "WAIT SIGNAL"
              : isApproval
                ? "HUMAN APPROVAL"
                : "INPUT REQUIRED"}
          </span>
          <div
            style={{
              color: "#111827",
              fontSize: 15,
              fontWeight: 700,
            }}
          >
            {intervention.stepId}
          </div>
          <span style={{ fontSize: 12, lineHeight: 1.6 }}>{statusLabel}</span>
        </div>
        <div style={{ display: "flex", flexWrap: "wrap", gap: 8 }}>
          <span
            style={{
              background: cardTone.badge,
              borderRadius: 999,
              fontFamily:
                "SFMono-Regular, ui-monospace, SFMono-Regular, Menlo, monospace",
              fontSize: 11,
              padding: "3px 8px",
            }}
          >
            {intervention.stepId}
          </span>
          {intervention.signalName ? (
            <span
              style={{
                background: "rgba(255,255,255,0.72)",
                borderRadius: 999,
                fontSize: 11,
                padding: "3px 8px",
              }}
            >
              Signal: {intervention.signalName}
            </span>
          ) : null}
          {intervention.variableName ? (
            <span
              style={{
                background: "rgba(255,255,255,0.72)",
                borderRadius: 999,
                fontSize: 11,
                padding: "3px 8px",
              }}
            >
              Variable: {intervention.variableName}
            </span>
          ) : null}
          {intervention.timeoutSeconds ? (
            <span
              style={{
                background: "#ffffff",
                border: `1px solid ${cardTone.border}`,
                borderRadius: 999,
                fontSize: 11,
                fontWeight: 600,
                padding: "3px 8px",
              }}
            >
              Timeout {intervention.timeoutSeconds}s
            </span>
          ) : null}
        </div>
      </div>

      <div style={{ display: "flex", flexDirection: "column", gap: 12, padding: "14px" }}>
        <div style={{ color: "#374151", fontSize: 13, lineHeight: 1.7 }}>
          {intervention.prompt}
        </div>

        <div
          style={{
            display: "grid",
            gap: 10,
            gridTemplateColumns: "repeat(auto-fit, minmax(150px, 1fr))",
          }}
        >
          <div
            style={{
              background: "#fafaf8",
              border: "1px solid #ece8e1",
              borderRadius: 12,
              padding: "10px 12px",
            }}
          >
            <div style={{ color: "#78716c", fontSize: 11, fontWeight: 700 }}>
              Next action
            </div>
            <div style={{ color: "#111827", fontSize: 12, marginTop: 4 }}>
              {isSignal
                ? "Send the signal to unblock the run."
                : isApproval
                  ? "Approve or reject this gate."
                  : "Provide the missing value and resume."}
            </div>
          </div>
          <div
            style={{
              background: "#fafaf8",
              border: "1px solid #ece8e1",
              borderRadius: 12,
              padding: "10px 12px",
            }}
          >
            <div style={{ color: "#78716c", fontSize: 11, fontWeight: 700 }}>
              Context
            </div>
            <div style={{ color: "#111827", fontSize: 12, marginTop: 4 }}>
              {helperText}
            </div>
          </div>
        </div>

        <label
          style={{
            color: "#6b7280",
            display: "flex",
            flexDirection: "column",
            fontSize: 12,
            fontWeight: 700,
            gap: 8,
            letterSpacing: "0.04em",
            textTransform: "uppercase",
          }}
        >
          {isSignal
            ? "Signal payload"
            : isApproval
              ? "Operator note"
              : "Required input"}
          <textarea
            aria-label={`Run intervention input ${intervention.key}`}
            disabled={busy}
            placeholder={placeholder}
            style={{
              background: "#ffffff",
              border: `1px solid ${cardTone.border}`,
              borderRadius: 12,
              color: "#1f2937",
              fontFamily: "inherit",
              fontSize: 13,
              fontWeight: 400,
              letterSpacing: "normal",
              lineHeight: 1.6,
              minHeight: 84,
              padding: "10px 12px",
              resize: "vertical",
              textTransform: "none",
              width: "100%",
            }}
            value={value}
            onChange={(event) => setValue(event.target.value)}
          />
        </label>

        <div
          style={{
            alignItems: "center",
            display: "flex",
            flexWrap: "wrap",
            gap: 8,
            justifyContent: "space-between",
          }}
        >
          <span style={{ color: "#6b7280", fontSize: 12 }}>
            {requiresInput && !trimmedValue
              ? "A value is required before the run can continue."
              : isSignal
                ? "Sending the signal will unblock this wait state."
                : isApproval
                  ? "Reject only when the run should stop at this gate."
                  : "Resume will continue the run with the value above."}
          </span>
          <div
            style={{
              alignItems: "center",
              display: "flex",
              flexWrap: "wrap",
              gap: 8,
            }}
          >
            <ApprovalActionButton
              busy={busy || (requiresInput && !trimmedValue)}
              label={busy ? "Applying..." : primaryLabel}
              onClick={() =>
                onSubmit?.({
                  kind: isSignal ? "signal" : isApproval ? "approve" : "resume",
                  value: trimmedValue || undefined,
                })
              }
              tone="approve"
            />
            {isApproval ? (
              <ApprovalActionButton
                busy={busy}
                label="Reject"
                onClick={() =>
                  onSubmit?.({
                    kind: "reject",
                    value: trimmedValue || undefined,
                  })
                }
                tone="reject"
              />
            ) : null}
          </div>
        </div>
      </div>
    </div>
  );
}

export function ChatMessageBubble({
  activeApprovalRequestId,
  activeRunInterventionKey,
  message,
  onApprovalDecision,
  onRunInterventionAction,
}: {
  activeApprovalRequestId?: string | null;
  activeRunInterventionKey?: string | null;
  message: ChatMessage;
  onApprovalDecision?: (requestId: string, approved: boolean) => void;
  onRunInterventionAction?: (
    messageId: string,
    intervention: PendingRunInterventionInfo,
    action: RunInterventionAction
  ) => void;
}): React.ReactElement {
  const isUser = message.role === "user";
  const [actionsOpen, setActionsOpen] = useState(false);
  const hasSteps = (message.steps?.length ?? 0) > 0;
  const hasTools = (message.toolCalls?.length ?? 0) > 0;
  const isProcessingApproval =
    activeApprovalRequestId &&
    message.pendingApproval?.requestId === activeApprovalRequestId;
  const isProcessingRunIntervention =
    activeRunInterventionKey &&
    message.pendingRunIntervention?.key === activeRunInterventionKey;

  if (isUser) {
    return (
      <div style={{ display: "flex", justifyContent: "flex-end" }}>
        <div
          style={{
            background: "#2563eb",
            borderRadius: 18,
            borderBottomRightRadius: 8,
            color: "#ffffff",
            fontSize: 14,
            lineHeight: 1.7,
            maxWidth: "80%",
            padding: "12px 16px",
            whiteSpace: "pre-wrap",
            wordBreak: "break-word",
          }}
        >
          {message.content}
        </div>
      </div>
    );
  }

  return (
    <div style={{ display: "flex", gap: 12 }}>
      <div
        style={{
          alignItems: "center",
          background: "linear-gradient(135deg, #8b5cf6 0%, #4f46e5 100%)",
          borderRadius: 999,
          boxShadow: "0 10px 25px rgba(99, 102, 241, 0.18)",
          color: "#ffffff",
          display: "flex",
          flexShrink: 0,
          height: 28,
          justifyContent: "center",
          marginTop: 4,
          width: 28,
        }}
      >
        <svg
          fill="none"
          stroke="currentColor"
          strokeWidth={2}
          style={{ height: 14, width: 14 }}
          viewBox="0 0 24 24"
        >
          <path
            d="M9.75 3.104v5.714a2.25 2.25 0 01-.659 1.591L5 14.5M9.75 3.104a24.301 24.301 0 014.5 0m0 0v5.714c0 .597.237 1.17.659 1.591L19.8 15.3M19.8 15.3l-1.57.393A9.065 9.065 0 0112 15a9.065 9.065 0 00-6.23.693L5 14.5m14.8.8l1.402 1.402c1.232 1.232.65 3.318-1.067 3.611A48.309 48.309 0 0112 21c-2.773 0-5.491-.235-8.135-.687-1.718-.293-2.3-2.379-1.067-3.61L5 14.5"
            strokeLinecap="round"
            strokeLinejoin="round"
          />
        </svg>
      </div>

      <div style={{ flex: 1, maxWidth: "85%", minWidth: 0 }}>
        {message.thinking ? (
          <ThinkingBlock
            isStreaming={message.status === "streaming"}
            text={message.thinking}
          />
        ) : null}

        {message.pendingApproval ? (
          <ApprovalCard
            approval={message.pendingApproval}
            busy={Boolean(isProcessingApproval)}
            onDecision={onApprovalDecision}
          />
        ) : null}

        {message.pendingRunIntervention ? (
          <RunInterventionCard
            busy={Boolean(isProcessingRunIntervention)}
            intervention={message.pendingRunIntervention}
            onSubmit={(action) =>
              onRunInterventionAction?.(
                message.id,
                message.pendingRunIntervention!,
                action
              )
            }
          />
        ) : null}

        {hasSteps || hasTools ? (
          <div style={{ marginBottom: 6 }}>
            <button
              className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
              onClick={() => setActionsOpen((value) => !value)}
              style={{
                alignItems: "center",
                background: "transparent",
                border: "none",
                color: "#9ca3af",
                cursor: "pointer",
                display: "flex",
                fontSize: 12,
                gap: 6,
                padding: "4px 0",
              }}
              type="button"
            >
              <svg
                fill="none"
                stroke="currentColor"
                strokeWidth={2}
                style={{
                  height: 12,
                  transform: actionsOpen ? "rotate(90deg)" : "rotate(0deg)",
                  transition: "transform 120ms ease",
                  width: 12,
                }}
                viewBox="0 0 24 24"
              >
                <path
                  d="M9 5l7 7-7 7"
                  strokeLinecap="round"
                  strokeLinejoin="round"
                />
              </svg>
              <span>
                {(message.steps?.length ?? 0) + (message.toolCalls?.length ?? 0)}{" "}
                action
                {(message.steps?.length ?? 0) + (message.toolCalls?.length ?? 0) > 1
                  ? "s"
                  : ""}
              </span>
              {message.steps?.some((step) => step.status === "running") ||
              message.toolCalls?.some((tool) => tool.status === "running") ? (
                <span
                  style={{
                    animation: "pulse 1.5s ease-in-out infinite",
                    background: "#f59e0b",
                    borderRadius: 999,
                    display: "block",
                    height: 6,
                    width: 6,
                  }}
                />
              ) : null}
            </button>
            {actionsOpen ? (
              <div
                style={{
                  borderLeft: "2px solid #f3f4f6",
                  marginBottom: 10,
                  marginLeft: 6,
                  paddingLeft: 10,
                }}
              >
                {message.steps?.map((step) => (
                  <StepIndicator
                    key={`${step.id || step.name}-${step.startedAt}`}
                    step={step}
                  />
                ))}
                {message.toolCalls?.map((tool) => (
                  <ToolCallIndicator
                    key={`${tool.id}-${tool.startedAt}`}
                    tool={tool}
                  />
                ))}
              </div>
            ) : null}
          </div>
        ) : null}

        <div
          style={{
            color: "#1f2937",
            fontSize: 14,
            lineHeight: 1.75,
          }}
        >
          <div style={{ wordBreak: "break-word" }}>
            {renderContent(message.content)}
            {message.status === "streaming" && message.content ? (
              <span
                style={{
                  animation: "blink 1s step-end infinite",
                  background: "#9ca3af",
                  display: "inline-block",
                  height: 18,
                  marginLeft: 4,
                  verticalAlign: "text-bottom",
                  width: 2,
                }}
              />
            ) : null}
          </div>
          {!message.content && message.status === "streaming" ? (
            <div
              style={{
                alignItems: "center",
                display: "flex",
                gap: 6,
                padding: "10px 0",
              }}
            >
              {[0, 1, 2].map((index) => (
                <span
                  key={index}
                  style={{
                    animation: "bounce 1s ease-in-out infinite",
                    animationDelay: `${index * 160}ms`,
                    background: "#d1d5db",
                    borderRadius: 999,
                    display: "block",
                    height: 6,
                    width: 6,
                  }}
                />
              ))}
            </div>
          ) : null}
        </div>

        {message.status === "error" && message.error ? (
          <div
            style={{
              alignItems: "flex-start",
              background: "#fef2f2",
              border: "1px solid #fecaca",
              borderRadius: 12,
              color: "#dc2626",
              display: "flex",
              gap: 8,
              marginTop: 10,
              padding: "10px 12px",
            }}
          >
            <svg
              fill="none"
              stroke="currentColor"
              strokeWidth={2}
              style={{ flexShrink: 0, height: 16, marginTop: 2, width: 16 }}
              viewBox="0 0 24 24"
            >
              <path
                d="M12 9v3.75m9-.75a9 9 0 11-18 0 9 9 0 0118 0zm-9 3.75h.008v.008H12v-.008z"
                strokeLinecap="round"
                strokeLinejoin="round"
              />
            </svg>
            <span style={{ fontSize: 13 }}>{message.error}</span>
          </div>
        ) : null}
      </div>
    </div>
  );
}

export function ServiceSelector({
  onCreate,
  onSelect,
  selected,
  services,
}: {
  onCreate?: () => void;
  onSelect: (serviceId: string) => void;
  selected: string;
  services: readonly ServiceOption[];
}): React.ReactElement {
  return (
    <AevatarHeaderSelect
      ariaLabel="Chat service"
      maxWidth={220}
      menuAction={
        onCreate
          ? {
              label: "Create",
              onClick: onCreate,
            }
          : undefined
      }
      menuTitle="Services"
      minWidth={168}
      onChange={onSelect}
      options={services.map((service) => ({
        badge:
          service.kind === "nyxid-chat"
            ? "Built-in"
            : service.kind === "onboarding"
              ? "Setup"
              : "Service",
        description:
          service.kind === "nyxid-chat"
            ? "Built-in console assistant"
            : service.kind === "onboarding"
              ? "Connect an AI provider and save it to Studio Settings"
            : `${service.id}${service.deploymentStatus ? ` · ${service.deploymentStatus}` : ""}`,
        label: service.label,
        value: service.id,
      }))}
      value={selected}
    />
  );
}

export function ChatToolsMenu({
  advancedOpen,
  eventStreamOpen,
  onToggleAdvanced,
  onToggleEventStream,
}: {
  advancedOpen: boolean;
  eventStreamOpen: boolean;
  onToggleAdvanced: () => void;
  onToggleEventStream: () => void;
}): React.ReactElement {
  const [open, setOpen] = useState(false);
  const rootRef = useRef<HTMLDivElement | null>(null);
  const activeCount = Number(advancedOpen) + Number(eventStreamOpen);

  useEffect(() => {
    if (!open) {
      return undefined;
    }

    const handlePointerDown = (event: MouseEvent) => {
      if (!rootRef.current?.contains(event.target as Node)) {
        setOpen(false);
      }
    };

    const handleEscape = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        setOpen(false);
      }
    };

    window.addEventListener("mousedown", handlePointerDown);
    window.addEventListener("keydown", handleEscape);
    return () => {
      window.removeEventListener("mousedown", handlePointerDown);
      window.removeEventListener("keydown", handleEscape);
    };
  }, [open]);

  const menuItems = [
    {
      actionLabel: advancedOpen ? "Hide panel" : "Open panel",
      description:
        "Inspect scope state, launch endpoints, and review runtime evidence.",
      label: "Advanced Console",
      onClick: onToggleAdvanced,
      open: advancedOpen,
    },
    {
      actionLabel: eventStreamOpen ? "Hide stream" : "Show stream",
      description:
        "Review raw AGUI runtime events when you need protocol-level detail.",
      label: "Event Stream",
      onClick: onToggleEventStream,
      open: eventStreamOpen,
    },
  ] as const;

  return (
    <div ref={rootRef} style={{ position: "relative" }}>
      <button
        aria-expanded={open}
        aria-haspopup="menu"
        aria-label="Tools"
        className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
        onClick={() => setOpen((current) => !current)}
        style={{
          alignItems: "center",
          background: "#ffffff",
          border: `1px solid ${open ? "#d9e5fb" : "#e7e5e4"}`,
          borderRadius: 10,
          color: "#4b5563",
          cursor: "pointer",
          display: "inline-flex",
          fontSize: 12,
          fontWeight: 600,
          gap: 8,
          padding: "8px 12px",
        }}
        type="button"
      >
        <span>Tools</span>
        {activeCount > 0 ? (
          <span
            style={{
              background: "#eff6ff",
              borderRadius: 999,
              color: "#2563eb",
              fontSize: 11,
              fontWeight: 700,
              minWidth: 18,
              padding: "2px 6px",
              textAlign: "center",
            }}
          >
            {activeCount}
          </span>
        ) : null}
        <DownOutlined
          style={{
            color: open ? "#2563eb" : "#9ca3af",
            fontSize: 11,
            transform: open ? "rotate(180deg)" : undefined,
          }}
        />
      </button>

      {open ? (
        <div
          role="menu"
          style={{
            background: "#ffffff",
            border: "1px solid #e7e5e4",
            borderRadius: 16,
            boxShadow: "0 18px 42px rgba(15, 23, 42, 0.12)",
            minWidth: 320,
            padding: 10,
            position: "absolute",
            right: 0,
            top: "calc(100% + 10px)",
            zIndex: 12,
          }}
        >
          <div
            style={{
              color: "#9ca3af",
              fontSize: 11,
              fontWeight: 700,
              letterSpacing: "0.12em",
              padding: "4px 6px 10px",
              textTransform: "uppercase",
            }}
          >
            Operator Tools
          </div>
          <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
            {menuItems.map((item) => (
              <div
                key={item.label}
                style={{
                  background: item.open ? "#f8fafc" : "#ffffff",
                  border: `1px solid ${item.open ? "#cbd5e1" : "#ece8e1"}`,
                  borderRadius: 14,
                  display: "flex",
                  gap: 12,
                  justifyContent: "space-between",
                  padding: "12px 12px 12px 14px",
                }}
              >
                <div style={{ minWidth: 0 }}>
                  <div
                    style={{
                      alignItems: "center",
                      color: "#111827",
                      display: "flex",
                      gap: 8,
                    }}
                  >
                    <span style={{ fontSize: 13, fontWeight: 700 }}>
                      {item.label}
                    </span>
                    {item.open ? (
                      <span
                        style={{
                          background: "#ecfdf5",
                          borderRadius: 999,
                          color: "#047857",
                          fontSize: 10,
                          fontWeight: 700,
                          letterSpacing: "0.08em",
                          padding: "2px 8px",
                          textTransform: "uppercase",
                        }}
                      >
                        Live
                      </span>
                    ) : null}
                  </div>
                  <div
                    style={{
                      color: "#6b7280",
                      fontSize: 12,
                      lineHeight: 1.5,
                      marginTop: 4,
                    }}
                  >
                    {item.description}
                  </div>
                </div>
                <button
                  aria-label={item.label}
                  className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                  onClick={() => {
                    item.onClick();
                    setOpen(false);
                  }}
                  role="menuitem"
                  style={{
                    alignSelf: "center",
                    background: item.open ? "#eff6ff" : "#111827",
                    border: `1px solid ${item.open ? "#bfdbfe" : "#111827"}`,
                    borderRadius: 999,
                    color: item.open ? "#1d4ed8" : "#ffffff",
                    cursor: "pointer",
                    flexShrink: 0,
                    fontSize: 11,
                    fontWeight: 700,
                    padding: "8px 12px",
                  }}
                  type="button"
                >
                  {item.actionLabel}
                </button>
              </div>
            ))}
          </div>
        </div>
      ) : null}
    </div>
  );
}

export function ConversationLlmConfigBar({
  disabled = false,
  effectiveModel,
  effectiveRoute,
  effectiveRouteLabel,
  modelGroups,
  modelValue,
  modelsLoading,
  onModelChange,
  onReset,
  onRouteChange,
  routeOptions,
  routeValue,
}: {
  disabled?: boolean;
  effectiveModel: string;
  effectiveRoute: string;
  effectiveRouteLabel: string;
  modelGroups: readonly ConversationLlmModelGroup[];
  modelValue?: string;
  modelsLoading: boolean;
  onModelChange: (value: string | undefined) => void;
  onReset: () => void;
  onRouteChange: (value: string | undefined) => void;
  routeOptions: readonly ConversationRouteOption[];
  routeValue?: string;
}): React.ReactElement {
  const hasOverride =
    routeValue !== undefined || Boolean(trimConversationValue(modelValue));
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState("");
  const panelRef = useRef<HTMLDivElement | null>(null);
  const triggerRef = useRef<HTMLButtonElement | null>(null);
  const [panelPosition, setPanelPosition] = useState<{
    height: number;
    left: number;
    top: number;
    width: number;
  } | null>(null);
  const selectedModel = trimConversationValue(modelValue) || effectiveModel;
  const routeSelectValue = encodeConversationRouteSelectValue(routeValue);
  const normalizedQuery = query.trim().toLowerCase();
  const filteredGroups = useMemo(
    () =>
      modelGroups
        .map((group) => ({
          ...group,
          models: normalizedQuery
            ? group.models.filter((model) =>
                model.toLowerCase().includes(normalizedQuery)
              )
            : group.models,
        }))
        .filter((group) => group.models.length > 0),
    [modelGroups, normalizedQuery]
  );
  const exactModelMatch = useMemo(
    () =>
      Boolean(
        query.trim() &&
          modelGroups.some((group) =>
            group.models.some(
              (model) => model.toLowerCase() === normalizedQuery
            )
          )
      ),
    [modelGroups, normalizedQuery, query]
  );

  useEffect(() => {
    if (!open) {
      setPanelPosition(null);
      setQuery("");
      return undefined;
    }

    const updatePanelPosition = () => {
      const trigger = triggerRef.current;
      if (!trigger) {
        return;
      }

      const triggerRect = trigger.getBoundingClientRect();
      const viewportPadding = 12;
      const offset = 12;
      const preferredHeight = 460;
      const preferredWidth = 380;
      const panelWidth = Math.min(
        preferredWidth,
        window.innerWidth - viewportPadding * 2
      );
      const spaceAbove = Math.max(
        0,
        triggerRect.top - viewportPadding - offset
      );
      const spaceBelow = Math.max(
        0,
        window.innerHeight - triggerRect.bottom - viewportPadding - offset
      );
      const panelHeight = Math.min(
        preferredHeight,
        window.innerHeight - viewportPadding * 2
      );
      const preferAbove = spaceAbove >= panelHeight || spaceAbove >= spaceBelow;

      let left = triggerRect.left;
      left = Math.max(
        viewportPadding,
        Math.min(left, window.innerWidth - panelWidth - viewportPadding)
      );

      let top = preferAbove
        ? triggerRect.top - panelHeight - offset
        : triggerRect.bottom + offset;
      top = Math.max(
        viewportPadding,
        Math.min(top, window.innerHeight - panelHeight - viewportPadding)
      );

      setPanelPosition({
        height: panelHeight,
        left,
        top,
        width: panelWidth,
      });
    };

    const handlePointerDown = (event: MouseEvent) => {
      if (!(event.target instanceof Node)) {
        return;
      }

      if (
        triggerRef.current?.contains(event.target) ||
        panelRef.current?.contains(event.target)
      ) {
        return;
      }

      setOpen(false);
    };

    const handleEscape = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        setOpen(false);
      }
    };

    const rafId = window.requestAnimationFrame(updatePanelPosition);
    document.addEventListener("pointerdown", handlePointerDown);
    window.addEventListener("resize", updatePanelPosition);
    window.addEventListener("scroll", updatePanelPosition, true);
    window.addEventListener("keydown", handleEscape);
    return () => {
      window.cancelAnimationFrame(rafId);
      document.removeEventListener("pointerdown", handlePointerDown);
      window.removeEventListener("resize", updatePanelPosition);
      window.removeEventListener("scroll", updatePanelPosition, true);
      window.removeEventListener("keydown", handleEscape);
    };
  }, [open]);

  const handleModelSelect = useCallback(
    (nextModel?: string) => {
      onModelChange(trimConversationValue(nextModel));
      setOpen(false);
      setQuery("");
    },
    [onModelChange]
  );

  const renderPanel = () => {
    if (!open || typeof document === "undefined") {
      return null;
    }

    return createPortal(
      <div
        className="scope-chat-llm-panel"
        ref={panelRef}
        style={
          panelPosition
            ? {
                height: panelPosition.height,
                left: panelPosition.left,
                maxWidth: "min(380px, calc(100vw - 24px))",
                position: "fixed",
                top: panelPosition.top,
                width: panelPosition.width,
                zIndex: 90,
              }
            : {
                left: 0,
                position: "fixed",
                top: 0,
                visibility: "hidden",
                width: 380,
                zIndex: 90,
              }
        }
      >
        <div className="scope-chat-llm-panel-header">
          <div className="scope-chat-llm-panel-title">Conversation model</div>
          {hasOverride ? (
            <button
              className={joinInteractiveClassNames(
                "scope-chat-llm-reset",
                AEVATAR_INTERACTIVE_BUTTON_CLASS,
              )}
              onClick={() => {
                onReset();
                setOpen(false);
              }}
              type="button"
            >
              Reset
            </button>
          ) : null}
        </div>

        <div className="scope-chat-llm-search">
          <SearchOutlined className="scope-chat-llm-search-icon" />
          <input
            aria-label="Search conversation models"
            className="scope-chat-llm-search-input"
            onChange={(event) => setQuery(event.target.value)}
            onKeyDown={(event) => {
              if (event.key === "Enter" && query.trim()) {
                event.preventDefault();
                handleModelSelect(query.trim());
              }
            }}
            placeholder={modelsLoading ? "Loading models..." : "Search models..."}
            value={query}
          />
        </div>

        <div className="scope-chat-llm-route-row">
          <span className="scope-chat-llm-route-label">Route</span>
          <select
            aria-label="Conversation route"
            className="scope-chat-llm-route-select"
            onChange={(event) =>
              onRouteChange(
                decodeConversationRouteSelectValue(event.target.value)
              )
            }
            value={routeSelectValue}
          >
            <option value={CONVERSATION_ROUTE_DEFAULT_VALUE}>
              Config default
            </option>
            {routeOptions.map((option) => (
              <option key={option.value} value={option.value}>
                {option.label}
              </option>
            ))}
          </select>
        </div>

        <div className="scope-chat-llm-options">
          {query.trim() && !exactModelMatch ? (
            <button
              className={joinInteractiveClassNames(
                "scope-chat-llm-option scope-chat-llm-option--manual",
                AEVATAR_INTERACTIVE_CHIP_CLASS,
              )}
              onClick={() => handleModelSelect(query.trim())}
              type="button"
            >
              <div className="scope-chat-llm-option-main">
                <CheckOutlined style={{ opacity: 0 }} />
                <span>Use "{query.trim()}"</span>
              </div>
              <span className="scope-chat-llm-option-badge">Manual</span>
            </button>
          ) : null}

          {!modelsLoading && filteredGroups.length === 0 ? (
            <div className="scope-chat-llm-empty">
              No models for {effectiveRouteLabel}
            </div>
          ) : null}

          {filteredGroups.map((group) => (
            <div className="scope-chat-llm-group" key={group.id}>
              <div className="scope-chat-llm-group-label">{group.label}</div>
              {group.models.map((model) => {
                const isActive = selectedModel === model;
                return (
                  <button
                    className={joinInteractiveClassNames(
                      `scope-chat-llm-option${isActive ? " is-active" : ""}`,
                      AEVATAR_INTERACTIVE_CHIP_CLASS,
                    )}
                    key={model}
                    onClick={() => handleModelSelect(model)}
                    type="button"
                  >
                    <div className="scope-chat-llm-option-main">
                      <CheckOutlined style={{ opacity: isActive ? 1 : 0 }} />
                      <span>{model}</span>
                    </div>
                  </button>
                );
              })}
            </div>
          ))}
        </div>
      </div>,
      document.body
    );
  };

  return (
    <div className="scope-chat-llm-bar">
      <button
        aria-expanded={open}
        aria-haspopup="dialog"
        aria-label="Conversation model settings"
        className={joinInteractiveClassNames(
          "scope-chat-llm-trigger",
          AEVATAR_INTERACTIVE_BUTTON_CLASS,
        )}
        disabled={disabled}
        onClick={() => setOpen((value) => !value)}
        ref={triggerRef}
        type="button"
      >
        <span className="scope-chat-llm-trigger-label">
          {selectedModel || "Provider default"}
        </span>
        <DownOutlined
          className="scope-chat-llm-chevron"
          style={{
            fontSize: 15,
            transform: open ? "rotate(180deg)" : undefined,
          }}
        />
      </button>
      <span className="scope-chat-llm-inline-route">
        {effectiveRoute === USER_LLM_ROUTE_GATEWAY
          ? effectiveRouteLabel
          : `via ${effectiveRouteLabel}`}
      </span>
      {renderPanel()}
    </div>
  );
}

export function ChatInput({
  disabled,
  footer,
  isStreaming,
  onChange,
  placeholder,
  onSend,
  onStop,
  value,
}: {
  disabled: boolean;
  footer?: React.ReactNode;
  isStreaming: boolean;
  onChange: (value: string) => void;
  placeholder?: string;
  onSend: () => void;
  onStop: () => void;
  value: string;
}): React.ReactElement {
  const textareaRef = useRef<HTMLTextAreaElement | null>(null);

  const resizeTextarea = useCallback(() => {
    const element = textareaRef.current;
    if (!element) {
      return;
    }

    element.style.height = "auto";
    element.style.height = `${Math.min(element.scrollHeight, 160)}px`;
  }, []);

  const handleSend = useCallback(() => {
    if (!value.trim() || isStreaming || disabled) {
      return;
    }

    onSend();
    if (textareaRef.current) {
      textareaRef.current.style.height = "auto";
    }
  }, [disabled, isStreaming, onSend, value]);

  return (
    <div style={{ position: "relative" }}>
      <div
        style={{
          background: "#ffffff",
          border: "1px solid #e7e5e4",
          borderRadius: 18,
          boxShadow: "0 1px 2px rgba(15, 23, 42, 0.06)",
          display: "flex",
          flexDirection: "column",
          overflow: "hidden",
        }}
      >
        <div
          style={{
            alignItems: "flex-end",
            display: "flex",
          }}
        >
          <textarea
            disabled={disabled}
            onChange={(event) => {
              onChange(event.target.value);
              resizeTextarea();
            }}
            onKeyDown={(event) => {
              if (event.key === "Enter" && !event.shiftKey) {
                event.preventDefault();
                handleSend();
              }
            }}
            placeholder={placeholder || "Send a message..."}
            ref={textareaRef}
            rows={1}
            style={{
              background: "transparent",
              border: "none",
              color: "#111827",
              flex: 1,
              fontSize: 14,
              minHeight: 62,
              outline: "none",
              padding: "12px 16px 8px",
              resize: "none",
            }}
            value={value}
          />
          <div style={{ padding: "6px 8px 6px 0" }}>
            {isStreaming ? (
              <button
                aria-label="Stop"
                className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                onClick={onStop}
                style={{
                  alignItems: "center",
                  background: "#ef4444",
                  border: "none",
                  borderRadius: 10,
                  color: "#ffffff",
                  cursor: "pointer",
                  display: "flex",
                  height: 34,
                  justifyContent: "center",
                  width: 34,
                }}
                type="button"
              >
                <svg fill="currentColor" height="14" viewBox="0 0 24 24" width="14">
                  <rect height="12" rx="1" width="12" x="6" y="6" />
                </svg>
              </button>
            ) : (
              <button
                aria-label="Send"
                className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                disabled={!value.trim() || disabled}
                onClick={handleSend}
                style={{
                  alignItems: "center",
                  background: "#18181b",
                  border: "none",
                  borderRadius: 10,
                  color: "#ffffff",
                  cursor:
                    !value.trim() || disabled ? "not-allowed" : "pointer",
                  display: "flex",
                  height: 34,
                  justifyContent: "center",
                  opacity: !value.trim() || disabled ? 0.28 : 1,
                  width: 34,
                }}
                type="button"
              >
                <svg
                  fill="none"
                  stroke="currentColor"
                  strokeWidth={2}
                  height="16"
                  viewBox="0 0 24 24"
                  width="16"
                >
                  <path
                    d="M4.5 10.5L12 3m0 0l7.5 7.5M12 3v18"
                    strokeLinecap="round"
                    strokeLinejoin="round"
                  />
                </svg>
              </button>
            )}
          </div>
        </div>
        {footer ? (
          <div
            style={{
              alignItems: "center",
              display: "flex",
              gap: 12,
              minHeight: 34,
              padding: "0 16px 10px",
            }}
          >
            {footer}
          </div>
        ) : null}
      </div>
    </div>
  );
}

export function DebugPanel({
  events,
}: {
  events: readonly RuntimeEvent[];
}): React.ReactElement | null {
  if (events.length === 0) {
    return null;
  }

  return <RuntimeEventPreviewPanel events={events} title={`Raw Events (${events.length})`} />;
}

export function ConversationSidebar({
  activeId,
  conversations,
  onDelete,
  onNewChat,
  onSelect,
  onToggle,
  open,
}: {
  activeId: string | null;
  conversations: readonly ConversationMeta[];
  onDelete: (conversationId: string) => void;
  onNewChat: () => void;
  onSelect: (conversationId: string) => void;
  onToggle: () => void;
  open: boolean;
}): React.ReactElement {
  if (!open) {
    return (
      <div
        style={{
          alignItems: "center",
          background: "#ffffff",
          borderRight: "1px solid #e7e5e4",
          display: "flex",
          flexDirection: "column",
          flexShrink: 0,
          padding: "12px 0",
          width: 40,
        }}
      >
        <button
          aria-label="Show conversations"
          className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
          onClick={onToggle}
          style={{
            background: "transparent",
            border: "none",
            borderRadius: 8,
            color: "#9ca3af",
            cursor: "pointer",
            padding: 6,
          }}
          type="button"
        >
          <svg
            fill="none"
            stroke="currentColor"
            strokeWidth={2}
            height="16"
            viewBox="0 0 24 24"
            width="16"
          >
            <path
              d="M8.25 4.5l7.5 7.5-7.5 7.5"
              strokeLinecap="round"
              strokeLinejoin="round"
            />
          </svg>
        </button>
      </div>
    );
  }

  return (
    <aside
      style={{
        background: "#ffffff",
        borderRight: "1px solid #e7e5e4",
        display: "flex",
        flexDirection: "column",
        flexShrink: 0,
        width: 260,
      }}
    >
      <div
        style={{
          alignItems: "center",
          borderBottom: "1px solid #e7e5e4",
          display: "flex",
          justifyContent: "space-between",
          padding: "10px 12px",
        }}
      >
        <span
          style={{
            color: "#6b7280",
            fontSize: 12,
            fontWeight: 600,
            letterSpacing: "0.08em",
            textTransform: "uppercase",
          }}
        >
          History
        </span>
        <div style={{ display: "flex", gap: 4 }}>
          <button
            aria-label="New chat"
            className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
            onClick={onNewChat}
            style={{
              background: "transparent",
              border: "none",
              borderRadius: 8,
              color: "#9ca3af",
              cursor: "pointer",
              padding: 6,
            }}
            type="button"
          >
            <svg
              fill="none"
              stroke="currentColor"
              strokeWidth={2}
              height="16"
              viewBox="0 0 24 24"
              width="16"
            >
              <path
                d="M12 4.5v15m7.5-7.5h-15"
                strokeLinecap="round"
                strokeLinejoin="round"
              />
            </svg>
          </button>
          <button
            aria-label="Hide conversations"
            className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
            onClick={onToggle}
            style={{
              background: "transparent",
              border: "none",
              borderRadius: 8,
              color: "#9ca3af",
              cursor: "pointer",
              padding: 6,
            }}
            type="button"
          >
            <svg
              fill="none"
              stroke="currentColor"
              strokeWidth={2}
              height="16"
              viewBox="0 0 24 24"
              width="16"
            >
              <path
                d="M15.75 19.5L8.25 12l7.5-7.5"
                strokeLinecap="round"
                strokeLinejoin="round"
              />
            </svg>
          </button>
        </div>
      </div>

      <div
        style={{
          flex: 1,
          minHeight: 0,
          overflow: "auto",
        }}
      >
        {conversations.length === 0 ? (
          <div
            style={{
              color: "#d1d5db",
              fontSize: 12,
              padding: "28px 16px",
              textAlign: "center",
            }}
          >
            No conversations yet
          </div>
        ) : null}
        {conversations.map((conversation) => {
          const isActive = conversation.id === activeId;
          return (
            <div
              key={conversation.id}
              onClick={() => onSelect(conversation.id)}
              style={{
                background: isActive ? "#eff6ff" : "#ffffff",
                borderBottom: "1px solid #f3f4f6",
                borderLeft: isActive
                  ? "2px solid #3b82f6"
                  : "2px solid transparent",
                cursor: "pointer",
                padding: "10px 12px",
                position: "relative",
              }}
            >
              <div
                style={{
                  color: "#374151",
                  fontSize: 13,
                  fontWeight: 500,
                  overflow: "hidden",
                  paddingRight: 24,
                  textOverflow: "ellipsis",
                  whiteSpace: "nowrap",
                }}
              >
                {conversation.title || "Untitled"}
              </div>
              <div
                style={{
                  color: "#9ca3af",
                  fontSize: 11,
                  marginTop: 4,
                }}
              >
                {conversation.serviceId ? (
                  <span
                    style={{
                      color: "#6b7280",
                      fontFamily:
                        "SFMono-Regular, ui-monospace, SFMono-Regular, Menlo, monospace",
                    }}
                  >
                    {conversation.serviceId}
                  </span>
                ) : null}
                {conversation.serviceId ? " · " : ""}
                {conversation.messageCount} msg
                {conversation.messageCount !== 1 ? "s" : ""} ·{" "}
                {formatRelativeTime(conversation.updatedAt)}
              </div>
              <button
                aria-label={`Delete ${conversation.title || "conversation"}`}
                className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
                onClick={(event) => {
                  event.stopPropagation();
                  onDelete(conversation.id);
                }}
                style={{
                  background: "transparent",
                  border: "none",
                  borderRadius: 8,
                  color: "#d1d5db",
                  cursor: "pointer",
                  padding: 4,
                  position: "absolute",
                  right: 10,
                  top: 10,
                }}
                type="button"
              >
                <svg
                  fill="none"
                  stroke="currentColor"
                  strokeWidth={2}
                  height="14"
                  viewBox="0 0 24 24"
                  width="14"
                >
                  <path
                    d="M14.74 9l-.346 9m-4.788 0L9.26 9m9.968-3.21c.342.052.682.107 1.022.166m-1.022-.165L18.16 19.673a2.25 2.25 0 01-2.244 2.077H8.084a2.25 2.25 0 01-2.244-2.077L4.772 5.79m14.456 0a48.108 48.108 0 00-3.478-.397m-12 .562c.34-.059.68-.114 1.022-.165m0 0a48.11 48.11 0 013.478-.397m7.5 0v-.916c0-1.18-.91-2.164-2.09-2.201a51.964 51.964 0 00-3.32 0c-1.18.037-2.09 1.022-2.09 2.201v.916m7.5 0a48.667 48.667 0 00-7.5 0"
                    strokeLinecap="round"
                    strokeLinejoin="round"
                  />
                </svg>
              </button>
            </div>
          );
        })}
      </div>
    </aside>
  );
}

export function ChatMetaStrip({
  actorId,
  commandId,
  modelLabel,
  runId,
  routeLabel,
  scopeId,
  serviceId,
}: {
  actorId?: string;
  commandId?: string;
  modelLabel?: string;
  runId?: string;
  routeLabel?: string;
  scopeId?: string;
  serviceId?: string;
}): React.ReactElement {
  const [detailsOpen, setDetailsOpen] = useState(false);
  const primaryItems = [
    serviceId ? { label: "Service", value: serviceId } : null,
    routeLabel ? { label: "Route", value: routeLabel } : null,
    modelLabel ? { label: "Model", value: modelLabel } : null,
  ].filter(Boolean) as Array<{ label: string; value: string }>;
  const detailItems = [
    scopeId ? { label: "Scope", value: scopeId } : null,
    runId ? { label: "Run", value: runId } : null,
    actorId ? { label: "Actor", value: actorId } : null,
    commandId ? { label: "Command", value: commandId } : null,
  ].filter(Boolean) as Array<{ label: string; value: string }>;

  const renderChip = (
    item: { label: string; value: string },
    tone: "default" | "muted" = "default"
  ) => (
    <span
      key={`${item.label}:${item.value}`}
      style={{
        background: tone === "default" ? "#f5f5f4" : "#fafaf9",
        border: `1px solid ${tone === "default" ? "#e7e5e4" : "#eceae5"}`,
        borderRadius: 999,
        color: tone === "default" ? "#57534e" : "#78716c",
        display: "inline-flex",
        fontSize: 11,
        gap: 6,
        lineHeight: 1,
        padding: "6px 10px",
      }}
    >
      <span style={{ fontWeight: 700 }}>{item.label}</span>
      <span>{item.value}</span>
    </span>
  );

  if (primaryItems.length === 0 && detailItems.length === 0) {
    return <div style={{ marginTop: 8 }} />;
  }

  return (
    <div
      style={{
        alignItems: "center",
        display: "flex",
        flexDirection: "column",
        gap: 6,
        marginTop: 8,
      }}
    >
      <div
        style={{
          alignItems: "center",
          display: "flex",
          flexWrap: "wrap",
          gap: 6,
          justifyContent: "center",
        }}
      >
        {primaryItems.map((item) => renderChip(item))}
        {detailItems.length > 0 ? (
          <button
            className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
            onClick={() => setDetailsOpen((current) => !current)}
            style={{
              background: "transparent",
              border: "none",
              color: "#9ca3af",
              cursor: "pointer",
              fontSize: 11,
              fontWeight: 600,
              padding: "4px 2px",
            }}
            type="button"
          >
            {detailsOpen ? "Hide runtime details" : "Runtime details"}
          </button>
        ) : null}
      </div>
      {detailsOpen ? (
        <div
          style={{
            alignItems: "center",
            display: "flex",
            flexWrap: "wrap",
            gap: 6,
            justifyContent: "center",
          }}
        >
          {detailItems.map((item) => renderChip(item, "muted"))}
        </div>
      ) : null}
    </div>
  );
}

export function EmptyChatState({
  actionLabel,
  description,
  footnote,
  highlights,
  onAction,
  title,
}: {
  actionLabel?: string;
  description: string;
  footnote?: string;
  highlights?: readonly string[];
  onAction?: () => void;
  title: string;
}): React.ReactElement {
  return (
    <div
      style={{
        alignItems: "center",
        display: "flex",
        flexDirection: "column",
        justifyContent: "center",
        minHeight: 360,
        padding: "12px 0 20px",
        textAlign: "center",
      }}
    >
      <div
        style={{
          background: "#ffffff",
          border: "1px solid #ece8e1",
          borderRadius: 24,
          boxShadow: "0 20px 50px rgba(15, 23, 42, 0.08)",
          maxWidth: 520,
          padding: "28px 28px 24px",
          width: "100%",
        }}
      >
        <div
          style={{
            color: "#9ca3af",
            fontSize: 11,
            fontWeight: 700,
            letterSpacing: "0.14em",
            marginBottom: 14,
            textTransform: "uppercase",
          }}
        >
          Ready in Console
        </div>
        <div
          style={{
            alignItems: "center",
            display: "flex",
            height: 52,
            justifyContent: "center",
            margin: "0 auto 18px",
            width: 52,
          }}
        >
          <div
            style={{
              borderRadius: 18,
              boxShadow: "0 18px 36px rgba(15, 23, 42, 0.12)",
              display: "flex",
              height: 48,
              justifyContent: "center",
              overflow: "hidden",
              width: 48,
            }}
          >
            <img
              alt="NyxID"
              src="/nyxid-logo.png"
              style={{
                display: "block",
                height: "100%",
                width: "100%",
              }}
            />
          </div>
        </div>
        <div
          style={{
            color: "#111827",
            fontSize: 20,
            fontWeight: 700,
            marginBottom: 10,
          }}
        >
          {title}
        </div>
        <div
          style={{
            color: "#6b7280",
            fontSize: 14,
            lineHeight: 1.7,
            margin: "0 auto",
            maxWidth: 420,
          }}
        >
          {description}
        </div>
        {highlights && highlights.length > 0 ? (
          <div
            style={{
              display: "grid",
              gap: 10,
              gridTemplateColumns: "repeat(auto-fit, minmax(140px, 1fr))",
              marginTop: 20,
              textAlign: "left",
            }}
          >
            {highlights.map((item) => (
              <div
                key={item}
                style={{
                  background: "#fafaf8",
                  border: "1px solid #ece8e1",
                  borderRadius: 16,
                  color: "#57534e",
                  fontSize: 12,
                  lineHeight: 1.6,
                  minHeight: 74,
                  padding: "12px 14px",
                }}
              >
                {item}
              </div>
            ))}
          </div>
        ) : null}
        {actionLabel && onAction ? (
          <button
            className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
            onClick={onAction}
            style={{
              background: "#111827",
              border: "none",
              borderRadius: 999,
              color: "#ffffff",
              cursor: "pointer",
              fontSize: 13,
              fontWeight: 600,
              marginTop: 22,
              padding: "11px 18px",
            }}
            type="button"
          >
            {actionLabel}
          </button>
        ) : null}
        {footnote ? (
          <div
            style={{
              color: "#9ca3af",
              fontSize: 12,
              lineHeight: 1.6,
              marginTop: actionLabel && onAction ? 14 : 18,
            }}
          >
            {footnote}
          </div>
        ) : null}
      </div>
    </div>
  );
}

export function LoadingState(): React.ReactElement {
  return (
    <div
      style={{
        alignItems: "center",
        display: "flex",
        flex: 1,
        justifyContent: "center",
        minHeight: 360,
      }}
    >
      <Empty
        description="Loading chat workspace..."
        image={Empty.PRESENTED_IMAGE_SIMPLE}
      />
    </div>
  );
}
