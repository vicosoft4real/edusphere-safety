import js from "@eslint/js";

// Note: eslint-config-next stays as a dependency for `next build`'s own
// lint pass, but its runtime require of @rushstack/eslint-patch crashes
// under the installed ESLint, so the repo `lint` script uses core rules.
export default [
  { ignores: ["node_modules/**", ".next/**", "out/**", "next-env.d.ts"] },
  js.configs.recommended,
  {
    rules: {
      "no-unused-vars": ["error", { argsIgnorePattern: "^_", varsIgnorePattern: "^_" }],
      "no-console": "off",
      eqeqeq: ["error", "smart"],
    },
  },
];
