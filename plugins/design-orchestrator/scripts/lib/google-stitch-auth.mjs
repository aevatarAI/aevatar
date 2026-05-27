import { spawnSync } from "node:child_process";
import process from "node:process";
import { readOptionalEnv } from "./env.mjs";

export const stitchMcpUrl = "https://stitch.googleapis.com/mcp";

export function buildGcloudEnv() {
  const cloudSdkPython = readOptionalEnv("CLOUDSDK_PYTHON");

  return cloudSdkPython
    ? {
      ...process.env,
      CLOUDSDK_PYTHON: cloudSdkPython
    }
    : process.env;
}

export function resolveGoogleCloudAccount() {
  return readOptionalEnv("GOOGLE_CLOUD_ACCOUNT");
}

export function resolveGoogleCloudProject() {
  return readOptionalEnv("GOOGLE_CLOUD_PROJECT");
}

export function readGoogleCloudAccessToken() {
  const explicitToken = readOptionalEnv("STITCH_ACCESS_TOKEN");

  if (explicitToken) {
    return explicitToken;
  }

  const account = resolveGoogleCloudAccount();
  const args = ["auth", "print-access-token"];

  if (account) {
    args.push(`--account=${account}`);
  }

  const result = spawnSync(readOptionalEnv("GCLOUD_BIN", "gcloud"), args, {
    env: buildGcloudEnv(),
    encoding: "utf8"
  });

  if (result.status !== 0) {
    const stderr = result.stderr?.trim() || "Unable to get Google Cloud access token.";
    throw new Error(stderr);
  }

  const token = result.stdout.trim();

  if (!token) {
    throw new Error("Google Cloud access token command returned an empty token.");
  }

  return token;
}
