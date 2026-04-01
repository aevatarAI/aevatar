import type { ConnectorDefinition } from "./models/connector-definition.js";
import type { MainnetConnectorDto, MainnetRoleDto } from "./types.js";
/**
 * Map a Sisyphus ConnectorDefinition to the mainnet ConnectorDefinitionDto format.
 * See: Aevatar.Studio.Application/Studio/Contracts/ConnectorContracts.cs
 */
export declare function mapConnectorToMainnet(connector: ConnectorDefinition): MainnetConnectorDto;
/**
 * Map a workflow role (with resolved skill content) to the mainnet RoleDefinitionDto format.
 * See: Aevatar.Studio.Application/Studio/Contracts/RoleContracts.cs
 */
export declare function mapRoleToMainnet(role: {
    name: string;
    description?: string;
}, systemPrompt: string, connectorNames: string[]): MainnetRoleDto;
//# sourceMappingURL=dto-mappers.d.ts.map