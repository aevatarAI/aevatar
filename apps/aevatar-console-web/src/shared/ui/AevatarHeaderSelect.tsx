import { CheckOutlined, DownOutlined } from "@ant-design/icons";
import React, { useEffect, useMemo, useRef, useState } from "react";

export type AevatarHeaderSelectOption = {
  value: string;
  label: string;
  description?: string;
  badge?: string;
  disabled?: boolean;
};

type AevatarHeaderSelectProps = {
  ariaLabel: string;
  disabled?: boolean;
  maxWidth?: number;
  menuAction?: {
    label: string;
    onClick: () => void;
  };
  menuTitle?: string;
  minWidth?: number;
  onChange: (value: string) => void;
  options: readonly AevatarHeaderSelectOption[];
  placeholder?: string;
  showTriggerDescription?: boolean;
  value: string;
};

const rootStyle: React.CSSProperties = {
  display: "inline-block",
  position: "relative",
};

const triggerContentStyle: React.CSSProperties = {
  alignItems: "center",
  display: "flex",
  flex: 1,
  gap: 8,
  minWidth: 0,
};

const triggerTextStyle: React.CSSProperties = {
  display: "flex",
  flexDirection: "column",
  gap: 1,
  minWidth: 0,
};

const triggerLabelStyle: React.CSSProperties = {
  color: "#111827",
  fontSize: 13,
  fontWeight: 600,
  overflow: "hidden",
  textAlign: "left",
  textOverflow: "ellipsis",
  whiteSpace: "nowrap",
};

const triggerDescriptionStyle: React.CSSProperties = {
  color: "#8b5e3c",
  fontSize: 10,
  fontWeight: 600,
  letterSpacing: "0.12em",
  overflow: "hidden",
  textAlign: "left",
  textOverflow: "ellipsis",
  textTransform: "uppercase",
  whiteSpace: "nowrap",
};

const leadingGlyphStyle: React.CSSProperties = {
  background: "#f8f4ee",
  border: "1px solid #e7ddd2",
  borderRadius: 999,
  flexShrink: 0,
  height: 22,
  position: "relative",
  width: 22,
};

const glyphDotStyle: React.CSSProperties = {
  background: "#2563eb",
  borderRadius: 999,
  height: 6,
  left: "50%",
  position: "absolute",
  top: "50%",
  transform: "translate(-50%, -50%)",
  width: 6,
};

const countPillStyle: React.CSSProperties = {
  background: "#faf5ef",
  border: "1px solid #ece2d8",
  borderRadius: 999,
  color: "#8b5e3c",
  fontSize: 10,
  fontWeight: 700,
  letterSpacing: "0.08em",
  padding: "4px 8px",
  textTransform: "uppercase",
};

