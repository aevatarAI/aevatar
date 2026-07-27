import { isStudioApiErrorCode, studioApi } from './api';

const memberDeletionConfirmationAttempts = 5;
const memberDeletionConfirmationDelayMs = 500;

export const studioMemberNotFoundCode = 'STUDIO_MEMBER_NOT_FOUND';

export class StudioMemberDeletionNotConfirmedError extends Error {
  constructor() {
    super('Studio member deletion was accepted but not confirmed.');
    this.name = 'StudioMemberDeletionNotConfirmedError';
  }
}

export function isStudioMemberNotFound(error: unknown): boolean {
  return isStudioApiErrorCode(error, 404, studioMemberNotFoundCode);
}

function waitForMemberDeletionConfirmationTick(): Promise<void> {
  const testEnvironment =
    typeof process !== 'undefined' && process.env.NODE_ENV === 'test';
  return new Promise((resolve) => {
    globalThis.setTimeout(
      resolve,
      testEnvironment ? 0 : memberDeletionConfirmationDelayMs,
    );
  });
}

export async function waitForStudioMemberDeletion(input: {
  readonly memberId: string;
  readonly scopeId: string;
}): Promise<void> {
  for (
    let attempt = 0;
    attempt < memberDeletionConfirmationAttempts;
    attempt += 1
  ) {
    try {
      await studioApi.getMember(input.scopeId, input.memberId);
    } catch (error) {
      if (isStudioMemberNotFound(error)) {
        return;
      }
      throw error;
    }

    if (attempt < memberDeletionConfirmationAttempts - 1) {
      await waitForMemberDeletionConfirmationTick();
    }
  }

  throw new StudioMemberDeletionNotConfirmedError();
}
