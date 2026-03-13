#!/usr/bin/env node

const fs = require("fs");
const path = require("path");

function readStdin() {
  return new Promise((resolve, reject) => {
    let data = "";
    process.stdin.setEncoding("utf8");
    process.stdin.on("data", (chunk) => {
      data += chunk;
    });
    process.stdin.on("end", () => resolve(data));
    process.stdin.on("error", reject);
  });
}

function sanitizeSlug(value) {
  const normalized = String(value || "")
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "");
  return normalized || "marketing-delivery";
}

function buildArtifactType(value) {
  const normalized = String(value || "")
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "");
  return normalized || "delivery-bundle";
}

function buildFileName(slug, artifactType, now) {
  const yyyy = String(now.getFullYear());
  const mm = String(now.getMonth() + 1).padStart(2, "0");
  const dd = String(now.getDate()).padStart(2, "0");
  const hh = String(now.getHours()).padStart(2, "0");
  const min = String(now.getMinutes()).padStart(2, "0");
  const ss = String(now.getSeconds()).padStart(2, "0");
  return `${slug}-${artifactType}-${yyyy}-${mm}-${dd}-${hh}${min}${ss}.md`;
}

async function main() {
  const raw = await readStdin();
  const payload = JSON.parse(raw || "{}");
  const slug = sanitizeSlug(payload.slug);
  const artifactType = buildArtifactType(payload.artifactType);
  const content = String(payload.content || "").trim();

  if (!content) {
    throw new Error("content is required");
  }

  const repoRoot = path.resolve(__dirname, "..", "..");
  const outputDir = path.join(repoRoot, "docs", "examples");
  fs.mkdirSync(outputDir, { recursive: true });

  const fileName = buildFileName(slug, artifactType, new Date());
  const absolutePath = path.join(outputDir, fileName);
  fs.writeFileSync(absolutePath, `${content}\n`, "utf8");

  process.stdout.write(JSON.stringify({
    fileName,
    path: absolutePath,
  }));
}

main().catch((error) => {
  process.stderr.write(`${error.message || String(error)}\n`);
  process.exitCode = 1;
});
