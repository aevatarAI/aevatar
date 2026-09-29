import { render, screen, waitFor } from '@testing-library/react';
import * as React from 'react';
import ConsoleOperationNotice from './ConsoleOperationNotice';

const mockConsoleToast = {
  error: jest.fn(),
  info: jest.fn(),
  success: jest.fn(),
  warning: jest.fn(),
};

jest.mock('./ConsoleToast', () => ({
  useConsoleToast: () => mockConsoleToast,
}));

describe('ConsoleOperationNotice', () => {
  beforeEach(() => {
    jest.clearAllMocks();
  });

  it('routes errors to a safe toast without rendering the raw notice', async () => {
    const onClose = jest.fn();
    render(
      <ConsoleOperationNotice
        errorMessage="Action could not be completed. Try again."
        notice={{
          message: 'POST /api/action returned 500',
          type: 'error',
        }}
        onClose={onClose}
      />,
    );

    await waitFor(() =>
      expect(mockConsoleToast.error).toHaveBeenCalledWith(
        'Action could not be completed. Try again.',
      ),
    );
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(
      screen.queryByText('POST /api/action returned 500'),
    ).not.toBeInTheDocument();
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('keeps non-error operation notices visible', () => {
    render(
      <ConsoleOperationNotice
        errorMessage="Action could not be completed. Try again."
        notice={{ message: 'Action accepted', type: 'success' }}
      />,
    );

    expect(screen.getByRole('alert')).toHaveTextContent('Action accepted');
    expect(mockConsoleToast.error).not.toHaveBeenCalled();
  });
});
