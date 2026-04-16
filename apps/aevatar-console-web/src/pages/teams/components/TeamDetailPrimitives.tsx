import { Space, Tooltip, Typography, theme } from "antd";
import React from "react";

export const factValueFontFamily =
  '"SFMono-Regular", "SF Mono", Menlo, Monaco, Consolas, "Liberation Mono", "Courier New", monospace';

export const SignalCard: React.FC<{
  readonly caption?: React.ReactNode;
  readonly captionMonospace?: boolean;
  readonly captionTooltip?: React.ReactNode;
  readonly icon?: React.ReactNode;
  readonly label: React.ReactNode;
  readonly value: React.ReactNode;
}> = ({
  caption,
  captionMonospace = false,
  captionTooltip,
  icon,
  label,
  value,
}) => {
  const { token } = theme.useToken();

  return (
    <div
      style={{
        background: token.colorFillAlter,
        border: `1px solid ${token.colorBorderSecondary}`,
        borderRadius: 22,
        boxShadow: token.boxShadowSecondary,
        display: "flex",
        flexDirection: "column",
        gap: 8,
        minHeight: 108,
        padding: 18,
      }}
    >
      <Space align="center" size={10}>
        {icon ? <span style={{ color: token.colorPrimary }}>{icon}</span> : null}
        <Typography.Text style={{ fontSize: 13 }} type="secondary">
          {label}
        </Typography.Text>
      </Space>
      <Typography.Title level={4} style={{ margin: 0 }}>
        {value}
      </Typography.Title>
      {typeof caption === "string" ? (
        <Tooltip
          placement="topLeft"
          title={typeof captionTooltip === "string" ? captionTooltip : caption}
        >
          <Typography.Text
            ellipsis
            style={{
              display: "block",
              fontFamily: captionMonospace ? factValueFontFamily : undefined,
              fontSize: 13,
              maxWidth: "100%",
            }}
            type="secondary"
          >
            {caption}
          </Typography.Text>
        </Tooltip>
      ) : caption ? (
        <Typography.Text style={{ fontSize: 13 }} type="secondary">
          {caption}
        </Typography.Text>
      ) : null}
    </div>
  );
};

export const DetailPill: React.FC<{
  readonly compact?: boolean;
  readonly style?: React.CSSProperties;
  readonly text: string;
}> = ({ compact = false, style, text }) => (
  <span
    style={{
      borderRadius: 999,
      display: "inline-flex",
      fontSize: compact ? 12 : 13,
      fontWeight: 600,
      lineHeight: 1,
      padding: compact ? "7px 10px" : "10px 14px",
      whiteSpace: "nowrap",
      ...style,
    }}
  >
    {text}
  </span>
);

export const FactLine: React.FC<{
  readonly monospace?: boolean;
  readonly rows?: number;
  readonly secondary?: boolean;
  readonly text: string;
  readonly tooltipText?: string;
}> = ({
  monospace = true,
  rows = 1,
  secondary = false,
  text,
  tooltipText,
}) => {
  const normalized = text || "--";

  return (
    <Tooltip placement="topLeft" title={tooltipText || normalized}>
      <Typography.Text
        strong={!secondary}
        style={{
          display: "-webkit-box",
          fontFamily: monospace ? factValueFontFamily : undefined,
          overflow: "hidden",
          overflowWrap: rows === 1 ? "normal" : "anywhere",
          textOverflow: "ellipsis",
          WebkitBoxOrient: "vertical",
          WebkitLineClamp: rows,
          whiteSpace: rows === 1 ? "nowrap" : undefined,
          wordBreak: rows === 1 ? "normal" : "break-word",
        }}
        type={secondary ? "secondary" : undefined}
      >
        {normalized}
      </Typography.Text>
    </Tooltip>
  );
};
