import { Typography } from 'antd';
import React from 'react';

export function renderMultilineText(value: string | null | undefined) {
  if (!value) {
    return (
      <Typography.Text type="secondary">No source attached.</Typography.Text>
    );
  }

  return (
    <Typography.Paragraph
      copyable
      style={{
        marginBottom: 0,
        maxHeight: 360,
        overflow: 'auto',
        whiteSpace: 'pre-wrap',
      }}
    >
      {value}
    </Typography.Paragraph>
  );
}
