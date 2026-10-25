# 0001: establish a small executable graph subset first

Start with a linear string workflow to prove stable graph identities, fail-closed validation, serialization, revision snapshots, and framework translation without requiring credentials or external side effects.

Target .NET 10 using SDK 10.0.402. Keep the core free of Agent Framework references. The intended adapter package is `Microsoft.Agents.AI.Workflows` 1.26.0, whose published NuGet metadata includes a net10.0 target. Framework integration must compile and execute against that version before being called verified; published package XML documentation is an API reference, not runtime evidence.

Only registered input, uppercase, reverse, and output node types at version 1.0.0 are accepted initially. Unsupported nodes or shapes fail validation rather than being interpreted approximately. This is an incremental implementation boundary, not a restriction on the long-term product design.

Connection traversal carries strings between `out` and `in` ports. Each non-input node has exactly one incoming link; each non-output node has exactly one outgoing link. The initial runtime therefore needs no custom branch/join scheduler. New semantics require explicit contracts, framework mappings, and tests.
