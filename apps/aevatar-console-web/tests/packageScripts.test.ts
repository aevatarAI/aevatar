import fs from 'node:fs';
import path from 'node:path';

describe('package scripts', () => {
  it('runs jsdom UI tests serially to keep heavy Studio suites stable', () => {
    const packageJsonPath = path.join(__dirname, '..', 'package.json');
    const packageJson = JSON.parse(fs.readFileSync(packageJsonPath, 'utf8')) as {
      scripts?: Record<string, string>;
    };

    expect(packageJson.scripts?.['test:ui']).toBe(
      'jest --selectProjects jsdom --runInBand',
    );
  });
});
