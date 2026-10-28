# Explicit conversion insertion

## Decision

The Studio may offer a targeted conversion action after an output pin is
dropped onto an incompatible data input, but only for the already-supported
schema-1.39 string to integer conversion. The action is explicit and visible:
it creates an ordinary core.convert.string-to-integer node and two ordinary
data edges. It never performs implicit coercion or silently promotes a schema.

The source output and target input must both be unconnected. This keeps the
gesture atomic and avoids silently disconnecting an existing producer or
leaving its pure node orphaned. The resulting node is placed at the drop
position and the whole edit is one Studio undo operation. Other type pairs,
legacy graphs, occupied pins, read-only graphs, and unsupported schemas retain
the existing exact-type diagnostic.

## Compatibility boundary

The Core edit validates the endpoint kinds and exact types, enforces the
256-node limit, preserves execution connections, and validates the resulting
schema-1.39 definition. The parser and failure evidence remain the existing
StringIntegerConversion contract. The editor does not broaden the conversion
catalog or infer a migration from schema 1.31; schema promotion remains an
explicit, separately validated authoring action.

## Proof

Core tests cover successful typed insertion, stable execution preservation, and
the resulting valid definition. The graph editor exposes the action only for
the exact endpoint pair and keeps incompatible or occupied cases on the
existing recovery path. Browser coverage remains part of the workbench smoke
once its current library-region selectors are aligned with the rendered IDE
layout.
