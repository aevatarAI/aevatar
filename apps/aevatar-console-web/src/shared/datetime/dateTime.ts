import dayjs from 'dayjs';
export const DATE_TIME_DISPLAY_FORMAT = 'YYYY-MM-DD HH:mm:ss';

type DateTimeValue = string | number | Date | null | undefined;

export function formatDateTime(value: DateTimeValue, fallback = 'n/a'): string {
  if (value === null || value === undefined || value === '') {
    return fallback;
  }

  const parsed = dayjs(value);
  if (!parsed.isValid()) {
    return fallback;
  }

  return parsed.format(DATE_TIME_DISPLAY_FORMAT);
}
