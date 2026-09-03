import { DeleteOutlined, PlusOutlined } from '@ant-design/icons';
import {
  Button,
  Input,
  InputNumber,
  Segmented,
  Select,
  Switch,
  Typography,
} from 'antd';
import jsonParse from 'core-js-pure/actual/json/parse';
import React from 'react';
import { t } from '@/shared/i18n/messages';

type EditorMode = 'fields' | 'json';
type JsonNodeType =
  | 'array'
  | 'boolean'
  | 'null'
  | 'number'
  | 'object'
  | 'string';

type JsonObjectEntry = {
  readonly id: string;
  readonly key: string;
  readonly value: JsonNode;
};

type JsonNode = {
  readonly arrayItems?: readonly JsonNode[];
  readonly booleanValue?: boolean;
  readonly id: string;
  readonly numberValue?: string;
  readonly objectEntries?: readonly JsonObjectEntry[];
  readonly stringValue?: string;
  readonly type: JsonNodeType;
};

type StructuredJsonArgumentsEditorProps = {
  readonly disabled: boolean;
  readonly onChange: (value: string) => void;
  readonly onErrorChange?: (error: string) => void;
  readonly value: string;
};

type EditorNotice = {
  readonly controlId?: string;
  readonly kind: 'error' | 'warning';
  readonly message: string;
  readonly source: 'field' | 'json';
};

type ParseObjectResult =
  | { readonly kind: 'invalid' }
  | { readonly kind: 'unsafe-number' }
  | { readonly kind: 'valid'; readonly root: JsonNode };

type DecimalValue = {
  readonly coefficient: bigint;
  readonly exponent: bigint;
  readonly negativeZero: boolean;
};

type JsonParseWithSource = (
  text: string,
  reviver: (
    this: unknown,
    key: string,
    value: unknown,
    context?: { readonly source?: string },
  ) => unknown,
) => unknown;

let nextEditorNodeId = 0;

function createNodeId(): string {
  nextEditorNodeId += 1;
  return `argument-node-${nextEditorNodeId}`;
}

function createNode(type: JsonNodeType): JsonNode {
  const base = { id: createNodeId(), type } as const;
  switch (type) {
    case 'array':
      return { ...base, arrayItems: [] };
    case 'boolean':
      return { ...base, booleanValue: false };
    case 'null':
      return base;
    case 'number':
      return { ...base, numberValue: '0' };
    case 'object':
      return { ...base, objectEntries: [] };
    case 'string':
      return { ...base, stringValue: '' };
  }
}

function nodeFromValue(value: unknown): JsonNode {
  if (value === null) return createNode('null');
  if (Array.isArray(value)) {
    return {
      ...createNode('array'),
      arrayItems: value.map(nodeFromValue),
    };
  }
  switch (typeof value) {
    case 'boolean':
      return { ...createNode('boolean'), booleanValue: value };
    case 'number':
      return { ...createNode('number'), numberValue: String(value) };
    case 'object':
      return {
        ...createNode('object'),
        objectEntries: Object.entries(value as Record<string, unknown>).map(
          ([key, entryValue]) => ({
            id: createNodeId(),
            key,
            value: nodeFromValue(entryValue),
          }),
        ),
      };
    default:
      return { ...createNode('string'), stringValue: String(value ?? '') };
  }
}

function valueFromNode(node: JsonNode): unknown {
  switch (node.type) {
    case 'array':
      return (node.arrayItems ?? []).map(valueFromNode);
    case 'boolean':
      return node.booleanValue ?? false;
    case 'null':
      return null;
    case 'number': {
      const parsed = Number(node.numberValue ?? '0');
      return Number.isFinite(parsed) ? parsed : 0;
    }
    case 'object':
      return Object.fromEntries(
        (node.objectEntries ?? []).map((entry) => [
          entry.key,
          valueFromNode(entry.value),
        ]),
      );
    case 'string':
      return node.stringValue ?? '';
  }
}

