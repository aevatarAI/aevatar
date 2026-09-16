export const channelRuntimeConfigFixture = {
  registration_id: 'registration-alpha',
  scope_id: 'scope-alpha',
  platform: 'telegram',
  state_version: 12,
  authorization_mode: 'explicit_service_allowlist',
  service_ids: ['user-service-github'],
  default_skill: { name: 'team-helper', version: '2.1' },
  runtime_config: {
    instructions: 'Keep replies concise.',
    default_skill: { name: 'team-helper', version: '2.1' },
    tool_set_refs: ['approved-tools'],
    extra_tool_names: ['calendar_lookup'],
    nyxid_service_selectors: [
      { service_slug: 'api-github', endpoint_names: ['read_issue'] },
    ],
    credential_source_mode: 'registration_agent_key',
  },
};
