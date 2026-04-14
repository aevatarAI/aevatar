import {
  buildWorkflowCatalogOptions,
  findWorkflowCatalogItem,
  listVisibleWorkflowCatalogItems,
  resolveWorkflowCatalogSelection,
} from './catalogVisibility';

const catalog = [
  {
    name: 'visible_flow',
    description: 'Visible workflow',
    category: 'runtime',
    group: 'library',
    groupLabel: 'Library',
    sortOrder: 10,
    source: 'repo',
    sourceLabel: 'Repo',
    showInLibrary: true,
    isPrimitiveExample: false,
    requiresLlmProvider: false,
    primitives: [],
  },
  {
    name: 'hidden_example',
    description: 'Hidden workflow',
    category: 'examples',
    group: 'primitive-examples',
    groupLabel: 'Primitive Mini Examples',
    sortOrder: 20,
    source: 'repo',
    sourceLabel: 'Mini',
    showInLibrary: false,
    isPrimitiveExample: true,
    requiresLlmProvider: false,
    primitives: ['assign'],
  },
];

describe('catalogVisibility', () => {
  it('filters hidden workflows from library-facing lists', () => {
    expect(listVisibleWorkflowCatalogItems(catalog).map((item) => item.name)).toEqual([
      'visible_flow',
    ]);
  });

  it('preserves an explicit hidden selection when it still exists in the catalog', () => {
    expect(resolveWorkflowCatalogSelection(catalog, 'hidden_example')).toBe(
      'hidden_example',
    );
    expect(findWorkflowCatalogItem(catalog, 'hidden_example')?.showInLibrary).toBe(
      false,
    );
  });

  it('adds a temporary hidden option for a selected hidden workflow', () => {
    expect(buildWorkflowCatalogOptions(catalog, 'hidden_example')).toEqual([
      {
        label: 'hidden_example · Primitive Mini Examples · Hidden from library',
        value: 'hidden_example',
      },
      {
        label: 'visible_flow · Library',
        value: 'visible_flow',
      },
    ]);
  });

  it('adds a temporary unavailable option when the selected workflow is missing from the catalog', () => {
    expect(buildWorkflowCatalogOptions(catalog, 'direct')).toEqual([
      {
        label: 'direct · Unavailable in catalog',
        value: 'direct',
      },
      {
        label: 'visible_flow · Library',
        value: 'visible_flow',
      },
    ]);
  });
});
