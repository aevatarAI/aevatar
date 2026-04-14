import '@testing-library/jest-dom';
import { act, cleanup } from '@testing-library/react';
import { Modal } from 'antd';
import { cleanupTestQueryClients } from './reactQueryTestUtils';

afterEach(() => {
  cleanup();
  act(() => {
    Modal.destroyAll();
  });
  cleanupTestQueryClients();
  jest.restoreAllMocks();
  jest.clearAllMocks();
  window.localStorage.clear();
  window.sessionStorage.clear();
  window.history.replaceState({}, '', '/');
  document.body.className = '';
  document.body.removeAttribute('style');
});
