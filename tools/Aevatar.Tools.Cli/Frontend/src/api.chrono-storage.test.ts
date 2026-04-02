import { describe, expect, it } from 'vitest';
import { getChronoStorageServiceErrorMessage, isChronoStorageServiceError } from './api';

describe('chrono-storage API helpers', () => {
  it('detects chrono-storage errors by structured code', () => {
    expect(isChronoStorageServiceError({
      code: 'chrono_storage_service_unavailable',
      dependency: 'chrono-storage-service',
    })).toBe(true);
  });

  it('returns the backend message when present', () => {
    expect(getChronoStorageServiceErrorMessage({
      message: 'Studio could not access chrono-storage-service because NyxID requires approval.',
    })).toBe('Studio could not access chrono-storage-service because NyxID requires approval.');
  });

  it('falls back to a clear chrono-storage hint', () => {
    expect(getChronoStorageServiceErrorMessage({})).toContain('chrono-storage-service');
    expect(getChronoStorageServiceErrorMessage({})).toContain('approval');
  });
});
