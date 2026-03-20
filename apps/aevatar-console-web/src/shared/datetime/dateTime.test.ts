import { DATE_TIME_DISPLAY_FORMAT, formatDateTime } from './dateTime';

describe('formatDateTime', () => {
  it('formats local Date objects into the shared display format', () => {
    expect(formatDateTime(new Date(2026, 2, 12, 8, 30, 45))).toBe(
      '2026-03-12 08:30:45',
    );
  });

  it('formats local datetime strings into the shared display format', () => {
    expect(formatDateTime('2026-03-12T08:30:45')).toBe('2026-03-12 08:30:45');
  });

  it('returns the fallback for missing or invalid values', () => {
    expect(formatDateTime(undefined)).toBe('n/a');
    expect(formatDateTime('', 'none')).toBe('none');
    expect(formatDateTime('not-a-date')).toBe('n/a');
  });

  it('exports the stable shared display format', () => {
    expect(DATE_TIME_DISPLAY_FORMAT).toBe('YYYY-MM-DD HH:mm:ss');
  });
});
