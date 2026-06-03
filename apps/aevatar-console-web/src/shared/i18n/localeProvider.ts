import {
  enUSIntl,
  zhCNIntl,
} from '@ant-design/pro-components';
import enUS from 'antd/locale/en_US';
import zhCN from 'antd/locale/zh_CN';

export type ConsoleLocale = 'en-US' | 'zh-CN';

export function normalizeConsoleLocale(locale?: string | null): ConsoleLocale {
  const normalized = (locale || '').trim().replace(/_/g, '-').toLowerCase();

  if (normalized.startsWith('en')) {
    return 'en-US';
  }

  return 'zh-CN';
}

export function resolveAntdLocale(locale?: string | null) {
  return normalizeConsoleLocale(locale) === 'en-US' ? enUS : zhCN;
}

export function resolveProIntl(locale?: string | null) {
  return normalizeConsoleLocale(locale) === 'en-US' ? enUSIntl : zhCNIntl;
}
