const fs = require("node:fs");
const https = require("node:https");
const path = require("node:path");
const ts = require("../../apps/aevatar-console-web/node_modules/typescript");

const repoRoot = path.resolve(__dirname, "..", "..");
const frontendRoot = path.join(repoRoot, "apps", "aevatar-console-web");
const srcRoot = path.join(frontendRoot, "src");
const localesDir = path.join(srcRoot, "locales");
const enProjectMessagesPath = path.join(localesDir, "projectMessages.en-US.ts");
const zhProjectMessagesPath = path.join(localesDir, "projectMessages.zh-CN.ts");
const cachePath = path.join(__dirname, ".translation-cache-en-zh.json");
const messagesImport = "@/shared/i18n/messages";

const uiPropNames = new Set([
  "aria-label",
  "cancelText",
  "caption",
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
  "subtitle",
  "title",
  "titleHelp",
  "tooltip",
]);
const englishCopyPattern = /[A-Za-z]/;
const chineseTermReplacements = [
  [/跑步/g, "运行"],
  [/负载夹具/g, "加载示例数据"],
  [/夹具/g, "示例数据"],
  [/奥尔良谷物/g, "Orleans Grain"],
  [/输入网址/g, "Type URL"],
  [/种子夹具/g, "预置样例"],
  [/短暂的/g, "临时态"],
  [/入会成员/g, "入口成员"],
  [/迅速的/g, "Prompt"],
  [/国家版本/g, "状态版本"],
  [/国家版/g, "状态版本"],
  [/原始原木/g, "原始日志"],
  [/手柄阻挡器/g, "处理阻塞项"],
  [/活动码头/g, "事件面板"],
  [/促销理由/g, "发布理由"],
  [/促销/g, "发布提案"],
  [/提出进化论/g, "提交演进提案"],
  [/进化论/g, "演进"],
  [/台阶式/g, "步骤类型"],
  [/证实/g, "校验"],
  [/包裹/g, "包"],
  [/帆布/g, "画布"],
  [/转速/g, "修订"],
  [/节省/g, "保存"],
  [/榜样/g, "角色模型"],
  [/GAAgent/g, "GAgent"],
  [/公开赛/g, "打开运行"],
  [/味精/g, "msg"],
  [/居住/g, "实时"],
  [/法学硕士/g, "LLM"],
  [/图书馆/g, "库"],
  [/儿童步骤/g, "子步骤"],
  [/装车/g, "加载"],
  [/退休了/g, "已停用"],
  [/被拯救/g, "已保存"],
  [/请求类型网址/g, "请求 Type URL"],
  [/响应类型 url/g, "响应 Type URL"],
  [/选秀运行/g, "草稿运行"],
  [/工作室编辑/g, "Studio 编辑"],
  [/录音室/g, "Studio"],
  [/高级套餐/g, "高级包"],
  [/干净的/g, "无错误"],
  [/条目行为/g, "入口行为"],
  [/进入行为/g, "入口行为"],
  [/剧本推广/g, "脚本发布"],
  [/模板·种子/g, "模板 · 预置"],
  [/ActorID/g, "Actor ID"],
  [/脚本ID/g, "脚本 ID"],
  [/运行ID/g, "运行 ID"],
  [/最后活动/g, "最后事件"],
  [/全球工具/g, "全局工具"],
  [/工作空间/g, "工作区"],
  [/草案/g, "草稿"],
  [/参与者快照/g, "Actor 快照"],
  [/参与者 ID/g, "Actor ID"],
  [/参与者/g, "Actor"],
  [/有效负载/g, "Payload"],
  [/负载类型 URL/g, "Payload Type URL"],
  [/负载文本/g, "Payload 文本"],
  [/原始负载/g, "Raw Payload"],
  [/执行负载/g, "执行 Payload"],
  [/运行时间/g, "运行时"],
  [/提供商/g, "Provider"],
  [/提供者/g, "Provider"],
  [/身份验证/g, "鉴权"],
  [/修订版/g, "修订"],
  [/工作室/g, "Studio"],
  [/人门/g, "人工 Gate"],
  [/人性化回放/g, "人工回放"],
  [/高级原体/g, "高级原始请求体"],
  [/先进的原始方法/g, "高级原始方法"],
  [/行动背景/g, "操作上下文"],
  [/下一步行动/g, "下一步操作"],
  [/需要采取行动/g, "需要操作"],
  [/项目范围/g, "项目 Scope"],
  [/团队范围/g, "团队 Scope"],
  [/当前范围/g, "当前 Scope"],
  [/部署范围/g, "部署 Scope"],
  [/治理范围/g, "治理 Scope"],
  [/服务范围/g, "服务 Scope"],
  [/适用范围/g, "应用 Scope"],
  [/运行时适合/g, "运行时匹配度"],
  [/运行时间限制/g, "运行时限制"],
  [/步骤总结/g, "步骤摘要"],
  [/角色总结/g, "角色摘要"],
  [/定义总结/g, "定义摘要"],
  [/状态总结/g, "状态摘要"],
  [/步枝/g, "步骤分支"],
  [/长寿命状态/g, "长期状态"],
  [/提示和有效载荷/g, "Prompt 和 Payload"],
  [/有效载荷/g, "Payload"],
  [/执行提示/g, "执行 Prompt"],
  [/暂停提示/g, "暂停 Prompt"],
  [/干预提示/g, "干预 Prompt"],
  [/初始提示/g, "初始 Prompt"],
  [/系统提示/g, "System Prompt"],
  [/直接提示/g, "直接 Prompt"],
  [/草稿提示/g, "草稿 Prompt"],
  [/需要提示/g, "需要 Prompt"],
  [/发送此提示/g, "发送这个 Prompt"],
  [/发送上面的提示/g, "发送上面的 Prompt"],
  [/提示词/g, "Prompt"],
  [/上证所/g, "SSE"],
  [/思维/g, "思考"],
  [/空谈/g, "空对话"],
  [/残疾人/g, "已禁用"],
  [/技术领域/g, "技术字段"],
  [/烟雾测试/g, "冒烟测试"],
  [/流媒体/g, "流式输出"],
  [/安慰/g, "控制台"],
  [/团队流程/g, "团队 Workflow"],
  [/测试问题/g, "测试 Prompt"],
  [/边桌/g, "边列表"],
  [/目标 endpoint/g, "目标 Endpoint"],
  [/endpoint catalog/g, "Endpoint Catalog"],
  [/endpoint 暴露/g, "Endpoint 暴露"],
  [/作用域内绑定/g, "Scope 绑定"],
  [/scoped endpoint/g, "scoped Endpoint"],
  [/draft-run endpoint/g, "draft-run Endpoint"],
  [/最近scoped run/g, "最近的 scoped run"],
  [/prompt 或载荷/g, "Prompt 或 Payload"],
  [/脚本演练事实/g, "脚本 dry run 事实"],
  [/分类意图，检测语言/g, "classify_intent, detect_language"],
  [/GAgent mode/g, "GAgent 模式"],
  [/service \/ Endpoint/g, "Service / Endpoint"],
  [/transcript/g, "对话记录"],
  [/source editor/g, "源代码编辑器"],
  [/script draft/g, "脚本草稿"],
  [/typed source/g, "类型化源代码"],
  [/dry-run 迭代/g, "试运行迭代"],
  [/脚本 dry run/g, "脚本试运行"],
  [/选择 typed GAgent/g, "选择类型化 GAgent"],
  [/当前 step type/g, "当前步骤类型"],
  [/raw JSON/g, "原始 JSON"],
  [/简历被接受/g, "继续运行请求已接受"],
  [/当前拦截器/g, "当前阻塞项"],
  [/拓扑文摘/g, "拓扑摘要"],
  [/破坏性的/g, "高风险操作"],
  [/全部的/g, "总计"],
  [/原型/g, "Proto"],
];

