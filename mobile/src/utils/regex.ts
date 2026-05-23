export interface RegexFilterRule {
  source: string;
  flags: string;
  regex: RegExp;
}

export interface RegexParseResult {
  rules: RegexFilterRule[];
  normalizedMarkdown: string;
}

export const REGEX_SKILL_TEMPLATE = [
  'Please generate regex rules for Fluentia filler-word filtering.',
  'Requirements:',
  '- Return Markdown only.',
  '- Put the final regex rules inside one fenced code block.',
  '- One regex per line.',
  '- Prefer safe patterns for spoken filler words and repeated hesitation fragments.',
  '- Do not include explanation inside the code block.',
  '- If flags are needed, use /pattern/flags syntax. Otherwise output raw regex text.',
  '',
  'Target examples to remove:',
  '- um, uh, er, ah',
  '- repeated hesitation like 嗯嗯, 呃啊, 就是说, 那个那个',
  '- duplicated filler surrounded by spaces or punctuation',
  '',
  'Example output format:',
  '```regex',
  '\\b(?:um|uh|er|ah)\\b',
  '(?<=^|[\\s,.;!?])(?:嗯+|呃+)(?=$|[\\s,.;!?])',
  '```',
].join('\n');

const CODE_BLOCK_PATTERN = /```(?:regex|regexp)?\s*\n([\s\S]*?)```/gi;
const COMMENT_PATTERN = /^(#|\/\/)/;

function parseRule(line: string): RegexFilterRule {
  const trimmed = line.trim();
  if (!trimmed) {
    throw new Error('Regex line is empty.');
  }

  let source = trimmed;
  let flags = 'gu';

  if (trimmed.startsWith('/') && trimmed.lastIndexOf('/') > 0) {
    const lastSlash = trimmed.lastIndexOf('/');
    source = trimmed.slice(1, lastSlash);
    const explicitFlags = trimmed.slice(lastSlash + 1);
    const uniqueFlags = Array.from(new Set((explicitFlags.includes('g') ? explicitFlags : `${explicitFlags}g`).split(''))).join('');
    flags = uniqueFlags || 'g';
  }

  return {
    source,
    flags,
    regex: new RegExp(source, flags),
  };
}

export function parseRegexMarkdown(markdown: string): RegexParseResult {
  const matches = Array.from(markdown.matchAll(CODE_BLOCK_PATTERN));
  if (matches.length === 0) {
    throw new Error('No regex code block found. Paste the AI output with a fenced code block.');
  }

  const rules: RegexFilterRule[] = [];
  const normalizedBlocks: string[] = [];

  for (const match of matches) {
    const block = match[1] ?? '';
    const normalizedLines: string[] = [];

    for (const rawLine of block.split(/\r?\n/)) {
      const line = rawLine.trim();
      if (!line || COMMENT_PATTERN.test(line)) {
        continue;
      }

      const rule = parseRule(line);
      rules.push(rule);
      normalizedLines.push(line);
    }

    if (normalizedLines.length > 0) {
      normalizedBlocks.push(['```regex', ...normalizedLines, '```'].join('\n'));
    }
  }

  if (rules.length === 0) {
    throw new Error('No regex rule found inside the Markdown code block.');
  }

  return {
    rules,
    normalizedMarkdown: normalizedBlocks.join('\n\n'),
  };
}

export function applyRegexFilters(input: string, markdown: string, enabled: boolean): string {
  if (!enabled || !input || !markdown.trim()) {
    return input;
  }

  let next = input;
  const { rules } = parseRegexMarkdown(markdown);
  for (const rule of rules) {
    next = next.replace(rule.regex, '');
  }

  return next.replace(/\s{2,}/g, ' ').trimStart();
}
