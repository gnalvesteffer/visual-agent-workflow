# Source and local integration

Upstream: https://github.com/Mindrally/skills/tree/7682ca77710e0971eab4e0ae5dddfa281aea0ba5/design-systems

Pinned commit: `7682ca77710e0971eab4e0ae5dddfa281aea0ba5`. `SKILL.md` and [LICENSE](LICENSE) are retained unchanged (Apache-2.0). No root or design-systems NOTICE file is present at this commit.

Skill SHA-256: `275910a02eb924f7bc471264329d7ef158eed4b2014b04d5fa25d895965f8211`.

Read [project-guide](../project-guide/SKILL.md) and [editor design system](../../../docs/design-system.md). Use Blazor parameters and Razor components in place of the illustrative React/TypeScript APIs. Keep tokens scoped to component roots and inherited host overrides; do not copy the global :root example. Existing Playwright and HtmlRenderer checks provide component examples and regression evidence; Storybook, Chromatic, package publication, and governance approval are not prerequisites for authorized UI work. Document actual variants and states without introducing unused abstractions. Update at an explicit reviewed commit with original license and refreshed digest.
