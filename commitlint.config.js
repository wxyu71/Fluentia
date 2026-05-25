module.exports = {
  extends: ['@commitlint/config-conventional'],
  rules: {
    'type-enum': [
      2,
      'always',
      ['feat', 'fix', 'chore', 'refactor', 'test', 'docs', 'ci', 'style', 'perf', 'revert'],
    ],
    'subject-max-length': [2, 'always', 72],
    'body-max-line-length': [1, 'always', 200],
  },
};
