import '@testing-library/jest-dom';
import { cleanup } from '@testing-library/react';
import { cleanupTestQueryClients } from './reactQueryTestUtils';

afterEach(() => {
  cleanup();
  cleanupTestQueryClients();
});
