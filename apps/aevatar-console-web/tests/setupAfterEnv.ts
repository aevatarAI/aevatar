import '@testing-library/jest-dom';
import { act, cleanup, configure } from '@testing-library/react';
import { Modal } from 'antd';
import { cleanupTestQueryClients } from './reactQueryTestUtils';

// The default 1s async budget is too tight for loaded CI runners: a query
// invalidation round trip that settles in ~450ms locally can exceed 1s there,
// surfacing as a stale assertion rather than a real defect. A satisfied
// waitFor still resolves immediately, so this only widens the failure budget
// and costs passing tests nothing.
configure({ asyncUtilTimeout: 5000 });

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
