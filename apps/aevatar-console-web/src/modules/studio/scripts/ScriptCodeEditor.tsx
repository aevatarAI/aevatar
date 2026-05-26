import Editor, {
  loader,
  type BeforeMount,
  type OnMount,
} from '@monaco-editor/react';
import React from 'react';
import 'monaco-editor/esm/vs/basic-languages/csharp/csharp.contribution';
import type * as MonacoEditorNamespace from 'monaco-editor';

const monacoEditor = require(
  'monaco-editor/esm/vs/editor/editor.api.js',
) as typeof MonacoEditorNamespace;

export type ScriptEditorMarker = {
  startLineNumber: number;
  startColumn: number;
  endLineNumber: number;
  endColumn: number;
  severity: 'error' | 'warning' | 'info';
  message: string;
  code?: string;
  source?: string;
};

export type ScriptEditorFocusTarget = {
  filePath: string;
  startLineNumber: number;
  startColumn: number;
  endLineNumber: number;
  endColumn: number;
  token: string;
};

loader.config({ monaco: monacoEditor });

const editorShellStyle: React.CSSProperties = {
  border: '1px solid var(--ant-color-border-secondary)',
  borderRadius: 16,
  overflow: 'hidden',
  minHeight: 480,
  height: '100%',
};

function toMonacoSeverity(
  severity: ScriptEditorMarker['severity'],
): MonacoEditorNamespace.MarkerSeverity {
  switch (severity) {
    case 'error':
      return monacoEditor.MarkerSeverity.Error;
    case 'warning':
      return monacoEditor.MarkerSeverity.Warning;
    default:
      return monacoEditor.MarkerSeverity.Info;
  }
}

type ScriptCodeEditorProps = {
  value: string;
  filePath: string;
  language: 'csharp' | 'plaintext';
  focusTarget?: ScriptEditorFocusTarget | null;
  markers: ScriptEditorMarker[];
  onChange: (value: string) => void;
};

const ScriptCodeEditor: React.FC<ScriptCodeEditorProps> = ({
  value,
  filePath,
  language,
  focusTarget,
  markers,
  onChange,
}) => {
  const editorRef = React.useRef<MonacoEditorNamespace.editor.IStandaloneCodeEditor | null>(
    null,
  );

  const beforeMount = React.useMemo<BeforeMount>(
    () => (monaco) => {
      monaco.editor.defineTheme('aevatar-scripts', {
        base: 'vs',
        inherit: true,
        colors: {
          'editor.background': '#ffffff',
          'editor.lineHighlightBackground': '#faf7f2',
          'editor.selectionBackground': '#dbeafe',
          'editorCursor.foreground': '#2563eb',
        },
        rules: [],
      });
    },
    [],
  );

  const applyMarkers = React.useCallback(() => {
    const model = editorRef.current?.getModel();
    if (!model) {
      return;
    }

    monacoEditor.editor.setModelMarkers(
      model,
      'aevatar-scripts',
      markers.map((marker) => ({
        startLineNumber: marker.startLineNumber,
        startColumn: marker.startColumn,
        endLineNumber: marker.endLineNumber,
        endColumn: marker.endColumn,
        severity: toMonacoSeverity(marker.severity),
        message: marker.message,
        code: marker.code,
        source: marker.source,
      })),
    );
  }, [markers]);

  const onMount = React.useMemo<OnMount>(
    () => (editor, monaco) => {
      editorRef.current = editor;
      monaco.editor.setTheme('aevatar-scripts');
      applyMarkers();
    },
    [applyMarkers],
  );

  React.useEffect(() => {
    applyMarkers();
  }, [applyMarkers]);

  React.useEffect(() => {
    if (!focusTarget || focusTarget.filePath !== filePath) {
      return;
    }

    const editor = editorRef.current;
    if (!editor) {
      return;
    }

    const selection = {
      startLineNumber: focusTarget.startLineNumber,
      startColumn: focusTarget.startColumn,
      endLineNumber: focusTarget.endLineNumber,
      endColumn: focusTarget.endColumn,
    };

    editor.focus();
    editor.setSelection(selection);
    editor.revealLineInCenter(focusTarget.startLineNumber);
  }, [filePath, focusTarget]);

  React.useEffect(
    () => () => {
      editorRef.current = null;
    },
    [],
  );

  return (
    <div style={editorShellStyle}>
      <Editor
        beforeMount={beforeMount}
        defaultLanguage={language}
        height="100%"
        keepCurrentModel
        language={language}
        onChange={(next) => onChange(next ?? '')}
        onMount={onMount}
        options={{
          automaticLayout: true,
          fontFamily: 'Menlo, Monaco, Consolas, monospace',
          fontLigatures: false,
          fontSize: 13,
          glyphMargin: true,
          minimap: { enabled: false },
          padding: { top: 16, bottom: 16 },
          renderLineHighlight: 'all',
          roundedSelection: true,
          scrollBeyondLastLine: false,
          smoothScrolling: true,
          insertSpaces: true,
          tabSize: 4,
        }}
        path={`file:///studio/scripts/${filePath || 'Behavior.cs'}`}
        saveViewState
        theme="aevatar-scripts"
        value={value}
      />
    </div>
  );
};

export default ScriptCodeEditor;
