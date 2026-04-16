import dayjs from 'dayjs';
export const DATE_TIME_DISPLAY_FORMAT = 'YYYY-MM-DD HH:mm:ss';
export const COMPACT_DATE_TIME_DISPLAY_FORMAT = 'MM-DD HH:mm';
export const CLOCK_TIME_DISPLAY_FORMAT = 'HH:mm:ss';

type DateTimeValue = string | number | Date | null | undefined;

function formatByPattern(
  value: DateTimeValue,
  pattern: string,
  fallback: string,
): string {
  if (value === null || value === undefined || value === '') {
    return fallback;
  }

  const parsed = dayjs(value);
  if (!parsed.isValid()) {
    return fallback;
  }

  return parsed.format(pattern);
}

export function formatDateTime(value: DateTimeValue, fallback = 'n/a'): string {
  return formatByPattern(value, DATE_TIME_DISPLAY_FORMAT, fallback);
}

export function formatCompactDateTime(value: DateTimeValue, fallback = 'n/a'): string {
  return formatByPattern(value, COMPACT_DATE_TIME_DISPLAY_FORMAT, fallback);
}

export function formatTimeOnly(value: DateTimeValue, fallback = 'n/a'): string {
  return formatByPattern(value, CLOCK_TIME_DISPLAY_FORMAT, fallback);
}
