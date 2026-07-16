import type React from 'react';
import type { StudioTeamSummary } from '@/shared/studio/models';

export type TeamDetailTabId = string;

export type TeamDetailTabLabel = {
  readonly defaultMessage: string;
  readonly id: string;
};

export type TeamDetailContext = {
  readonly navigation: {
    readonly buildTabHref: (tabId: TeamDetailTabId) => string;
  };
  readonly refresh: () => Promise<void>;
  readonly scopeId: string;
  readonly teamId: string;
  readonly teamSummary: StudioTeamSummary | null;
};

export type TeamDetailTabViewProps = {
  readonly context: TeamDetailContext;
};

export type TeamDetailTabModule<TViewProps extends object> = {
  readonly default: React.ComponentType<TeamDetailTabViewProps & TViewProps>;
};

export type TeamDetailTabDefinitionInput<
  THostModel,
  TViewProps extends object,
> = {
  readonly id: TeamDetailTabId;
  readonly isAvailable?: (context: TeamDetailContext) => boolean;
  readonly label: TeamDetailTabLabel;
  readonly load: () => Promise<TeamDetailTabModule<TViewProps>>;
  readonly selectHostProps?: (hostModel: THostModel) => TViewProps;
};

export type TeamDetailTabDefinition<THostModel> = {
  readonly id: TeamDetailTabId;
  readonly isAvailable?: (context: TeamDetailContext) => boolean;
  readonly label: TeamDetailTabLabel;
  readonly load: () => Promise<{
    readonly default: React.ComponentType<object>;
  }>;
  readonly selectHostProps?: (hostModel: THostModel) => object;
};

export type TeamDetailTabLookup = {
  readonly defaultTabId: TeamDetailTabId;
  readonly has: (tabId: string) => boolean;
};

export type TeamDetailTabRegistry<THostModel> = TeamDetailTabLookup & {
  readonly definitions: readonly TeamDetailTabDefinition<THostModel>[];
  readonly find: (
    tabId: string,
  ) => TeamDetailTabDefinition<THostModel> | undefined;
  readonly listAvailable: (
    context: TeamDetailContext,
  ) => readonly TeamDetailTabDefinition<THostModel>[];
  readonly resolve: (
    tabId: string,
    context: TeamDetailContext,
  ) => TeamDetailTabDefinition<THostModel>;
};

const teamDetailTabIdPattern = /^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$/;
const maximumTeamDetailTabIdLength = 64;

function normalizeTabId(tabId: string): string {
  return tabId.trim().toLowerCase();
}

function validateDefinition<THostModel>(
  definition: TeamDetailTabDefinition<THostModel>,
): void {
  if (
    definition.id !== normalizeTabId(definition.id) ||
    definition.id.length > maximumTeamDetailTabIdLength ||
    !teamDetailTabIdPattern.test(definition.id)
  ) {
    throw new Error(
      `Invalid Team detail tab id "${definition.id}". Use 1-64 lowercase letters, numbers, or hyphen-separated segments.`,
    );
  }

  if (!definition.label.id.trim() || !definition.label.defaultMessage.trim()) {
    throw new Error(
      `Team detail tab "${definition.id}" must declare a localized label.`,
    );
  }
}

export function defineTeamDetailTab<
  THostModel,
  TViewProps extends object = Record<never, never>,
>(
  definition: TeamDetailTabDefinitionInput<THostModel, TViewProps>,
): TeamDetailTabDefinition<THostModel> {
  return definition as unknown as TeamDetailTabDefinition<THostModel>;
}

export function createTeamDetailTabRegistry<THostModel>(options: {
  readonly defaultTabId: TeamDetailTabId;
  readonly definitions: readonly TeamDetailTabDefinition<THostModel>[];
}): TeamDetailTabRegistry<THostModel> {
  if (options.definitions.length === 0) {
    throw new Error(
      'A Team detail tab registry must contain at least one tab.',
    );
  }

  const definitions = options.definitions.map((definition) => {
    validateDefinition(definition);
    return Object.freeze({
      ...definition,
      label: Object.freeze({ ...definition.label }),
    });
  });
  const definitionsById = new Map<
    string,
    TeamDetailTabDefinition<THostModel>
  >();

  definitions.forEach((definition) => {
    if (definitionsById.has(definition.id)) {
      throw new Error(`Duplicate Team detail tab id "${definition.id}".`);
    }
    definitionsById.set(definition.id, definition);
  });

  const defaultTabId = normalizeTabId(options.defaultTabId);
  const defaultDefinition = definitionsById.get(defaultTabId);
  if (!defaultDefinition) {
    throw new Error(
      `Default Team detail tab "${options.defaultTabId}" is not registered.`,
    );
  }
  if (defaultDefinition.isAvailable) {
    throw new Error(
      `Default Team detail tab "${defaultTabId}" must always be available.`,
    );
  }

  const frozenDefinitions = Object.freeze(definitions);
  const find = (tabId: string) => definitionsById.get(normalizeTabId(tabId));
  const listAvailable = (context: TeamDetailContext) =>
    frozenDefinitions.filter(
      (definition) =>
        !definition.isAvailable || definition.isAvailable(context),
    );

  return Object.freeze({
    defaultTabId,
    definitions: frozenDefinitions,
    find,
    has: (tabId: string) => Boolean(find(tabId)),
    listAvailable,
    resolve: (tabId: string, context: TeamDetailContext) => {
      const definition = find(tabId);
      if (
        definition &&
        (!definition.isAvailable || definition.isAvailable(context))
      ) {
        return definition;
      }
      return defaultDefinition;
    },
  });
}
