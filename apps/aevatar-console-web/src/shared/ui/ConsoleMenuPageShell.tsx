import { Typography } from 'antd';
import React from 'react';

type ConsoleMenuPageShellProps = {
  readonly breadcrumb: React.ReactNode;
  readonly children: React.ReactNode;
  readonly description?: React.ReactNode;
  readonly extra?: React.ReactNode;
  readonly surfaceStyle?: React.CSSProperties;
  readonly surfacePadding?: number | string;
  readonly title: React.ReactNode;
};

const rootStyle: React.CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: 20,
  minHeight: 0,
};

const headerStyle: React.CSSProperties = {
  alignItems: 'flex-start',
  display: 'flex',
  gap: 16,
  justifyContent: 'space-between',
};

const headerTextStyle: React.CSSProperties = {
  display: 'flex',
  flex: 1,
  flexDirection: 'column',
  gap: 8,
  minWidth: 0,
};

const breadcrumbStyle: React.CSSProperties = {
  color: '#8c8c8c',
  fontSize: 14,
  fontWeight: 600,
  lineHeight: '22px',
};

const titleStyle: React.CSSProperties = {
  color: '#1d2129',
  fontSize: 24,
  fontWeight: 700,
  lineHeight: 1.25,
  margin: 0,
};

const descriptionStyle: React.CSSProperties = {
  color: '#8c8c8c',
  fontSize: 14,
  lineHeight: 1.6,
  margin: 0,
  maxWidth: 880,
};

const defaultSurfaceStyle: React.CSSProperties = {
  background: '#fafcff',
  borderRadius: 24,
  boxShadow: '0 20px 48px rgba(15, 23, 42, 0.06)',
  display: 'flex',
  flex: 1,
  flexDirection: 'column',
  minHeight: 0,
};

export const ConsoleMenuPageShell: React.FC<ConsoleMenuPageShellProps> = ({
  breadcrumb,
  children,
  description,
  extra,
  surfaceStyle,
  surfacePadding = 24,
  title,
}) => (
  <div style={rootStyle}>
    <div style={headerStyle}>
      <div style={headerTextStyle}>
        <Typography.Text style={breadcrumbStyle}>{breadcrumb}</Typography.Text>
        <Typography.Title level={2} style={titleStyle}>
          {title}
        </Typography.Title>
        {description ? (
          <Typography.Paragraph style={descriptionStyle}>
            {description}
          </Typography.Paragraph>
        ) : null}
      </div>
      {extra ? <div style={{ flexShrink: 0 }}>{extra}</div> : null}
    </div>
    <div
      style={{ ...defaultSurfaceStyle, ...surfaceStyle, padding: surfacePadding }}
    >
      {children}
    </div>
  </div>
);

export default ConsoleMenuPageShell;
