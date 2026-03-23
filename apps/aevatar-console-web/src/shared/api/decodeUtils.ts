export type Decoder<T> = (value: unknown, label?: string) => T;

type JsonRecord = Record<string, unknown>;

export function expectRecord(value: unknown, label: string): JsonRecord {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    throw new Error(`${label} must be an object.`);
  }

  return value as JsonRecord;
}

export function expectArray<T>(
  value: unknown,
  label: string,
  decoder: Decoder<T>
): T[] {
  if (!Array.isArray(value)) {
    throw new Error(`${label} must be an array.`);
  }

  return value.map((entry, index) => decoder(entry, `${label}[${index}]`));
}

export function expectString(value: unknown, label: string): string {
  if (typeof value !== "string") {
    throw new Error(`${label} must be a string.`);
  }

  return value;
}

export function expectBoolean(value: unknown, label: string): boolean {
  if (typeof value !== "boolean") {
    throw new Error(`${label} must be a boolean.`);
  }

  return value;
}

export function expectNumber(value: unknown, label: string): number {
  if (typeof value !== "number" || Number.isNaN(value)) {
    throw new Error(`${label} must be a number.`);
  }

  return value;
}

export function expectNullableNumber(
  value: unknown,
  label: string
): number | null {
  return value === null ? null : expectNumber(value, label);
}

export function expectNullableBoolean(
  value: unknown,
  label: string
): boolean | null {
  return value === null ? null : expectBoolean(value, label);
}

export function expectNullableString(
  value: unknown,
  label: string
): string | null {
  return value === null ? null : expectString(value, label);
}

export function expectOptionalString(
  value: unknown,
  label: string
): string | undefined {
  return value === undefined || value === null
    ? undefined
    : expectString(value, label);
}

export function expectOptionalBoolean(
  value: unknown,
  label: string
): boolean | undefined {
  return value === undefined || value === null
    ? undefined
    : expectBoolean(value, label);
}

export function expectOptionalNumber(
  value: unknown,
  label: string
): number | undefined {
  return value === undefined || value === null
    ? undefined
    : expectNumber(value, label);
}

export function expectStringArray(value: unknown, label: string): string[] {
  if (!Array.isArray(value)) {
    throw new Error(`${label} must be an array.`);
  }

  return value.map((entry, index) => expectString(entry, `${label}[${index}]`));
}

export function expectStringRecord(
  value: unknown,
  label: string
): Record<string, string> {
  const record = expectRecord(value, label);
  return Object.fromEntries(
    Object.entries(record).map(([key, entry]) => [
      key,
      expectString(entry, `${label}.${key}`),
    ])
  );
}
