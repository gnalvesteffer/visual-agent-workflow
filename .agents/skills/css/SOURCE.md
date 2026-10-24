# CSS skill provenance

- Requested listing: https://explainx.ai/skills/mindrally/skills/css
- Upstream repository: https://github.com/Mindrally/skills
- Pinned commit: `7682ca77710e0971eab4e0ae5dddfa281aea0ba5`
- Original skill: https://github.com/Mindrally/skills/blob/7682ca77710e0971eab4e0ae5dddfa281aea0ba5/css/SKILL.md
- Original license: https://github.com/Mindrally/skills/blob/7682ca77710e0971eab4e0ae5dddfa281aea0ba5/LICENSE
- License: Apache-2.0; the complete upstream license is retained in [LICENSE](LICENSE).
- Skill SHA-256: `1ae648cb363e969fb03f8cfc9c4f30186aafdf8f1dbd69353d651a9bbd3a0a02`

`SKILL.md` and `LICENSE` are copied without modification. This provenance file is a local addition. No upstream root NOTICE file was present at this commit. The requested listing was blocked by the environment proxy; the skill was retrieved directly from the matching owner/repository/path identified by that URL.

For this repository, pair the upstream CSS guidance with [graph-experience-review](../graph-experience-review/SKILL.md). Preserve the embeddable Razor library's styling boundary: put library defaults and custom properties under its component root rather than changing the consuming application's global `:root`, body, or element styles. Application-level tokens remain the host's choice. Project architecture and user instructions take precedence over generic styling recommendations.

To update, inspect the upstream skill and license at an explicit new commit, copy the required files together, refresh the commit and digest here, and validate frontmatter, references, and attribution. Do not automatically import other upstream skills or application changes.

For decorative elevation, provide inherited role-based shadow overrides and preserve explicit focus outlines. Forced-colors suppression must cover nodes, panels, floating menus and hover states, not just textured chrome. The theme browser review verifies node-shadow opt-out and forced-colors removal.
