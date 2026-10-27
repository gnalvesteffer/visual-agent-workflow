---
name: unit-testing
description: Use when writing or refactoring focused C# unit tests, choosing test boundaries, or reviewing test readability and determinism.
---

# Unit testing

Use this skill for focused tests of one unit's observable behavior. For broader verification planning, concurrency, integration boundaries, and reporting evidence, also use [quality assurance](../quality-assurance/SKILL.md). This project uses xUnit and existing test projects; do not add a test framework or mocking package without a concrete need.

## Design the test around behavior

- Test a public contract or externally observable outcome, not private methods, call order, or internal data structures unless those interactions are themselves the contract.
- Keep each test about one scenario and one behavior. Multiple assertions are fine when they describe the same outcome; split unrelated outcomes so failures point to one cause.
- Name tests so the unit, condition, and expected result are clear, for example `Resolve_UnknownParameter_ThrowsValidationException`.
- Structure tests declaratively as Arrange, Act, Assert. Keep setup and expected values obvious, and use the smallest input that demonstrates the behavior.
- Prefer a direct value assertion over reimplementing production logic in expected-value helpers. Use theories for multiple inputs that exercise the same rule.
- Avoid control flow, sleeps, and complex loops in tests. Use explicit synchronization and bounded waits when concurrency is the behavior under test.

## Keep tests deterministic and independent

- A unit test should run quickly, repeatably, and without human input. Do not call real external services, models, MCP servers, or the network from unit tests.
- Replace nondeterministic collaborators with the smallest useful fake or stub. Prefer a small handwritten test double when it makes behavior clearer; use interaction verification only when the absence, count, or order of an external effect is part of the behavior.
- Introduce a seam for time, randomness, IDs, or other nondeterministic input when a rule depends on it. Avoid broad mocking of collaborators whose real behavior is the subject of the test.
- Give each test its own mutable state and temporary directory. Tests must not depend on execution order, shared static state, ambient culture, timezone, or machine-specific paths.
- If filesystem durability, SQLite transactions, subprocesses, HTTP, framework integration, or browser behavior is material to the guarantee, use the repository's integration tests instead of disguising it as a unit test with mocks.

For subprocess protocol fixtures, isolate working-directory configuration as well as environment variables. The pure MCP demo uses a host builder with defaults disabled, so caller appsettings cannot crash or reconfigure the fixture or allocate unnecessary file watchers. `StudioStdioMcpTests.DemoFixtureDoesNotLoadCallerApplicationConfiguration` verifies discovery from a directory containing invalid application JSON.

## Make failures useful

- For a bug fix, add the smallest regression test that fails for the reported behavior before changing the implementation when practical. Confirm that the failure is the intended assertion, then rerun after the fix.
- Cover relevant boundary and failure cases, but do not add tests solely to raise a coverage number or assert every implementation detail.
- Avoid shared setup that hides important inputs. Use a small fixture builder only when it reduces repetition without obscuring each test's scenario.
- Run the focused test first, then the owning test project and the appropriate broader suite. Investigate unexpected failures instead of retrying until green.

## Fit this project's test boundaries

- Pure workflow contracts, validation, serialization, hashes, and editing rules belong in `VisualAgentWorkflow.Core.Tests`.
- Agent Framework and provider behavior belongs in `VisualAgentWorkflow.AgentFramework.Tests` or `VisualAgentWorkflow.Mcp.Tests`; use deterministic clients for unit behavior and controlled protocol/MCP fixtures when the real adapter boundary matters.
- Storage consistency, persistence, lifecycle, and API behavior belong in `VisualAgentWorkflow.Hosting.Tests` and should exercise the actual store or host boundary.
- Playwright user journeys belong in the existing scripts/browser checks, not unit tests.
- Keep actual model inference opt-in and separately categorized; standard cross-platform tests must not require model downloads or credentials.

For provenance and the project's adaptations, see [SOURCE.md](SOURCE.md).
