import js from "@eslint/js";
import globals from "globals";
import reactHooks from "eslint-plugin-react-hooks";
import tseslint from "typescript-eslint";

export default tseslint.config(
  { ignores: ["node_modules"] },
  {
    files: ["**/*.{ts,tsx}"],
    extends: [js.configs.recommended, ...tseslint.configs.strict],
    languageOptions: { globals: globals.browser },
    plugins: { "react-hooks": reactHooks },
    rules: { ...reactHooks.configs.recommended.rules, "@typescript-eslint/no-explicit-any": "error" },
  },
  // Tests assert on fixtures they built; a failed lookup fails the test either way.
  { files: ["**/*.test.ts"], rules: { "@typescript-eslint/no-non-null-assertion": "off" } },
);
