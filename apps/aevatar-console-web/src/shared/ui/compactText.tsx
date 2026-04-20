import { Tag, Tooltip, Typography } from "antd";
import React from "react";

export const aevatarMonoFontFamily = '"IBM Plex Mono", "SF Mono", monospace';

export function truncateMiddle(value: string, head = 4, tail = 4): string {
  if (!value || value.length <= head + tail + 3) {
    return value;
  }

  return `${value.slice(0, head)}...${value.slice(-tail)}`;
}

export function truncateTail(value: string, max = 18): string {
  if (!value || value.length <= max) {
    return value;
  }

  return `${value.slice(0, Math.max(1, max - 3))}...`;
}

type AevatarCompactTextProps = {
  color?: string;
  copyable?: boolean;
  head?: number;
  maxChars?: number;
  maxWidth?: React.CSSProperties["maxWidth"];
  mode?: "middle" | "tail";
  monospace?: boolean;
  singleLine?: boolean;
  strong?: boolean;
  style?: React.CSSProperties;
  tail?: number;
  value: string;
};

export const AevatarCompactText: React.FC<AevatarCompactTextProps> = ({
  color,
  copyable = false,
  head = 4,
  maxChars = 18,
  maxWidth = "100%",
  mode = "middle",
  monospace = false,
  singleLine = true,
  strong = false,
  style,
  tail = 4,
  value,
}) => {
  const displayValue =
    mode === "middle" ? truncateMiddle(value, head, tail) : truncateTail(value, maxChars);
  const content = (
    <Typography.Text
      copyable={copyable ? { text: value } : undefined}
      strong={strong}
      style={{
        color: color ?? "inherit",
        display: "inline-block",
        fontFamily: monospace ? aevatarMonoFontFamily : undefined,
        maxWidth,
        overflow: "hidden",
        overflowWrap: singleLine ? "normal" : "anywhere",
        textOverflow: "ellipsis",
        whiteSpace: singleLine ? "nowrap" : "normal",
        wordBreak: singleLine ? "normal" : "break-word",
        ...style,
      }}
    >
      {displayValue}
    </Typography.Text>
  );

  return displayValue !== value ? <Tooltip title={value}>{content}</Tooltip> : content;
};

type AevatarCompactTagProps = {
  color?: string;
  head?: number;
  maxChars?: number;
  maxWidth?: React.CSSProperties["maxWidth"];
  mode?: "middle" | "tail";
  monospace?: boolean;
  style?: React.CSSProperties;
  tail?: number;
  value: string;
};

export const AevatarCompactTag: React.FC<AevatarCompactTagProps> = ({
  color,
  head = 4,
  maxChars = 18,
  maxWidth = 128,
  mode = "middle",
  monospace = true,
  style,
  tail = 4,
  value,
}) => {
  const displayValue =
    mode === "middle" ? truncateMiddle(value, head, tail) : truncateTail(value, maxChars);
  const tag = (
    <Tag
      color={color}
      style={{
        fontFamily: monospace ? aevatarMonoFontFamily : undefined,
        marginInlineEnd: 0,
        maxWidth,
        overflow: "hidden",
        textOverflow: "ellipsis",
        whiteSpace: "nowrap",
        ...style,
      }}
    >
      {displayValue}
    </Tag>
  );

  return displayValue !== value ? <Tooltip title={value}>{tag}</Tooltip> : tag;
};
