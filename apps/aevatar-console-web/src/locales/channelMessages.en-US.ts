export default {
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
  'channels.platform.lark.description': 'Connect your bot to chats and groups.',
  'channels.platform.feishu.description': 'Feishu, using the same local flow.',
  'channels.platform.telegram.description':
    'Connect with a BotFather bot token. Webhook setup is handled for you.',
  'channels.platform.discord.description': 'Server and direct-message support.',
  'channels.platform.slack.description': 'Workspace bot support.',
  'channels.connected': 'Connected',
  'channels.connectedDescription': 'Channels connected to your account.',
  'channels.bot': '{platform} bot',
  'channels.skill.notSet': 'Not set',
  'channels.skill.version': 'Version {version}',
  'channels.skill.open': 'Open {name} in Ornn',
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
  'channels.error.unavailable':
    'This channel is unavailable or you do not have access.',
  'channels.details': 'Channel details',
  'channels.details.loading': 'Loading channel details',
  'channels.breadcrumb': 'Channel navigation',
  'channels.registration': 'Registration',
  'channels.inboundMessages': 'Inbound messages',
  'channels.botId': 'Bot ID',
  'channels.scope': 'Scope',
  'channels.provider': 'Provider',
  'channels.keyId': 'Agent key ID',
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
  'channels.connect.label': 'Label',
  'channels.connect.skillName': 'Skill name',
  'channels.connect.nameLookupUnavailable':
    'Automatic bot-name lookup is not available yet. Enter a Label and Skill name below to connect.',
  'channels.connect.nameHelp': 'Use the Telegram bot name or enter your own.',
  'channels.connect.tokenRequired': 'Enter the bot token from BotFather.',
  'channels.connect.nameRequired':
    'Enter a name while automatic lookup is unavailable.',
  'channels.connect.services': 'Services',
  'channels.connect.selected': '{count} selected',
  'channels.connect.servicesHelp':
    'Select the services this bot can use from your NyxID account.',
  'channels.connect.servicesLoading': 'Loading services',
  'channels.connect.servicesError':
    'Could not load your services. Retry before connecting.',
  'channels.connect.servicesEmpty':
    'No services are available in your NyxID account. You can connect this bot without service access.',
  'channels.connect.searchServices': 'Search services by name or slug',
  'channels.connect.serviceUnavailable': 'Unavailable',
  'channels.connect.personal': 'Personal',
  'channels.connect.organization': 'Organization',
  'channels.connect.noMatches': 'No services match your search.',
  'channels.connect.selectionHelp':
    'Only selected services will be available to this bot.',
  'channels.connect.selectionChanged':
    'Some selected services are no longer available. Deselect them before connecting.',
  'channels.connect.pending':
    'Telegram setup was submitted. Waiting for your channel to appear.',
  'channels.connect.success':
    'Telegram added. Send your bot a message to get started.',
  'channels.connect.error.token':
    'Check the bot token from BotFather and try again.',
  'channels.connect.error.services':
    'The selected services are no longer available. Review your selection and try again.',
  'channels.connect.error.skill':
    'The Skill name could not be used. Check the name and your access in Ornn.',
  'channels.connect.error.authorization':
    'Your session or service access needs attention. Sign in again and review your NyxID access.',
  'channels.connect.error.conflict':
    'This bot may already be connected. Check your channels before trying again.',
  'channels.connect.error.configuration':
    'Channel setup is unavailable. Ask your administrator to check the deployment configuration.',
  'channels.connect.error.rejected':
    'Could not connect Telegram. Check the bot token, Skill name, and selected services, then try again.',
  'channels.connect.error.uncertain':
    'The connection result could not be confirmed. Check your channels before starting another connection.',
  'channels.connect.discardTitle': 'Discard this connection setup?',
  'channels.connect.discard': 'Discard',
  'channels.connect.stay': 'Stay',
  'channels.connect.discardHelp':
    'Your bot token and unsaved choices will be cleared.',
};
