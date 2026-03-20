import { buildPlaygroundRoute, buildYamlBrowserRoute } from './navigation';

describe('playground navigation helpers', () => {
  it('builds yaml browser routes with optional source and workflow', () => {
    expect(buildYamlBrowserRoute()).toBe('/yaml');
    expect(buildYamlBrowserRoute({ workflow: 'direct' })).toBe('/yaml?workflow=direct');
    expect(
      buildYamlBrowserRoute({ workflow: 'direct', source: 'playground' }),
    ).toBe('/yaml?workflow=direct&source=playground');
  });

  it('builds playground routes with template import and prompt', () => {
    expect(buildPlaygroundRoute()).toBe('/playground');
    expect(
      buildPlaygroundRoute({
        template: 'human_input_manual_triage',
        importTemplate: true,
        prompt: 'Review this flow',
      }),
    ).toBe(
      '/playground?template=human_input_manual_triage&import=1&prompt=Review+this+flow',
    );
  });
});