function decimalValue(source: string): DecimalValue | null {
  const match = source.match(
    /^(-?)(0|[1-9]\d*)(?:\.(\d+))?(?:[eE]([+-]?\d+))?$/,
  );
  if (!match) return null;
  const [, sign, integer, fraction = '', sourceExponent = '0'] = match;
  let coefficient = BigInt(`${integer}${fraction}`);
  let exponent = BigInt(sourceExponent) - BigInt(fraction.length);
  while (coefficient !== 0n && coefficient % 10n === 0n) {
    coefficient /= 10n;
    exponent += 1n;
  }
  if (coefficient === 0n) exponent = 0n;
  return {
    coefficient: sign === '-' ? -coefficient : coefficient,
    exponent,
    negativeZero: sign === '-' && coefficient === 0n,
  };
}

function numberSourceRoundTrips(source: string, value: number): boolean {
  if (!Number.isFinite(value)) return false;
  if (Number.isInteger(value) && !Number.isSafeInteger(value)) return false;
  const serialized = JSON.stringify(value);
  if (!serialized) return false;
  const originalValue = decimalValue(source);
  const serializedValue = decimalValue(serialized);
  return (
    originalValue !== null &&
    serializedValue !== null &&
    originalValue.coefficient === serializedValue.coefficient &&
    originalValue.exponent === serializedValue.exponent &&
    originalValue.negativeZero === serializedValue.negativeZero
  );
}

function parseObject(value: string): ParseObjectResult {
  try {
    let hasUnsupportedNumber = false;
    const parsed = value.trim()
      ? (jsonParse as JsonParseWithSource)(
          value,
          (_key, parsedValue, context) => {
            if (
              typeof parsedValue === 'number' &&
              (!context?.source ||
                !numberSourceRoundTrips(context.source, parsedValue))
            ) {
              hasUnsupportedNumber = true;
            }
            return parsedValue;
          },
        )
      : {};
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
      return { kind: 'invalid' };
    }
    if (hasUnsupportedNumber) return { kind: 'unsafe-number' };
    return { kind: 'valid', root: nodeFromValue(parsed) };
  } catch {
    return { kind: 'invalid' };
  }
}

function findUnsupportedNumberNode(node: JsonNode): JsonNode | null {
  if (node.type === 'number') {
    const numberSource = node.numberValue ?? '0';
    return numberSourceRoundTrips(numberSource, Number(numberSource))
      ? null
      : node;
  }
  if (node.type === 'array') {
    for (const item of node.arrayItems ?? []) {
      const unsafeNode = findUnsupportedNumberNode(item);
      if (unsafeNode) return unsafeNode;
    }
  }
  if (node.type === 'object') {
    for (const entry of node.objectEntries ?? []) {
      const unsafeNode = findUnsupportedNumberNode(entry.value);
      if (unsafeNode) return unsafeNode;
    }
  }
  return null;
}

function hasSiblingProperty(
  node: JsonNode,
  entryId: string,
  nextKey: string,
): boolean {
  if (node.type === 'object') {
    const entries = node.objectEntries ?? [];
    if (entries.some((entry) => entry.id === entryId)) {
      return entries.some(
        (entry) => entry.id !== entryId && entry.key === nextKey,
      );
    }
    return entries.some((entry) =>
      hasSiblingProperty(entry.value, entryId, nextKey),
    );
  }
  if (node.type === 'array') {
    return (node.arrayItems ?? []).some((item) =>
      hasSiblingProperty(item, entryId, nextKey),
    );
  }
  return false;
}

function updateNode(
  node: JsonNode,
  nodeId: string,
  update: (current: JsonNode) => JsonNode,
): JsonNode {
  if (node.id === nodeId) return update(node);
  if (node.type === 'object') {
    return {
      ...node,
      objectEntries: (node.objectEntries ?? []).map((entry) => ({
        ...entry,
        value: updateNode(entry.value, nodeId, update),
      })),
    };
  }
  if (node.type === 'array') {
    return {
      ...node,
      arrayItems: (node.arrayItems ?? []).map((item) =>
        updateNode(item, nodeId, update),
      ),
    };
  }
  return node;
}

