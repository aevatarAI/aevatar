import fs from 'node:fs';
import path from 'node:path';
import ts from 'typescript';

const SOURCE_ROOT = path.resolve(__dirname, '..', '..');
const TOOLTIP_ADAPTER = path.join(__dirname, 'AevatarTooltip.tsx');

function listTypeScriptFiles(directory: string): string[] {
  return fs.readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const entryPath = path.join(directory, entry.name);
    if (entry.isDirectory()) return listTypeScriptFiles(entryPath);
    if (
      !entry.isFile() ||
      !/\.tsx?$/.test(entry.name) ||
      entry.name.endsWith('.d.ts')
    ) {
      return [];
    }
    return [entryPath];
  });
}

function directlyImportsAntTooltip(filePath: string): boolean {
  const sourceFile = ts.createSourceFile(
    filePath,
    fs.readFileSync(filePath, 'utf8'),
    ts.ScriptTarget.Latest,
    true,
    filePath.endsWith('.tsx') ? ts.ScriptKind.TSX : ts.ScriptKind.TS,
  );

  return sourceFile.statements.some((statement) => {
    if (
      !ts.isImportDeclaration(statement) ||
      !ts.isStringLiteral(statement.moduleSpecifier) ||
      statement.moduleSpecifier.text !== 'antd'
    ) {
      return false;
    }
    const bindings = statement.importClause?.namedBindings;
    return (
      bindings !== undefined &&
      ts.isNamedImports(bindings) &&
      bindings.elements.some(
        (element) => (element.propertyName ?? element.name).text === 'Tooltip',
      )
    );
  });
}

function usesTypographyEllipsisTooltip(filePath: string): boolean {
  const sourceFile = ts.createSourceFile(
    filePath,
    fs.readFileSync(filePath, 'utf8'),
    ts.ScriptTarget.Latest,
    true,
    filePath.endsWith('.tsx') ? ts.ScriptKind.TSX : ts.ScriptKind.TS,
  );
  let found = false;

  function visit(node: ts.Node): void {
    if (
      ts.isJsxAttribute(node) &&
      node.name.getText(sourceFile) === 'ellipsis' &&
      node.initializer &&
      ts.isJsxExpression(node.initializer) &&
      node.initializer.expression
    ) {
      const inspectEllipsisValue = (value: ts.Node): void => {
        if (
          ts.isPropertyAssignment(value) &&
          value.name.getText(sourceFile) === 'tooltip'
        ) {
          found = true;
          return;
        }
        ts.forEachChild(value, inspectEllipsisValue);
      };

      inspectEllipsisValue(node.initializer.expression);
    }

    if (!found) ts.forEachChild(node, visit);
  }

  visit(sourceFile);
  return found;
}

describe('AevatarTooltip import boundary', () => {
  it('keeps the Ant Tooltip dependency inside the shared adapter', () => {
    const directConsumers = listTypeScriptFiles(SOURCE_ROOT)
      .filter((filePath) => filePath !== TOOLTIP_ADAPTER)
      .filter(directlyImportsAntTooltip)
      .map((filePath) => path.relative(SOURCE_ROOT, filePath))
      .sort();

    expect(directConsumers).toEqual([]);
  });

  it('keeps Typography ellipsis tooltips behind the shared adapter', () => {
    const bypasses = listTypeScriptFiles(SOURCE_ROOT)
      .filter(usesTypographyEllipsisTooltip)
      .map((filePath) => path.relative(SOURCE_ROOT, filePath))
      .sort();

    expect(bypasses).toEqual([]);
  });
});
