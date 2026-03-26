import { Typography } from 'antd';
import React from 'react';
import { codeBlockStyle } from '@/shared/ui/proComponents';

export function renderMultilineText(value: string | null | undefined) {
  if (!value) {
    return (
      <Typography.Text type="secondary">No source attached.</Typography.Text>
    );
  }

  return (
    <Typography.Paragraph
      copyable
      style={{ ...codeBlockStyle, marginBottom: 0, maxHeight: 360 }}
    >
      {value}
    </Typography.Paragraph>
  );
}
