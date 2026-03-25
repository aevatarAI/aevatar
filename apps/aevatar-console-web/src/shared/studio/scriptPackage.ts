import type {
  ScriptPackage,
  ScriptPackageEntry,
  ScriptPackageFile,
  ScriptPackageFileKind,
} from './scriptsModels';

const PACKAGE_FORMAT = 'aevatar.scripting.package.v1';

type PersistedPackageShape = {
  format?: string;
  cSharpSources?: Array<{
    path?: string;
    content?: string;
    Path?: string;
    Content?: string;
  }>;
  csharpSources?: Array<{
    path?: string;
    content?: string;
    Path?: string;
    Content?: string;
  }>;
  CsharpSources?: Array<{
    path?: string;
    content?: string;
    Path?: string;
    Content?: string;
  }>;
  protoFiles?: Array<{
    path?: string;
    content?: string;
    Path?: string;
    Content?: string;
  }>;
  ProtoFiles?: Array<{
    path?: string;
    content?: string;
    Path?: string;
    Content?: string;
  }>;
  entryBehaviorTypeName?: string;
  entrySourcePath?: string;
  EntryBehaviorTypeName?: string;
  EntrySourcePath?: string;
};

function normalizePath(path: string, fallbackPath: string): string {
  const normalized = String(path || fallbackPath)
    .replace(/\\/g, '/')
    .trim()
    .replace(/^\.\/+/, '')
    .replace(/^\/+/, '');

  if (!normalized || normalized === '..' || normalized.includes('../')) {
    return fallbackPath;
  }

  return normalized;
}

function normalizeFiles(
  files: ScriptPackageFile[] | undefined,
  fallbackPath: string,
): ScriptPackageFile[] {
  const next = new Map<string, string>();
  for (const file of files || []) {
    const path = normalizePath(file?.path || fallbackPath, fallbackPath);
    next.set(path, String(file?.content || ''));
  }

  return Array.from(next.entries())
    .sort((left, right) => left[0].localeCompare(right[0]))
    .map(([path, content]) => ({ path, content }));
}

export function createScriptPackage(
  csharpSources: ScriptPackageFile[],
  protoFiles: ScriptPackageFile[] = [],
  entryBehaviorTypeName = '',
  entrySourcePath = '',
): ScriptPackage {
  const normalizedCsharp = normalizeFiles(csharpSources, 'Behavior.cs');
  const normalizedProto = normalizeFiles(protoFiles, 'schema.proto');
  const resolvedEntrySourcePath = normalizedCsharp.some(
    (file) => file.path === entrySourcePath,
  )
    ? entrySourcePath
    : normalizedCsharp[0]?.path || '';

  return {
    format: PACKAGE_FORMAT,
    csharpSources: normalizedCsharp,
    protoFiles: normalizedProto,
    entryBehaviorTypeName: String(entryBehaviorTypeName || '').trim(),
    entrySourcePath: resolvedEntrySourcePath,
  };
}

export function createSingleSourcePackage(
  source: string,
  path = 'Behavior.cs',
): ScriptPackage {
  return createScriptPackage([{ path, content: source }], [], '', path);
}

export function deserializePersistedSource(sourceText: string): ScriptPackage {
  const rawText = String(sourceText || '');
  const trimmed = rawText.trimStart();
  if (!trimmed.startsWith('{')) {
    return createSingleSourcePackage(rawText);
  }

  try {
    const parsed = JSON.parse(rawText) as PersistedPackageShape;
    if (parsed?.format !== PACKAGE_FORMAT) {
      return createSingleSourcePackage(rawText);
    }

    const csharpSources = Array.isArray(parsed.cSharpSources)
      ? parsed.cSharpSources
      : Array.isArray(parsed.csharpSources)
        ? parsed.csharpSources
        : Array.isArray(parsed.CsharpSources)
          ? parsed.CsharpSources
          : [];

    return createScriptPackage(
      csharpSources.map((file) => ({
        path: String(file?.path || file?.Path || 'Behavior.cs'),
        content: String(file?.content || file?.Content || ''),
      })),
      Array.isArray(parsed.protoFiles)
        ? parsed.protoFiles.map((file) => ({
            path: String(file?.path || file?.Path || 'schema.proto'),
            content: String(file?.content || file?.Content || ''),
          }))
        : Array.isArray(parsed.ProtoFiles)
          ? parsed.ProtoFiles.map((file) => ({
              path: String(file?.path || file?.Path || 'schema.proto'),
              content: String(file?.content || file?.Content || ''),
            }))
          : [],
      parsed.entryBehaviorTypeName || parsed.EntryBehaviorTypeName || '',
      parsed.entrySourcePath || parsed.EntrySourcePath || '',
    );
  } catch {
    return createSingleSourcePackage(rawText);
  }
}

export function coerceScriptPackage(payload: unknown): ScriptPackage | null {
  if (!payload || typeof payload !== 'object') {
    return null;
  }

  try {
    return deserializePersistedSource(JSON.stringify(payload));
  } catch {
    return null;
  }
}

