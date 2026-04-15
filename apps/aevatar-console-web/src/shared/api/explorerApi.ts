import {
  expectArray,
  expectRecord,
  readNullableString,
} from "@/shared/api/http/decoders";
import { readResponseError } from "@/shared/api/http/error";
import { authFetch } from "@/shared/auth/fetch";

export type ExplorerManifestEntry = {
  key: string;
  type: string;
  name?: string;
  updatedAt?: string;
};

export type ExplorerManifestResponse = {
  files: ExplorerManifestEntry[];
  version?: number;
};

function encodeExplorerKey(key: string): string {
  return key
    .split("/")
    .map((segment) => encodeURIComponent(segment))
    .join("/");
}

function decodeManifestEntry(
  value: unknown,
  label = "ExplorerManifestEntry"
): ExplorerManifestEntry {
  const record = expectRecord(value, label);
  return {
    key: readNullableString(record, "key", `${label}.key`) ?? "",
    type: readNullableString(record, "type", `${label}.type`) ?? "",
    name: readNullableString(record, "name", `${label}.name`) ?? undefined,
    updatedAt:
      readNullableString(record, ["updatedAt", "updated_at"], `${label}.updatedAt`) ??
      undefined,
  };
}

function decodeManifestResponse(
  value: unknown,
  label = "ExplorerManifestResponse"
): ExplorerManifestResponse {
  const record = expectRecord(value, label);
  const versionValue = record.version;
  return {
    files: expectArray(
      record.files ?? [],
      `${label}.files`,
      decodeManifestEntry
    ),
    version:
      typeof versionValue === "number" && Number.isFinite(versionValue)
        ? versionValue
        : undefined,
  };
}

async function requestText(input: string, init?: RequestInit): Promise<string> {
  const response = await authFetch(input, {
    credentials: "same-origin",
    ...init,
  });
  if (!response.ok) {
    throw new Error(await readResponseError(response));
  }

  return response.text();
}

async function requestJson<T>(
  input: string,
  decoder: (value: unknown, label?: string) => T,
  init?: RequestInit
): Promise<T> {
  const response = await authFetch(input, {
    credentials: "same-origin",
    ...init,
  });
  if (!response.ok) {
    throw new Error(await readResponseError(response));
  }

  return decoder(await response.json());
}

export const explorerApi = {
  getManifest(): Promise<ExplorerManifestResponse> {
    return requestJson("/api/explorer/manifest", decodeManifestResponse);
  },

  getFile(key: string): Promise<string> {
    return requestText(`/api/explorer/files/${encodeExplorerKey(key)}`);
  },

  async putFile(key: string, content: string): Promise<void> {
    await requestText(`/api/explorer/files/${encodeExplorerKey(key)}`, {
      method: "PUT",
      headers: {
        Accept: "text/plain",
        "Content-Type": "text/plain",
      },
      body: content,
    });
  },

  async deleteFile(key: string): Promise<void> {
    await requestText(`/api/explorer/files/${encodeExplorerKey(key)}`, {
      method: "DELETE",
      headers: {
        Accept: "text/plain",
      },
    });
  },
};
