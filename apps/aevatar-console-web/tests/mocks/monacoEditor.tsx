import React from 'react';

type MockEditorHandle = {
  focus: () => void;
  getModel: () => object;
  revealLineInCenter: (_lineNumber: number) => void;
  setSelection: (_selection: unknown) => void;
};

type MockMonacoEditorProps = {
  ariaLabel?: string;
  beforeMount?: (monaco: typeof mockMonaco) => void;
  defaultLanguage?: string;
  defaultValue?: string;
  height?: string | number;
  language?: string;
  onChange?: (value: string | undefined) => void;
  onMount?: (editor: MockEditorHandle, monaco: typeof mockMonaco) => void;
  options?: {
    padding?: {
      bottom?: number;
      top?: number;
    };
  };
  path?: string;
  placeholder?: string;
  value?: string;
};

const mockMonaco = {
  editor: {
    defineTheme: () => undefined,
    setModelMarkers: () => undefined,
    setTheme: () => undefined,
  },
};

export const loader = {
  config: () => undefined,
};

export function useMonaco(): typeof mockMonaco {
  return mockMonaco;
}

const MonacoEditor = React.forwardRef<HTMLTextAreaElement, MockMonacoEditorProps>(
  function MonacoEditor(
    {
      ariaLabel,
      beforeMount,
      defaultLanguage,
      defaultValue,
      height,
      language,
      onChange,
      onMount,
      options,
      path,
      placeholder,
      value,
    },
    ref,
  ) {
    const textareaRef = React.useRef<HTMLTextAreaElement | null>(null);

    React.useImperativeHandle(ref, () => textareaRef.current as HTMLTextAreaElement, []);

    React.useEffect(() => {
      beforeMount?.(mockMonaco);
    }, [beforeMount]);

    React.useEffect(() => {
      if (!textareaRef.current) {
        return;
      }

      const editorHandle: MockEditorHandle = {
        focus: () => textareaRef.current?.focus(),
        getModel: () => ({ path }),
        revealLineInCenter: () => undefined,
        setSelection: () => undefined,
      };

      onMount?.(editorHandle, mockMonaco);
    }, [onMount, path]);

    return (
      <textarea
        aria-label={ariaLabel ?? language ?? defaultLanguage ?? 'Code editor'}
        data-testid="mock-monaco-editor"
        placeholder={placeholder}
        ref={textareaRef}
        style={{
          height: typeof height === 'number' ? `${height}px` : (height ?? '100%'),
          paddingBottom: options?.padding?.bottom,
          paddingTop: options?.padding?.top,
          width: '100%',
        }}
        value={value ?? defaultValue ?? ''}
        onChange={(event) => onChange?.(event.target.value)}
      />
    );
  },
);

export default MonacoEditor;
