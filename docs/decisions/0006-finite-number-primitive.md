# Finite number primitive and direct MCP number pins

**Status: Accepted with bounded scope**  
**Scope: Blueprint-inspired authoring contracts**  
**Decision date: 2026-10-27**

## Context

The graph type catalog exposed strings, booleans, signed integers, and JSON,
but direct MCP tools whose top-level schema declared a numeric argument could
not be represented by a typed data pin. Treating the value as text would make
the tool contract ambiguous and could serialize a number as a quoted string.

## Decision

Schema 1.40 adds `number` as a portable finite IEEE-754 `System.Double` value.
It adds only a pure `Number constant` node and allows top-level MCP JSON Schema
`type: number` properties to become typed MCP input pins. The runtime emits
these values as JSON numbers.

Number constants reject NaN and both infinities. Connections require exact type
identity; there are no implicit integer/number conversions, operators,
workflow-parameter bindings, graph-function support, or nested-number pin
expansion in this increment. Nested MCP objects and arrays remain JSON pins.

## Compatibility and boundaries

- Schemas before 1.40 and their hashes remain unchanged.
- Schema 1.37 keeps its existing string/integer/boolean pin behavior.
- Number graphs remain straight-line direct-MCP graphs with the existing host
  grant, pinned contract, execution, bounds, and playback rules.
- `number` is not added to graph-function bodies or generic output values yet.
- Runtime validation repeats the finite check at the serialization boundary.

## Proof

Core tests cover catalog identity, typed number pin resolution, finite-value
validation, non-finite rejection, and schema-1.37 compatibility. Agent
Framework tests verify that a connected number constant reaches the MCP broker
as a JSON number rather than quoted text.
