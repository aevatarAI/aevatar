export type Decoder<T> = (value: unknown, label?: string) => T;

export type JsonRecord = Record<string, unknown>;

function normalizeKeys(keys: string | string[]): string[] {
  return Array.isArray(keys) ? keys : [keys];
}

function findValue(record: JsonRecord, keys: string | string[]): unknown {
  for (const key of normalizeKeys(keys)) {
    if (key in record) {
      return record[key];
    }
  }

  return undefined;
}

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

export function expectNumber(value: unknown, label: string): number {
  if (typeof value !== "number" || Number.isNaN(value)) {
    throw new Error(`${label} must be a number.`);
  }

  return value;
}

export function expectBoolean(value: unknown, label: string): boolean {
  if (typeof value !== "boolean") {
    throw new Error(`${label} must be a boolean.`);
  }

  return value;
}

export function readString(
  record: JsonRecord,
  keys: string | string[],
  label: string
): string {
  return expectString(findValue(record, keys), label);
}

export function readNumber(
  record: JsonRecord,
  keys: string | string[],
  label: string
): number {
  return expectNumber(findValue(record, keys), label);
}

export function readBoolean(
  record: JsonRecord,
  keys: string | string[],
  label: string
): boolean {
  return expectBoolean(findValue(record, keys), label);
}

export function readNullableString(
  record: JsonRecord,
  keys: string | string[],
  label: string
): string | null {
  const value = findValue(record, keys);
  return value === null || value === undefined
    ? null
    : expectString(value, label);
}

export function readOptionalString(
  record: JsonRecord,
  keys: string | string[],
  label: string
): string | undefined {
  const value = findValue(record, keys);
  return value === null || value === undefined
    ? undefined
    : expectString(value, label);
}

export function readStringArray(
  record: JsonRecord,
  keys: string | string[],
  label: string
): string[] {
  const value = findValue(record, keys);
  if (!Array.isArray(value)) {
    throw new Error(`${label} must be an array.`);
  }

  return value.map((entry, index) => expectString(entry, `${label}[${index}]`));
}

export function readStringRecord(
  record: JsonRecord,
  keys: string | string[],
  label: string
): Record<string, string> {
  const value = findValue(record, keys);
  if (value === undefined || value === null) {
    return {};
  }

  const nested = expectRecord(value, label);
  return Object.fromEntries(
    Object.entries(nested).map(([key, entry]) => [
      key,
      expectString(entry, `${label}.${key}`),
    ])
  );
}

export function readOptionalRecord(
  record: JsonRecord,
  keys: string | string[],
  label: string
): JsonRecord | undefined {
  const value = findValue(record, keys);
  return value === undefined || value === null
    ? undefined
    : expectRecord(value, label);
}

export function readOptionalArray<T>(
  record: JsonRecord,
  keys: string | string[],
  label: string,
  decoder: Decoder<T>
): T[] {
  const value = findValue(record, keys);
  return value === undefined || value === null
    ? []
    : expectArray(value, label, decoder);
}

export function normalizeEnumValue(
  value: unknown,
  label: string,
  mapping: Record<string, string>
): string {
  if (typeof value === "number") {
    return mapping[String(value)] ?? String(value);
  }

  if (typeof value === "string") {
    const direct = mapping[value];
    if (direct) {
      return direct;
    }

    const normalized = value.trim().toLowerCase();
    return mapping[normalized] ?? value;
  }

  throw new Error(`${label} must be a string or number.`);
}
