# Source and project integration

Upstream: https://github.com/aaronontheweb/dotnet-skills/tree/ac0deb2f531ff5090718cb0069c73ba3f9f95f69/skills/csharp-api-design

Pinned commit: `ac0deb2f531ff5090718cb0069c73ba3f9f95f69`. Original skill and supporting Markdown files are retained unchanged. MIT license, copyright Aaron Stannard, retained in [LICENSE](LICENSE).

- `SKILL.md` SHA-256: `d5d8dc4e037af7dfbfb44247d69d348f02f9b84cd4b18f5fd8f392bb21afe857`

## Local application

Read [project-guide](../project-guide/SKILL.md) first. User requirements and established repository contracts take precedence over generic upstream examples.

Preserve source, binary, and wire compatibility independently. Add overloads rather than changing existing signatures with optional parameters. Do not replace established GUID/string identifiers or change canonical JSON/hashes for style. Breaking public changes require an explicit migration and major-version policy; minor releases do not remove APIs. Never copy a blocking .Result compatibility bridge. Use existing compatibility fixtures instead of automatically adding test dependencies or publication workflows.

Related upstream skill names are suggestions, not installed dependencies. Update only at an explicit reviewed commit, retaining license and required reference files and updating these digests.
