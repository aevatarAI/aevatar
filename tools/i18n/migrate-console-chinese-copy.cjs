const fs = require("node:fs");
const https = require("node:https");
const path = require("node:path");
const ts = require("../../apps/aevatar-console-web/node_modules/typescript");

const repoRoot = path.resolve(__dirname, "..", "..");
const frontendRoot = path.join(repoRoot, "apps", "aevatar-console-web");
const srcRoot = path.join(frontendRoot, "src");
const localesDir = path.join(srcRoot, "locales");
const cachePath = path.join(__dirname, ".translation-cache.json");
const translationQueuePath = path.join(__dirname, ".translation-queue.json");
const enProjectMessagesPath = path.join(localesDir, "projectMessages.en-US.ts");
const zhProjectMessagesPath = path.join(localesDir, "projectMessages.zh-CN.ts");
const messagesImport = '@/shared/i18n/messages';

const hanPattern = /\p{Script=Han}/u;
const allHanPattern = /\p{Script=Han}/gu;
const identifierPattern = /^[A-Za-z_$][\w$]*$/;
const allowedTopLevelChinese = new Set();

const jsxAttributeNames = new Set([
  "aria-label",
  "cancelText",
  "copy",
  "description",
  "emptyText",
  "extra",
  "helperText",
  "label",
  "loadLabel",
  "message",
  "okText",
  "placeholder",
  "title",
  "titleHelp",
  "tooltip",
]);

const skippedLiteralParents = new Set([
  ts.SyntaxKind.ImportDeclaration,
  ts.SyntaxKind.ExportDeclaration,
  ts.SyntaxKind.ExternalModuleReference,
  ts.SyntaxKind.ImportSpecifier,
  ts.SyntaxKind.ExportSpecifier,
  ts.SyntaxKind.PropertySignature,
  ts.SyntaxKind.TypeLiteral,
  ts.SyntaxKind.InterfaceDeclaration,
  ts.SyntaxKind.TypeAliasDeclaration,
  ts.SyntaxKind.LiteralType,
]);

function readJson(filePath, fallback) {
  if (!fs.existsSync(filePath)) {
    return fallback;
  }
  return JSON.parse(fs.readFileSync(filePath, "utf8"));
}

function writeJson(filePath, value) {
  fs.writeFileSync(filePath, `${JSON.stringify(value, null, 2)}\n`);
}

function walk(dir, result = []) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (entry.name.startsWith(".umi")) {
      continue;
    }
    const fullPath = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      if (entry.name === "locales") {
        continue;
      }
      walk(fullPath, result);
      continue;
    }
    if (
      /\.(ts|tsx)$/.test(entry.name) &&
      !/\.test\.(ts|tsx)$/.test(entry.name) &&
      !/\.d\.ts$/.test(entry.name)
    ) {
      result.push(fullPath);
    }
  }
  return result.sort();
}

function getLineAndCharacter(sourceFile, node) {
  return sourceFile.getLineAndCharacterOfPosition(node.getStart(sourceFile));
}

function isInSkippedSyntax(node) {
  for (let parent = node.parent; parent; parent = parent.parent) {
    if (skippedLiteralParents.has(parent.kind)) {
      return true;
    }
    if (
      ts.isPropertyAssignment(parent) &&
      parent.name === node &&
      !parent.name.getText().startsWith("[")
    ) {
      return true;
    }
    if (ts.isBindingElement(parent) || ts.isImportClause(parent)) {
      return true;
    }
  }
  return false;
}

function isInsideTypeNode(node) {
  for (let parent = node.parent; parent; parent = parent.parent) {
    if (
      ts.isTypeNode(parent) ||
      ts.isInterfaceDeclaration(parent) ||
      ts.isTypeAliasDeclaration(parent)
    ) {
      return true;
    }
  }
  return false;
}

function isInsideFunctionLike(node) {
  for (let parent = node.parent; parent; parent = parent.parent) {
    if (ts.isFunctionLike(parent)) {
      return true;
    }
  }
  return false;
}

function isModuleLevel(node) {
  return !isInsideFunctionLike(node);
}

function isDirectiveLiteral(node) {
  const parent = node.parent;
  return (
    ts.isExpressionStatement(parent) &&
    parent.expression === node &&
    (node.text === "use strict" || node.text === "use client")
  );
}

