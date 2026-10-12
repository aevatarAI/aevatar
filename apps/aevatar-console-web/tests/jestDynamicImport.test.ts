import { runInNewContext } from 'node:vm';
import type { Config } from '@umijs/max/test';
import config from '../jest.config';

type TransformerModule = {
  default: {
    createTransformer: (options: unknown) => {
      process: (
        source: string,
        file: string,
        options: { config: Config.InitialOptions; configString: string },
      ) => { code: string };
    };
  };
};

it('loads deferred modules asynchronously through CommonJS in both Jest projects', async () => {
  for (const project of config.projects as Config.InitialOptions[]) {
    const transform = project.transform?.['^.+\\.(t|j)sx?$'];
    if (!Array.isArray(transform)) {
      throw new Error('Expected the configured Umi source transformer.');
    }
    const [transformPath, transformOptions] = transform;
    const transformerModule = require(transformPath) as TransformerModule;
    const transformer =
      transformerModule.default.createTransformer(transformOptions);
    const { code } = transformer.process(
      'module.exports = () => import("./deferred-panel");',
      `${project.rootDir}/tests/deferred-panel-loader.ts`,
      {
        config: { ...project, globals: project.globals ?? {} },
        configString: JSON.stringify(project),
      },
    );
    const panel = { __esModule: true, default: 'Panel', named: 'panel action' };
    const loadModule = jest.fn(() => panel);
    const module = { exports: () => Promise.resolve(panel) };
    runInNewContext(code, { module, exports: module.exports, require: loadModule });

    expect(loadModule).not.toHaveBeenCalled();
    const pending = module.exports();
    expect(loadModule).not.toHaveBeenCalled();
    await expect(pending).resolves.toMatchObject(panel);
    expect(loadModule).toHaveBeenCalledWith('./deferred-panel');

    const unavailable = new Error('Chunk unavailable');
    loadModule.mockImplementationOnce(() => {
      throw unavailable;
    });
    await expect(module.exports()).rejects.toBe(unavailable);
  }
});
