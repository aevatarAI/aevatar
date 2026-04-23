import { Typography } from 'antd';
import React from 'react';

type ConsoleMenuPageShellProps = {
  readonly breadcrumb: React.ReactNode;
  readonly children: React.ReactNode;
  readonly description?: React.ReactNode;
  readonly extra?: React.ReactNode;
  readonly surfacePadding?: number | string;
  readonly title: React.ReactNode;
};

const rootStyle: React.CSSProperties = {
  boxSizing: 'border-box',
  display: 'flex',
  flexDirection: 'column',
  gap: 20,
  maxWidth: '100%',
  minHeight: 0,
  minWidth: 0,
  width: '100%',
};

const headerStyle: React.CSSProperties = {
  alignItems: 'flex-start',
  boxSizing: 'border-box',
  display: 'flex',
  gap: 16,
  justifyContent: 'space-between',
  maxWidth: '100%',
  minWidth: 0,
  width: '100%',
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

const surfaceStyle: React.CSSProperties = {
  background: '#fafcff',
  borderRadius: 24,
  boxSizing: 'border-box',
  boxShadow: '0 20px 48px rgba(15, 23, 42, 0.06)',
  display: 'flex',
  flex: 1,
  flexDirection: 'column',
  maxWidth: '100%',
  minHeight: 0,
  minWidth: 0,
  overflowX: 'hidden',
  width: '100%',
};

export const ConsoleMenuPageShell: React.FC<ConsoleMenuPageShellProps> = ({
  breadcrumb,
  children,
  description,
  extra,
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
    <div style={{ ...surfaceStyle, padding: surfacePadding }}>{children}</div>
  </div>
);

export default ConsoleMenuPageShell;
