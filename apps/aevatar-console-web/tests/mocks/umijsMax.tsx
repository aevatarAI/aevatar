import React from 'react';
import enUSMessages from '../../src/locales/en-US';
import zhCNMessages from '../../src/locales/zh-CN';

type MessageDescriptor = {
  readonly defaultMessage?: string;
  readonly id: string;
};

type MessageValue = string | number | boolean | null | undefined | React.ReactNode;
type LocaleListener = () => void;

let currentLocale = 'en-US';
const localeListeners = new Set<LocaleListener>();

const catalogs: Record<string, Record<string, string>> = {
  'en-US': enUSMessages,
  'zh-CN': zhCNMessages,
};

function interpolate(message: string, values?: Record<string, MessageValue>): string {
  if (!values) {
    return message;
  }

  return message.replace(/\{([A-Za-z0-9_]+)\}/g, (match, key) => {
    const value = values[key];
    return value === null || value === undefined ? match : String(value);
  });
}

function formatMessage(
  descriptor: MessageDescriptor,
  values?: Record<string, MessageValue>,
): string {
  const catalog = catalogs[currentLocale] || catalogs['en-US'];
  const message = catalog[descriptor.id] || descriptor.defaultMessage || descriptor.id;
  return interpolate(message, values);
}

export function getLocale(): string {
  return currentLocale;
}

export function setLocale(locale: string): void {
  currentLocale = catalogs[locale] ? locale : 'en-US';
  localeListeners.forEach((listener) => listener());
}

export function useIntl() {
  React.useSyncExternalStore(
    (listener) => {
      localeListeners.add(listener);
      return () => {
        localeListeners.delete(listener);
      };
    },
    () => currentLocale,
    () => currentLocale,
  );

  return {
    formatMessage,
    locale: currentLocale,
  };
}

export function getIntl() {
  return {
    formatMessage,
    locale: currentLocale,
  };
}

export const FormattedMessage: React.FC<{
  readonly defaultMessage?: string;
  readonly id: string;
  readonly values?: Record<string, MessageValue>;
}> = ({ defaultMessage, id, values }) => (
  <>{formatMessage({ defaultMessage, id }, values)}</>
);
