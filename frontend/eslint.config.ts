import vue from 'eslint-plugin-vue'
import tseslint from 'typescript-eslint'

const typescriptRules = {
  '@typescript-eslint/no-explicit-any': 'off',
  '@typescript-eslint/consistent-type-imports': ['error', { prefer: 'type-imports' }],
} as const

export default [
  { ignores: ['dist', 'node_modules', 'playwright-report', 'test-results'] },
  ...vue.configs['flat/recommended'],
  {
    files: ['**/*.{ts,tsx}'],
    languageOptions: { parser: tseslint.parser },
    plugins: { '@typescript-eslint': tseslint.plugin },
    rules: typescriptRules,
  },
  {
    files: ['**/*.vue'],
    languageOptions: { parserOptions: { parser: tseslint.parser } },
    plugins: { '@typescript-eslint': tseslint.plugin },
    rules: {
      ...typescriptRules,
      'vue/multi-word-component-names': 'off',
      'vue/require-default-prop': 'off',
      'vue/max-attributes-per-line': 'off',
      'vue/singleline-html-element-content-newline': 'off',
      'vue/html-closing-bracket-newline': 'off',
      'vue/html-indent': 'off',
      'vue/attributes-order': 'off',
      'vue/html-self-closing': 'off',
      'vue/multiline-html-element-content-newline': 'off',
      'vue/html-quotes': 'off',
    },
  },
]
