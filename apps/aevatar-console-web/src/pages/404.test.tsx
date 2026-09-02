import { fireEvent, render, screen } from '@testing-library/react';
import React from 'react';
import { CONSOLE_HOME_ROUTE } from '@/shared/navigation/consoleHome';
import { history } from '@/shared/navigation/history';
import NoFoundPage from './404';

describe('NoFoundPage', () => {
  it('returns to console home through SPA navigation', () => {
    window.history.replaceState({}, '', '/missing-page');
    const pushSpy = jest.spyOn(history, 'push');

    render(<NoFoundPage />);

    fireEvent.click(screen.getByRole('button', { name: /return to projects/i }));

    expect(pushSpy).toHaveBeenCalledWith(CONSOLE_HOME_ROUTE);
    expect(window.location.pathname).toBe(CONSOLE_HOME_ROUTE);
  });
});
