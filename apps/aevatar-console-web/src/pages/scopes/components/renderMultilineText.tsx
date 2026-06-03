import { Typography } from 'antd';
import React from 'react';
import { codeBlockStyle } from '@/shared/ui/proComponents';
import { t } from "@/shared/i18n/messages";

export function renderMultilineText(value: string | null | undefined) {
  if (!value) {
    return (
      <Typography.Text style={{ color: 'var(--ant-color-text-secondary)' }}>
        {t("pages.scopes.rendermultilinetext.no.source.attached", "No source attached.")}</Typography.Text>
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
