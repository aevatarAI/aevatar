import { act, fireEvent, render, screen } from '@testing-library/react';
import { setLocale } from '@umijs/max';
import React from 'react';
import { ConsoleLocaleGate } from './ConsoleLocaleGate';
import { loadConsoleLocale } from './loadConsoleLocale';

// Model the catalog-download boundary; retain the real gate and recovery UI.
jest.mock('./loadConsoleLocale', () => ({ loadConsoleLocale: jest.fn() }));
const mockedLoad = jest.mocked(loadConsoleLocale);

describe('ConsoleLocaleGate', () => {
  beforeEach(() => {
    mockedLoad.mockReset();
    setLocale('zh-CN', false);
  });

  it('keeps pages unmounted until their language loads, and recovers from a failed download', async () => {
    let fail!: (error: Error) => void;
    let finish!: () => void;
    const firstDownload = new Promise<void>((_resolve, reject) => {
      fail = reject;
    });
    const retryDownload = new Promise<void>((resolve) => {
      finish = resolve;
    });
    mockedLoad
      .mockReturnValueOnce(firstDownload)
      .mockReturnValueOnce(retryDownload);
    render(
      React.createElement(
        ConsoleLocaleGate,
        null,
        React.createElement('h1', null, 'Workflow editor'),
      ),
    );

    expect(screen.getByRole('status')).toHaveTextContent('加载中…');
    expect(screen.queryByRole('heading')).not.toBeInTheDocument();
    expect(mockedLoad).toHaveBeenCalledWith('zh-CN');

    await act(async () => fail(new Error('Offline')));
    expect(screen.getByRole('alert')).toHaveTextContent(
      '无法加载此语言，请重试。',
    );
    expect(screen.queryByRole('heading')).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: /重\s*试/ }));
    expect(screen.getByRole('status')).toHaveTextContent('加载中…');
    expect(screen.queryByRole('heading')).not.toBeInTheDocument();
    await act(async () => finish());
    expect(
      screen.getByRole('heading', { name: 'Workflow editor' }),
    ).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });
});
