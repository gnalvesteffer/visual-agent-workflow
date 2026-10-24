# Source and local adaptation

This project skill adapts the official [.NET unit testing best practices](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-best-practices), maintained in the [dotnet/docs repository](https://github.com/dotnet/docs/blob/main/docs/core/testing/unit-testing-best-practices.md). The source was reviewed on 2026-10-24. Its guidance on fast, isolated, repeatable tests, behavior-focused names, Arrange/Act/Assert, minimal data, and avoiding infrastructure dependencies informs this skill. The wording here is original and tailored to this repository; it is not a verbatim copy.

The repository-specific test boundaries reflect its current xUnit projects and scripts. Concurrency and integration guidance is coordinated with [quality assurance](../quality-assurance/SKILL.md), which remains the owner of broader risk-based verification and evidence reporting.
