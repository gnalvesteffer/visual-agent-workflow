# Source and local integration

Upstream: https://github.com/softaworks/agent-toolkit/tree/3027f20f3181758385a1bb8c022d4041dfb4de84/skills/mermaid-diagrams

Pinned commit: `3027f20f3181758385a1bb8c022d4041dfb4de84`. Upstream skill/reference files and [LICENSE](LICENSE) are retained unchanged. License: MIT.

## SHA-256

- `README.md`: `e2f7f6e8d4415c57368cf0492eb98b12be18526f963383a9e64dcb741eb602de`
- `SKILL.md`: `79bca3c767a821a28aa7d83a9673010b2066afa4889923ea5946153c8967e824`
- `references/advanced-features.md`: `5b816a153915de49ccae229a70103e724f68c3fc8b7a5b38f67a59f973af37bc`
- `references/architecture-diagrams.md`: `117bc72208e20dacb355d007b444dce7433f174a0ba9b76ab0a3361b8d580706`
- `references/c4-diagrams.md`: `28e73813c730721995aab4084b913459687a8065f28e2a5e8ef7a94cd8116fd9`
- `references/class-diagrams.md`: `5eba3606f37be8409843826901f0c846a1bd4796e2e9145c787d4e5ba1646ce3`
- `references/erd-diagrams.md`: `639ee58f4925e0721fc3a2c70fd083ec256fe861baa62a56d039c01315fc58ff`
- `references/flowcharts.md`: `73b599797659ced9211ab947e3d4aee4f3ef2bfb4c1aefcfd6537276d2a21f96`
- `references/sequence-diagrams.md`: `11900f082d21c7e3fc0addcd2bf50689a1c36a5af806207942a2f772cb5e2b3b`

## Local application

Use [documentation-maintenance](../documentation-maintenance/SKILL.md) and [project-guide](../project-guide/SKILL.md). User instructions and implemented behavior take precedence over generic examples.

Prefer portable flowcharts, sequence/state/ER diagrams with short quoted labels. Do not assume architecture-beta icons, ELK, C4 extensions, embedded HTML, or renderer integrations are supported by GitHub. Validate our own diagrams with the pinned local Mermaid renderer; never send private diagrams to an online renderer. Example libraries and commands are illustrations, not permission to publish or install unrelated tools. Read references on demand for diagram types actually used.

Update only at a reviewed explicit commit, preserving license, supporting files, and refreshed digests. Referenced external tools are not automatically installed dependencies.
