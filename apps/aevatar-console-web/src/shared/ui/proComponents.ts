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

export const scrollPanelStyle: CSSProperties = {
  maxHeight: 520,
  overflowY: 'auto',
  overflowX: 'hidden',
  paddingRight: 4,
};

export const tallScrollPanelStyle: CSSProperties = {
  maxHeight: 600,
  overflowY: 'auto',
  overflowX: 'hidden',
  paddingRight: 4,
};