function readJson(filePath, fallback) {
  if (!fs.existsSync(filePath)) {
    return fallback;
  }
  return JSON.parse(fs.readFileSync(filePath, "utf8"));
}

function writeJson(filePath, value) {
  fs.writeFileSync(filePath, `${JSON.stringify(value, null, 2)}\n`);
}

function walk(directory, result = []) {
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    if (entry.name.startsWith(".umi")) {
      continue;
    }
    const fullPath = path.join(directory, entry.name);
    if (entry.isDirectory()) {
      if (entry.name === "locales") {
        continue;
      }
      walk(fullPath, result);
      continue;
    }
    if (
      /\.(ts|tsx)$/.test(entry.name) &&
      !/\.d\.ts$/.test(entry.name) &&
      !/\.test\.(ts|tsx)$/.test(entry.name)
    ) {
      result.push(fullPath);
    }
  }
  return result.sort();
}

function parseCatalogFile(filePath) {
  if (!fs.existsSync(filePath)) {
    return {};
  }
  const text = fs.readFileSync(filePath, "utf8");
  const sourceFile = ts.createSourceFile(filePath, text, ts.ScriptTarget.Latest, true);
  const messages = {};
  function visit(node) {
    if (
      ts.isVariableDeclaration(node) &&
      node.name.getText(sourceFile) === "projectMessages" &&
      node.initializer &&
      ts.isObjectLiteralExpression(node.initializer)
    ) {
      for (const property of node.initializer.properties) {
        if (
          ts.isPropertyAssignment(property) &&
          ts.isStringLiteralLike(property.name) &&
          ts.isStringLiteralLike(property.initializer)
        ) {
          messages[property.name.text] = property.initializer.text;
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
  fs.writeFileSync(
    filePath,
    `const projectMessages = {\n${body}${body ? "\n" : ""}};\n\nexport default projectMessages;\n`,
  );
}

function hasMessagesImport(sourceText) {
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

function isInsideRuntimeScope(node) {
  for (let parent = node.parent; parent; parent = parent.parent) {
    if (
      ts.isFunctionLike(parent) ||
      ts.isJsxElement(parent) ||
      ts.isJsxFragment(parent) ||
      ts.isJsxSelfClosingElement(parent)
    ) {
      return true;
    }
  }
  return false;
}

function isInsideExistingI18nCall(node) {
  for (let parent = node.parent; parent; parent = parent.parent) {
    if (
      ts.isCallExpression(parent) &&
      ts.isIdentifier(parent.expression) &&
      (parent.expression.text === "t" ||
        parent.expression.text === "formatConsoleMessage")
    ) {
      return true;
    }
    if (
      ts.isCallExpression(parent) &&
      ts.isPropertyAccessExpression(parent.expression) &&
      parent.expression.name.text === "formatMessage"
    ) {
      return true;
    }
  }
  return false;
}

function isImportOrExportLiteral(node) {
  for (let parent = node.parent; parent; parent = parent.parent) {
    if (
      ts.isExportDeclaration(parent) ||
      ts.isExternalModuleReference(parent) ||
      ts.isImportDeclaration(parent)
    ) {
      return true;
    }
  }
  return false;
}

function isObjectKey(node) {
  return (
    ts.isPropertyAssignment(node.parent) &&
    node.parent.name === node &&
    !node.parent.name.getText().startsWith("[")
  );
}

function isLikelyUiCopy(value, type) {
  const text = value.replace(/\s+/g, " ").trim();
  if (!englishCopyPattern.test(text)) {
    return false;
  }
  if (text.length < 3 || text.length > 220) {
    return false;
  }
  if (/^\$?[a-z0-9_./:#?=&%{}-]+$/i.test(text) && !text.includes(" ")) {
    if (type === "jsxText" && /^[A-Za-z][A-Za-z-]{2,}$/.test(text)) {
      return true;
    }
    if (/^(C#|CSS|HTML|JSON|TS|TSX|JS|JSX|SSE|URL|URI|API|SDK|ID|LLM)$/i.test(text)) {
      return false;
    }
    return false;
  }
  if (/^[a-z0-9]+(\.[a-z0-9_-]+)+$/i.test(text)) {
    return false;
  }
  if (/^#[0-9a-f]{3,8}$/i.test(text)) {
    return false;
  }
  if (/^type\.googleapis\.com\//i.test(text)) {
    return false;
  }
  return true;
}

function filePrefix(filePath) {
  return path
    .relative(srcRoot, filePath)
    .replace(/\.(tsx|ts)$/, "")
    .split(path.sep)
    .filter((segment) => segment !== "components" && segment !== "runtime")
    .join(".")
    .replace(/[^A-Za-z0-9]+/g, ".")
    .replace(/^\.+|\.+$/g, "")
    .toLowerCase();
}

function extractWords(value) {
  return value
    .replace(/\{[^}]+\}/g, " ")
    .replace(/[^A-Za-z0-9]+/g, " ")
    .trim()
    .split(/\s+/)
    .filter((word) => word.length > 1)
    .slice(0, 5)
    .join(".")
    .toLowerCase();
}

function createKeyAllocator(existingKeys) {
  const counts = new Map();
  return (filePath, defaultMessage) => {
    const prefix = filePrefix(filePath) || "project";
    const hint = extractWords(defaultMessage);
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

function collectExistingKeys(...catalogs) {
  const keys = new Set();
  for (const catalog of catalogs) {
    for (const key of Object.keys(catalog)) {
      keys.add(key);
    }
  }
  return keys;
}

function collectPlansForFile(filePath, sourceFile) {
  const plans = [];
  function addPlan(node, text, type) {
    const defaultMessage = text.replace(/\s+/g, " ").trim();
    if (!isLikelyUiCopy(defaultMessage, type)) {
      return;
    }
    if (!isInsideRuntimeScope(node)) {
      return;
    }
    plans.push({ defaultMessage, node, type });
  }

  function visit(node) {
    if (ts.isJsxText(node)) {
      if (!isInsideExistingI18nCall(node)) {
        addPlan(node, node.getText(sourceFile), "jsxText");
      }
      return;
    }

    if (
      (ts.isStringLiteralLike(node) || ts.isNoSubstitutionTemplateLiteral(node)) &&
      !isInsideExistingI18nCall(node) &&
      !isImportOrExportLiteral(node) &&
      !isObjectKey(node)
    ) {
      const parent = node.parent;
      if (
        ts.isJsxAttribute(parent) &&
        parent.initializer === node &&
        uiPropNames.has(parent.name.getText())
      ) {
        addPlan(node, node.text, "jsxAttribute");
        return;
      }

      if (
        ts.isPropertyAssignment(parent) &&
        parent.initializer === node
      ) {
        const name = parent.name.getText(sourceFile).replace(/^['"]|['"]$/g, "");
        if (uiPropNames.has(name)) {
          addPlan(node, node.text, "string");
          return;
        }
      }
    }

    ts.forEachChild(node, visit);
  }

  visit(sourceFile);
  return plans;
}

function applyPlans(sourceText, sourceFile, plans) {
  let nextText = sourceText;
  const edits = plans.map((plan) => {
    const expression = `t(${JSON.stringify(plan.key)}, ${JSON.stringify(
      plan.defaultMessage,
    )})`;
    if (plan.type === "jsxText") {
      return {
        start: plan.node.getStart(sourceFile),
        end: plan.node.end,
        text: `{${expression}}`,
      };
    }
    if (plan.type === "jsxAttribute") {
      return {
        start: plan.node.getStart(sourceFile),
        end: plan.node.end,
        text: `{${expression}}`,
      };
    }
    return {
      start: plan.node.getStart(sourceFile),
      end: plan.node.end,
      text: expression,
    };
  });

  for (const edit of edits.sort((a, b) => b.start - a.start)) {
    nextText = nextText.slice(0, edit.start) + edit.text + nextText.slice(edit.end);
  }

  if (edits.length > 0 && !hasMessagesImport(nextText)) {
    const importEnd = findLastImportEnd(sourceFile);
    const importLine = `\nimport { t } from "${messagesImport}";`;
    nextText =
      importEnd > 0
        ? nextText.slice(0, importEnd) + importLine + nextText.slice(importEnd)
        : `${importLine.trimStart()}\n${nextText}`;
  }

  return nextText;
}

function translateOne(text, cache) {
  if (cache[text]) {
    return Promise.resolve(cache[text]);
  }
  const url =
    "https://translate.googleapis.com/translate_a/single?client=gtx&sl=en&tl=zh-CN&dt=t&q=" +
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
            ?.map((part) => part?.[0] || "")
            .join("")
            .trim();
          if (!translated) {
            reject(new Error(`Missing translation for: ${text}`));
            return;
          }
          cache[text] = polishChinese(translated);
          resolve(cache[text]);
        });
      })
      .on("error", reject);
  });
}

function polishChinese(value) {
  let polished = value
    .replace(/会员/g, "成员")
    .replace(/工作流程/g, "Workflow")
    .replace(/工作流/g, "Workflow")
    .replace(/工作室/g, "Studio")
    .replace(/演员/g, "Actor")
    .replace(/代理/g, "Agent")
    .replace(/盖根特/g, "GAgent")
    .replace(/加根特/g, "GAgent")
    .replace(/nyxid/gi, "NyxID")
    .replace(/aevatar/gi, "Aevatar")
    .replace(/studio/gi, "Studio")
    .replace(/gagent/gi, "GAgent")
    .replace(/actor/gi, "Actor")
    .replace(/workflow/gi, "Workflow")
    .replace(/protobuf/gi, "Protobuf")
    .replace(/stringvalue/g, "StringValue")
    .replace(/appscriptcommand/g, "AppScriptCommand")
    .replace(/api/g, "API")
    .replace(/sdk/g, "SDK")
    .replace(/id/g, "ID")
    .replace(/\s+/g, " ")
    .trim();

  for (const [pattern, replacement] of chineseTermReplacements) {
    polished = polished.replace(pattern, replacement);
  }

  return normalizeChineseTechnicalSpacing(polished);
}

function normalizeChineseTechnicalSpacing(value) {
  const terms = [
    "Aevatar",
    "Actor",
    "Agent",
    "API",
    "Base64",
    "Chat",
    "Config",
    "Endpoint",
    "Explorer",
    "GAgent",
    "HTTP",
    "ID",
    "JSON",
    "MCP",
    "NyxID",
    "Ornn",
    "Payload",
    "Primitive",
    "Prompt",
    "Provider",
    "Run",
    "Runs",
    "Scope",
    "SDK",
    "Signal",
    "SSE",
    "Studio",
    "Team",
    "Type URL",
    "URL",
    "Workflow",
    "YAML",
  ];
  const termPattern = terms
    .map((term) => term.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"))
    .join("|");
  const left = new RegExp(`([\\u4e00-\\u9fff])(${termPattern})`, "g");
  const right = new RegExp(`(${termPattern})([\\u4e00-\\u9fff])`, "g");
  return value
    .replace(left, "$1 $2")
    .replace(right, "$1 $2")
    .replace(/\s+([，。：；！？、])/g, "$1")
    .replace(/（\s+/g, "（")
    .replace(/\s+）/g, "）")
    .trim();
}

async function translateAll(texts, cache) {
  const results = new Map();
  const queue = [...new Set(texts)];
  const workers = Array.from({ length: 8 }, async () => {
    while (queue.length > 0) {
      const text = queue.shift();
      if (!text) {
        continue;
      }
      results.set(text, await translateOne(text, cache));
      if (results.size % 50 === 0) {
        console.log(`translated ${results.size}`);
        writeJson(cachePath, cache);
      }
    }
  });
  await Promise.all(workers);
  writeJson(cachePath, cache);
  return results;
}

async function main() {
  const enMessages = parseCatalogFile(enProjectMessagesPath);
  const zhMessages = parseCatalogFile(zhProjectMessagesPath);
  const cache = readJson(cachePath, {});
  const existingKeys = collectExistingKeys(enMessages, zhMessages);
  const allocateKey = createKeyAllocator(existingKeys);
  const filePlans = [];

  for (const filePath of walk(srcRoot)) {
    const sourceText = fs.readFileSync(filePath, "utf8");
    const sourceFile = ts.createSourceFile(
      filePath,
      sourceText,
      ts.ScriptTarget.Latest,
      true,
      filePath.endsWith(".tsx") ? ts.ScriptKind.TSX : ts.ScriptKind.TS,
    );
    const plans = collectPlansForFile(filePath, sourceFile);
    if (plans.length > 0) {
      filePlans.push({ filePath, plans, sourceFile, sourceText });
    }
  }

  const translations = await translateAll(
    filePlans.flatMap(({ plans }) => plans.map((plan) => plan.defaultMessage)),
    cache,
  );
  let migrated = 0;
  for (const filePlan of filePlans) {
    for (const plan of filePlan.plans) {
      plan.key = allocateKey(filePlan.filePath, plan.defaultMessage);
      enMessages[plan.key] = plan.defaultMessage;
      zhMessages[plan.key] = translations.get(plan.defaultMessage) || plan.defaultMessage;
      migrated += 1;
    }
    fs.writeFileSync(
      filePlan.filePath,
      applyPlans(filePlan.sourceText, filePlan.sourceFile, filePlan.plans),
    );
  }

  writeCatalog(enProjectMessagesPath, enMessages);
  writeCatalog(zhProjectMessagesPath, zhMessages);
  console.log(`Migrated ${migrated} English UI copy nodes across ${filePlans.length} files.`);
}

main().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