export function serializePersistedSource(pkg: ScriptPackage): string {
  const normalized = createScriptPackage(
    pkg.csharpSources,
    pkg.protoFiles,
    pkg.entryBehaviorTypeName,
    pkg.entrySourcePath,
  );

  if (
    normalized.protoFiles.length === 0 &&
    normalized.csharpSources.length === 1 &&
    !normalized.entryBehaviorTypeName.trim()
  ) {
    return normalized.csharpSources[0]?.content || '';
  }

  return JSON.stringify({
    format: normalized.format,
    cSharpSources: normalized.csharpSources,
    protoFiles: normalized.protoFiles,
    entryBehaviorTypeName: normalized.entryBehaviorTypeName,
    entrySourcePath: normalized.entrySourcePath,
  });
}

export function getPackageEntries(pkg: ScriptPackage): ScriptPackageEntry[] {
  return [
    ...pkg.csharpSources.map((file) => ({
      kind: 'csharp' as const,
      path: file.path,
      content: file.content,
    })),
    ...pkg.protoFiles.map((file) => ({
      kind: 'proto' as const,
      path: file.path,
      content: file.content,
    })),
  ];
}

export function getSelectedPackageEntry(
  pkg: ScriptPackage,
  selectedFilePath: string,
): ScriptPackageEntry | null {
  const entries = getPackageEntries(pkg);
  if (entries.length === 0) {
    return null;
  }

  return entries.find((entry) => entry.path === selectedFilePath) || entries[0];
}

export function getPackageFileKind(
  pkg: ScriptPackage,
  filePath: string,
): ScriptPackageFileKind | null {
  if (pkg.csharpSources.some((file) => file.path === filePath)) {
    return 'csharp';
  }

  if (pkg.protoFiles.some((file) => file.path === filePath)) {
    return 'proto';
  }

  return null;
}

export function updatePackageFileContent(
  pkg: ScriptPackage,
  filePath: string,
  content: string,
): ScriptPackage {
  const nextKind = getPackageFileKind(pkg, filePath);
  if (!nextKind) {
    return pkg;
  }

  const nextFiles =
    nextKind === 'csharp'
      ? pkg.csharpSources.map((file) =>
          file.path === filePath ? { ...file, content } : file,
        )
      : pkg.protoFiles.map((file) =>
          file.path === filePath ? { ...file, content } : file,
        );

  return createScriptPackage(
    nextKind === 'csharp' ? nextFiles : pkg.csharpSources,
    nextKind === 'proto' ? nextFiles : pkg.protoFiles,
    pkg.entryBehaviorTypeName,
    pkg.entrySourcePath,
  );
}

export function addPackageFile(
  pkg: ScriptPackage,
  kind: ScriptPackageFileKind,
  filePath: string,
  content = '',
): ScriptPackage {
  const path = normalizePath(
    filePath,
    kind === 'csharp' ? 'Behavior.cs' : 'schema.proto',
  );
  const nextFiles =
    kind === 'csharp'
      ? [...pkg.csharpSources, { path, content }]
      : [...pkg.protoFiles, { path, content }];

  return createScriptPackage(
    kind === 'csharp' ? nextFiles : pkg.csharpSources,
    kind === 'proto' ? nextFiles : pkg.protoFiles,
    pkg.entryBehaviorTypeName,
    pkg.entrySourcePath || (kind === 'csharp' ? path : ''),
  );
}

export function renamePackageFile(
  pkg: ScriptPackage,
  filePath: string,
  nextFilePath: string,
): ScriptPackage {
  const kind = getPackageFileKind(pkg, filePath);
  if (!kind) {
    return pkg;
  }

  const normalizedNextPath = normalizePath(nextFilePath, filePath);
  const nextCsharpSources =
    kind === 'csharp'
      ? pkg.csharpSources.map((file) =>
          file.path === filePath
            ? { ...file, path: normalizedNextPath }
            : file,
        )
      : pkg.csharpSources;
  const nextProtoFiles =
    kind === 'proto'
      ? pkg.protoFiles.map((file) =>
          file.path === filePath
            ? { ...file, path: normalizedNextPath }
            : file,
        )
      : pkg.protoFiles;

  return createScriptPackage(
    nextCsharpSources,
    nextProtoFiles,
    pkg.entryBehaviorTypeName,
    pkg.entrySourcePath === filePath ? normalizedNextPath : pkg.entrySourcePath,
  );
}

export function removePackageFile(
  pkg: ScriptPackage,
  filePath: string,
): ScriptPackage {
  const nextCsharpSources = pkg.csharpSources.filter(
    (file) => file.path !== filePath,
  );
  const nextProtoFiles = pkg.protoFiles.filter((file) => file.path !== filePath);
  const nextEntrySourcePath = nextCsharpSources.some(
    (file) => file.path === pkg.entrySourcePath,
  )
    ? pkg.entrySourcePath
    : nextCsharpSources[0]?.path || '';

  return createScriptPackage(
    nextCsharpSources,
    nextProtoFiles,
    pkg.entryBehaviorTypeName,
    nextEntrySourcePath,
  );
}

export function setEntrySourcePath(
  pkg: ScriptPackage,
  filePath: string,
): ScriptPackage {
  if (!pkg.csharpSources.some((file) => file.path === filePath)) {
    return pkg;
  }

  return createScriptPackage(
    pkg.csharpSources,
    pkg.protoFiles,
    pkg.entryBehaviorTypeName,
    filePath,
  );
}

export function updateEntryBehaviorTypeName(
  pkg: ScriptPackage,
  entryBehaviorTypeName: string,
): ScriptPackage {
  return createScriptPackage(
    pkg.csharpSources,
    pkg.protoFiles,
    entryBehaviorTypeName,
    pkg.entrySourcePath,
  );
}
