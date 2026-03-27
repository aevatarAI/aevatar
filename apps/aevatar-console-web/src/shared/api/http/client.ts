import { authFetch } from "@/shared/auth/fetch";
import type { Decoder } from "../decodeUtils";
import { readResponseError } from "./error";

export type QueryValue =
  | string
  | number
  | boolean
  | Array<string | number | boolean>
  | null
  | undefined;

const JSON_HEADERS = {
  Accept: "application/json",
  "Content-Type": "application/json",
};

export function withQuery(
  path: string,
  query?: Record<string, QueryValue>
): string {
  if (!query) {
    return path;
  }

  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(query)) {
    if (value === undefined || value === null || value === "") {
      continue;
    }

    if (Array.isArray(value)) {
      for (const entry of value) {
        params.append(key, String(entry));
      }
      continue;
    }

    params.set(key, String(value));
  }

  if (params.size === 0) {
    return path;
  }

  return `${path}?${params.toString()}`;
}

export async function requestJson<T>(
  input: string,
  decoder: Decoder<T>,
  init?: RequestInit
): Promise<T> {
  const response = await authFetch(input, init);
  if (!response.ok) {
    throw new Error(await readResponseError(response));
  }

  return decoder(await response.json());
}

export function jsonBody(body: unknown): RequestInit {
  return {
    body: JSON.stringify(body),
    headers: JSON_HEADERS,
  };
}
