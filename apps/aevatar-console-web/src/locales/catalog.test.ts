import enUSMessages from './en-US';
import zhCNMessages from './zh-CN';
import routes from '../../config/routes';

type ConsoleRoute = {
  name?: string;
  routes?: ConsoleRoute[];
};

function collectRouteNames(routeItems: ConsoleRoute[]): string[] {
  return routeItems.flatMap((route) => [
    ...(route.name ? [route.name] : []),
    ...(route.routes ? collectRouteNames(route.routes) : []),
  ]);
}

function collectPlaceholders(value: string): string[] {
  const names = new Set<string>();

  for (const match of value.matchAll(/\{([A-Za-z_][A-Za-z0-9_]*)/g)) {
    names.add(match[1]);
  }

  return [...names].sort();
}

describe('console locale catalogs', () => {
  it('keeps the English and Chinese message catalogs structurally aligned', () => {
    expect(Object.keys(zhCNMessages).sort()).toEqual(Object.keys(enUSMessages).sort());
  });

  it('keeps the English message catalog free of Chinese copy', () => {
    const allowedChineseKeys = new Set(['common.language.zhCN']);
    const keysWithChineseCopy = Object.entries(enUSMessages)
      .filter(([key, value]) => !allowedChineseKeys.has(key) && /\p{Script=Han}/u.test(value))
      .map(([key]) => key);

    expect(keysWithChineseCopy).toEqual([]);
  });

  it('keeps ICU placeholder names aligned across locales', () => {
    const enUSCatalog: Record<string, string> = enUSMessages;
    const zhCNCatalog: Record<string, string> = zhCNMessages;
    const placeholderMismatches = Object.keys(enUSCatalog)
      .filter(
        (key) =>
          JSON.stringify(collectPlaceholders(enUSCatalog[key])) !==
          JSON.stringify(collectPlaceholders(zhCNCatalog[key])),
      );

    expect(placeholderMismatches).toEqual([]);
  });

  it('keeps every named route backed by menu locale entries', () => {
    const enUSCatalog: Record<string, string> = enUSMessages;
    const zhCNCatalog: Record<string, string> = zhCNMessages;
    const expectedMenuKeys = [...new Set(collectRouteNames(routes))].map(
      (name) => `menu.${name}`,
    );

    expect(
      expectedMenuKeys.filter((key) => enUSCatalog[key] === undefined),
    ).toEqual([]);
    expect(
      expectedMenuKeys.filter((key) => zhCNCatalog[key] === undefined),
    ).toEqual([]);
  });

  it('keeps team member action copy in message catalogs instead of components', () => {
    expect(enUSMessages['teams.members.actions.editInStudio']).toBe('Edit in Studio');
    expect(zhCNMessages['teams.members.actions.editInStudio']).toBe('在 Studio 中编辑');
    expect(enUSMessages['teams.detail.status.buildReady']).toBe('Buildable');
    expect(zhCNMessages['teams.detail.status.buildReady']).toBe('可构建');
  });

  it('keeps Settings selection status and remediation labels localized', () => {
    expect(enUSMessages['pages.settings.index.system.default']).toBe('System default');
    expect(zhCNMessages['pages.settings.index.system.default']).toBe('系统默认值');
    expect(enUSMessages['pages.settings.index.update.submitted']).toBe(
      'Update submitted · {commandId}',
    );
    expect(zhCNMessages['pages.settings.index.update.submitted']).toBe(
      '更新已提交 · {commandId}',
    );
    expect(enUSMessages['pages.settings.index.selection.needs.repair']).toBe(
      'Saved selection needs repair',
    );
    expect(zhCNMessages['pages.settings.index.selection.needs.repair']).toBe(
      '已保存选择需要修复',
    );
    expect(enUSMessages['pages.settings.index.verification.unavailable']).toBe(
      'Verification unavailable',
    );
    expect(zhCNMessages['pages.settings.index.verification.unavailable']).toBe(
      '暂时无法验证',
    );
  });

  it('keeps Chinese engineering and product terms from regressing to literal machine translations', () => {
    const zhCatalogText = Object.values(zhCNMessages).join('\n');
    const bannedMachineTranslations = [
      '跑步',
      '负载夹具',
      '夹具',
      '奥尔良谷物',
      '输入网址',
      '种子夹具',
      '短暂的',
      '入会成员',
      '迅速的',
      '国家版',
      '国家版本',
      '原始原木',
      '手柄阻挡器',
      '活动码头',
      '促销',
      '进化论',
      '台阶式',
      '证实',
      '包裹',
      '帆布',
      '转速',
      '节省',
      '榜样',
      'GAAgent',
      '公开赛',
      '味精',
      '居住',
      '法学硕士',
      '图书馆',
      '儿童步骤',
      '装车',
      '退休了',
      '被拯救',
      '请求类型网址',
      '响应类型 url',
      '选秀运行',
      '工作室编辑',
      '录音室',
      '高级套餐',
      '干净的',
      '条目行为',
      '进入行为',
      '剧本推广',
      '模板·种子',
      'ActorID',
      '脚本ID',
      '运行ID',
      '最后活动',
      '全球工具',
      '工作空间',
      '草案',
      '参与者',
      '有效负载',
      '负载类型 URL',
      '运行时间',
      '提供商',
      '提供者',
      '身份验证',
      '修订版',
      '人门',
      '人性化回放',
      '高级原体',
      '先进的原始方法',
      '行动背景',
      '下一步行动',
      '需要采取行动',
      '项目范围',
      '团队范围',
      '运行时适合',
      '运行时间限制',
      '步骤总结',
      '步枝',
      '长寿命',
      '上证所',
      '思维',
      '空谈',
      '残疾人',
      '技术领域',
      '烟雾测试',
      '流媒体',
      '成员库存',
      '当前 member',
      '当前member',
      '这个 team',
      '高级编辑',
      '安慰',
      '团队流程',
      '测试问题',
      '边桌',
      '目标 endpoint',
      'endpoint catalog',
      'endpoint 暴露',
      '作用域内绑定',
      'scoped endpoint',
      'draft-run endpoint',
      '最近scoped run',
      'prompt 或载荷',
      '脚本演练事实',
      '分类意图，检测语言',
      'GAgent mode',
      'service / Endpoint',
      'transcript',
      'source editor',
      '当前 script',
      'script draft',
      'typed source',
      'dry-run 迭代',
      '脚本 dry run',
      '选择 typed GAgent',
      '当前 step type',
      'raw JSON',
      '简历被接受',
      '当前拦截器',
      '拓扑文摘',
      '破坏性的',
      '全部的',
      '原型',
      'dry-运行',
      'Dry-运行',
      'Draft-运行',
      'workflow canvas',
      'step detail',
      'No parameters configured',
      'Select role',
      'PROMPT INSTRUCTION',
      'Instruction added before',
      'Run the current draft',
      '建设模式',
      '构建行动',
      '草稿准备好了',
      '画布·现场',
      'Build surface',
      'published contract',
      'authoring 和 dry-run',
      '折叠码头',
      '扩展坞',
      '调整底座',
      '信号Payload',
      '负载 Type URL',
      '高级原始请求体',
      '高级原始方法',
      '高级原始路径',
      '先进的 Payload 和运输',
      '舞台能力姿势',
      '准备推广',
      '基本网址',
      '默认标头',
      '不可触摸',
    ];

    expect(
      bannedMachineTranslations.filter((term) => zhCatalogText.includes(term)),
    ).toEqual([]);
  });
});