function isJsxAttributeString(node) {
  return ts.isJsxAttribute(node.parent) && node.parent.initializer === node;
}

function isEligibleJsxAttribute(node) {
  if (!isJsxAttributeString(node)) {
    return false;
  }
  const attribute = node.parent;
  return jsxAttributeNames.has(attribute.name.getText());
}

function normalizeJsxText(value) {
  return value.replace(/\s+/g, " ").trim();
}

function shouldSkipStringLiteral(node, sourceFile, filePath) {
  if (!hanPattern.test(node.text)) {
    return true;
  }
  if (isDirectiveLiteral(node) || isInSkippedSyntax(node) || isInsideTypeNode(node)) {
    return true;
  }
  if (isModuleLevel(node) && !allowedTopLevelChinese.has(filePath)) {
    return true;
  }
  if (isJsxAttributeString(node) && !isEligibleJsxAttribute(node)) {
    return true;
  }
  return false;
}

function hasExistingMessagesImport(sourceText) {
  return (
    sourceText.includes(`from "${messagesImport}"`) ||
    sourceText.includes(`from '${messagesImport}'`)
  );
}

function findLastImportEnd(sourceFile) {
  let end = 0;
  for (const statement of sourceFile.statements) {
    if (ts.isImportDeclaration(statement)) {
      end = statement.end;
    }
  }
  return end;
}

function filePrefix(filePath) {
  const relativePath = path
    .relative(srcRoot, filePath)
    .replace(/\.(tsx|ts)$/, "")
    .split(path.sep)
    .filter((segment) => segment !== "components" && segment !== "runtime")
    .join(".");
  return relativePath
    .replace(/[^A-Za-z0-9]+/g, ".")
    .replace(/^\.+|\.+$/g, "")
    .toLowerCase();
}

function extractWords(value) {
  const ascii = value
    .replace(/\{[^}]+\}/g, " ")
    .replace(/[^A-Za-z0-9]+/g, " ")
    .trim()
    .split(/\s+/)
    .filter((part) => part.length > 1)
    .slice(0, 4)
    .join(".");
  return ascii.toLowerCase();
}

function collectExistingProjectMessages() {
  const existing = new Set();
  for (const catalogPath of [enProjectMessagesPath, zhProjectMessagesPath]) {
    if (!fs.existsSync(catalogPath)) {
      continue;
    }
    const text = fs.readFileSync(catalogPath, "utf8");
    for (const match of text.matchAll(/['"]([^'"]+)['"]\s*:/g)) {
      existing.add(match[1]);
    }
  }
  return existing;
}

function createKeyAllocator(existingKeys) {
  const counts = new Map();
  return (filePath, english) => {
    const prefix = filePrefix(filePath) || "project";
    const hint = extractWords(english);
    const base = hint ? `${prefix}.${hint}` : `${prefix}.copy`;
    const nextIndex = (counts.get(base) ?? 0) + 1;
    counts.set(base, nextIndex);
    let candidate = nextIndex === 1 ? base : `${base}.${nextIndex}`;
    let suffix = nextIndex;
    while (existingKeys.has(candidate)) {
      suffix += 1;
      candidate = `${base}.${suffix}`;
    }
    existingKeys.add(candidate);
    return candidate;
  };
}

function isKnownMessageId(value) {
  return /^[a-z0-9]+(?:\.[a-z0-9]+)*$/.test(value);
}

function toSentenceCase(value) {
  const trimmed = value.trim();
  if (!trimmed) {
    return trimmed;
  }
  return `${trimmed.charAt(0).toUpperCase()}${trimmed.slice(1)}`;
}

function parseCatalogFile(filePath) {
  if (!fs.existsSync(filePath)) {
    return {};
  }
  const text = fs.readFileSync(filePath, "utf8");
  const sourceFile = ts.createSourceFile(filePath, text, ts.ScriptTarget.Latest, true);
  let messages = {};
  function visit(node) {
    if (ts.isVariableDeclaration(node) && node.initializer && ts.isObjectLiteralExpression(node.initializer)) {
      const name = node.name.getText(sourceFile);
      if (name === "projectMessages") {
        for (const property of node.initializer.properties) {
          if (!ts.isPropertyAssignment(property)) {
            continue;
          }
          const keyNode = property.name;
          const valueNode = property.initializer;
          const key = ts.isStringLiteralLike(keyNode) ? keyNode.text : keyNode.getText(sourceFile);
          if (ts.isStringLiteralLike(valueNode) || ts.isNoSubstitutionTemplateLiteral(valueNode)) {
            messages[key] = valueNode.text;
          }
        }
      }
    }
    ts.forEachChild(node, visit);
  }
  visit(sourceFile);
  return messages;
}

