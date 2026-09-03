import { fireEvent, render, screen } from '@testing-library/react';
import React from 'react';
import StructuredJsonArgumentsEditor from './StructuredJsonArgumentsEditor';

function renderEditor(initialValue: string) {
  function Harness() {
    const [value, setValue] = React.useState(initialValue);
    const [error, setError] = React.useState('');
    return (
      <>
        <StructuredJsonArgumentsEditor
          disabled={false}
          onChange={setValue}
          onErrorChange={setError}
          value={value}
        />
        <output data-testid="editor-error">{error}</output>
      </>
    );
  }

  render(<Harness />);
}

describe('StructuredJsonArgumentsEditor', () => {
  it('opens in Fields mode and renders nested values with matching controls', () => {
    renderEditor('{"query":{"request":"$input"},"limit":3,"enabled":true}');

    expect(screen.getByRole('radio', { name: 'Fields' })).toBeChecked();
    expect(screen.getAllByText('Property').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Type').length).toBeGreaterThan(0);
    expect(
      screen.getByRole('textbox', { name: 'Value for query.request' }),
    ).toHaveValue('$input');
    expect(
      screen.getByRole('spinbutton', { name: 'Value for limit' }),
    ).toHaveValue('3');
    expect(
      screen.getByRole('switch', { name: 'Value for enabled' }),
    ).toBeChecked();
  });

  it('parses safe numbers when native JSON.parse omits source context', () => {
    const nativeParse = JSON.parse;
    const parseSpy = jest
      .spyOn(JSON, 'parse')
      .mockImplementation((text, reviver) =>
        nativeParse(
          text,
          reviver
            ? function (this: unknown, key: string, parsedValue: unknown) {
                return reviver.call(this, key, parsedValue);
              }
            : undefined,
        ),
      );

    try {
      renderEditor('{"count":2}');

      expect(screen.getByRole('radio', { name: 'Fields' })).toBeChecked();
      expect(
        screen.getByRole('spinbutton', { name: 'Value for count' }),
      ).toHaveValue('2');
    } finally {
      parseSpy.mockRestore();
    }
  });

  it('synchronizes field edits to JSON while preserving the object shape', () => {
    renderEditor('{"query":{"request":"$input"}}');

    fireEvent.change(
      screen.getByRole('textbox', { name: 'Value for query.request' }),
      { target: { value: '$result' } },
    );
    fireEvent.click(screen.getByRole('radio', { name: 'JSON' }));

    expect(
      JSON.parse(
        (
          screen.getByRole('textbox', {
            name: 'Arguments JSON',
          }) as HTMLTextAreaElement
        ).value,
      ),
    ).toEqual({ query: { request: '$result' } });
  });

  it('keeps array items structured instead of falling back to a JSON textarea', () => {
    renderEditor('{"tags":["alpha",false]}');

    expect(
      screen.getByRole('textbox', { name: 'Value for tags[1]' }),
    ).toHaveValue('alpha');
    expect(
      screen.getByRole('switch', { name: 'Value for tags[2]' }),
    ).not.toBeChecked();
    expect(
      screen.getByRole('button', { name: 'Add item to tags' }),
    ).toBeVisible();
  });

  it('adds, types, and removes properties without requiring raw JSON', () => {
    renderEditor('{}');

    fireEvent.click(screen.getByRole('button', { name: 'Add property' }));
    fireEvent.change(
      screen.getByRole('textbox', { name: 'Property name for property' }),
      { target: { value: 'timeout' } },
    );
    fireEvent.mouseDown(
      screen.getByRole('combobox', { name: 'Value type for timeout' }),
    );
    const numberOptions = screen.getAllByText('Number');
    fireEvent.click(numberOptions[numberOptions.length - 1]);
    fireEvent.change(
      screen.getByRole('spinbutton', { name: 'Value for timeout' }),
      { target: { value: '30' } },
    );
    fireEvent.click(screen.getByRole('radio', { name: 'JSON' }));

    expect(
      JSON.parse(
        (
          screen.getByRole('textbox', {
            name: 'Arguments JSON',
          }) as HTMLTextAreaElement
        ).value,
      ),
    ).toEqual({ timeout: 30 });

    fireEvent.click(screen.getByRole('radio', { name: 'Fields' }));
    fireEvent.click(screen.getByRole('button', { name: 'Remove timeout' }));
    fireEvent.click(screen.getByRole('radio', { name: 'JSON' }));
    expect(screen.getByRole('textbox', { name: 'Arguments JSON' })).toHaveValue(
      '{}',
    );
  });

  it('rebuilds fields from valid JSON and preserves invalid JSON for correction', () => {
    renderEditor('{"query":"$input"}');
    fireEvent.click(screen.getByRole('radio', { name: 'JSON' }));

    const jsonInput = screen.getByRole('textbox', { name: 'Arguments JSON' });
    fireEvent.change(jsonInput, {
      target: { value: '{"request":"$result"}' },
    });
    fireEvent.click(screen.getByRole('radio', { name: 'Fields' }));
    expect(
      screen.getByRole('textbox', { name: 'Value for request' }),
    ).toHaveValue('$result');

    fireEvent.click(screen.getByRole('radio', { name: 'JSON' }));
    fireEvent.change(screen.getByRole('textbox', { name: 'Arguments JSON' }), {
      target: { value: '{"request":' },
    });
    fireEvent.click(screen.getByRole('radio', { name: 'Fields' }));

    expect(screen.getByRole('radio', { name: 'JSON' })).toBeChecked();
    expect(screen.getByRole('textbox', { name: 'Arguments JSON' })).toHaveValue(
      '{"request":',
    );
    expect(screen.getByRole('alert')).toHaveTextContent(
      'Enter a valid JSON object before switching to Fields.',
    );
  });

  it('treats a child emission as a one-shot echo before later external updates', () => {
    const onChange = jest.fn();
    const onErrorChange = jest.fn();
    const props = { disabled: false, onChange, onErrorChange };
    const { rerender } = render(
      <StructuredJsonArgumentsEditor {...props} value='{"initial":"A"}' />,
    );

    fireEvent.click(screen.getByRole('radio', { name: 'JSON' }));
    const invalidValue = '{"request":';
    fireEvent.change(screen.getByRole('textbox', { name: 'Arguments JSON' }), {
      target: { value: invalidValue },
    });

    rerender(<StructuredJsonArgumentsEditor {...props} value={invalidValue} />);
    rerender(
      <StructuredJsonArgumentsEditor {...props} value='{"external":"C"}' />,
    );
    rerender(<StructuredJsonArgumentsEditor {...props} value={invalidValue} />);

    expect(screen.getByRole('textbox', { name: 'Arguments JSON' })).toHaveValue(
      invalidValue,
    );
    expect(screen.getByRole('alert')).toHaveTextContent(
      'Enter a valid JSON object before switching to Fields.',
    );
    expect(onErrorChange).toHaveBeenLastCalledWith(
      'Enter a valid JSON object before switching to Fields.',
    );
  });

  it('rejects duplicate property names without discarding either root value', () => {
    renderEditor('{"name":"Ada","count":2}');

    fireEvent.change(
      screen.getByRole('textbox', { name: 'Property name for count' }),
      { target: { value: 'name' } },
    );

    expect(
      screen.getByRole('textbox', { name: 'Property name for count' }),
    ).toHaveValue('count');
    expect(screen.getByRole('alert')).toHaveTextContent(
      'Property names must be unique. "name" already exists in this object.',
    );
    expect(screen.getByTestId('editor-error')).toHaveTextContent(
      'Property names must be unique.',
    );

    fireEvent.change(
      screen.getByRole('textbox', { name: 'Property name for count' }),
      { target: { value: 'total' } },
    );
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('rejects duplicate property names within the same nested object', () => {
    renderEditor('{"query":{"request":"$input","format":"json"}}');

    fireEvent.change(
      screen.getByRole('textbox', { name: 'Property name for query.format' }),
      { target: { value: 'request' } },
    );

    expect(
      screen.getByRole('textbox', { name: 'Property name for query.format' }),
    ).toHaveValue('format');
    expect(screen.getByRole('alert')).toHaveTextContent(
      'Property names must be unique. "request" already exists in this object.',
    );
  });

  it('keeps unsafe integer JSON exact and prevents switching to Fields', () => {
    const unsafeJson = '{"id":9007199254740993,"label":"old"}';
    renderEditor(unsafeJson);

    expect(screen.getByRole('radio', { name: 'JSON' })).toBeChecked();
    expect(screen.getByRole('textbox', { name: 'Arguments JSON' })).toHaveValue(
      unsafeJson,
    );
    expect(screen.getByRole('alert')).toHaveTextContent(
      'This JSON contains a number that Fields mode cannot represent safely. Edit it in JSON or use a string.',
    );

    fireEvent.click(screen.getByRole('radio', { name: 'Fields' }));

    expect(screen.getByRole('radio', { name: 'JSON' })).toBeChecked();
    expect(screen.getByRole('textbox', { name: 'Arguments JSON' })).toHaveValue(
      unsafeJson,
    );
  });

  it('keeps overflowing exponent notation in JSON mode', () => {
    const overflowingJson = '{"amount":1e400}';
    renderEditor(overflowingJson);

    expect(screen.getByRole('radio', { name: 'JSON' })).toBeChecked();
    expect(screen.getByRole('textbox', { name: 'Arguments JSON' })).toHaveValue(
      overflowingJson,
    );
    expect(screen.getByRole('alert')).toHaveTextContent(
      'This JSON contains a number that Fields mode cannot represent safely.',
    );
  });

  it.each([
    ['underflowing exponent', '{"amount":1e-400}'],
    ['high-precision decimal', '{"amount":0.1234567890123456789}'],
  ])('keeps a lossy %s exact in JSON mode', (_label, lossyJson) => {
    renderEditor(lossyJson);

    expect(screen.getByRole('radio', { name: 'JSON' })).toBeChecked();
    expect(screen.getByRole('textbox', { name: 'Arguments JSON' })).toHaveValue(
      lossyJson,
    );
    expect(screen.getByRole('alert')).toHaveTextContent(
      'This JSON contains a number that Fields mode cannot represent safely.',
    );
  });

  it.each([
    ['decimal zero', '{"amount":0.0}'],
    ['exponent zero', '{"amount":0e5}'],
  ])('opens a safely representable %s in Fields mode', (_label, safeJson) => {
    renderEditor(safeJson);

    expect(screen.getByRole('radio', { name: 'Fields' })).toBeChecked();
    expect(
      screen.getByRole('spinbutton', { name: 'Value for amount' }),
    ).toHaveValue('0');
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('blocks unsafe integers entered through a number field', () => {
    renderEditor('{"id":1}');

    fireEvent.change(screen.getByRole('spinbutton', { name: 'Value for id' }), {
      target: { value: '9007199254740993' },
    });

    expect(screen.getByRole('alert')).toHaveTextContent(
      'This JSON contains a number that Fields mode cannot represent safely. Edit it in JSON or use a string.',
    );
    expect(screen.getByTestId('editor-error')).toHaveTextContent(
      'cannot represent safely',
    );
  });

  it('blocks non-finite exponent values entered through a number field', () => {
    renderEditor('{"amount":1}');

    fireEvent.change(
      screen.getByRole('spinbutton', { name: 'Value for amount' }),
      { target: { value: '1e400' } },
    );

    expect(screen.getByRole('alert')).toHaveTextContent(
      'This JSON contains a number that Fields mode cannot represent safely.',
    );
    expect(screen.getByTestId('editor-error')).toHaveTextContent(
      'cannot represent safely',
    );
  });

  it('accepts a safely representable zero entered through a number field', () => {
    renderEditor('{"amount":1}');

    fireEvent.input(
      screen.getByRole('spinbutton', { name: 'Value for amount' }),
      { target: { value: '0.0' } },
    );

    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.getByTestId('editor-error')).toBeEmptyDOMElement();
  });

  it('associates JSON errors with the textarea', () => {
    renderEditor('{"query":"$input"}');
    fireEvent.click(screen.getByRole('radio', { name: 'JSON' }));
    const jsonInput = screen.getByRole('textbox', { name: 'Arguments JSON' });

    fireEvent.change(jsonInput, { target: { value: '{"query":' } });

    const error = screen.getByRole('alert');
    expect(jsonInput).toHaveAttribute('aria-invalid', 'true');
    expect(jsonInput).toHaveAttribute('aria-describedby', error.id);
  });

  it('gives nested add actions contextual accessible names', () => {
    renderEditor('{"query":{},"tags":[]}');

    expect(
      screen.getByRole('button', { name: 'Add property to query' }),
    ).toBeVisible();
    expect(
      screen.getByRole('button', { name: 'Add item to tags' }),
    ).toBeVisible();
    expect(screen.getByRole('button', { name: 'Add property' })).toBeVisible();
  });
});
