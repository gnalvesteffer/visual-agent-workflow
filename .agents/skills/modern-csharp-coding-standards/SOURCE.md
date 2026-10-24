# Source and project integration

Upstream: https://github.com/aaronontheweb/dotnet-skills/tree/ac0deb2f531ff5090718cb0069c73ba3f9f95f69/skills/csharp-coding-standards

Pinned commit: `ac0deb2f531ff5090718cb0069c73ba3f9f95f69`. Original skill and supporting Markdown files are retained unchanged. MIT license, copyright Aaron Stannard, retained in [LICENSE](LICENSE).

- `SKILL.md` SHA-256: `e40104dd1b7328cef2aa7a1db1917353cb8b26f4acb246c4fa12abb8d327b2e9`
- `anti-patterns-and-reflection.md` SHA-256: `6dfc0904421bdded9cc83fe155bcddf2bc1babf72889fd9b8ec697f6c2a64429`
- `composition-and-error-handling.md` SHA-256: `4b0033c32b581a7ed98523a4137429b5ef653960e3bfc4be1a12575b58ace652`
- `performance-and-api-design.md` SHA-256: `579c5c798eb68e70f80a863bdf70b0db6f427f981789de621335e7fff17c1557`
- `value-objects-and-patterns.md` SHA-256: `8c27d3f8b6a4c07635016bd6726caa1f929b62a8c0975884ce475270ebee0e76`

## Local application

Read [project-guide](../project-guide/SKILL.md) first. User requirements and established repository contracts take precedence over generic upstream examples.

Keep published records and identifiers compatible. Value objects are not universally structs: consider default values, copying, serialization, and existing contracts. Prefer composition but retain required framework inheritance such as ErrorBoundary. Use cancellation for observation without canceling backend-owned runs. Do not add blocking async bridges, unsafe accessors, unsafe blocks, pooled buffers, or blanket ConfigureAwait(false) in UI code. Profile before low-level optimization. Upstream snippets are illustrative, not compiled project code.

Related upstream skill names are suggestions, not installed dependencies. Update only at an explicit reviewed commit, retaining license and required reference files and updating these digests.

## Declarative readability requirement

Write C# that reads declaratively, with intention-revealing names, pure transformations, immutable updates, named predicates, and pattern matching where these improve clarity. Read and apply the shared [declarative code guidance](../clean-code/declarative-code.md). Make authorization, transactions, awaited effects, cancellation, and ordering visible. LINQ is appropriate for queries, not hidden side effects; retain straightforward loops where clearer and avoid cosmetic DSLs, dense chains, or changes to behavior/performance merely for style. This requirement supplements the unchanged upstream skill.