function updateObjectEntry(
  node: JsonNode,
  entryId: string,
  update: (current: JsonObjectEntry) => JsonObjectEntry,
): JsonNode {
  if (node.type === 'object') {
    return {
      ...node,
      objectEntries: (node.objectEntries ?? []).map((entry) => ({
        ...(entry.id === entryId ? update(entry) : entry),
        value: updateObjectEntry(entry.value, entryId, update),
      })),
    };
  }
  if (node.type === 'array') {
    return {
      ...node,
      arrayItems: (node.arrayItems ?? []).map((item) =>
        updateObjectEntry(item, entryId, update),
      ),
    };
  }
  return node;
}

function removeObjectEntry(node: JsonNode, entryId: string): JsonNode {
  if (node.type === 'object') {
    return {
      ...node,
      objectEntries: (node.objectEntries ?? [])
        .filter((entry) => entry.id !== entryId)
        .map((entry) => ({
          ...entry,
          value: removeObjectEntry(entry.value, entryId),
        })),
    };
  }
  if (node.type === 'array') {
    return {
      ...node,
      arrayItems: (node.arrayItems ?? []).map((item) =>
        removeObjectEntry(item, entryId),
      ),
    };
  }
  return node;
}

function removeArrayItem(node: JsonNode, itemId: string): JsonNode {
  if (node.type === 'array') {
    return {
      ...node,
      arrayItems: (node.arrayItems ?? [])
        .filter((item) => item.id !== itemId)
        .map((item) => removeArrayItem(item, itemId)),
    };
  }
  if (node.type === 'object') {
    return {
      ...node,
      objectEntries: (node.objectEntries ?? []).map((entry) => ({
        ...entry,
        value: removeArrayItem(entry.value, itemId),
      })),
    };
  }
  return node;
}

function uniquePropertyName(entries: readonly JsonObjectEntry[]): string {
  const existing = new Set(entries.map((entry) => entry.key));
  if (!existing.has('property')) return 'property';
  let suffix = 2;
  while (existing.has(`property${suffix}`)) suffix += 1;
  return `property${suffix}`;
}

function noticeForParseResult(
  result: Exclude<ParseObjectResult, { readonly kind: 'valid' }>,
): EditorNotice {
  if (result.kind === 'unsafe-number') {
    return {
      kind: 'warning',
      message: t(
        'workflowActivityVNext.arguments.unsupportedNumber',
        'This JSON contains a number that Fields mode cannot represent safely. Edit it in JSON or use a string.',
      ),
      source: 'json',
    };
  }
  return {
    kind: 'error',
    message: t(
      'workflowActivityVNext.arguments.invalidObject',
      'Enter a valid JSON object before switching to Fields.',
    ),
    source: 'json',
  };
}

const StructuredJsonArgumentsEditor: React.FC<
  StructuredJsonArgumentsEditorProps
