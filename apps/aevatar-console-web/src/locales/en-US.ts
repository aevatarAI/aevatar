import projectMessages from './projectMessages.en-US';

const enUSMessages = {
  ...projectMessages,
  'common.appName': 'Aevatar',
  'common.language.english': 'English',
  'common.language.label': 'Language',
  'common.language.switch': 'Switch language',
  'common.language.zhCN': '中文',
  'common.user.logout': 'Logout',
  'common.user.settings': 'Settings',
  'menu.Connectors': 'Connectors',
  'menu.Create Team': 'Create Team',
  'menu.Deployments': 'Deployments',
  'menu.Event Stream': 'Event Stream',
  'menu.Files': 'Files',
  'menu.Governance': 'Governance',
  'menu.Members': 'Members',
  'menu.Mission Control': 'Mission Control',
  'menu.My Teams': 'My Teams',
  'menu.Services': 'Services',
  'menu.Settings': 'Settings',
  'menu.Team Details': 'Team Details',
  'menu.Team Member Invoke': 'Team Member Invoke',
  'menu.Team Member Workflow Studio': 'Team Member Workflow Studio',
  'menu.Topology': 'Topology',
  'nav.groups.platform': 'Platform',
  'nav.groups.settings': 'Settings',
  'nav.groups.teams': 'Teams',
  'nav.items.deployments': 'Deployments',
  'nav.items.eventStream': 'Event Stream',
  'nav.items.governance': 'Governance',
  'nav.items.myTeams': 'My Teams',
  'nav.items.services': 'Services',
  'nav.items.settings': 'Settings',
  'nav.items.topology': 'Topology',
  'teams.home.actions.createTeam': 'Create team',
  'teams.home.actions.createMember': 'Create member',
  'teams.home.actions.createWorkflowMember': 'Create workflow member',
  'teams.home.actions.debugEntryWorkflow': 'Debug entry workflow',
  'teams.home.actions.debugWorkflow': 'Debug workflow',
  'teams.home.actions.editEntryMember': 'Edit entry member',
  'teams.home.actions.editMember': 'Edit member',
  'teams.home.actions.manageMembers': 'Manage members',
  'teams.home.actions.viewMembers': 'View members',
  'teams.home.actions.viewTeam': 'View team',
  'teams.home.alerts.authFailedTitle': 'Login verification failed',
  'teams.home.alerts.authUnavailableDescription':
    'Login status is temporarily unavailable. Refresh and try again.',
  'teams.home.alerts.localAuthFallbackDescription':
    '{issue} Continuing with the local login session.',
  'teams.home.alerts.localAuthFallbackTitle':
    'Login verification failed. Using local login session',
  'teams.home.alerts.membersUnavailable':
    'The member roster for this workspace is temporarily unavailable.',
  'teams.home.alerts.noScope':
    'No usable team scope could be resolved from the current login state. Refresh and try again.',
  'teams.home.alerts.partialSignals': 'Some team signals are temporarily unavailable',
  'teams.home.alerts.teamsUnavailable':
    'The team roster for this workspace is temporarily unavailable.',
  'teams.home.attention.draft': 'Draft',
  'teams.home.attention.failed': 'Needs action',
  'teams.home.attention.healthy': 'Running',
  'teams.home.attention.noBoundService': 'Binding pending',
  'teams.home.attention.noRecentRuns': 'Run pending',
  'teams.home.attention.unknown': 'Needs confirmation',
  'teams.home.attention.waiting': 'Needs attention',
  'teams.home.attentionDetail.memberFailed':
    'The latest member run is in an unhealthy state.',
  'teams.home.attentionDetail.memberHealthy':
    'The latest member run is healthy. Continue to details for more context.',
  'teams.home.attentionDetail.memberNoBoundService':
    'This member is ready to bind, but it does not have a stable callable entry yet.',
  'teams.home.attentionDetail.memberNoRecentRuns':
    'The member is bound to a service, but has no recent run records.',
  'teams.home.attentionDetail.memberStage': 'This member is still in {stage}.',
  'teams.home.attentionDetail.memberWaiting':
    'The latest member run is waiting for human input or an external signal.',
  'teams.home.attentionDetail.teamArchived':
    'This team is archived. The list keeps only its backend roster fact.',
  'teams.home.attentionDetail.teamNoMembers':
    'This team exists as a backend fact, but no members have been assigned.',
  'teams.home.breadcrumb': 'Aevatar / Teams',
  'teams.home.empty.description':
    'This account has not created any teams yet. Your AI teams will appear here after creation.',
  'teams.home.errors.rosterUnavailable': 'The team list cannot be loaded right now.',
  'teams.home.facts.currentStatus': 'Current status',
  'teams.home.facts.latestUpdate': 'Latest update',
  'teams.home.facts.members': 'Members',
  'teams.home.facts.relatedServices': 'Related services',
  'teams.home.facts.services': 'Services',
  'teams.home.facts.status': 'Status',
  'teams.home.facts.teamMembers': 'Team members',
  'teams.home.facts.update': 'Update',
  'teams.home.lifecycle.bindReady': 'Callable',
  'teams.home.lifecycle.buildReady': 'Buildable',
  'teams.home.lifecycle.created': 'Created',
  'teams.home.lifecycle.unknown': 'Unknown status',
  'teams.home.loading.roster': 'Reading team list.',
  'teams.home.member.count': '{count} members',
  'teams.home.member.none': 'No members',
  'teams.home.member.previewWithMore': '{name} and {count} members',
  'teams.home.member.unnamed': 'Unnamed member',
  'teams.home.roster.description':
    'Teams are grouped by members and recent run signals so unhealthy or waiting items surface first.',
  'teams.home.roster.title': 'Team list',
  'teams.home.service.boundPending': 'Bound, pending confirmation',
  'teams.home.service.none': 'No bound service',
  'teams.home.service.unbound': 'Unbound',
  'teams.home.status.bindingPending': 'Binding pending',
  'teams.home.status.draft': 'Draft',
  'teams.home.status.failed': 'Unhealthy',
  'teams.home.status.completed': 'Completed',
  'teams.home.status.needsAttention': 'Needs attention',
  'teams.home.status.runPending': 'Run pending',
  'teams.home.status.running': 'Running',
  'teams.home.status.stable': 'Stable',
  'teams.home.status.unknown': 'Unknown',
  'teams.home.summary.actionable': 'Teams needing action',
  'teams.home.summary.healthy': 'Recently completed teams',
  'teams.home.summary.total': 'Total AI teams',
  'teams.home.team.unnamed': 'Unnamed team',
  'teams.home.title': 'My AI teams',
  'teams.home.view.cards': 'Card view',
  'teams.home.view.cardsAria': 'Team card view',
  'teams.home.view.compactAria': 'Compact team view',
  'teams.home.view.list': 'List view',
  'teams.home.view.switchToCards': 'Switch to card view',
  'teams.home.view.switchToList': 'Switch to list view',
  'teams.detail.actions.archive': 'Archive team',
  'teams.detail.actions.edit': 'Edit team',
  'teams.detail.actions.more': 'More actions',
  'teams.detail.actions.moreAria': 'Team actions',
  'teams.detail.actions.test': 'Test team',
  'teams.detail.archive.hint.noTeam': 'Select a real team before archiving.',
  'teams.detail.archive.hint.ready': 'The team summary must be loaded before archiving.',
  'teams.detail.archive.modal.content':
    'After archiving, this team is de-emphasized in active member lists, but you can still edit configuration and inspect history.',
  'teams.detail.archive.modal.title': 'Archive this team?',
  'teams.detail.breadcrumb.detail': 'Team detail',
  'teams.detail.breadcrumb.teams': 'Teams',
  'teams.detail.edit.hint.noTeam': 'Select a real team before editing.',
  'teams.detail.edit.hint.ready': 'The team summary must be loaded before editing.',
  'teams.detail.edit.modal.description': 'Team description',
  'teams.detail.edit.modal.descriptionAria': 'Edit team description',
  'teams.detail.edit.modal.help':
    'This updates the team summary. Archived teams can still be edited and maintained.',
  'teams.detail.edit.modal.name': 'Team name',
  'teams.detail.edit.modal.nameAria': 'Edit team name',
  'teams.detail.edit.modal.save': 'Save team',
  'teams.detail.edit.modal.title': 'Edit team',
  'teams.detail.empty.description':
    'This URL only has workspace context and no concrete team identity. Return to the teams list and choose a team.',
  'teams.detail.empty.panel': 'No team selected',
  'teams.detail.empty.title': 'Team detail',
  'teams.detail.empty.subtitle': 'Choose a team from the teams list before opening its detail page.',
  'teams.detail.heading.currentTeam': 'Current team',
  'teams.detail.heading.default': 'Team detail',
  'teams.detail.loading': 'Loading team detail...',
  'teams.detail.messages.archiveFailed': 'Failed to archive team.',
  'teams.detail.messages.archiveSuccess': 'Team archived.',
  'teams.detail.messages.entryClearFailed': 'Team entry update failed.',
  'teams.detail.messages.entryClearSubmitted':
    'Team entry removal submitted. Waiting for sync confirmation.',
  'teams.detail.messages.entrySetFailed': 'Team entry update failed.',
  'teams.detail.messages.entrySetSubmitted':
    'Team entry change submitted. Waiting for sync confirmation.',
  'teams.detail.messages.nameRequired': 'Team name is required.',
  'teams.detail.messages.teamTestEmpty': 'Team returned an empty response.',
  'teams.detail.messages.teamTestStopped': 'Team Test stopped.',
  'teams.detail.messages.updateFailed': 'Failed to update team.',
  'teams.detail.messages.updateSuccess': 'Team updated.',
  'teams.detail.meta.memberCount': '{count, plural, one {# member} other {# members}}',
  'teams.detail.meta.scopeId': 'Workspace',
  'teams.detail.meta.teamId': 'Team',
  'teams.detail.overview.cards.currentRun': 'Latest run',
  'teams.detail.overview.cards.currentMember': 'Current member',
  'teams.detail.overview.cards.currentService': 'Current service',
  'teams.detail.overview.cards.entryMember': 'Entry member',
  'teams.detail.overview.cards.latestUpdate': 'Latest update',
  'teams.detail.overview.composition.empty.description':
    'There are not enough facts to build team composition yet.',
  'teams.detail.overview.composition.empty.title': 'No team composition yet',
  'teams.detail.overview.composition.title': 'Team composition',
  'teams.detail.overview.configuration.bindingCurrentService': 'Routes to {service}',
  'teams.detail.overview.configuration.bindingMode': 'Binding mode',
  'teams.detail.overview.configuration.bindingNoService':
    'No primary service entry has been matched yet.',
  'teams.detail.overview.configuration.primaryService': 'Primary service entry',
  'teams.detail.overview.configuration.title': 'Configuration details',
  'teams.detail.overview.configuration.versionIdentity': 'Version identity',
  'teams.detail.overview.configuration.workflow': 'Team workflow',
  'teams.detail.overview.entry.configuredCaption':
    'Calls to this team route to this member first.',
  'teams.detail.overview.entry.unconfigured': 'Not configured',
  'teams.detail.overview.entry.unconfiguredCaption':
    'Set an entry member before testing or invoking.',
  'teams.detail.overview.fallback.currentExecution': 'Current execution',
  'teams.detail.overview.fallback.noRecentRun': 'No recent run',
  'teams.detail.overview.fallback.primaryService': 'Primary service',
  'teams.detail.overview.fallback.serviceEntry': 'Service entry {serviceId}',
  'teams.detail.overview.fallback.teamWorkflow': 'Team workflow',
  'teams.detail.overview.identity.noService': 'No service is visible yet',
  'teams.detail.overview.identity.noVisibleRun': 'No visible run synced yet',
  'teams.detail.overview.composition.memberDraft': 'Not bound yet.',
  'teams.detail.overview.composition.memberReady':
    'Bound and ready to receive traffic.',
  'teams.detail.overview.configuration.versionAvailable':
    'Current serving version is available.',
  'teams.detail.overview.configuration.versionPending':
    'Serving version is pending.',
  'teams.detail.overview.configuration.workflowLinked': 'Workflow draft is linked.',
  'teams.detail.overview.configuration.workflowPending':
    'Workflow draft is not linked yet.',
  'teams.detail.overview.member.selectedCaption':
    "Selected from this team's members.",
  'teams.detail.overview.run.visibleCaption': 'Latest run is available.',
  'teams.detail.overview.service.boundCaption':
    'Traffic is routed through the bound service.',
  'teams.detail.overview.service.boundFallback': 'Bound service',
  'teams.detail.overview.service.configuredCaption': 'Service routing is configured.',
  'teams.detail.overview.pill.run': 'Run · {value}',
  'teams.detail.overview.pill.runMissing': 'No recent visible run',
  'teams.detail.overview.pill.service': 'Service · {value}',
  'teams.detail.overview.pill.servicePending': 'Service pending',
  'teams.detail.overview.pill.version': 'Version · {value}',
  'teams.detail.overview.pill.versionPending': 'Version pending',
  'teams.detail.overview.status.title': 'Current state',
  'teams.detail.runtimeStatus.completed': 'Completed',
  'teams.detail.runtimeStatus.default': 'Default version',
  'teams.detail.runtimeStatus.draft': 'Draft',
  'teams.detail.runtimeStatus.failed': 'Unhealthy',
  'teams.detail.runtimeStatus.published': 'Published',
  'teams.detail.runtimeStatus.retired': 'Retired',
  'teams.detail.runtimeStatus.running': 'Running',
  'teams.detail.runtimeStatus.waiting': 'Waiting',
  'teams.detail.status.active': 'Active',
  'teams.detail.status.archived': 'Archived',
  'teams.detail.status.bindReady': 'Callable',
  'teams.detail.status.buildReady': 'Buildable',
  'teams.detail.status.created': 'Created',
  'teams.detail.status.kind.actor': 'Actor',
  'teams.detail.status.kind.gagent': 'Agent',
  'teams.detail.status.kind.role': 'Role',
  'teams.detail.status.kind.runtime': 'Runtime',
  'teams.detail.status.kind.script': 'Script',
  'teams.detail.status.kind.service': 'Service',
  'teams.detail.status.kind.unknown': 'Unrecognized',
  'teams.detail.status.kind.workflow': 'Workflow',
  'teams.detail.status.unknown': 'Unknown status',
  'teams.detail.tabList.label': 'Team detail tabs',
  'teams.detail.tabs.automations': 'Automations',
  'teams.detail.tabs.members': 'Team members',
  'teams.detail.tabs.overview': 'Overview',
  'teams.detail.test.actions.retry': 'Retry',
  'teams.detail.test.actions.start': 'Start test',
  'teams.detail.test.actions.stop': 'Stop',
  'teams.detail.test.currentMemberContext':
    'Current page selected {member}, but Team Test still starts through the entry member.',
  'teams.detail.test.disabled.archived': 'Archived teams cannot start a new test.',
  'teams.detail.test.entry.buildFirst': 'Build / Bind first',
  'teams.detail.test.entry.checking.description':
    'You can choose an entry member after the member roster finishes syncing.',
  'teams.detail.test.entry.checking.title': 'Checking entry member',
  'teams.detail.test.entry.empty.description':
    'Create a member first, then finish Build / Bind before testing the team.',
  'teams.detail.test.entry.empty.title': 'This team has no members yet',
  'teams.detail.test.entry.fallback': 'Entry member',
  'teams.detail.test.entry.noReady.description':
    'A member must finish Build / Bind and become callable before testing the team.',
  'teams.detail.test.entry.noReady.title': 'No member is ready as entry',
  'teams.detail.test.entry.noneSelected': 'No entry member selected',
  'teams.detail.test.entry.notInRoster':
    'Entry member is not in the current team roster',
  'teams.detail.test.entry.promptRequiredTitle': 'Enter a test prompt first.',
  'teams.detail.test.entry.rosterUnavailable.description':
    'The team member roster cannot be read right now, so an entry member cannot be selected.',
  'teams.detail.test.entry.rosterUnavailable.title': 'Member roster unavailable',
  'teams.detail.test.entry.setAndTest': 'Set entry and test',
  'teams.detail.test.entrySyncing.action': 'Retry',
  'teams.detail.test.entrySyncing.description':
    'The backend accepted the team entry change, but the read model has not confirmed the new entry member yet. Try testing the team again later.',
  'teams.detail.test.entrySyncing.title': 'Team entry is syncing',
  'teams.detail.test.entry.testable': 'Testable',
  'teams.detail.test.errors.aborted.description':
    'This test was stopped. The current transcript remains on the page.',
  'teams.detail.test.errors.aborted.title': 'Test stopped',
  'teams.detail.test.errors.backendUnsupported.description':
    'The backend has not deployed the team entry-member or team invoke endpoint yet. The frontend keeps the entry configuration and test draft so you can retry when backend support is available.',
  'teams.detail.test.errors.backendUnsupported.title':
    'Team test is not supported by this backend yet',
  'teams.detail.test.errors.conflict.title': 'Team state changed',
  'teams.detail.test.errors.entryMismatch.description':
    'The entry member does not belong to the current team. Choose a member from this team.',
  'teams.detail.test.errors.entryMismatch.title': 'Entry member mismatch',
  'teams.detail.test.errors.entryMissing.description':
    'This team has no entry member yet. Choose a bound member as the entry first.',
  'teams.detail.test.errors.entryMissing.title': 'No entry member configured',
  'teams.detail.test.errors.entryNotFound.description':
    'The current entry member is not visible in this team roster. Choose the entry member again.',
  'teams.detail.test.errors.entryNotFound.title': 'Entry member unavailable',
  'teams.detail.test.errors.entryNotReady.description':
    'The entry member has not finished Build / Bind yet, so it cannot run team tests.',
  'teams.detail.test.errors.entryNotReady.title': 'Entry member is not ready',
  'teams.detail.test.errors.failed': 'Team test failed.',
  'teams.detail.test.errors.invalidEntry.title': 'Invalid entry member',
  'teams.detail.test.errors.network.description':
    'The network request was interrupted. Check login status or retry later.',
  'teams.detail.test.errors.network.title': 'Network request failed',
  'teams.detail.test.errors.permissionDenied.description':
    'The current account cannot modify or test this team.',
  'teams.detail.test.errors.permissionDenied.title': 'Permission denied',
  'teams.detail.test.errors.teamArchived.description':
    'Archived teams cannot start a new test.',
  'teams.detail.test.errors.teamArchived.title': 'Team is archived',
  'teams.detail.test.errors.teamNotFound.description':
    'This team is not visible in the current scope. Return to the teams list and choose it again.',
  'teams.detail.test.errors.teamNotFound.title': 'Team not found',
  'teams.detail.test.history.empty': 'Test results will appear here.',
  'teams.detail.test.history.title': 'Test log',
  'teams.detail.test.history.waiting': 'Waiting for team response...',
  'teams.detail.test.lastResult': 'Last test · {time}',
  'teams.detail.test.modal.title': 'Test team',
  'teams.detail.test.prompt.aria': 'Test prompt',
  'teams.detail.test.prompt.placeholder':
    'Enter the problem this team should handle...',
  'teams.detail.test.service.label': 'Service',
  'teams.detail.test.status.error': 'Failed',
  'teams.detail.test.status.idle': 'Ready',
  'teams.detail.test.status.running': 'Testing',
  'teams.detail.test.status.settingEntry': 'Setting entry',
  'teams.detail.test.status.stopped': 'Stopped',
  'teams.detail.test.status.success': 'Completed',
  'teams.detail.test.archivedHint': 'Archived teams cannot start new tests.',
  'teams.detail.test.subtitle': 'Start a real team invocation through the entry member.',
  'teams.detail.update.empty': 'No visible update time yet',
  'teams.detail.update.fromRun': 'From run {runId}',
  'teams.detail.update.fromTeam': 'From team update time',
  'teams.detail.update.fromVisibleRun': 'From the latest visible run',
  'teams.detail.update.fromWorkflow': 'From workflow update time',
  'shared.studio.nodeConfiguration.assign.target.label': 'Target variable',
  'shared.studio.nodeConfiguration.assign.target.placeholder': 'result',
  'shared.studio.nodeConfiguration.assign.value.label': 'Value',
  'shared.studio.nodeConfiguration.assign.value.placeholder': '$input',
  'shared.studio.nodeConfiguration.cache.childStep.label': 'Cached node',
  'shared.studio.nodeConfiguration.cache.key.label': 'Cache key',
  'shared.studio.nodeConfiguration.cache.key.placeholder': '$input',
  'shared.studio.nodeConfiguration.cache.ttl.label': 'TTL seconds',
  'shared.studio.nodeConfiguration.cache.ttl.placeholder': '600',
  'shared.studio.nodeConfiguration.checkpoint.name.label': 'Checkpoint name',
  'shared.studio.nodeConfiguration.checkpoint.name.placeholder': 'before_publish',
  'shared.studio.nodeConfiguration.conditional.condition.label': 'Condition',
  'shared.studio.nodeConfiguration.conditional.condition.placeholder':
    'eq($input, "ok")',
  'shared.studio.nodeConfiguration.connectorCall.connector.label': 'Connector',
  'shared.studio.nodeConfiguration.connectorCall.connector.placeholder':
    'Configured connector name',
  'shared.studio.nodeConfiguration.connectorCall.method.label': 'Method',
  'shared.studio.nodeConfiguration.connectorCall.method.option.delete': 'DELETE',
  'shared.studio.nodeConfiguration.connectorCall.method.option.get': 'GET',
  'shared.studio.nodeConfiguration.connectorCall.method.option.patch': 'PATCH',
  'shared.studio.nodeConfiguration.connectorCall.method.option.post': 'POST',
  'shared.studio.nodeConfiguration.connectorCall.method.option.put': 'PUT',
  'shared.studio.nodeConfiguration.connectorCall.onError.label': 'On error',
  'shared.studio.nodeConfiguration.connectorCall.operation.label': 'Operation',
  'shared.studio.nodeConfiguration.connectorCall.operation.placeholder':
    'Operation or endpoint name',
  'shared.studio.nodeConfiguration.connectorCall.path.label': 'Path',
  'shared.studio.nodeConfiguration.connectorCall.path.placeholder': '/v1/items',
  'shared.studio.nodeConfiguration.connectorCall.retry.label': 'Retries',
  'shared.studio.nodeConfiguration.connectorCall.retry.placeholder': '0',
  'shared.studio.nodeConfiguration.connectorCall.timeout.label': 'Timeout ms',
  'shared.studio.nodeConfiguration.connectorCall.timeout.placeholder': '10000',
  'shared.studio.nodeConfiguration.delay.duration.label': 'Duration ms',
  'shared.studio.nodeConfiguration.delay.duration.placeholder': '1000',
  'shared.studio.nodeConfiguration.dynamicWorkflow.originalInput.description':
    'Optional input passed into the generated workflow after YAML extraction.',
  'shared.studio.nodeConfiguration.dynamicWorkflow.originalInput.label':
    'Original input',
  'shared.studio.nodeConfiguration.dynamicWorkflow.originalInput.placeholder':
    '$input',
  'shared.studio.nodeConfiguration.emit.eventType.label': 'Event type',
  'shared.studio.nodeConfiguration.emit.eventType.placeholder':
    'workflow.completed',
  'shared.studio.nodeConfiguration.emit.payload.label': 'Payload',
  'shared.studio.nodeConfiguration.emit.payload.placeholder': '$input',
  'shared.studio.nodeConfiguration.evaluate.criteria.label': 'Criteria',
  'shared.studio.nodeConfiguration.evaluate.criteria.placeholder':
    'correctness and clarity',
  'shared.studio.nodeConfiguration.evaluate.onBelow.label':
    'Below threshold branch',
  'shared.studio.nodeConfiguration.evaluate.onBelow.placeholder': 'rewrite',
  'shared.studio.nodeConfiguration.evaluate.scale.label': 'Scale',
  'shared.studio.nodeConfiguration.evaluate.scale.placeholder': '1-5',
  'shared.studio.nodeConfiguration.evaluate.threshold.label': 'Threshold',
  'shared.studio.nodeConfiguration.evaluate.threshold.placeholder': '4',
  'shared.studio.nodeConfiguration.foreach.delimiter.label': 'Delimiter',
  'shared.studio.nodeConfiguration.foreach.delimiter.placeholder': '\\n---\\n',
  'shared.studio.nodeConfiguration.foreach.subStepType.label': 'Item step',
  'shared.studio.nodeConfiguration.foreach.subTargetRole.label':
    'Item target role',
  'shared.studio.nodeConfiguration.foreach.subTargetRole.placeholder':
    'assistant',
  'shared.studio.nodeConfiguration.guard.check.label': 'Check',
  'shared.studio.nodeConfiguration.guard.check.option.contains':
    'Contains keyword',
  'shared.studio.nodeConfiguration.guard.check.option.jsonValid':
    'Input is valid JSON',
  'shared.studio.nodeConfiguration.guard.check.option.maxLength':
    'Within max length',
  'shared.studio.nodeConfiguration.guard.check.option.notEmpty':
    'Input is not empty',
  'shared.studio.nodeConfiguration.guard.check.option.regex': 'Matches regex',
  'shared.studio.nodeConfiguration.guard.onFailure.label': 'On failure',
  'shared.studio.nodeConfiguration.humanApproval.onReject.label': 'On rejection',
  'shared.studio.nodeConfiguration.humanApproval.onReject.option.fail':
    'Fail the run',
  'shared.studio.nodeConfiguration.humanApproval.onReject.option.skip':
    'Skip this step',
  'shared.studio.nodeConfiguration.humanApproval.prompt.label':
    'Approval prompt',
  'shared.studio.nodeConfiguration.humanApproval.prompt.placeholder':
    'Approve this step?',
  'shared.studio.nodeConfiguration.humanInput.prompt.label': 'Input prompt',
  'shared.studio.nodeConfiguration.humanInput.prompt.placeholder':
    'Please provide the missing input.',
  'shared.studio.nodeConfiguration.humanInput.variable.label':
    'Response variable',
  'shared.studio.nodeConfiguration.humanInput.variable.placeholder':
    'human_response',
  'shared.studio.nodeConfiguration.llmCall.instruction.description':
    'Prepended to the run message before the role is called.',
  'shared.studio.nodeConfiguration.llmCall.instruction.label': 'Instruction',
  'shared.studio.nodeConfiguration.llmCall.instruction.placeholder':
    'Tell the role what this step should do.',
  'shared.studio.nodeConfiguration.mapReduce.delimiter.label': 'Delimiter',
  'shared.studio.nodeConfiguration.mapReduce.delimiter.placeholder': '\\n---\\n',
  'shared.studio.nodeConfiguration.mapReduce.mapStepType.label': 'Map step',
  'shared.studio.nodeConfiguration.mapReduce.mapTargetRole.label':
    'Map target role',
  'shared.studio.nodeConfiguration.mapReduce.mapTargetRole.placeholder':
    'mapper',
  'shared.studio.nodeConfiguration.mapReduce.reducePromptPrefix.label':
    'Reduce instruction',
  'shared.studio.nodeConfiguration.mapReduce.reducePromptPrefix.placeholder':
    'Merge these chunk summaries:',
  'shared.studio.nodeConfiguration.mapReduce.reduceStepType.label':
    'Reduce step',
  'shared.studio.nodeConfiguration.mapReduce.reduceTargetRole.label':
    'Reduce target role',
  'shared.studio.nodeConfiguration.mapReduce.reduceTargetRole.placeholder':
    'reducer',
  'shared.studio.nodeConfiguration.option.onFailure.branch': 'Go to a branch',
  'shared.studio.nodeConfiguration.option.onFailure.fail': 'Fail the run',
  'shared.studio.nodeConfiguration.option.onFailure.skip': 'Skip this step',
  'shared.studio.nodeConfiguration.parallel.count.label': 'Parallel count',
  'shared.studio.nodeConfiguration.parallel.count.placeholder': '3',
  'shared.studio.nodeConfiguration.parallel.voteStepType.label': 'Vote step',
  'shared.studio.nodeConfiguration.parallel.workers.label': 'Workers',
  'shared.studio.nodeConfiguration.parallel.workers.placeholder':
    'agent_a,agent_b,agent_c',
  'shared.studio.nodeConfiguration.race.count.label': 'Winner count',
  'shared.studio.nodeConfiguration.race.count.placeholder': '2',
  'shared.studio.nodeConfiguration.race.workers.label': 'Workers',
  'shared.studio.nodeConfiguration.race.workers.placeholder':
    'fast_model,cheap_model',
  'shared.studio.nodeConfiguration.reflect.criteria.label': 'Criteria',
  'shared.studio.nodeConfiguration.reflect.criteria.placeholder':
    'accuracy and conciseness',
  'shared.studio.nodeConfiguration.reflect.maxRounds.label': 'Max rounds',
  'shared.studio.nodeConfiguration.reflect.maxRounds.placeholder': '3',
  'shared.studio.nodeConfiguration.retrieveFacts.query.label': 'Query',
  'shared.studio.nodeConfiguration.retrieveFacts.query.placeholder':
    'What facts should this step retrieve?',
  'shared.studio.nodeConfiguration.retrieveFacts.topK.label': 'Top K',
  'shared.studio.nodeConfiguration.retrieveFacts.topK.placeholder': '3',
  'shared.studio.nodeConfiguration.stepType.option.assign': 'Assign',
  'shared.studio.nodeConfiguration.stepType.option.cache': 'Cache',
  'shared.studio.nodeConfiguration.stepType.option.checkpoint': 'Checkpoint',
  'shared.studio.nodeConfiguration.stepType.option.conditional': 'Conditional',
  'shared.studio.nodeConfiguration.stepType.option.connectorCall':
    'Connector call',
  'shared.studio.nodeConfiguration.stepType.option.delay': 'Delay',
  'shared.studio.nodeConfiguration.stepType.option.dynamicWorkflow':
    'Dynamic workflow',
  'shared.studio.nodeConfiguration.stepType.option.emit': 'Emit',
  'shared.studio.nodeConfiguration.stepType.option.evaluate': 'Evaluate',
  'shared.studio.nodeConfiguration.stepType.option.foreach': 'For each',
  'shared.studio.nodeConfiguration.stepType.option.guard': 'Guard',
  'shared.studio.nodeConfiguration.stepType.option.humanApproval':
    'Human approval',
  'shared.studio.nodeConfiguration.stepType.option.humanInput': 'Human input',
  'shared.studio.nodeConfiguration.stepType.option.llmCall': 'LLM call',
  'shared.studio.nodeConfiguration.stepType.option.mapReduce': 'Map reduce',
  'shared.studio.nodeConfiguration.stepType.option.parallel': 'Parallel',
  'shared.studio.nodeConfiguration.stepType.option.race': 'Race',
  'shared.studio.nodeConfiguration.stepType.option.reflect': 'Reflect',
  'shared.studio.nodeConfiguration.stepType.option.retrieveFacts':
    'Retrieve facts',
  'shared.studio.nodeConfiguration.stepType.option.switch': 'Switch',
  'shared.studio.nodeConfiguration.stepType.option.toolCall': 'Tool call',
  'shared.studio.nodeConfiguration.stepType.option.transform': 'Transform',
  'shared.studio.nodeConfiguration.stepType.option.vote': 'Vote',
  'shared.studio.nodeConfiguration.stepType.option.waitSignal':
    'Wait for signal',
  'shared.studio.nodeConfiguration.stepType.option.while': 'While',
  'shared.studio.nodeConfiguration.stepType.option.workflowCall':
    'Workflow call',
  'shared.studio.nodeConfiguration.stepType.option.workflowYamlValidate':
    'Workflow YAML validation',
  'shared.studio.nodeConfiguration.switch.on.description':
    'Value matched against branch keys such as bug, feature, or _default.',
  'shared.studio.nodeConfiguration.switch.on.label': 'Switch on',
  'shared.studio.nodeConfiguration.switch.on.placeholder': '$input',
  'shared.studio.nodeConfiguration.toolCall.tool.label': 'Tool',
  'shared.studio.nodeConfiguration.toolCall.tool.placeholder': 'web_search',
  'shared.studio.nodeConfiguration.transform.operation.label': 'Operation',
  'shared.studio.nodeConfiguration.transform.operation.option.count':
    'Count lines',
  'shared.studio.nodeConfiguration.transform.operation.option.identity':
    'Pass through',
  'shared.studio.nodeConfiguration.transform.operation.option.join':
    'Join sections',
  'shared.studio.nodeConfiguration.transform.operation.option.jsonExtract':
    'Extract JSON',
  'shared.studio.nodeConfiguration.transform.operation.option.lowercase':
    'Lowercase',
  'shared.studio.nodeConfiguration.transform.operation.option.split':
    'Split into sections',
  'shared.studio.nodeConfiguration.transform.operation.option.take':
    'Take first lines',
  'shared.studio.nodeConfiguration.transform.operation.option.takeLast':
    'Take last lines',
  'shared.studio.nodeConfiguration.transform.operation.option.trim':
    'Trim whitespace',
  'shared.studio.nodeConfiguration.transform.operation.option.uppercase':
    'Uppercase',
  'shared.studio.nodeConfiguration.waitSignal.signalName.label': 'Signal name',
  'shared.studio.nodeConfiguration.waitSignal.signalName.placeholder': 'continue',
  'shared.studio.nodeConfiguration.waitSignal.timeout.label': 'Timeout ms',
  'shared.studio.nodeConfiguration.waitSignal.timeout.placeholder': '60000',
  'shared.studio.nodeConfiguration.while.condition.label': 'Condition',
  'shared.studio.nodeConfiguration.while.condition.placeholder':
    'lt(iteration, 5)',
  'shared.studio.nodeConfiguration.while.maxIterations.label': 'Max iterations',
  'shared.studio.nodeConfiguration.while.maxIterations.placeholder': '5',
  'shared.studio.nodeConfiguration.while.step.label': 'Loop step',
  'shared.studio.nodeConfiguration.workflowCall.lifecycle.label': 'Lifecycle',
  'shared.studio.nodeConfiguration.workflowCall.lifecycle.option.inline':
    'Inline call',
  'shared.studio.nodeConfiguration.workflowCall.lifecycle.option.scope':
    'Use scope workflow',
  'shared.studio.nodeConfiguration.workflowCall.workflow.label': 'Workflow',
  'shared.studio.nodeConfiguration.workflowCall.workflow.placeholder':
    'child_workflow',
  'teams.members.actions.build': 'Build',
  'teams.members.actions.clearEntry': 'Clear entry member',
  'teams.members.actions.create': 'Create member',
  'teams.members.actions.createFirst': 'Create first member',
  'teams.members.actions.createFirstWorkflow': 'Create first workflow member',
  'teams.members.actions.createWorkflowMember': 'Create workflow member',
  'teams.members.actions.editInStudio': 'Edit in Studio',
  'teams.members.actions.automate': 'Automate',
  'teams.members.actions.invokeRequiresBinding':
    'Bind this workflow member before invoking it.',
  'teams.members.actions.invokeWorkflow': 'Invoke',
  'teams.members.actions.setEntry': 'Set as entry member',
  'teams.members.actions.workflowOnly': 'Workflow only',
  'teams.members.actions.workflowOnlyTitle':
    'This console currently supports workflow members only.',
  'teams.members.actions.workflowStudio': 'Workflow Studio',
  'teams.automations.actions.addRecurringWork': 'Add recurring work',
  'teams.automations.actions.create': 'New automation',
  'teams.automations.actions.delete': 'Delete',
  'teams.automations.actions.edit': 'Edit',
  'teams.automations.actions.pause': 'Pause',
  'teams.automations.actions.resume': 'Resume',
  'teams.automations.actions.runNow': 'Run now',
  'teams.automations.createPanel.description':
    'Pick a published member, describe the job, choose a cadence, and preview the next runs before creating it.',
  'teams.automations.createPanel.title': 'Give a member recurring work',
  'teams.automations.description':
    'Recurring work belongs to a member. The team view shows every commitment so operators can see what will run next and what needs attention.',
  'teams.automations.empty.description':
    'Create an automation from a published member so this team has visible recurring commitments.',
  'teams.automations.empty.title': 'No recurring work yet',
  'teams.automations.error.description':
    'Refresh the page or try again after the schedule service is available.',
  'teams.automations.error.title': 'Automations could not load',
  'teams.automations.form.cadence': 'Cadence',
  'teams.automations.form.cadenceAria': 'Automation cadence',
  'teams.automations.form.create': 'Create automation',
  'teams.automations.form.cron': 'Cron expression',
  'teams.automations.form.cronAria': 'Cron expression',
  'teams.automations.form.defaultTitle': '{memberName} recurring work',
  'teams.automations.form.displayName': 'Name',
  'teams.automations.form.displayNameAria': 'Automation name',
  'teams.automations.form.displayNamePlaceholder': 'Daily escalation digest',
  'teams.automations.form.editPromptHint':
    'Re-enter the recurring prompt to save changes.',
  'teams.automations.form.editTitle': 'Edit automation',
  'teams.automations.form.enabled': 'Enabled',
  'teams.automations.form.identityMissing':
    "Waiting for this member's published service identity.",
  'teams.automations.form.identityReady': 'Targets published service {serviceId}.',
  'teams.automations.form.member': 'Member',
  'teams.automations.form.memberAria': 'Automation member',
  'teams.automations.form.preset.custom': 'Custom cron',
  'teams.automations.form.preset.dailyMorning': 'Daily · 09:00',
  'teams.automations.form.preset.hourly': 'Hourly',
  'teams.automations.form.preset.weekdaysMorning': 'Weekdays · 09:00',
  'teams.automations.form.preset.weeklyMonday': 'Monday · 09:00',
  'teams.automations.form.preview': 'Preview next runs',
  'teams.automations.form.previewHint':
    'Preview uses the schedule service before saving.',
  'teams.automations.form.prompt': 'Recurring prompt',
  'teams.automations.form.promptAria': 'Recurring prompt',
  'teams.automations.form.promptPlaceholder':
    'Summarize escalations, blocked accounts, and follow-up owners.',
  'teams.automations.form.save': 'Save changes',
  'teams.automations.form.timezone': 'Timezone',
  'teams.automations.form.timezoneAria': 'Timezone',
  'teams.automations.form.title': 'New member automation',
  'teams.automations.member.publishFirst':
    'Publish this member before adding recurring work.',
  'teams.automations.member.unknown': 'Unknown member',
  'teams.automations.member.workflowOnly':
    'Only workflow members can have recurring work.',
  'teams.automations.messages.createFailed':
    'Automation was not created: {message}',
  'teams.automations.messages.createSuccess': 'Automation created.',
  'teams.automations.messages.cronRequired': 'Enter a cron expression first.',
  'teams.automations.messages.deleteSuccess': 'Automation deleted.',
  'teams.automations.messages.previewFailed': 'Preview failed: {message}',
  'teams.automations.messages.promptRequired':
    'Describe the recurring work before saving it.',
  'teams.automations.messages.runNowFailed': 'Run request failed: {message}',
  'teams.automations.messages.runNowSuccess': 'Run requested.',
  'teams.automations.messages.serviceIdentityLoading':
    'Service identity is still loading.',
  'teams.automations.messages.serviceIdentityMissing':
    'The selected member does not have a service identity yet.',
  'teams.automations.messages.updateFailed':
    'Automation was not updated: {message}',
  'teams.automations.messages.updateSuccess': 'Automation updated.',
  'teams.automations.noPublishedMember.description':
    'Automations need a member with a published service identity before they can run.',
  'teams.automations.noPublishedMember.title': 'Publish a member first',
  'teams.automations.preview.daily.cadence': 'Every weekday · 09:00',
  'teams.automations.preview.daily.member': 'Support Analyst',
  'teams.automations.preview.daily.nextRun': 'Next run today',
  'teams.automations.preview.daily.prompt':
    'Summarize escalations, blocked accounts, and follow-up owners.',
  'teams.automations.preview.daily.title': 'Daily customer escalation digest',
  'teams.automations.preview.runsThroughMember':
    'Runs through the member service',
  'teams.automations.preview.runsThroughService':
    'Runs through {serviceId}',
  'teams.automations.preview.status.active': 'Active',
  'teams.automations.preview.status.attention': 'Needs attention',
  'teams.automations.preview.weekly.cadence': 'Friday · 16:30',
  'teams.automations.preview.weekly.member': 'Release Manager',
  'teams.automations.preview.weekly.nextRun': 'Needs channel permission',
  'teams.automations.preview.weekly.prompt':
    'Prepare release handoff notes and flag deploy risks.',
  'teams.automations.preview.weekly.title': 'Weekly release handoff',
  'teams.automations.previewOnly': 'Automation API wiring is coming next.',
  'teams.automations.row.nextRun': 'Next {time}',
  'teams.automations.row.noNextRun': 'No next run',
  'teams.automations.row.target': 'Workflow chat · {endpoint}',
  'teams.automations.status.active': 'Active',
  'teams.automations.status.error': 'Error',
  'teams.automations.status.paused': 'Paused',
  'teams.automations.title': 'Automations',
  'teams.automations.untitled': 'Untitled automation',
  'teams.automations.unavailable.title': 'Not ready for automation',
  'teams.automations.upcoming.attention.caption':
    'Weekly release handoff needs attention',
  'teams.automations.upcoming.empty': 'No upcoming runs are visible yet.',
  'teams.automations.upcoming.friday': 'Friday · 16:30',
  'teams.automations.upcoming.memberCaption': '{memberName} recurring work',
  'teams.automations.upcoming.scheduled.caption':
    'Scheduled teammate commitment',
  'teams.automations.upcoming.title': 'Upcoming',
  'teams.automations.upcoming.today': 'Today · 09:00',
  'teams.automations.upcoming.tomorrow': 'Tomorrow · 18:00',
  'teams.members.columns.actions': 'Actions',
  'teams.members.columns.implementation': 'Implementation',
  'teams.members.columns.member': 'Member',
  'teams.members.columns.role': 'Role',
  'teams.members.columns.service': 'Service',
  'teams.members.count': '{count, plural, one {# member} other {# members}}',
  'teams.members.description':
    'Review team members, choose the Team entry member, and open workflow members in Studio. Invoke is available only after a workflow member is bound to a published service.',
  'teams.members.empty.description':
    'The team exists as a backend fact, but its current member roster is empty. New members will appear here.',
  'teams.members.empty.title': 'This team has no members yet',
  'teams.members.entry': 'Entry member',
  'teams.members.selected': 'Selected',
  'teams.members.unnamed': 'Untitled member',
  'teams.members.service.bound': 'Bound service',
  'teams.members.service.needsBinding':
    'Bind this member before invoking it.',
  'teams.members.service.notBound': 'Not bound yet',
  'teams.members.service.ready': 'Ready to invoke.',
  'teams.members.loading.description': 'Reading members for this team.',
  'teams.members.loading.title': 'Reading member roster',
  'teams.members.noSelection.description':
    'Choose a team from the list to review its members.',
  'teams.members.noSelection.title': 'No real team selected',
  'teams.members.roster': 'Member roster',
  'teams.members.syncing.description':
    'The team has been created and its member roster is syncing. This view refreshes automatically.',
  'teams.members.syncing.title': 'Member roster is syncing',
  'teams.members.title': 'Team members',
  'teams.members.unavailable.description':
    'The member roster for this team cannot be read right now.',
  'teams.members.unavailable.title': 'Member roster unavailable',
  'pages.teammemberinvoke.back': 'Team members',
  'pages.teammemberinvoke.description':
    'Run the bound published workflow member and keep the runtime observation pinned to this member.',
  'pages.teammemberinvoke.endpoint.missing':
    'No callable endpoint is available.',
  'pages.teammemberinvoke.endpoint.missing.description':
    'The published service has no callable endpoints available to this page.',
  'pages.teammemberinvoke.fact.member': 'Member',
  'pages.teammemberinvoke.fact.revision': 'Serving state',
  'pages.teammemberinvoke.fact.service': 'Service',
  'pages.teammemberinvoke.fact.workflow': 'Implementation',
  'pages.teammemberinvoke.implementation.workflow': 'Workflow',
  'pages.teammemberinvoke.load.failed':
    'Member invoke context could not be loaded.',
  'pages.teammemberinvoke.loading': 'Loading invoke context...',
  'pages.teammemberinvoke.member': 'Member',
  'pages.teammemberinvoke.next.step': 'Next step',
  'pages.teammemberinvoke.open.studio': 'Workflow Studio',
  'pages.teammemberinvoke.resolve.in.studio': 'Open Workflow Studio',
  'pages.teammemberinvoke.route.missing': 'Missing member route',
  'pages.teammemberinvoke.route.missing.description':
    'Open this page from a concrete team member so the invoke target stays stable.',
  'pages.teammemberinvoke.service.pending':
    'Published service is not visible yet.',
  'pages.teammemberinvoke.service.pending.description':
    'The member binding exists, but the service catalog has not exposed its callable endpoints yet.',
  'pages.teammemberinvoke.title': 'Run workflow member',
  'pages.teammemberinvoke.revision.ready': 'Ready',
  'pages.teammemberinvoke.service.bound': 'Bound service',
  'pages.teammemberinvoke.unbound': 'This workflow member is not bound yet.',
  'pages.teammemberinvoke.unbound.description':
    'Bind this workflow member first so it has a published callable service and endpoint contract.',
  'pages.teammemberinvoke.workflow.only':
    'Invoke is available for workflow members only.',
  'pages.teammemberinvoke.workflow.only.description':
    "This page only runs workflow members. Use the member's own surface for other implementation kinds.",
  'teamMemberWorkflowStudio.alerts.linkedWorkflowMissing.description':
    'You can build or paste the workflow here. Saving creates a reusable workflow draft until the member link is materialized.',
  'teamMemberWorkflowStudio.alerts.linkedWorkflowMissing.title':
    'No workflow draft is linked to this member yet.',
  'teamMemberWorkflowStudio.common.close': 'Close',
  'teamMemberWorkflowStudio.executionPanel.consoleAria':
    'Draft run console',
  'teamMemberWorkflowStudio.executionPanel.duration': 'Duration',
  'teamMemberWorkflowStudio.executionPanel.emptyEvidence':
    'Usage, snapshots, and raw observed events will appear here when the backend emits them.',
  'teamMemberWorkflowStudio.executionPanel.emptyLogs':
    'Run logs will appear here after the workflow draft returns events.',
  'teamMemberWorkflowStudio.executionPanel.emptyOutput':
    'Output will appear after the draft run emits a result.',
  'teamMemberWorkflowStudio.executionPanel.evidence': 'Evidence frames',
  'teamMemberWorkflowStudio.executionPanel.events': 'Events',
  'teamMemberWorkflowStudio.executionPanel.items': 'items',
  'teamMemberWorkflowStudio.executionPanel.logs': 'Logs',
  'teamMemberWorkflowStudio.executionPanel.output': 'Output',
  'teamMemberWorkflowStudio.executionPanel.rawFrames':
    '{count} run event(s) received, but no step logs are available yet.',
  'teamMemberWorkflowStudio.executionPanel.resultFirst': 'Result',
  'teamMemberWorkflowStudio.executionPanel.runLog': 'Run log',
  'teamMemberWorkflowStudio.executionPanel.steps': 'Steps',
  'teamMemberWorkflowStudio.executionPanel.summary': 'Summary',
  'teamMemberWorkflowStudio.executionPanel.timeline': 'Timeline',
  'teamMemberWorkflowStudio.executionsPanel.description':
    'This tab only shows executions that can be safely scoped to the current workflow member by stable workflow or service identifiers.',
  'teamMemberWorkflowStudio.executionsPanel.empty':
    'No safely scoped executions are available for this workflow member.',
  'teamMemberWorkflowStudio.executionsPanel.fallbackName':
    'Workflow execution',
  'teamMemberWorkflowStudio.executionsPanel.inspect': 'Inspect',
  'teamMemberWorkflowStudio.executionsPanel.sectionAria':
    'Workflow executions',
  'teamMemberWorkflowStudio.executionsPanel.serviceMeta':
    'service {serviceId}',
  'teamMemberWorkflowStudio.executionsPanel.title': 'Executions',
  'teamMemberWorkflowStudio.executionsPanel.unknownStatus': 'unknown',
  'teamMemberWorkflowStudio.header.activateAria': 'Activate workflow member',
  'teamMemberWorkflowStudio.header.activation.active': 'Active',
  'teamMemberWorkflowStudio.header.activation.error': 'Error',
  'teamMemberWorkflowStudio.header.activation.inactive': 'Inactive',
  'teamMemberWorkflowStudio.header.activation.publishing': 'Publishing',
  'teamMemberWorkflowStudio.header.activation.ready': 'Ready',
  'teamMemberWorkflowStudio.header.addNode': 'Add node',
  'teamMemberWorkflowStudio.header.automations.publishFirst':
    'Publish this member before adding recurring work.',
  'teamMemberWorkflowStudio.header.automations.saveFirst':
    'Save this member before adding recurring work.',
  'teamMemberWorkflowStudio.header.back': 'Back',
  'teamMemberWorkflowStudio.header.currentTeam': 'Current team',
  'teamMemberWorkflowStudio.header.deleteConnection': 'Delete connection',
  'teamMemberWorkflowStudio.header.deleteNode': 'Delete node',
  'teamMemberWorkflowStudio.header.editWorkflowName': 'Edit workflow name',
  'teamMemberWorkflowStudio.header.identityAria': 'Workflow identity',
  'teamMemberWorkflowStudio.header.inputSet': 'input set',
  'teamMemberWorkflowStudio.header.nodeActionsAria':
    'Workflow draft and node actions',
  'teamMemberWorkflowStudio.header.openAutomations':
    'Open recurring work for this member',
  'teamMemberWorkflowStudio.header.pasteYaml': 'Paste YAML',
  'teamMemberWorkflowStudio.header.primaryActionsAria':
    'Workflow primary actions',
  'teamMemberWorkflowStudio.header.prepareDraftRun': 'Prepare draft run',
  'teamMemberWorkflowStudio.header.runMessage': 'Run message',
  'teamMemberWorkflowStudio.header.runActiveMember': 'Run draft',
  'teamMemberWorkflowStudio.header.runDraft': 'Run draft',
  'teamMemberWorkflowStudio.header.recurringWork': 'Recurring work',
  'teamMemberWorkflowStudio.header.save': 'Save',
  'teamMemberWorkflowStudio.header.saveDraft': 'Save draft',
  'teamMemberWorkflowStudio.header.tabs.editor': 'Editor',
  'teamMemberWorkflowStudio.header.tabs.executions': 'Executions',
  'teamMemberWorkflowStudio.header.tabs.runs': 'Runs',
  'teamMemberWorkflowStudio.header.publish.binding': 'Binding',
  'teamMemberWorkflowStudio.header.publish.draft': 'Draft',
  'teamMemberWorkflowStudio.header.publish.error': 'Error',
  'teamMemberWorkflowStudio.header.publish.published': 'Published',
  'teamMemberWorkflowStudio.header.publish.publishing': 'Publishing',
  'teamMemberWorkflowStudio.header.publishMember': 'Publish member workflow',
  'teamMemberWorkflowStudio.header.publishMemberShort': 'Publish member',
  'teamMemberWorkflowStudio.header.teamBreadcrumb': 'Team',
  'teamMemberWorkflowStudio.header.unsavedChanges': 'Unsaved changes',
  'teamMemberWorkflowStudio.header.viewYaml': 'View YAML',
  'teamMemberWorkflowStudio.header.viewYamlUnavailable':
    'Load the workflow draft before viewing YAML.',
  'teamMemberWorkflowStudio.header.viewsAria': 'Workflow views',
  'teamMemberWorkflowStudio.header.workflowTitleAria': 'Workflow title',
  'teamMemberWorkflowStudio.nodeDetail.advancedRawConfiguration':
    'Advanced raw configuration',
  'teamMemberWorkflowStudio.nodeDetail.advancedRawConfigurationDescription':
    'Use this only when a node option is not available as a guided field.',
  'teamMemberWorkflowStudio.nodeDetail.applyRawConfiguration': 'Apply raw JSON',
  'teamMemberWorkflowStudio.nodeDetail.configuration': 'Configuration',
  'teamMemberWorkflowStudio.nodeDetail.configurationDescription':
    'Edit the fields this node uses when the draft runs.',
  'teamMemberWorkflowStudio.nodeDetail.noSemanticFields':
    'This node type does not have guided fields yet. Use advanced raw configuration when needed.',
  'teamMemberWorkflowStudio.nodeDetail.rawConfigurationAria':
    'Raw node configuration',
  'teamMemberWorkflowStudio.nodeDetail.rawConfigurationError':
    'Raw node configuration must be a JSON object.',
  'teamMemberWorkflowStudio.nodeDetail.sectionAria': 'Node detail',
  'teamMemberWorkflowStudio.nodeDetail.updateNode': 'Update node',
  'teamMemberWorkflowStudio.nodeInspector.basics': 'Basics',
  'teamMemberWorkflowStudio.nodeInspector.branches': 'Branches',
  'teamMemberWorkflowStudio.nodeInspector.branchesUnavailable':
    'Branches unavailable',
  'teamMemberWorkflowStudio.nodeInspector.closeAria': 'Close node inspector',
  'teamMemberWorkflowStudio.nodeInspector.flow': 'Flow',
  'teamMemberWorkflowStudio.nodeInspector.nextStep': 'Next step',
  'teamMemberWorkflowStudio.nodeInspector.noBranches': 'No branches',
  'teamMemberWorkflowStudio.nodeInspector.notSet': 'Not set',
  'teamMemberWorkflowStudio.nodeInspector.resizeHandle':
    'Resize node inspector',
  'teamMemberWorkflowStudio.nodeInspector.sectionAria': 'Node inspector',
  'teamMemberWorkflowStudio.nodeInspector.selectedNode': 'Selected node',
  'teamMemberWorkflowStudio.nodeInspector.targetRole': 'Target role',
  'teamMemberWorkflowStudio.nodeInspector.type': 'Type',
  'teamMemberWorkflowStudio.nodeLibrary.closeAria': 'Close node library',
  'teamMemberWorkflowStudio.nodeLibrary.emptySearch':
    'No nodes match this search.',
  'teamMemberWorkflowStudio.nodeLibrary.insertNodeAria': 'Insert {nodeName} node',
  'teamMemberWorkflowStudio.nodeLibrary.searchAria': 'Search nodes',
  'teamMemberWorkflowStudio.nodeLibrary.searchPlaceholder': 'Search nodes',
  'teamMemberWorkflowStudio.nodeLibrary.sectionAria': 'Node library',
  'teamMemberWorkflowStudio.nodeLibrary.title': 'Node library',
  'teamMemberWorkflowStudio.resize.executionPanel': 'Resize run console',
  'teamMemberWorkflowStudio.resize.sidePanel': 'Resize side panel',
  'teamMemberWorkflowStudio.draftRunPanel.messageLabel':
    'Draft run input',
  'teamMemberWorkflowStudio.draftRunPanel.messagePlaceholder':
    'Optional input sent to this workflow draft run',
  'teamMemberWorkflowStudio.draftRunPanel.sectionAria': 'Draft run panel',
  'teamMemberWorkflowStudio.draftRunPanel.startDraftRun':
    'Start draft run',
  'teamMemberWorkflowStudio.draftRunPanel.title': 'Draft run',
  'teamMemberWorkflowStudio.runsPanel.description':
    'This tab only shows runs with an explicit link to the current workflow member.',
  'teamMemberWorkflowStudio.runsPanel.empty':
    'No runs are linked to this workflow member yet.',
  'teamMemberWorkflowStudio.runsPanel.fallbackName': 'Member run',
  'teamMemberWorkflowStudio.runsPanel.openRun': 'Open run',
  'teamMemberWorkflowStudio.runsPanel.preview.error': 'Error',
  'teamMemberWorkflowStudio.runsPanel.preview.input': 'Input',
  'teamMemberWorkflowStudio.runsPanel.preview.output': 'Output',
  'teamMemberWorkflowStudio.runsPanel.sectionAria': 'Member runs',
  'teamMemberWorkflowStudio.runsPanel.title': 'Member runs',
  'teamMemberWorkflowStudio.runsPanel.unknownStatus': 'unknown',
  'teamMemberWorkflowStudio.yamlImportPanel.cancel': 'Cancel',
  'teamMemberWorkflowStudio.yamlImportPanel.closeAria':
    'Close paste YAML panel',
  'teamMemberWorkflowStudio.yamlImportPanel.import': 'Import',
  'teamMemberWorkflowStudio.yamlImportPanel.placeholder':
    'name: Untitled workflow\nsteps:\n  - id: triage\n    type: llm_call',
  'teamMemberWorkflowStudio.yamlImportPanel.sectionAria':
    'Paste workflow YAML panel',
  'teamMemberWorkflowStudio.yamlImportPanel.subtitle':
    'Import into the current draft',
  'teamMemberWorkflowStudio.yamlImportPanel.textareaAria': 'Workflow YAML',
  'teamMemberWorkflowStudio.yamlImportPanel.title': 'Paste YAML',
  'teamMemberWorkflowStudio.yamlPanel.closeAria': 'Close YAML panel',
  'teamMemberWorkflowStudio.yamlPanel.copy': 'Copy',
  'teamMemberWorkflowStudio.yamlPanel.copyFailed': 'Failed to copy YAML.',
  'teamMemberWorkflowStudio.yamlPanel.copySuccess': 'YAML copied.',
  'teamMemberWorkflowStudio.yamlPanel.empty':
    'No YAML is available for this draft.',
  'teamMemberWorkflowStudio.yamlPanel.retry': 'Retry',
  'teamMemberWorkflowStudio.yamlPanel.sectionAria': 'Workflow YAML panel',
  'teamMemberWorkflowStudio.yamlPanel.subtitle': 'Current draft source',
  'teamMemberWorkflowStudio.yamlPanel.textareaAria': 'Current workflow YAML',
  'teamMemberWorkflowStudio.yamlPanel.title': 'Workflow YAML',
  'pages.studio.studiomembercurrentrunpanel.details': 'Details',
  'pages.studio.studiomemberinvokeinspector.copy':
    'Endpoint, payload, run events, and recent history are available here without taking over the task page.',
  'pages.studio.studiomemberinvokeinspector.current.run': 'Current run',
  'pages.studio.studiomemberinvokeinspector.close':
    'Close details',
  'pages.studio.studiomemberinvokeinspector.drag.handle':
    'Drag details panel',
  'pages.studio.studiomemberinvokeinspector.endpoint': 'Endpoint',
  'pages.studio.studiomemberinvokeinspector.endpoint.2': 'Endpoint',
  'pages.studio.studiomemberinvokeinspector.history': 'History',
  'pages.studio.studiomemberinvokeinspector.payload': 'Payload',
  'pages.studio.studiomemberinvokeinspector.payload.base64': 'Payload base64',
  'pages.studio.studiomemberinvokeinspector.payload.base64.2':
    'Payload base64',
  'pages.studio.studiomemberinvokeinspector.payload.type.url':
    'Payload type URL',
  'pages.studio.studiomemberinvokeinspector.payload.type.url.2':
    'Payload type URL',
  'pages.studio.studiomemberinvokeinspector.paste.encoded.protobuf.payload.when':
    'Paste encoded protobuf payload when this type cannot be built from text.',
  'pages.studio.studiomemberinvokeinspector.revision': 'Revision',
  'pages.studio.studiomemberinvokeinspector.resize.handle':
    'Resize details panel',
  'pages.studio.studiomemberinvokeinspector.run': 'Run',
  'pages.studio.studiomemberinvokeinspector.service.target': 'Service target',
  'pages.studio.studiomemberinvokeinspector.title': 'Details',
  'pages.studio.studiomemberinvokepanel.endpoint': 'Endpoint',
  'pages.studio.studiomemberinvokepanel.inspector': 'Details',
};

export default enUSMessages;
