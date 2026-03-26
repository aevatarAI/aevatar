import type { ProCardProps, ProTableProps } from '@ant-design/pro-components';
import type { CSSProperties } from 'react';

export const moduleCardProps: Pick<
  ProCardProps,
  'ghost' | 'headerBordered' | 'boxShadow'
> = {
  ghost: false,
  headerBordered: true,
  boxShadow: true,
};

export const compactTableCardProps: NonNullable<
  ProTableProps<Record<string, unknown>, Record<string, unknown>>['cardProps']
> = {
  bodyStyle: { padding: 0 },
};

export const stretchColumnStyle: CSSProperties = {
  display: 'flex',
};

export const fillCardStyle: CSSProperties = {
  width: '100%',
  height: '100%',
};

export const cardStackStyle: CSSProperties = {
  width: '100%',
  display: 'flex',
  flexDirection: 'column',
  gap: 16,
};

export const embeddedPanelStyle: CSSProperties = {
  border: '1px solid var(--ant-color-border-secondary)',
  borderRadius: 8,
  padding: 16,
  background: 'var(--ant-color-bg-container)',
};

export const compactPanelHeight = 520;

export const tallPanelHeight = 600;

export const scrollPanelStyle: CSSProperties = {
  maxHeight: compactPanelHeight,
  overflowY: 'auto',
  overflowX: 'hidden',
  paddingRight: 4,
};

export const tallScrollPanelStyle: CSSProperties = {
  maxHeight: tallPanelHeight,
  overflowY: 'auto',
  overflowX: 'hidden',
  paddingRight: 4,
};

export const scrollViewportBodyStyle: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  minHeight: 0,
  overflow: 'hidden',
};

export const scrollViewportStyle: CSSProperties = {
  ...scrollPanelStyle,
  flex: 1,
  minHeight: 0,
  maxHeight: 'none',
};

export const drawerBodyStyle: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  height: '100%',
  minHeight: 0,
  overflow: 'hidden',
  padding: 12,
};

export const drawerScrollStyle: CSSProperties = {
  flex: 1,
  minHeight: 0,
  overflowX: 'hidden',
  overflowY: 'auto',
  paddingRight: 4,
};

export const summaryFieldGridStyle: CSSProperties = {
  display: 'grid',
  gap: 12,
  gridTemplateColumns: 'repeat(auto-fit, minmax(140px, 1fr))',
};

export const summaryFieldStyle: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: 4,
  minWidth: 0,
};

export const summaryFieldLabelStyle: CSSProperties = {
  color: 'var(--ant-color-text-secondary)',
  fontSize: 12,
};

export const summaryMetricGridStyle: CSSProperties = {
  display: 'grid',
  gap: 8,
  gridTemplateColumns: 'repeat(2, minmax(0, 1fr))',
};

export const summaryMetricStyle: CSSProperties = {
  background: 'var(--ant-color-fill-quaternary)',
  border: '1px solid var(--ant-color-border-secondary)',
  borderRadius: 10,
  display: 'flex',
  flexDirection: 'column',
  gap: 4,
  minWidth: 0,
  padding: '10px 12px',
};

export const summaryMetricValueStyle: CSSProperties = {
  color: 'var(--ant-color-text)',
  fontSize: 13,
  fontWeight: 600,
  lineHeight: 1.35,
};

export const cardListStyle: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: 12,
};

export const cardListItemStyle: CSSProperties = {
  ...embeddedPanelStyle,
  background: 'var(--ant-color-fill-quaternary)',
  display: 'flex',
  flexDirection: 'column',
  gap: 12,
  padding: 14,
};

export const cardListHeaderStyle: CSSProperties = {
  alignItems: 'flex-start',
  display: 'flex',
  gap: 12,
  justifyContent: 'space-between',
  width: '100%',
};

export const cardListMainStyle: CSSProperties = {
  display: 'flex',
  flex: 1,
  flexDirection: 'column',
  gap: 8,
  minWidth: 0,
};

export const cardListActionStyle: CSSProperties = {
  display: 'flex',
  flexWrap: 'wrap',
  gap: 8,
  justifyContent: 'flex-start',
};

export const cardListUrlStyle: CSSProperties = {
  margin: 0,
  wordBreak: 'break-all',
};

export const codeBlockStyle: CSSProperties = {
  background: 'var(--ant-color-fill-quaternary)',
  border: '1px solid var(--ant-color-border-secondary)',
  borderRadius: 10,
  marginTop: 12,
  maxHeight: 260,
  overflow: 'auto',
  padding: 12,
  whiteSpace: 'pre-wrap',
  wordBreak: 'break-word',
};
