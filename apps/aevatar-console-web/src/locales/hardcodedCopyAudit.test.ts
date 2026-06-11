import fs from 'node:fs';
import path from 'node:path';
import ts from 'typescript';
import enUSMessages from './en-US';

const sourceRoot = path.resolve(__dirname, '..');
const chineseTextPattern = /\p{Script=Han}/u;
const englishTextPattern = /[A-Za-z]/;

const jsxTextAttributeNames = new Set([
  'aria-label',
  'cancelText',
  'caption',
  'copy',
  'description',
  'emptyText',
  'extra',
  'helperText',
  'label',
  'loadLabel',
  'message',
  'okText',
  'placeholder',
  'subtitle',
  'text',
  'title',
  'titleHelp',
  'tooltip',
  'testTeamHint',
  'testTeamLabel',
  'value',
]);

const objectUiCopyPropertyNames = new Set([
  'action',
  'actor',
  'detail',
  'primaryActionLabel',
  'secondaryActionLabel',
  'summary',
  'text',
]);

const sampleInputPropertyNames = new Set([
  'inputPreview',
  'prompt',
  'sampleInput',
]);

type I18nFallbackDefault = {
  defaultMessage: string;
  id: string;
  location: string;
};

function collectPlaceholders(value: string): string[] {
  const names = new Set<string>();

  for (const match of value.matchAll(/\{([A-Za-z_][A-Za-z0-9_]*)/g)) {
    names.add(match[1]);
  }

  return [...names].sort();
}

function collectProductionSourceFiles(
  directory: string,
  result: string[] = [],
): string[] {
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    if (entry.name.startsWith('.umi')) {
      continue;
    }

    const fullPath = path.join(directory, entry.name);
    if (entry.isDirectory()) {
      if (entry.name === 'locales') {
        continue;
      }
      collectProductionSourceFiles(fullPath, result);
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

function isImportOrExportLiteral(node: ts.Node): boolean {
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

function isObjectPropertyName(node: ts.Node): boolean {
  return (
    ts.isPropertyAssignment(node.parent) &&
    node.parent.name === node &&
    !node.parent.name.getText().startsWith('[')
  );
}

function getPropertyAssignmentName(node: ts.Node): string | null {
  if (!ts.isPropertyAssignment(node.parent) || node.parent.initializer !== node) {
    return null;
  }

  const { name } = node.parent;
  if (
    ts.isIdentifier(name) ||
    ts.isStringLiteral(name) ||
    ts.isNumericLiteral(name)
  ) {
    return name.text;
  }

  return null;
}

function isInsideJsonStringifyPayload(node: ts.Node): boolean {
  for (let parent = node.parent; parent; parent = parent.parent) {
    if (
      ts.isCallExpression(parent) &&
      ts.isPropertyAccessExpression(parent.expression) &&
      parent.expression.expression.getText() === 'JSON' &&
      parent.expression.name.text === 'stringify'
    ) {
      return true;
    }
  }

  return false;
}

function isTypeOnlyLiteral(node: ts.Node): boolean {
  for (let parent = node.parent; parent; parent = parent.parent) {
    if (
      ts.isInterfaceDeclaration(parent) ||
      ts.isLiteralTypeNode(parent) ||
      ts.isPropertySignature(parent) ||
      ts.isTypeAliasDeclaration(parent) ||
      ts.isTypeLiteralNode(parent)
    ) {
      return true;
    }
  }

  return false;
}

function isUiFacingStringLiteral(
  node: ts.NoSubstitutionTemplateLiteral | ts.StringLiteralLike | ts.TemplateExpression,
): boolean {
  if (
    isImportOrExportLiteral(node) ||
    isObjectPropertyName(node) ||
    isTypeOnlyLiteral(node)
  ) {
    return false;
  }

  if (ts.isJsxAttribute(node.parent) && node.parent.initializer === node) {
    return jsxTextAttributeNames.has(node.parent.name.getText());
  }

  if (ts.isPropertyAssignment(node.parent) && node.parent.initializer === node) {
    const name = getPropertyAssignmentName(node);
    if (
      !name ||
      sampleInputPropertyNames.has(name) ||
      isInsideJsonStringifyPayload(node)
    ) {
      return false;
    }

    return jsxTextAttributeNames.has(name) || objectUiCopyPropertyNames.has(name);
  }

  if (isDirectlyRenderedJsxExpressionLiteral(node)) {
    return true;
  }

  return false;
}

function isDirectlyRenderedJsxExpressionLiteral(node: ts.Node): boolean {
  let isInJsxExpression = false;

  for (let parent = node.parent; parent; parent = parent.parent) {
    if (ts.isJsxAttribute(parent)) {
      return false;
    }

    if (ts.isJsxExpression(parent)) {
      if (ts.isJsxAttribute(parent.parent)) {
        return false;
      }
      isInJsxExpression = true;
      break;
    }
  }

  return isInJsxExpression && isRenderableExpressionPosition(node);
}

function isRenderableExpressionPosition(node: ts.Node): boolean {
  const parent = node.parent;

  if (!parent) {
    return false;
  }

  if (ts.isParenthesizedExpression(parent) && parent.expression === node) {
    return isRenderableExpressionPosition(parent);
  }

  if (ts.isAsExpression(parent) && parent.expression === node) {
    return isRenderableExpressionPosition(parent);
  }

  if (ts.isConditionalExpression(parent)) {
    return parent.whenTrue === node || parent.whenFalse === node;
  }

  if (ts.isBinaryExpression(parent)) {
    return (
      parent.right === node &&
      (parent.operatorToken.kind === ts.SyntaxKind.BarBarToken ||
        parent.operatorToken.kind === ts.SyntaxKind.QuestionQuestionToken)
    );
  }

  return false;
}

function isInsideExistingI18nCall(node: ts.Node): boolean {
  for (let parent = node.parent; parent; parent = parent.parent) {
    if (
      ts.isCallExpression(parent) &&
      ts.isIdentifier(parent.expression) &&
      (parent.expression.text === 't' ||
        parent.expression.text === 'formatConsoleMessage')
    ) {
      return true;
    }

    if (
      ts.isCallExpression(parent) &&
      ts.isPropertyAccessExpression(parent.expression) &&
      parent.expression.name.text === 'formatMessage'
    ) {
      return true;
    }
  }

  return false;
}

function isLikelyHardcodedEnglishUiText(
  value: string,
  type: 'jsxAttribute' | 'jsxExpression' | 'jsxText' | 'string',
): boolean {
  const text = value.replace(/\s+/g, ' ').trim();

  if (!englishTextPattern.test(text)) {
    return false;
  }

  if (text.length < 3 || text.length > 240) {
    return false;
  }

  if (/^[a-z0-9_-]+\.[a-z0-9_-]+$/i.test(text)) {
    return false;
  }

  if (/^\$?[a-z0-9_./:#?=&%{}-]+$/i.test(text) && !text.includes(' ')) {
    return (
      (type === 'jsxText' ||
        (type === 'jsxExpression' && /^[A-Z]/.test(text))) &&
      /^[A-Za-z][A-Za-z.-]{2,}$/.test(text) &&
      !/^(API|CSS|HTML|ID|JS|JSON|JSX|LLM|SDK|SSE|TS|TSX|URI|URL)$/i.test(text)
    );
  }

  if (/^[a-z0-9]+(\.[a-z0-9_-]+)+$/i.test(text)) {
    return false;
  }

  if (/^#[0-9a-f]{3,8}$/i.test(text)) {
    return false;
  }

  if (/^rgba?\(/i.test(text)) {
    return false;
  }

  if (/^type\.googleapis\.com\//i.test(text)) {
    return false;
  }

  return true;
}

function collectHardcodedUiText(
  filePath: string,
  shouldReport: (
    text: string,
    type: 'jsxAttribute' | 'jsxExpression' | 'jsxText' | 'string',
  ) => boolean,
): string[] {
  const sourceText = fs.readFileSync(filePath, 'utf8');
  return collectHardcodedUiTextFromSource(filePath, sourceText, shouldReport);
}

function collectHardcodedChineseUiText(filePath: string): string[] {
  return collectHardcodedUiText(filePath, (text) =>
    chineseTextPattern.test(text),
  );
}

function collectHardcodedEnglishUiText(filePath: string): string[] {
  return collectHardcodedUiText(filePath, isLikelyHardcodedEnglishUiText);
}

function collectHardcodedEnglishUiTextWithoutExpressions(filePath: string): string[] {
  return collectHardcodedUiText(filePath, (text, type) =>
    type === 'jsxExpression'
      ? false
      : isLikelyHardcodedEnglishUiText(text, type),
  );
}

function collectHardcodedEnglishUiTextFromSource(
  sourceText: string,
  fileName = 'fixture.tsx',
): string[] {
  return collectHardcodedUiTextFromSource(
    fileName,
    sourceText,
    isLikelyHardcodedEnglishUiText,
  );
}

function collectHardcodedUiTextFromSource(
  filePath: string,
  sourceText: string,
  shouldReport: (
    text: string,
    type: 'jsxAttribute' | 'jsxExpression' | 'jsxText' | 'string',
  ) => boolean,
): string[] {
  const sourceFile = ts.createSourceFile(
    filePath,
    sourceText,
    ts.ScriptTarget.Latest,
    true,
    filePath.endsWith('.tsx') ? ts.ScriptKind.TSX : ts.ScriptKind.TS,
  );
  const violations: string[] = [];

  function getTemplateStaticText(node: ts.TemplateExpression): string {
    return [
      node.head.text,
      ...node.templateSpans.map((span) => span.literal.text),
    ].join('');
  }

  function addViolation(node: ts.Node, text: string): void {
    const { line, character } = sourceFile.getLineAndCharacterOfPosition(
      node.getStart(sourceFile),
    );
    violations.push(
      `${path.relative(sourceRoot, filePath)}:${line + 1}:${character + 1} ${text
        .replace(/\s+/g, ' ')
        .trim()}`,
    );
  }

  function visit(node: ts.Node): void {
    if (ts.isJsxText(node)) {
      const text = node.getText(sourceFile).replace(/\s+/g, ' ').trim();
      if (shouldReport(text, 'jsxText')) {
        addViolation(node, text);
      }
      ts.forEachChild(node, visit);
      return;
    }

    if (
      ts.isStringLiteralLike(node) &&
      !isInsideExistingI18nCall(node) &&
      isUiFacingStringLiteral(node) &&
      shouldReport(
        node.text,
        ts.isJsxAttribute(node.parent)
          ? 'jsxAttribute'
          : isDirectlyRenderedJsxExpressionLiteral(node)
            ? 'jsxExpression'
            : 'string',
      )
    ) {
      addViolation(node, node.text);
      return;
    }

    if (
      ts.isNoSubstitutionTemplateLiteral(node) &&
      !isInsideExistingI18nCall(node) &&
      isUiFacingStringLiteral(node) &&
      shouldReport(
        node.text,
        isDirectlyRenderedJsxExpressionLiteral(node) ? 'jsxExpression' : 'string',
      )
    ) {
      addViolation(node, node.text);
      return;
    }

    if (
      ts.isTemplateExpression(node) &&
      !isInsideExistingI18nCall(node) &&
      isUiFacingStringLiteral(node) &&
      shouldReport(
        getTemplateStaticText(node),
        isDirectlyRenderedJsxExpressionLiteral(node) ? 'jsxExpression' : 'string',
      )
    ) {
      addViolation(node, node.getText(sourceFile));
      return;
    }

    ts.forEachChild(node, visit);
  }

  visit(sourceFile);
  return violations;
}

function collectHardcodedEnglishUiReturnText(filePath: string): string[] {
  const sourceText = fs.readFileSync(filePath, 'utf8');
  return collectHardcodedEnglishUiReturnTextFromSource(sourceText, filePath);
}

function collectHardcodedEnglishUiReturnTextFromSource(
  sourceText: string,
  fileName = 'fixture.tsx',
): string[] {
  const sourceFile = ts.createSourceFile(
    fileName,
    sourceText,
    ts.ScriptTarget.Latest,
    true,
    fileName.endsWith('.tsx') ? ts.ScriptKind.TSX : ts.ScriptKind.TS,
  );
  const violations: string[] = [];

  function getTemplateStaticText(node: ts.TemplateExpression): string {
    return [
      node.head.text,
      ...node.templateSpans.map((span) => span.literal.text),
    ].join('');
  }

  function addViolation(node: ts.Node, text: string): void {
    const { line, character } = sourceFile.getLineAndCharacterOfPosition(
      node.getStart(sourceFile),
    );
    violations.push(
      `${path.relative(sourceRoot, fileName)}:${line + 1}:${character + 1} ${text
        .replace(/\s+/g, ' ')
        .trim()}`,
    );
  }

  function getEnclosingFunctionName(node: ts.Node): string | null {
    for (let parent = node.parent; parent; parent = parent.parent) {
      if (
        (ts.isFunctionDeclaration(parent) ||
          ts.isFunctionExpression(parent) ||
          ts.isMethodDeclaration(parent)) &&
        parent.name
      ) {
        return parent.name.getText(sourceFile);
      }

      if (ts.isArrowFunction(parent)) {
        if (
          ts.isVariableDeclaration(parent.parent) &&
          ts.isIdentifier(parent.parent.name)
        ) {
          return parent.parent.name.text;
        }

        if (ts.isPropertyAssignment(parent.parent)) {
          return getObjectPropertyName(parent.parent.name);
        }
      }
    }

    return null;
  }

  function isUiCopyReturnHelper(node: ts.ReturnStatement): boolean {
    const functionName = getEnclosingFunctionName(node);
    return Boolean(
      functionName &&
        /handoff|feedbackmessage|nextstep|observationevidence/i.test(
          functionName,
        ),
    );
  }

  function visitReturnExpression(node: ts.Node): void {
    if (
      ts.isJsxElement(node) ||
      ts.isJsxFragment(node) ||
      ts.isJsxSelfClosingElement(node)
    ) {
      return;
    }

    if (
      (ts.isStringLiteralLike(node) || ts.isNoSubstitutionTemplateLiteral(node)) &&
      !isInsideExistingI18nCall(node) &&
      !isObjectPropertyName(node) &&
      isLikelyHardcodedEnglishUiText(node.text, 'string')
    ) {
      addViolation(node, node.text);
      return;
    }

    if (
      ts.isTemplateExpression(node) &&
      !isInsideExistingI18nCall(node) &&
      isLikelyHardcodedEnglishUiText(getTemplateStaticText(node), 'string')
    ) {
      addViolation(node, node.getText(sourceFile));
      return;
    }

    ts.forEachChild(node, visitReturnExpression);
  }

  function visit(node: ts.Node): void {
    if (ts.isReturnStatement(node) && node.expression && isUiCopyReturnHelper(node)) {
      visitReturnExpression(node.expression);
      return;
    }

    ts.forEachChild(node, visit);
  }

  visit(sourceFile);
  return violations;
}

function isFunctionLike(node: ts.Node): boolean {
  return ts.isFunctionLike(node) || ts.isArrowFunction(node);
}

function getCallExpressionName(node: ts.CallExpression): string | null {
  if (ts.isIdentifier(node.expression)) {
    return node.expression.text;
  }

  if (ts.isPropertyAccessExpression(node.expression)) {
    return node.expression.name.text;
  }

  return null;
}

function getStaticStringValue(node: ts.Node | undefined): string | null {
  if (
    node &&
    (ts.isStringLiteralLike(node) || ts.isNoSubstitutionTemplateLiteral(node))
  ) {
    return node.text;
  }

  return null;
}

function getObjectPropertyName(node: ts.PropertyName): string | null {
  if (
    ts.isIdentifier(node) ||
    ts.isStringLiteral(node) ||
    ts.isNumericLiteral(node)
  ) {
    return node.text;
  }

  return null;
}

function getObjectStringProperty(
  node: ts.ObjectLiteralExpression,
  propertyName: string,
): string | null {
  for (const property of node.properties) {
    if (
      ts.isPropertyAssignment(property) &&
      getObjectPropertyName(property.name) === propertyName
    ) {
      return getStaticStringValue(property.initializer);
    }
  }

  return null;
}

function getJsxStringAttribute(
  node: ts.JsxOpeningLikeElement,
  attributeName: string,
): string | null {
  for (const property of node.attributes.properties) {
    if (
      ts.isJsxAttribute(property) &&
      ts.isIdentifier(property.name) &&
      property.name.text === attributeName &&
      property.initializer
    ) {
      return getStaticStringValue(property.initializer);
    }
  }

  return null;
}

function collectI18nFallbackDefaults(filePath: string): I18nFallbackDefault[] {
  const sourceText = fs.readFileSync(filePath, 'utf8');
  const sourceFile = ts.createSourceFile(
    filePath,
    sourceText,
    ts.ScriptTarget.Latest,
    true,
    filePath.endsWith('.tsx') ? ts.ScriptKind.TSX : ts.ScriptKind.TS,
  );
  const defaults: I18nFallbackDefault[] = [];

  function locationOf(node: ts.Node): string {
    const { line, character } = sourceFile.getLineAndCharacterOfPosition(
      node.getStart(sourceFile),
    );
    return `${path.relative(sourceRoot, filePath)}:${line + 1}:${character + 1}`;
  }

  function addDefault(
    node: ts.Node,
    id: string | null,
    defaultMessage: string | null,
  ): void {
    if (!id || !defaultMessage) {
      return;
    }

    defaults.push({
      defaultMessage,
      id,
      location: locationOf(node),
    });
  }

  function visit(node: ts.Node): void {
    if (ts.isCallExpression(node)) {
      const callName = getCallExpressionName(node);

      if (callName === 't') {
        addDefault(
          node,
          getStaticStringValue(node.arguments[0]),
          getStaticStringValue(node.arguments[1]),
        );
      }

      if (
        callName === 'formatMessage' &&
        node.arguments[0] &&
        ts.isObjectLiteralExpression(node.arguments[0])
      ) {
        addDefault(
          node,
          getObjectStringProperty(node.arguments[0], 'id'),
          getObjectStringProperty(node.arguments[0], 'defaultMessage'),
        );
      }
    }

    if (
      (ts.isJsxOpeningElement(node) || ts.isJsxSelfClosingElement(node)) &&
      node.tagName.getText(sourceFile) === 'T'
    ) {
      addDefault(
        node,
        getJsxStringAttribute(node, 'id'),
        getJsxStringAttribute(node, 'defaultMessage'),
      );
    }

    ts.forEachChild(node, visit);
  }

  visit(sourceFile);
  return defaults;
}

function containsI18nFormattingCall(node: ts.Node): boolean {
  let found = false;

  function visit(current: ts.Node): void {
    if (found) {
      return;
    }

    if (ts.isCallExpression(current)) {
      const callName = getCallExpressionName(current);
      if (
        callName === 'formatConsoleMessage' ||
        callName === 'formatMessage' ||
        callName === 't'
      ) {
        found = true;
        return;
      }
    }

    ts.forEachChild(current, visit);
  }

  visit(node);
  return found;
}

function collectStaticI18nFormatting(filePath: string): string[] {
  const sourceText = fs.readFileSync(filePath, 'utf8');
  const sourceFile = ts.createSourceFile(
    filePath,
    sourceText,
    ts.ScriptTarget.Latest,
    true,
    filePath.endsWith('.tsx') ? ts.ScriptKind.TSX : ts.ScriptKind.TS,
  );
  const violations: string[] = [];

  function addViolation(node: ts.Node, reason: string): void {
    const { line, character } = sourceFile.getLineAndCharacterOfPosition(
      node.getStart(sourceFile),
    );
    violations.push(
      `${path.relative(sourceRoot, filePath)}:${line + 1}:${character + 1} ${reason}`,
    );
  }

  function getFunctionDepth(node: ts.Node): number {
    let depth = 0;
    for (let parent = node.parent; parent; parent = parent.parent) {
      if (isFunctionLike(parent)) {
        depth += 1;
      }
    }
    return depth;
  }

  function visit(node: ts.Node): void {
    if (ts.isCallExpression(node)) {
      const callName = getCallExpressionName(node);
      if (
        (callName === 'formatConsoleMessage' || callName === 't') &&
        getFunctionDepth(node) === 0
      ) {
        addViolation(
          node,
          `module-scope ${callName}() freezes copy before locale changes`,
        );
      }

      if (
        callName === 'useMemo' &&
        node.arguments.length >= 2 &&
        ts.isArrayLiteralExpression(node.arguments[1]) &&
        node.arguments[1].elements.length === 0 &&
        containsI18nFormattingCall(node.arguments[0])
      ) {
        addViolation(
          node,
          'empty-dependency useMemo() contains i18n formatting and will not update on locale changes',
        );
      }
    }

    ts.forEachChild(node, visit);
  }

  visit(sourceFile);
  return violations;
}

describe('console-wide i18n migration guard', () => {
  it('keeps production UI copy out of hardcoded Chinese literals', () => {
    const violations = collectProductionSourceFiles(sourceRoot).flatMap(
      collectHardcodedChineseUiText,
    );

    expect(violations).toEqual([]);
  });

  it('keeps production UI copy out of hardcoded English JSX text and UI properties', () => {
    const violations = collectProductionSourceFiles(sourceRoot).flatMap(
      collectHardcodedEnglishUiText,
    );

    expect(violations).toEqual([]);
  });

  it('detects hardcoded English copy inside directly rendered JSX expressions', () => {
    const violations = collectHardcodedEnglishUiTextFromSource(`
      const Demo = ({ description, loading, row }) => (
        <section>
          <p>{description || "No detail"}</p>
          <button>{loading ? "Sending..." : "Send Signal"}</button>
          <span>{row.message ?? "No message"}</span>
          <span>{t("demo.already.localized", "Already localized")}</span>
        </section>
      );
    `);

    expect(violations.map((item) => item.replace(/^.*? /, ''))).toEqual([
      'No detail',
      'Sending...',
      'Send Signal',
      'No message',
    ]);
  });

  it('detects hardcoded English copy returned from UI handoff helpers', () => {
    const violations = collectHardcodedEnglishUiReturnTextFromSource(`
      function getObserveHandoffText(active: boolean) {
        return active
          ? "Observe will follow backend events."
          : t("demo.localized.handoff", "Already localized handoff.");
      }

      const buildActionFeedbackMessage = () => {
        return \`Runtime accepted \${commandId}.\`;
      };

      function formatRuntimeStatus(status: string) {
        return "running";
      }
    `);

    expect(violations.map((item) => item.replace(/^.*? /, ''))).toEqual([
      'Observe will follow backend events.',
      '`Runtime accepted ${commandId}.`',
    ]);
  });

  it('keeps helper-returned UI handoff copy inside i18n', () => {
    const violations = collectProductionSourceFiles(sourceRoot).flatMap(
      collectHardcodedEnglishUiReturnText,
    );

    expect(violations).toEqual([]);
  });

  it('keeps runtime-formatted copy out of static initialization paths', () => {
    const violations = collectProductionSourceFiles(sourceRoot).flatMap(
      collectStaticI18nFormatting,
    );

    expect(violations).toEqual([]);
  });

  it('keeps i18n fallback defaults backed by the English catalog', () => {
    const enUSCatalog: Record<string, string> = enUSMessages;
    const defaults = collectProductionSourceFiles(sourceRoot).flatMap(
      collectI18nFallbackDefaults,
    );
    const defaultsWithChineseCopy = defaults
      .filter(({ defaultMessage }) => chineseTextPattern.test(defaultMessage))
      .map(({ defaultMessage, id, location }) => `${location} ${id}: ${defaultMessage}`);
    const missingCatalogEntries = defaults
      .filter(({ id }) => enUSCatalog[id] === undefined)
      .map(({ defaultMessage, id, location }) => `${location} ${id}: ${defaultMessage}`);
    const placeholderMismatches = defaults
      .filter(
        ({ defaultMessage, id }) =>
          enUSCatalog[id] !== undefined &&
          JSON.stringify(collectPlaceholders(enUSCatalog[id])) !==
            JSON.stringify(collectPlaceholders(defaultMessage)),
      )
      .map(
        ({ defaultMessage, id, location }) =>
          `${location} ${id}: ${defaultMessage} <> ${enUSCatalog[id]}`,
      );

    expect(defaultsWithChineseCopy).toEqual([]);
    expect(missingCatalogEntries).toEqual([]);
    expect(placeholderMismatches).toEqual([]);
  });
});