export const AevatarHeaderSelect: React.FC<AevatarHeaderSelectProps> = ({
  ariaLabel,
  disabled = false,
  maxWidth,
  menuAction,
  menuTitle,
  minWidth = 164,
  onChange,
  options,
  placeholder = "Select",
  showTriggerDescription = false,
  value,
}) => {
  const [open, setOpen] = useState(false);
  const rootRef = useRef<HTMLDivElement | null>(null);

  const selectedOption = useMemo(
    () => options.find((option) => option.value === value) ?? options[0] ?? null,
    [options, value],
  );

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

  return (
    <div ref={rootRef} style={rootStyle}>
      <button
        aria-expanded={open}
        aria-haspopup="listbox"
        aria-label={ariaLabel}
        disabled={disabled}
        onClick={() => setOpen((current) => !current)}
        title={selectedOption?.label || placeholder}
        style={{
          alignItems: "center",
          background: "#ffffff",
          border: `1px solid ${open ? "#d9e5fb" : "#e7ddd2"}`,
          borderRadius: 14,
          boxShadow: "0 1px 3px rgba(15, 23, 42, 0.06)",
          cursor: disabled ? "not-allowed" : "pointer",
          display: "inline-flex",
          gap: 8,
          justifyContent: "space-between",
          maxWidth,
          minWidth,
          opacity: disabled ? 0.55 : 1,
          padding: "6px 10px",
        }}
        type="button"
      >
        <span style={triggerContentStyle}>
          <span style={leadingGlyphStyle}>
            <span style={glyphDotStyle} />
          </span>
          <span style={triggerTextStyle}>
            <span style={triggerLabelStyle}>
              {selectedOption?.label || placeholder}
            </span>
            {showTriggerDescription ? (
              <span style={triggerDescriptionStyle}>
                {selectedOption?.description || menuTitle || ariaLabel}
              </span>
            ) : null}
          </span>
        </span>

        <span
          style={{
            alignItems: "center",
            background: "transparent",
            border: "none",
            borderRadius: 0,
            color: open ? "#2563eb" : "#9ca3af",
            display: "inline-flex",
            flexShrink: 0,
            height: 16,
            justifyContent: "center",
            width: 16,
          }}
        >
          <DownOutlined
            style={{
              fontSize: 11,
              transform: open ? "rotate(180deg)" : undefined,
            }}
          />
        </span>
      </button>

      {open ? (
        <div
          aria-label={ariaLabel}
          role="listbox"
          style={{
            background: "#fffaf6",
            border: "1px solid #e7ddd2",
            borderRadius: 22,
            boxShadow: "0 24px 56px rgba(15, 23, 42, 0.16)",
            left: 0,
            marginTop: 10,
            minWidth: Math.max(minWidth + 48, 280),
            padding: 10,
            position: "absolute",
            top: "100%",
            zIndex: 20,
          }}
        >
          <div
            style={{
              alignItems: "center",
              display: "flex",
              gap: 10,
              justifyContent: "space-between",
              padding: "2px 4px 10px",
            }}
          >
            <div style={{ minWidth: 0 }}>
              <div
                style={{
                  color: "#8b5e3c",
                  fontSize: 10,
                  fontWeight: 700,
                  letterSpacing: "0.14em",
                  textTransform: "uppercase",
                }}
              >
                {menuTitle || ariaLabel}
              </div>
              <div
                style={{
                  color: "#6b7280",
                  fontSize: 12,
                  marginTop: 4,
                }}
              >
                Switch the active service route.
              </div>
            </div>
            <div
              style={{
                alignItems: "center",
                display: "flex",
                flexShrink: 0,
                gap: 8,
              }}
            >
              {menuAction ? (
                <button
                  onClick={() => {
                    setOpen(false);
                    menuAction.onClick();
                  }}
                  style={{
                    background: "#faf5ef",
                    border: "1px solid #ece2d8",
                    borderRadius: 999,
                    boxShadow: "0 1px 2px rgba(139, 94, 60, 0.08)",
                    color: "#8b5e3c",
                    cursor: "pointer",
                    fontSize: 11,
                    fontWeight: 700,
                    letterSpacing: "0.04em",
                    padding: "6px 12px",
                  }}
                  type="button"
                >
                  {menuAction.label}
                </button>
              ) : null}
              <span style={countPillStyle}>{options.length}</span>
            </div>
          </div>

          {options.map((option) => {
            const active = option.value === value;
            const baseBorder = active ? "#c7dbff" : "#efe5da";
            return (
              <button
                aria-selected={active}
                disabled={option.disabled}
                key={option.value}
                onClick={() => {
                  if (option.disabled) {
                    return;
                  }

                  onChange(option.value);
                  setOpen(false);
                }}
                role="option"
                style={{
                  alignItems: "center",
                  background: active ? "#f3f7ff" : "#ffffff",
                  border: `1px solid ${baseBorder}`,
                  borderRadius: 16,
                  color: option.disabled ? "#d1d5db" : "#111827",
                  cursor: option.disabled ? "not-allowed" : "pointer",
                  display: "flex",
                  gap: 12,
                  justifyContent: "space-between",
                  marginTop: 6,
                  padding: "10px 12px",
                  textAlign: "left",
                  width: "100%",
                }}
                type="button"
              >
                <span
                  style={{
                    alignItems: "center",
                    display: "flex",
                    gap: 12,
                    minWidth: 0,
                  }}
                >
                  <span
                    style={{
                      ...leadingGlyphStyle,
                      background: active ? "#ffffff" : "#f8f4ee",
                    }}
                  >
                    <span
                      style={{
                        ...glyphDotStyle,
                        background: active ? "#2563eb" : "#b08968",
                      }}
                    />
                  </span>
                  <span
                    style={{
                      display: "flex",
                      flexDirection: "column",
                      gap: 3,
                      minWidth: 0,
                    }}
                  >
                    <span
                      style={{
                        color: active ? "#1d4ed8" : "#111827",
                        fontSize: 13,
                        fontWeight: 600,
                        overflow: "hidden",
                        textOverflow: "ellipsis",
                        whiteSpace: "nowrap",
                      }}
                    >
                      {option.label}
                    </span>
                    {option.description ? (
                      <span
                        style={{
                          color: active ? "#5b86e5" : "#8b5e3c",
                          fontSize: 11,
                          overflow: "hidden",
                          textOverflow: "ellipsis",
                          whiteSpace: "nowrap",
                        }}
                      >
                        {option.description}
                      </span>
                    ) : null}
                  </span>
                </span>

                <span
                  style={{
                    alignItems: "center",
                    display: "flex",
                    flexShrink: 0,
                    gap: 8,
                  }}
                >
                  {option.badge ? (
                    <span
                      style={{
                        ...countPillStyle,
                        background: active ? "#ffffff" : "#faf5ef",
                        color: active ? "#2563eb" : "#8b5e3c",
                      }}
                    >
                      {option.badge}
                    </span>
                  ) : null}
                  <span
                    style={{
                      alignItems: "center",
                      background: active ? "#2563eb" : "transparent",
                      borderRadius: 999,
                      color: active ? "#ffffff" : "transparent",
                      display: "inline-flex",
                      height: 20,
                      justifyContent: "center",
                      width: 20,
                    }}
                  >
                    <CheckOutlined style={{ fontSize: 11 }} />
                  </span>
                </span>
              </button>
            );
          })}
        </div>
      ) : null}
    </div>
  );
};
