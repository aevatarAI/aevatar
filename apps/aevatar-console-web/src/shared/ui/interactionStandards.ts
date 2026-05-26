export const AEVATAR_INTERACTIVE_BUTTON_CLASS = 'aevatar-interactive-button';
export const AEVATAR_INTERACTIVE_CHIP_CLASS = 'aevatar-interactive-chip';
export const AEVATAR_PRESSABLE_CARD_CLASS = 'aevatar-pressable-card';

export function joinInteractiveClassNames(
  ...classNames: Array<string | false | null | undefined>
): string {
  return classNames.filter(Boolean).join(' ');
}
