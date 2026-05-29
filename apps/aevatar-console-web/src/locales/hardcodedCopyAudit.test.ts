import fs from 'node:fs';
import path from 'node:path';
import ts from 'typescript';

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
  'title',
  'titleHelp',
  'tooltip',
  'value',
]);

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
    const name = node.parent.name.getText().replace(/^['"]|['"]$/g, '');
    return jsxTextAttributeNames.has(name);
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
  type: 'jsxAttribute' | 'jsxText' | 'string',
): boolean {
  const text = value.replace(/\s+/g, ' ').trim();

  if (!englishTextPattern.test(text)) {
    return false;
  }

  if (text.length < 3 || text.length > 240) {
    return false;
  }

  if (/^\$?[a-z0-9_./:#?=&%{}-]+$/i.test(text) && !text.includes(' ')) {
    return (
      type === 'jsxText' &&
      /^[A-Za-z][A-Za-z-]{2,}$/.test(text) &&
      !/^(API|CSS|HTML|ID|JS|JSON|JSX|LLM|SDK|SSE|TS|TSX|URI|URL)$/i.test(text)
    );
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

function collectHardcodedUiText(
  filePath: string,
  shouldReport: (
    text: string,
    type: 'jsxAttribute' | 'jsxText' | 'string',
  ) => boolean,
): string[] {
  const sourceText = fs.readFileSync(filePath, 'utf8');
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
        ts.isJsxAttribute(node.parent) ? 'jsxAttribute' : 'string',
      )
    ) {
      addViolation(node, node.text);
      return;
    }

    if (
      ts.isNoSubstitutionTemplateLiteral(node) &&
      !isInsideExistingI18nCall(node) &&
      isUiFacingStringLiteral(node) &&
      shouldReport(node.text, 'string')
    ) {
      addViolation(node, node.text);
      return;
    }

    if (
      ts.isTemplateExpression(node) &&
      !isInsideExistingI18nCall(node) &&
      isUiFacingStringLiteral(node) &&
      shouldReport(getTemplateStaticText(node), 'string')
    ) {
      addViolation(node, node.getText(sourceFile));
      return;
    }

    ts.forEachChild(node, visit);
  }

  visit(sourceFile);
  return violations;
}

function collectHardcodedChineseUiText(filePath: string): string[] {
  return collectHardcodedUiText(filePath, (text) =>
    chineseTextPattern.test(text),
  );
}

function collectHardcodedEnglishUiText(filePath: string): string[] {
  return collectHardcodedUiText(filePath, isLikelyHardcodedEnglishUiText);
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

  it('keeps production UI copy out of hardcoded English literals', () => {
    const violations = collectProductionSourceFiles(sourceRoot).flatMap(
      collectHardcodedEnglishUiText,
    );

    expect(violations).toEqual([]);
  });

  it('keeps runtime-formatted copy out of static initialization paths', () => {
    const violations = collectProductionSourceFiles(sourceRoot).flatMap(
      collectStaticI18nFormatting,
    );

    expect(violations).toEqual([]);
  });
});