function writeCatalog(filePath, messages) {
  const entries = Object.entries(messages).sort(([a], [b]) => a.localeCompare(b));
  const body = entries
    .map(([key, value]) => `  ${JSON.stringify(key)}: ${JSON.stringify(value)},`)
    .join("\n");
  const content = `const projectMessages = {\n${body}${body ? "\n" : ""}};\n\nexport default projectMessages;\n`;
  fs.writeFileSync(filePath, content);
}

function translateOne(text, cache) {
  if (cache[text]) {
    return Promise.resolve(cache[text]);
  }
  const url =
    "https://translate.googleapis.com/translate_a/single?client=gtx&sl=zh-CN&tl=en&dt=t&q=" +
    encodeURIComponent(text);
  return new Promise((resolve, reject) => {
    https
      .get(url, (response) => {
        let data = "";
        response.setEncoding("utf8");
        response.on("data", (chunk) => {
          data += chunk;
        });
        response.on("end", () => {
          if (response.statusCode < 200 || response.statusCode >= 300) {
            reject(new Error(`Translate request failed: ${response.statusCode}`));
            return;
          }
          const parsed = JSON.parse(data);
          const translated = parsed?.[0]
            ?.map((item) => item?.[0] || "")
            .join("")
            .trim();
          if (!translated) {
            reject(new Error(`Translate response did not include text for: ${text}`));
            return;
          }
          cache[text] = polishTranslation(translated);
          resolve(cache[text]);
        });
      })
      .on("error", reject);
  });
}

