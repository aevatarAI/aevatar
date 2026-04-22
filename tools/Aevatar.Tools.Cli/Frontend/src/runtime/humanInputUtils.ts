export type HumanInputChoice = {
  key: string;
  label: string;
  value: string;
};

export function parseHumanInputChoices(
  prompt: string,
  options?: string[],
): {
  questionText: string;
  choices: HumanInputChoice[];
} {
  const structuredOptions = Array.isArray(options)
    ? options.filter((option): option is string => typeof option === 'string' && option.trim().length > 0)
    : [];

  if (structuredOptions.length >= 2) {
    return {
      questionText: prompt.trim(),
      choices: structuredOptions.map((option, index) => ({
        key: String(index + 1),
        label: option,
        value: String(index + 1),
      })),
    };
  }

  const lines = prompt.split('\n');
  const choicePattern = /^\s*([0-9]+|[A-Za-z])[.)]\s+(.+)$/;
  const choices: HumanInputChoice[] = [];
  let firstChoiceIndex = -1;

  for (let index = 0; index < lines.length; index += 1) {
    const match = lines[index].match(choicePattern);
    if (match) {
      if (firstChoiceIndex < 0) {
        firstChoiceIndex = index;
      }

      choices.push({
        key: match[1],
        label: match[2].trim(),
        value: match[1],
      });
      continue;
    }

    if (choices.length > 0 && lines[index].trim() === '') {
      continue;
    }

    if (choices.length > 0) {
      break;
    }
  }

  if (choices.length < 2) {
    return {
      questionText: prompt,
      choices: [],
    };
  }

  return {
    questionText: lines.slice(0, firstChoiceIndex).join('\n').trim(),
    choices,
  };
}
