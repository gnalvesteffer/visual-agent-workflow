# Source and project integration

Upstream: https://github.com/aaronontheweb/dotnet-skills/tree/ac0deb2f531ff5090718cb0069c73ba3f9f95f69/skills/project-structure

Pinned commit: `ac0deb2f531ff5090718cb0069c73ba3f9f95f69`. Original skill and supporting Markdown files are retained unchanged. MIT license, copyright Aaron Stannard, retained in [LICENSE](LICENSE).

- `SKILL.md` SHA-256: `8698fae2587ffa4d1abc35f60115545351272c8d85ffc5d93943a347a214e867`

## Local application

Read [project-guide](../project-guide/SKILL.md) first. User requirements and established repository contracts take precedence over generic upstream examples.

Keep the pinned .NET 10 SDK, latestPatch roll-forward, .slnx solution, nullable checks, warnings-as-errors, and locked restore. Central package management pins the existing versions; it is not an upgrade. Core owns contracts, adapters depend inward, Blazor depends only on Core, and Studio composes concrete services. Do not introduce SourceLink, package publishing, release metadata, or licensing claims until needed. SDK roll-forward examples must be checked against actual SDK behavior.

Related upstream skill names are suggestions, not installed dependencies. Update only at an explicit reviewed commit, retaining license and required reference files and updating these digests.