> = ({ disabled, onChange, onErrorChange, value }) => {
  const [initialParse] = React.useState(() => parseObject(value));
  const initialRoot =
    initialParse.kind === 'valid' ? initialParse.root : createNode('object');
  const [mode, setMode] = React.useState<EditorMode>(
    initialParse.kind === 'valid' ? 'fields' : 'json',
  );
  const [root, setRoot] = React.useState<JsonNode>(initialRoot);
  const [rawText, setRawText] = React.useState(value.trim() ? value : '{}');
  const [notice, setNotice] = React.useState<EditorNotice | null>(() => {
    if (initialParse.kind === 'invalid') {
      return {
        kind: 'error',
        message: t(
          'workflowActivityVNext.arguments.invalidObject',
          'Enter a valid JSON object before switching to Fields.',
        ),
        source: 'json',
      };
    }
    if (initialParse.kind === 'unsafe-number') {
      return {
        kind: 'warning',
        message: t(
          'workflowActivityVNext.arguments.unsupportedNumber',
          'This JSON contains a number that Fields mode cannot represent safely. Edit it in JSON or use a string.',
        ),
        source: 'json',
      };
    }
    return null;
  });
  const lastEmittedValueRef = React.useRef<string | null>(null);
  const noticeId = React.useId();
  const editorError = notice?.kind === 'error' ? notice.message : '';

  React.useEffect(() => {
    onErrorChange?.(editorError);
  }, [editorError, onErrorChange]);

  React.useEffect(() => {
    if (value === lastEmittedValueRef.current) {
      lastEmittedValueRef.current = null;
      return;
    }
    lastEmittedValueRef.current = null;
    const nextParse = parseObject(value);
    setRawText(value.trim() ? value : '{}');
    if (nextParse.kind !== 'valid') {
      setMode('json');
      setNotice(noticeForParseResult(nextParse));
      return;
    }
    setRoot(nextParse.root);
    setNotice(null);
  }, [value]);

  const commitRoot = (nextRoot: JsonNode) => {
    if (disabled) return;
    const unsafeNode = findUnsupportedNumberNode(nextRoot);
    if (unsafeNode) {
      setRoot(nextRoot);
      setNotice({
        controlId: unsafeNode.id,
        kind: 'error',
        message: t(
          'workflowActivityVNext.arguments.unsupportedNumber',
          'This JSON contains a number that Fields mode cannot represent safely. Edit it in JSON or use a string.',
        ),
        source: 'field',
      });
      return;
    }
    const nextText = JSON.stringify(valueFromNode(nextRoot), null, 2);
    setRoot(nextRoot);
    setRawText(nextText);
    setNotice(null);
    lastEmittedValueRef.current = nextText === value ? null : nextText;
    onChange(nextText);
  };

  const changeMode = (nextMode: EditorMode) => {
    if (nextMode === 'json') {
      if (notice?.source === 'field') {
        const currentParse = parseObject(rawText);
        if (currentParse.kind === 'valid') {
          setRoot(currentParse.root);
          setNotice(null);
        } else {
          setNotice(noticeForParseResult(currentParse));
        }
      }
      setMode('json');
      return;
    }
    const nextParse = parseObject(rawText);
    if (nextParse.kind !== 'valid') {
      setNotice(noticeForParseResult(nextParse));
      return;
    }
    setRoot(nextParse.root);
    setNotice(null);
    setMode('fields');
  };

  const changeRawText = (nextText: string) => {
    if (disabled) return;
    setRawText(nextText);
    lastEmittedValueRef.current = nextText === value ? null : nextText;
    onChange(nextText);
    const nextParse = parseObject(nextText);
    if (nextParse.kind === 'valid') {
      setRoot(nextParse.root);
      setNotice(null);
      return;
    }
    setNotice(noticeForParseResult(nextParse));
  };

  const typeOptions = (
    [
      ['string', 'String'],
      ['number', 'Number'],
      ['boolean', 'Boolean'],
      ['object', 'Object'],
      ['array', 'Array'],
      ['null', 'Null'],
    ] as const
  ).map(([optionValue, defaultMessage]) => ({
    label: t(
      `workflowActivityVNext.arguments.type.${optionValue}`,
      defaultMessage,
    ),
    value: optionValue,
  }));

  const renderValue = (node: JsonNode, path: string): React.ReactNode => {
    const valueLabel = t(
      'workflowActivityVNext.arguments.valueAria',
      'Value for {path}',
      { path },
    );
    switch (node.type) {
      case 'string':
        return (
          <Input
            aria-label={valueLabel}
            disabled={disabled}
            onChange={(event) =>
              commitRoot(
                updateNode(root, node.id, (current) => ({
                  ...current,
                  stringValue: event.target.value,
                })),
              )
            }
            value={node.stringValue ?? ''}
          />
        );
      case 'number':
        return (
          <InputNumber
            aria-describedby={
              notice?.controlId === node.id ? noticeId : undefined
            }
            aria-invalid={notice?.controlId === node.id}
            aria-label={valueLabel}
            disabled={disabled}
            onInput={(nextText) =>
              commitRoot(
                updateNode(root, node.id, (current) => ({
                  ...current,
                  numberValue: nextText.trim() ? nextText : '0',
                })),
              )
            }
            onChange={(nextValue) =>
              commitRoot(
                updateNode(root, node.id, (current) => ({
                  ...current,
                  numberValue: String(nextValue ?? 0),
                })),
              )
            }
            stringMode
            value={node.numberValue ?? '0'}
          />
        );
      case 'boolean':
        return (
          <Switch
            aria-label={valueLabel}
            checked={node.booleanValue ?? false}
            disabled={disabled}
            onChange={(checked) =>
              commitRoot(
                updateNode(root, node.id, (current) => ({
                  ...current,
                  booleanValue: checked,
                })),
              )
            }
          />
        );
      case 'null':
        return (
          <Typography.Text
            className="wa-vnext__arguments-null"
            type="secondary"
          >
            {t('workflowActivityVNext.arguments.nullValue', 'No value')}
          </Typography.Text>
        );
      case 'object':
        return renderObject(node, path);
      case 'array':
        return renderArray(node, path);
    }
  };

  const renderTypeSelect = (node: JsonNode, path: string) => (
    <Select<JsonNodeType>
      aria-label={t(
        'workflowActivityVNext.arguments.valueTypeAria',
        'Value type for {path}',
        { path },
      )}
      disabled={disabled}
      onChange={(nextType) =>
        commitRoot(updateNode(root, node.id, () => createNode(nextType)))
      }
      options={typeOptions}
      value={node.type}
    />
  );

  const renderObject = (node: JsonNode, path: string): React.ReactNode => {
    const entries = node.objectEntries ?? [];
    return (
      <div className="wa-vnext__arguments-collection">
        {entries.length === 0 ? (
          <Typography.Text
            className="wa-vnext__arguments-empty"
            type="secondary"
          >
            {t(
              'workflowActivityVNext.arguments.emptyObject',
              'No properties yet',
            )}
          </Typography.Text>
        ) : null}
        {entries.length > 0 ? (
          <div className="wa-vnext__arguments-column-labels">
            <span>
              {t('workflowActivityVNext.arguments.columnProperty', 'Property')}
            </span>
            <span>
              {t('workflowActivityVNext.arguments.columnType', 'Type')}
            </span>
            <span />
          </div>
        ) : null}
        {entries.map((entry) => {
          const entryPath = path ? `${path}.${entry.key}` : entry.key;
          const displayPath =
            entryPath ||
            t(
              'workflowActivityVNext.arguments.unnamedProperty',
              'unnamed property',
            );
          return (
            <div className="wa-vnext__arguments-entry" key={entry.id}>
              <div className="wa-vnext__arguments-entry-heading">
                <Input
                  aria-describedby={
                    notice?.controlId === entry.id ? noticeId : undefined
                  }
                  aria-invalid={notice?.controlId === entry.id}
                  aria-label={t(
                    'workflowActivityVNext.arguments.propertyNameAria',
                    'Property name for {path}',
                    { path: displayPath },
                  )}
                  disabled={disabled}
                  onChange={(event) => {
                    const nextKey = event.target.value;
                    if (hasSiblingProperty(root, entry.id, nextKey)) {
                      setNotice({
                        controlId: entry.id,
                        kind: 'error',
                        message: t(
                          'workflowActivityVNext.arguments.duplicateProperty',
                          'Property names must be unique. "{property}" already exists in this object.',
                          { property: nextKey },
                        ),
                        source: 'field',
                      });
                      return;
                    }
                    commitRoot(
                      updateObjectEntry(root, entry.id, (current) => ({
                        ...current,
                        key: nextKey,
                      })),
                    );
                  }}
                  placeholder={t(
                    'workflowActivityVNext.arguments.propertyName',
                    'Property name',
                  )}
                  value={entry.key}
                />
                {renderTypeSelect(entry.value, displayPath)}
                <Button
                  aria-label={t(
                    'workflowActivityVNext.arguments.removePropertyAria',
                    'Remove {path}',
                    { path: displayPath },
                  )}
                  danger
                  disabled={disabled}
                  icon={<DeleteOutlined />}
                  onClick={() => commitRoot(removeObjectEntry(root, entry.id))}
                  type="text"
                />
              </div>
              <div className="wa-vnext__arguments-entry-value">
                {renderValue(entry.value, displayPath)}
              </div>
            </div>
          );
        })}
        <Button
          aria-label={
            path
              ? t(
                  'workflowActivityVNext.arguments.addPropertyAria',
                  'Add property to {path}',
                  { path },
                )
              : t('workflowActivityVNext.arguments.addProperty', 'Add property')
          }
          disabled={disabled}
          icon={<PlusOutlined />}
          onClick={() =>
            commitRoot(
              updateNode(root, node.id, (current) => ({
                ...current,
                objectEntries: [
                  ...(current.objectEntries ?? []),
                  {
                    id: createNodeId(),
                    key: uniquePropertyName(current.objectEntries ?? []),
                    value: createNode('string'),
                  },
                ],
              })),
            )
          }
          type="dashed"
        >
          {t('workflowActivityVNext.arguments.addProperty', 'Add property')}
        </Button>
      </div>
    );
  };

  const renderArray = (node: JsonNode, path: string): React.ReactNode => {
    const items = node.arrayItems ?? [];
    return (
      <div className="wa-vnext__arguments-collection">
        {items.length === 0 ? (
          <Typography.Text
            className="wa-vnext__arguments-empty"
            type="secondary"
          >
            {t('workflowActivityVNext.arguments.emptyArray', 'No items yet')}
          </Typography.Text>
        ) : null}
        {items.map((item, index) => {
          const itemPath = `${path}[${index + 1}]`;
          return (
            <div className="wa-vnext__arguments-entry" key={item.id}>
              <div className="wa-vnext__arguments-array-heading">
                <Typography.Text>
                  {t('workflowActivityVNext.arguments.item', 'Item {number}', {
                    number: index + 1,
                  })}
                </Typography.Text>
                {renderTypeSelect(item, itemPath)}
                <Button
                  aria-label={t(
                    'workflowActivityVNext.arguments.removeItemAria',
                    'Remove item {number}',
                    { number: index + 1 },
                  )}
                  danger
                  disabled={disabled}
                  icon={<DeleteOutlined />}
                  onClick={() => commitRoot(removeArrayItem(root, item.id))}
                  type="text"
                />
              </div>
              <div className="wa-vnext__arguments-entry-value">
                {renderValue(item, itemPath)}
              </div>
            </div>
          );
        })}
        <Button
          aria-label={t(
            'workflowActivityVNext.arguments.addItemAria',
            'Add item to {path}',
            { path },
          )}
          disabled={disabled}
          icon={<PlusOutlined />}
          onClick={() =>
            commitRoot(
              updateNode(root, node.id, (current) => ({
                ...current,
                arrayItems: [
                  ...(current.arrayItems ?? []),
                  createNode('string'),
                ],
              })),
            )
          }
          type="dashed"
        >
          {t('workflowActivityVNext.arguments.addItem', 'Add item')}
        </Button>
      </div>
    );
  };

  return (
    <div className="wa-vnext__arguments-editor">
      <Segmented<EditorMode>
        block
        disabled={disabled}
        onChange={changeMode}
        options={[
          {
            label: t('workflowActivityVNext.arguments.mode.fields', 'Fields'),
            value: 'fields',
          },
          {
            label: t('workflowActivityVNext.arguments.mode.json', 'JSON'),
            value: 'json',
          },
        ]}
        value={mode}
      />
      {mode === 'fields' ? (
        renderObject(root, '')
      ) : (
        <div className="wa-vnext__arguments-json">
          <Input.TextArea
            aria-describedby={notice ? noticeId : undefined}
            aria-invalid={notice?.kind === 'error'}
            aria-label={t(
              'workflowActivityVNext.arguments.jsonAria',
              'Arguments JSON',
            )}
            autoSize={{ maxRows: 14, minRows: 7 }}
            disabled={disabled}
            onChange={(event) => changeRawText(event.target.value)}
            spellCheck={false}
            status={notice?.kind === 'error' ? 'error' : undefined}
            value={rawText}
          />
        </div>
      )}
      {notice ? (
        <Typography.Text
          className="wa-vnext__arguments-error"
          id={noticeId}
          role="alert"
          type={notice.kind === 'error' ? 'danger' : 'warning'}
        >
          {notice.message}
        </Typography.Text>
      ) : null}
    </div>
  );
};

export default StructuredJsonArgumentsEditor;
