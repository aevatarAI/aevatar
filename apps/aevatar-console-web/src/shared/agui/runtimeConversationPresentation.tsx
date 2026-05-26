import React, { useState } from "react";
import { Empty, Typography } from "antd";
import type {
  RuntimeEvent,
  RuntimeStepInfo,
  RuntimeToolCallInfo,
} from "./runtimeEventSemantics";
import { AEVATAR_INTERACTIVE_BUTTON_CLASS } from "@/shared/ui/interactionStandards";

function renderInline(text: string): React.ReactNode[] {
  const parts = text.split(/(`[^`]+`)/g);
  return parts.flatMap((part, index) => {
    if (part.startsWith("`") && part.endsWith("`")) {
      return (
        <code
          key={`code-${index}`}
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
          {part.slice(1, -1)}
        </code>
      );
    }

    return part.split(/(\*\*[^*]+\*\*)/g).map((segment, segmentIndex) => {
      if (segment.startsWith("**") && segment.endsWith("**")) {
        return (
          <strong key={`strong-${index}-${segmentIndex}`}>
            {segment.slice(2, -2)}
          </strong>
        );
      }

      return <span key={`span-${index}-${segmentIndex}`}>{segment}</span>;
    });
  });
}

function renderContent(text: string): React.ReactNode {
  if (!text) {
    return null;
  }

  const blocks = text.split(/(```[\s\S]*?```)/g);
  return blocks.map((block, blockIndex) => {
    if (block.startsWith("```") && block.endsWith("```")) {
      const inner = block.slice(3, -3);
      const newlineIndex = inner.indexOf("\n");
      const language =
        newlineIndex > 0 ? inner.slice(0, newlineIndex).trim() : "";
      const code = newlineIndex > 0 ? inner.slice(newlineIndex + 1) : inner;
      return (
        <div
          key={`block-${blockIndex}`}
          style={{
            background: "#f8fafc",
            border: "1px solid #e5e7eb",
            borderRadius: 14,
            margin: "8px 0",
            overflow: "hidden",
          }}
        >
          {language ? (
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
              {language}
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
            {code}
          </pre>
        </div>
      );
    }

    return (
      <span key={`text-${blockIndex}`}>
        {block.split("\n").map((line, lineIndex, lines) => (
          <span key={`line-${blockIndex}-${lineIndex}`}>
            {renderInline(line)}
            {lineIndex < lines.length - 1 ? <br /> : null}
          </span>
        ))}
      </span>
    );
  });
}

function formatEventPreview(event: RuntimeEvent): string {
  if (event.type === "TEXT_MESSAGE_CONTENT") {
    return String(event.delta || "").slice(0, 120);
  }

  if (event.type === "RUN_ERROR") {
    return String(event.message || "");
  }

  if (event.type === "STEP_STARTED" || event.type === "STEP_FINISHED") {
    return String(event.stepName || "");
  }

  if (event.type === "CUSTOM") {
    return String((event.name as string) || "custom");
  }

  return "";
}

function StepIndicator({
  step,
}: {
  step: RuntimeStepInfo;
}): React.ReactElement {
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

function ToolCallIndicator({
  tool,
}: {
  tool: RuntimeToolCallInfo;
}): React.ReactElement {
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

export function RuntimeAssistantOutput({
  content,
  error,
  status,
  steps,
  thinking,
  title,
  toolCalls,
}: {
  content: string;
  error?: string;
  status: "complete" | "streaming" | "error";
  steps?: readonly RuntimeStepInfo[];
  thinking?: string;
  title?: string;
  toolCalls?: readonly RuntimeToolCallInfo[];
}): React.ReactElement {
  const [actionsOpen, setActionsOpen] = useState(false);
  const hasSteps = (steps?.length ?? 0) > 0;
  const hasTools = (toolCalls?.length ?? 0) > 0;

  return (
    <div
      style={{
        display: "flex",
        flexDirection: "column",
        gap: 10,
      }}
    >
      {title ? (
        <Typography.Text strong style={{ fontSize: 14 }}>
          {title}
        </Typography.Text>
      ) : null}
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

        <div style={{ flex: 1, minWidth: 0 }}>
          {thinking ? (
            <ThinkingBlock isStreaming={status === "streaming"} text={thinking} />
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
                  {(steps?.length ?? 0) + (toolCalls?.length ?? 0)} action
                  {(steps?.length ?? 0) + (toolCalls?.length ?? 0) > 1
                    ? "s"
                    : ""}
                </span>
                {steps?.some((step) => step.status === "running") ||
                toolCalls?.some((tool) => tool.status === "running") ? (
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
                  {steps?.map((step) => (
                    <StepIndicator
                      key={`${step.id || step.name}-${step.startedAt}`}
                      step={step}
                    />
                  ))}
                  {toolCalls?.map((tool) => (
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
              {renderContent(content)}
              {status === "streaming" && content ? (
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
            {!content && status === "streaming" ? (
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

          {status === "error" && error ? (
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
              <span style={{ fontSize: 13 }}>{error}</span>
            </div>
          ) : null}
        </div>
      </div>
    </div>
  );
}

export function RuntimeEventPreviewPanel({
  events,
  title,
}: {
  events: readonly RuntimeEvent[];
  title?: string;
}): React.ReactElement {
  if (events.length === 0) {
    return (
      <Empty
        description="No events have been observed yet."
        image={Empty.PRESENTED_IMAGE_SIMPLE}
      />
    );
  }

  return (
    <div
      style={{
        background: "#ffffff",
        border: "1px solid #e7e5e4",
        borderRadius: 14,
        overflow: "hidden",
      }}
    >
      <div
        style={{
          background: "#ffffff",
          borderBottom: "1px solid #e7e5e4",
          color: "#9ca3af",
          fontSize: 11,
          fontWeight: 600,
          letterSpacing: "0.08em",
          padding: "10px 14px",
          textTransform: "uppercase",
        }}
      >
        {title || `Raw Events (${events.length})`}
      </div>
      <div
        style={{
          borderTop: "1px solid transparent",
          display: "flex",
          flexDirection: "column",
          maxHeight: 240,
          overflow: "auto",
        }}
      >
        {events.map((event, index) => (
          <div
            key={`${event.type}-${event.timestamp || index}-${index}`}
            style={{
              alignItems: "flex-start",
              borderBottom:
                index === events.length - 1 ? "none" : "1px solid #f3f4f6",
              color: "#4b5563",
              display: "flex",
              fontFamily:
                "SFMono-Regular, ui-monospace, SFMono-Regular, Menlo, monospace",
              fontSize: 11,
              gap: 8,
              padding: "8px 14px",
            }}
          >
            <span
              style={{
                color: "#d1d5db",
                flexShrink: 0,
                textAlign: "right",
                width: 18,
              }}
            >
              {index + 1}
            </span>
            <span
              style={{
                color:
                  event.type === "RUN_ERROR"
                    ? "#ef4444"
                    : event.type === "TEXT_MESSAGE_CONTENT"
                      ? "#2563eb"
                      : event.type.startsWith("STEP")
                        ? "#f59e0b"
                        : event.type.startsWith("RUN")
                          ? "#22c55e"
                          : event.type.startsWith("TOOL")
                            ? "#8b5cf6"
                            : "#6b7280",
                flexShrink: 0,
                fontWeight: 700,
              }}
            >
              {event.type}
            </span>
            <span
              style={{
                color: "#9ca3af",
                minWidth: 0,
                overflow: "hidden",
                textOverflow: "ellipsis",
                whiteSpace: "nowrap",
              }}
            >
              {formatEventPreview(event)}
            </span>
          </div>
        ))}
      </div>
    </div>
  );
}