function polishTranslation(value) {
  return value
    .replace(/\bTeam\b/g, "team")
    .replace(/\bWorkflow\b/g, "workflow")
    .replace(/\bRollout\b/g, "rollout")
    .replace(/\bDeployment\b/g, "deployment")
    .replace(/\bServing\b/g, "serving")
    .replace(/\bActor\b/g, "actor")
    .replace(/\bApi\b/g, "API")
    .replace(/\bSdk\b/g, "SDK")
    .replace(/\bId\b/g, "ID")
    .replace(/\bAi\b/g, "AI")
    .replace(/\bC #\b/g, "C#")
    .replace(/\bProto\b/g, "Proto")
    .replace(/\s+([,.!?;:])/g, "$1")
    .replace(/（/g, "(")
    .replace(/）/g, ")")
    .replace(/。$/g, ".")
    .replace(/，/g, ", ")
    .replace(/：/g, ": ")
    .replace(/；/g, "; ")
    .replace(/“/g, '"')
    .replace(/”/g, '"')
    .replace(/‘/g, "'")
    .replace(/’/g, "'")
    .replace(/\s+/g, " ")
    .trim();
}

function buildDefaultMessage(english, placeholders) {
  let message = english;
  placeholders.forEach((placeholder, index) => {
    const translatedPlaceholderPattern = new RegExp(`\\{\\s*value\\s*${index + 1}\\s*\\}`, "gi");
    message = message.replace(translatedPlaceholderPattern, `{${placeholder}}`);
  });
  if (message === english) {
    placeholders.forEach((placeholder, index) => {
      message = message.replace(new RegExp(`\\{${index + 1}\\}`, "g"), `{${placeholder}}`);
    });
  }
  return message;
}

function createMessageText(value, placeholders = []) {
  let text = value;
  placeholders.forEach((placeholder, index) => {
    text = text.replaceAll(`__VALUE_${index + 1}__`, `{${placeholder}}`);
  });
  return text;
}

function literalText(node) {
  if (ts.isStringLiteralLike(node) || ts.isNoSubstitutionTemplateLiteral(node)) {
    return node.text;
  }
  return null;
}

function collectTemplateParts(node, sourceFile) {
  const parts = [];
  const placeholders = [];
  parts.push(node.head.text);
  node.templateSpans.forEach((span, index) => {
    const placeholder = `value${index + 1}`;
    placeholders.push(placeholder);
    parts.push(`__VALUE_${index + 1}__`);
    parts.push(span.literal.text);
  });
  return {
    placeholders,
    text: parts.join(""),
  };
}

function createReplacementExpression(key, defaultMessage, placeholders) {
  const args = [JSON.stringify(key), JSON.stringify(defaultMessage)];
  if (placeholders.length > 0) {
    args.push(
      `{ ${placeholders
        .map((placeholder, index) => `${placeholder}: __EXPR_${index + 1}__`)
        .join(", ")} }`,
    );
  }
  return `t(${args.join(", ")})`;
}

function replaceExpressionPlaceholders(expression, expressions) {
  let next = expression;
  expressions.forEach((value, index) => {
    next = next.replace(`__EXPR_${index + 1}__`, value);
  });
  return next;
}

function createMessagePlan({ filePath, keyAllocator, sourceFile, text, expressions = [] }) {
  return {
    defaultMessage: null,
    expressions,
    filePath,
    key: null,
    placeholders: expressions.map((_, index) => `value${index + 1}`),
    text,
  };
}

function collectPlansForFile(filePath, sourceFile) {
  const plans = [];
  function visit(node) {
    if (ts.isJsxText(node)) {
      const normalized = normalizeJsxText(node.getText(sourceFile));
      if (hanPattern.test(normalized)) {
        plans.push({
          expressions: [],
          node,
          text: normalized,
          type: "jsxText",
        });
      }
      return;
    }

    if (ts.isStringLiteralLike(node) && !shouldSkipStringLiteral(node, sourceFile, filePath)) {
      plans.push({
        expressions: [],
        node,
        text: node.text,
        type: isEligibleJsxAttribute(node) ? "jsxAttribute" : "string",
      });
      return;
    }

    if (ts.isNoSubstitutionTemplateLiteral(node) && hanPattern.test(node.text)) {
      if (!isInSkippedSyntax(node) && !isInsideTypeNode(node) && (!isModuleLevel(node) || allowedTopLevelChinese.has(filePath))) {
        plans.push({
          expressions: [],
          node,
          text: node.text,
          type: "templateNoSubstitution",
        });
      }
      return;
    }

    if (ts.isTemplateExpression(node) && hanPattern.test(node.getText(sourceFile))) {
      if (!isInSkippedSyntax(node) && !isInsideTypeNode(node) && !isModuleLevel(node)) {
        const collected = collectTemplateParts(node, sourceFile);
        plans.push({
          expressions: node.templateSpans.map((span) => span.expression.getText(sourceFile)),
          node,
          placeholders: collected.placeholders,
          text: createMessageText(collected.text, collected.placeholders),
          type: "template",
        });
      }
      return;
    }

    ts.forEachChild(node, visit);
  }
  visit(sourceFile);
  return plans;
}

function collectExistingTCallsForFile(filePath, sourceFile) {
  const plans = [];
  function visit(node) {
    if (
      ts.isCallExpression(node) &&
      ts.isIdentifier(node.expression) &&
      node.expression.text === "t" &&
      node.arguments.length >= 2
    ) {
      const idNode = node.arguments[0];
      const defaultNode = node.arguments[1];
      if (
        ts.isStringLiteralLike(idNode) &&
        isKnownMessageId(idNode.text) &&
        (ts.isStringLiteralLike(defaultNode) || ts.isNoSubstitutionTemplateLiteral(defaultNode))
      ) {
        const defaultMessage = toSentenceCase(defaultNode.text);
        plans.push({
          defaultMessage,
          expressions: [],
          filePath,
          key: idNode.text,
          placeholders: [],
          text: null,
        });
      }
    }
    ts.forEachChild(node, visit);
  }
  visit(sourceFile);
  return plans;
}

function collectKnownChineseForExistingCalls(sourceFile) {
  const values = [];
  function visit(node) {
    if (
      ts.isPropertyAssignment(node) &&
      ts.isIdentifier(node.name) &&
      node.name.text === "text" &&
      node.initializer &&
      ts.isStringLiteralLike(node.initializer) &&
      hanPattern.test(node.initializer.text)
    ) {
      values.push(node.initializer.text);
    }
    ts.forEachChild(node, visit);
  }
  visit(sourceFile);
  return values;
}

function applyPlans(sourceText, sourceFile, plans) {
  let nextText = sourceText;
  const edits = [];
  let changed = false;
  for (const plan of plans) {
    const start = plan.node.getStart(sourceFile);
    const end = plan.node.end;
    const replacementExpression = replaceExpressionPlaceholders(
      createReplacementExpression(plan.key, plan.defaultMessage, plan.placeholders || []),
      plan.expressions || [],
    );

    if (plan.type === "jsxText") {
      edits.push({ start, end, text: `{${replacementExpression}}` });
    } else if (plan.type === "jsxAttribute") {
      edits.push({ start, end, text: `{${replacementExpression}}` });
    } else {
      edits.push({ start, end, text: replacementExpression });
    }
    changed = true;
  }

  for (const edit of edits.sort((a, b) => b.start - a.start)) {
    nextText = nextText.slice(0, edit.start) + edit.text + nextText.slice(edit.end);
  }

  if (changed && !hasExistingMessagesImport(nextText)) {
    const importEnd = findLastImportEnd(sourceFile);
    const importLine = `\nimport { t } from "${messagesImport}";`;
    if (importEnd > 0) {
      nextText = nextText.slice(0, importEnd) + importLine + nextText.slice(importEnd);
    } else {
      nextText = `${importLine.trimStart()}\n${nextText}`;
    }
  }

  return nextText;
}

async function main() {
  const cache = readJson(cachePath, {});
  const enMessages = parseCatalogFile(enProjectMessagesPath);
  const zhMessages = parseCatalogFile(zhProjectMessagesPath);
  const queuedTranslations = readJson(translationQueuePath, {});
  const existingKeys = collectExistingProjectMessages();
  const allocateKey = createKeyAllocator(existingKeys);
  const files = walk(srcRoot);
  let totalPlans = 0;
  let totalExistingCalls = 0;
  let changedFiles = 0;
  const skipped = [];

  for (const filePath of files) {
    const sourceText = fs.readFileSync(filePath, "utf8");
    const sourceFile = ts.createSourceFile(
      filePath,
      sourceText,
      ts.ScriptTarget.Latest,
      true,
      filePath.endsWith(".tsx") ? ts.ScriptKind.TSX : ts.ScriptKind.TS,
    );
    const existingTCalls = collectExistingTCallsForFile(filePath, sourceFile);
    const knownChinese = collectKnownChineseForExistingCalls(sourceFile);
    for (const plan of existingTCalls) {
      enMessages[plan.key] = plan.defaultMessage;
      if (!zhMessages[plan.key]) {
        zhMessages[plan.key] = knownChinese.shift() || plan.defaultMessage;
      }
      totalExistingCalls += 1;
    }

    if (!hanPattern.test(sourceText)) {
      continue;
    }
    const plans = collectPlansForFile(filePath, sourceFile);

    if (plans.length === 0) {
      if (!allowedTopLevelChinese.has(filePath)) {
        skipped.push(path.relative(repoRoot, filePath));
      }
      continue;
    }

    for (const plan of plans) {
      const translated = queuedTranslations[plan.text] || await translateOne(plan.text, cache);
      const defaultMessage = buildDefaultMessage(translated, plan.placeholders || []);
      plan.defaultMessage = defaultMessage;
      plan.key = allocateKey(filePath, defaultMessage);
      enMessages[plan.key] = defaultMessage;
      zhMessages[plan.key] = plan.text;
      totalPlans += 1;
    }

    fs.writeFileSync(filePath, applyPlans(sourceText, sourceFile, plans));
    changedFiles += 1;
  }

  writeCatalog(enProjectMessagesPath, enMessages);
  writeCatalog(zhProjectMessagesPath, zhMessages);
  writeJson(cachePath, cache);
  console.log(`Cataloged ${totalExistingCalls} existing t() calls.`);
  console.log(`Migrated ${totalPlans} message nodes across ${changedFiles} files.`);
  if (skipped.length > 0) {
    console.log("Skipped files with remaining Chinese that need review:");
    skipped.forEach((file) => console.log(`- ${file}`));
  }
}

main().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
