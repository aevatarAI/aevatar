import fs from "node:fs";
import path from "node:path";
import process from "node:process";
import dotenv from "dotenv";

const pluginRoot = path.resolve(import.meta.dirname, "..", "..");
const dotenvPath = path.join(pluginRoot, ".env");

if (fs.existsSync(dotenvPath)) {
  dotenv.config({ path: dotenvPath });
}

export function readRequiredEnv(name) {
  const value = process.env[name];
  if (!value || !value.trim()) {
    throw new Error(`Missing required environment variable: ${name}`);
  }

  return value.trim();
}

export function readOptionalEnv(name, fallback = "") {
  const value = process.env[name];
  if (!value || !value.trim()) {
    return fallback;
  }

  return value.trim();
}

export function redactSecret(value) {
  if (!value) {
    return "";
  }

  if (value.length <= 8) {
    return "********";
  }

  return `${value.slice(0, 4)}...${value.slice(-4)}`;
}
