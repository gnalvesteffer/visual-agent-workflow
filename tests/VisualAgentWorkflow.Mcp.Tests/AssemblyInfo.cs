using Xunit;

// These tests start real .NET stdio servers. Bound fixture concurrency independently
// of host CPU discovery so resource-constrained runners do not exhaust startup deadlines.
[assembly: CollectionBehavior(MaxParallelThreads = 2)]
