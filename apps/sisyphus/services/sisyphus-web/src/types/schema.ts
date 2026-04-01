export interface SchemaDefinition {
  id: string
  name: string
  description: string
  entityType: 'node' | 'edge'
  nodeType: string
  applicableTypes: string[]
  jsonSchema: Record<string, unknown>
  createdAt: string
  updatedAt: string
}

export interface SchemaListItem {
  id: string
  name: string
  description: string
  entityType: 'node' | 'edge'
  nodeType: string
  applicableTypes: string[]
  jsonSchema?: Record<string, unknown>
  createdAt: string
  updatedAt: string
}

export interface SchemaCreatePayload {
  name: string
  description: string
  entityType: 'node' | 'edge'
  nodeType: string
  applicableTypes: string[]
  jsonSchema: Record<string, unknown>
}

export interface SchemaUpdatePayload {
  name?: string
  description?: string
  entityType?: 'node' | 'edge'
  nodeType?: string
  applicableTypes?: string[]
  jsonSchema?: Record<string, unknown>
}
