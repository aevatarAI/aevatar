import { StudioApiError, studioApi } from './api';
import {
  StudioMemberDeletionNotConfirmedError,
  waitForStudioMemberDeletion,
} from './memberDeletion';

describe('waitForStudioMemberDeletion', () => {
  afterEach(() => {
    jest.restoreAllMocks();
  });

  it('waits until member GET returns typed member-not-found', async () => {
    const getMember = jest
      .spyOn(studioApi, 'getMember')
      .mockResolvedValueOnce({} as never)
      .mockRejectedValueOnce(
        new StudioApiError('Member not found', 404, 'STUDIO_MEMBER_NOT_FOUND'),
      );

    await expect(
      waitForStudioMemberDeletion({
        scopeId: 'scope-alpha',
        memberId: 'm-alpha',
      }),
    ).resolves.toBeUndefined();
    expect(getMember).toHaveBeenCalledTimes(2);
  });

  it('surfaces an unrelated 404', async () => {
    jest
      .spyOn(studioApi, 'getMember')
      .mockRejectedValue(
        new StudioApiError('Route not found', 404, 'ROUTE_NOT_FOUND'),
      );

    await expect(
      waitForStudioMemberDeletion({
        scopeId: 'scope-alpha',
        memberId: 'm-alpha',
      }),
    ).rejects.toMatchObject({
      code: 'ROUTE_NOT_FOUND',
      message: 'Route not found',
      status: 404,
    });
  });

  it('reports an unconfirmed deletion after bounded observation attempts', async () => {
    const getMember = jest
      .spyOn(studioApi, 'getMember')
      .mockResolvedValue({} as never);

    await expect(
      waitForStudioMemberDeletion({
        scopeId: 'scope-alpha',
        memberId: 'm-alpha',
      }),
    ).rejects.toBeInstanceOf(StudioMemberDeletionNotConfirmedError);
    expect(getMember).toHaveBeenCalledTimes(5);
  });
});
