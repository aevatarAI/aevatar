import {
  AI_ACTIVITY_ROUTE,
  buildAIActivityRunDetailHref,
  parseAIActivityRunDetailPath,
} from './aiRoutes';

describe('AI activity routes', () => {
  it('round-trips opaque run ids without applying an id shape convention', () => {
    const href = buildAIActivityRunDetailHref('run.alpha/branch:7');

    expect(href).toBe('/ai/activity/runs/run.alpha%2Fbranch%3A7');
    expect(parseAIActivityRunDetailPath(href)).toEqual({
      runId: 'run.alpha/branch:7',
    });
  });

  it('fails closed for missing, malformed, or raw multi-segment run paths', () => {
    expect(buildAIActivityRunDetailHref('  ')).toBe(AI_ACTIVITY_ROUTE);
    expect(parseAIActivityRunDetailPath('/ai/activity/runs/')).toBeNull();
    expect(
      parseAIActivityRunDetailPath('/ai/activity/runs/run.alpha/extra'),
    ).toBeNull();
    expect(
      parseAIActivityRunDetailPath('/ai/activity/runs/%E0%A4%A'),
    ).toBeNull();
  });
});
