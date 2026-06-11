import { getIntl, useIntl } from "@umijs/max";
import React from "react";

type MessageValue =
  | string
  | number
  | boolean
  | null
  | undefined
  | React.ReactElement;

export type ConsoleMessageValues = Record<string, MessageValue>;
export type ConsoleMessageDescriptor = {
  readonly defaultMessage: string;
  readonly id: string;
};

export function t(
  id: string,
  defaultMessage: string,
  values?: ConsoleMessageValues,
): string {
  return getIntl().formatMessage({ defaultMessage, id }, values);
}

export function formatConsoleMessage(
  descriptor: ConsoleMessageDescriptor,
  values?: ConsoleMessageValues,
): string {
  return t(descriptor.id, descriptor.defaultMessage, values);
}

export const ConsoleMessage: React.FC<{
  readonly descriptor: ConsoleMessageDescriptor;
  readonly values?: ConsoleMessageValues;
}> = ({ descriptor, values }) => {
  const intl = useIntl();
  return <>{intl.formatMessage(descriptor, values)}</>;
};

export const T: React.FC<{
  readonly id: string;
  readonly defaultMessage: string;
  readonly values?: ConsoleMessageValues;
}> = ({ defaultMessage, id, values }) => {
  const intl = useIntl();
  return <>{intl.formatMessage({ defaultMessage, id }, values)}</>;
};
