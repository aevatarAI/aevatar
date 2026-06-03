/// <reference types="@testing-library/jest-dom" />

import '@umijs/max';

declare module 'slash2';
declare module '*.css';
declare module '*.less';
declare module '*.scss';
declare module '*.sass';
declare module '*.svg';
declare module '*.png';
declare module '*.jpg';
declare module '*.jpeg';
declare module '*.gif';
declare module '*.bmp';
declare module '*.tiff';
declare module 'omit.js';
declare module 'numeral';
declare module 'mockjs';

type IntlMessagePrimitive = string | number | boolean | null | undefined;

declare module '@umijs/max' {
  export type MessageDescriptor = {
    readonly defaultMessage?: string;
    readonly description?: string;
    readonly id: string;
  };

  export type ConsoleIntlShape = {
    readonly locale: string;
    readonly formatMessage: (
      descriptor: MessageDescriptor,
      values?: Record<string, IntlMessagePrimitive | import('react').ReactNode>,
    ) => string;
  };

  export function getIntl(): ConsoleIntlShape;
  export function getLocale(): string;
  export function setLocale(locale: string, realReload?: boolean): void;
  export function useIntl(): ConsoleIntlShape;
  export const FormattedMessage: import('react').ComponentType<{
    readonly defaultMessage?: string;
    readonly id: string;
    readonly values?: Record<string, IntlMessagePrimitive | import('react').ReactNode>;
  }>;
}
