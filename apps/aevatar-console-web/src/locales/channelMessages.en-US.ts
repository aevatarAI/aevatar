export default {
  'channels.edit.label': 'Label',
  'channels.edit.labelError':
    'Enter a non-empty label. If it is too long, shorten it and try again.',
  'channels.edit.labelFailed':
    'Could not save the label. Check it and try again.',
  'channels.edit.partialSave':
    'Label saved, but the skill name could not be updated. Try saving again.',
  'channels.edit': 'Edit',
  'channels.edit.title': 'Edit {platform}',
  'channels.edit.loadingTitle': 'Edit channel',
  'channels.edit.loading': 'Loading channel configuration',
  'channels.edit.configuration': 'Configuration',
  'channels.edit.save': 'Save changes',
  'channels.edit.defaults': 'NyxID defaults',
  'channels.edit.useDefaults': 'Use NyxID defaults',
  'channels.edit.unavailableService': 'Unavailable service',
  'channels.edit.servicesError':
    'Could not load your services. Try again before saving.',
  'channels.edit.servicesEmpty':
    'No services are available with your current authorization.',
  'channels.edit.missingServices':
    'Some saved services are no longer available. Deselect them before saving.',
  'channels.edit.selectionError':
    'Review your selected services and try again.',
  'channels.edit.skillError':
    'Check the skill name. Use no more than 128 characters.',
  'channels.edit.failed':
    'Could not save channel changes. Review your choices and try again.',
  'channels.edit.saved': 'Channel changes saved.',
  'channels.edit.submitted':
    'Changes submitted. They may take a moment to appear in channel details.',
  'channels.edit.readbackUnavailable':
    'Changes submitted, but the latest configuration could not be loaded. Refresh the channel details to view it.',
  'channels.edit.discardTitle': 'Discard your changes?',
  'channels.edit.discardHelp': 'Your unsaved channel changes will be lost.',
  'workflowActivityVNext.nav.channels': 'Channels',
  'channels.title': 'Connect your channels to your agent',
  'channels.description':
    'Choose a channel to connect your bot and select the services it can use.',
  'channels.intro':
    'Once connected, talk to your Aevatar bot directly from the channel.',
  'channels.available': 'Available channels',
  'channels.availableNow': 'Available',
  'channels.soon': 'Soon',
  'channels.connect': 'Connect',
  'channels.connectPlatform': 'Connect {platform}',
  'channels.platform.feishu': 'Feishu',
  'channels.platform.telegram.description':
    'Connect with a BotFather bot token. Webhook setup is handled for you.',
  'channels.platform.whatsapp.description': 'Chat with your bot on WhatsApp.',
  'channels.connected': 'Connected',
  'channels.connectedDescription': 'Channels connected to your account.',
  'channels.name.loading': 'Loading name…',
  'channels.name.unavailable': 'Name unavailable',
  'channels.name.retry': 'Reload channel name',
  'channels.column.name': 'Channel name',
  'channels.skill.notSet': 'Not set',
  'channels.skill.version': 'Version {version}',
  'channels.skill.open': 'Open {name} in Ornn',
  'channels.identifier.showFull': 'Show full ID',
  'channels.column.channel': 'Channel',
  'channels.column.skill': 'Skill',
  'channels.column.inbound': 'Inbound',
  'channels.column.delivery': 'Workflow delivery',
  'channels.column.actions': 'Actions',
  'channels.manage': 'Manage',
  'channels.managePlatform': '{platform} · Manage',
  'channels.refresh': 'Refresh channels',
  'channels.loading': 'Loading channels',
  'channels.status.checking': 'Checking…',
  'channels.status.active': 'Active',
  'channels.status.pending': 'Waiting for message',
  'channels.status.error': 'Needs attention',
  'channels.status.unknown': 'Unknown',
  'channels.delivery.ready': 'Ready',
  'channels.delivery.failed': 'Repair failed',
  'channels.delivery.repairing': 'Repairing',
  'channels.delivery.repairRequired': 'Needs repair',
  'channels.delivery.notEnabled': 'Not enabled',
  'channels.empty.title': 'No channels connected yet',
  'channels.empty.description':
    'Connect Telegram above to start talking to your agent.',
  'channels.retry': 'Try again',
  'channels.error.load': 'Could not load channels. Please try again.',
  'channels.error.refresh': 'Could not refresh channels. Try again.',
  'channels.error.names':
    'Could not load channel names. Use Refresh to try again.',
  'channels.error.unavailable':
    'This channel is unavailable or you do not have access.',
  'channels.details': 'Channel details',
  'channels.details.loading': 'Loading channel details',
  'channels.breadcrumb': 'Channel navigation',
  'channels.registration': 'Registration',
  'channels.inboundMessages': 'Inbound messages',
  'channels.services.title': 'Authorized services',
  'channels.services.loading': 'Loading service names',
  'channels.services.empty': 'No services authorized.',
  'channels.services.default':
    'Uses NyxID default authorization; individual services are not listed.',
  'channels.services.unavailable':
    'Authorization details are unavailable for this channel.',
  'channels.services.namesError':
    'Could not load service names. Please try again.',
  'channels.botId': 'Bot ID',
  'channels.provider': 'Provider',
  'channels.keyId': 'Agent key ID',
  'channels.openInNyxID': 'Open {label} {id} in NyxID (new tab)',
  'channels.back': 'Back to channels',
  'channels.remove': 'Remove',
  'channels.cancel': 'Cancel',
  'channels.remove.title': 'Remove this channel?',
  'channels.remove.description':
    'This bot will stop routing messages to Aevatar. To reconnect it, you will need to set it up again.',
  'channels.remove.error': 'Could not remove this channel. Please try again.',
  'channels.remove.pending':
    'Removal requested. Waiting for the channel list to confirm the change.',
  'channels.remove.confirming': 'Confirming removal…',
  'channels.remove.check': 'Check again',
  'channels.remove.success': 'Channel removed.',
  'channels.remove.warning':
    'Channel removed. Some cleanup needs attention in NyxID.',
  'channels.connect.title': 'Connect Telegram',
  'channels.connect.description':
    'Add your bot and choose the services it can use. Telegram webhook setup is automatic.',
  'channels.connect.botDetails': 'Bot details',
  'channels.connect.botToken': 'Bot token',
  'channels.connect.showToken': 'Show bot token',
  'channels.connect.hideToken': 'Hide bot token',
  'channels.connect.botFather': 'Get token from BotFather',
  'channels.connect.name': 'Channel name',
  'channels.connect.skillName': 'Skill name',
  'channels.connect.skillNameDefault': 'Defaults to the Telegram bot name',
  'channels.connect.optional': '(optional)',
  'channels.connect.botNameDefault':
    'Defaults to the bot name + 6 random digits',
  'channels.connect.tokenRequired': 'Enter the bot token from BotFather.',
  'channels.connect.services': 'Services',
  'channels.connect.selected': '{count} selected',
  'channels.connect.selectAll': 'Select all',
  'channels.connect.selectAllResults': 'Select all results',
  'channels.connect.servicesHelp':
    'Only services available through your current NyxID authorization are shown.',
  'channels.connect.servicesLoading': 'Loading services',
  'channels.connect.servicesError':
    'Could not load your services. Retry before connecting.',
  'channels.connect.servicesEmpty':
    'No services are available with your current NyxID authorization. You can connect this bot without service access.',
  'channels.connect.searchServices': 'Search services by name or slug',
  'channels.connect.serviceUnavailable': 'Unavailable',
  'channels.connect.personal': 'Personal',
  'channels.connect.organization': 'Organization',
  'channels.connect.noMatches': 'No services match your search.',
  'channels.connect.selectionHelp':
    'Only selected services will be available to this bot.',
  'channels.connect.selectionChanged':
    'Some selected services are no longer available. Deselect them before connecting.',
  'channels.connect.connecting': 'Connecting...',
  'channels.connect.awaitingConfirmation': 'Connection pending',
  'channels.connect.delayed':
    'Your request was submitted, but confirmation is taking longer than expected. You can return to Channels.',
  'channels.connect.success': 'Telegram channel created.',
  'channels.connect.error.token':
    'Check the bot token from BotFather and try again.',
  'channels.connect.error.botName':
    'Could not read the Telegram bot name. Please try again.',
  'channels.connect.error.services':
    'The selected services are no longer available. Review your selection and try again.',
  'channels.connect.error.skill':
    'Could not configure the bot skill. Check the Skill name and your access in Ornn.',
  'channels.connect.error.authorization':
    'Your session or service access needs attention. Sign in again and review your NyxID access.',
  'channels.connect.error.conflict':
    'This bot may already be connected. Check your channels before trying again.',
  'channels.connect.error.configuration':
    'Channel setup is unavailable. Ask your administrator to check the deployment configuration.',
  'channels.connect.error.rejected':
    'Could not connect Telegram. Check the bot token, Skill name, and selected services, then try again.',
  'channels.connect.error.uncertain':
    'Could not confirm the connection. Please try again.',
  'channels.connect.discardTitle': 'Discard this connection setup?',
  'channels.connect.discard': 'Discard',
  'channels.connect.stay': 'Stay',
  'channels.connect.discardHelp':
    'Your bot token and unsaved choices will be cleared.',
};
