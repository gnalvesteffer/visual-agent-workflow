# Bounded parallel graph blocks

The visual graph supports up to four sequential, explicit two-branch parallel blocks. Each is a visible fork and barrier, not an arrangement of nodes that happen to sit side by side. Within a block, branch 1 and branch 2 each receive the same incoming string and produce one string result. Blocks can run later in the workflow after an earlier join has completed, while nested or independently concurrent blocks remain unsupported.

```mermaid
flowchart LR
    Input[Connected string path] --> Fork[Parallel fork]
    Fork -->|branch_1| A[Branch 1 anchor]
    Fork -->|branch_2| B[Branch 2 anchor]
    A --> AWork[Branch 1 processing]
    B --> BWork[Branch 2 processing]
    AWork -->|branch_1| Join[Barrier join]
    BWork -->|branch_2| Join
    Join --> Result[JSON: branch_1 and branch_2]
    Result --> Next[Optional JSON selector]
```

Choose **Parallel block** from the node library while an ordinary string connection is selected. Studio inserts a fork, two named branch anchors, and a matching join. If the old path continued into a string node, Studio adds a root JSON selector after the join so that continuation remains valid. If the old path ended at Output, Output becomes JSON typed. The first block uses schema 1.14 or preserves a newer existing schema. Adding a second block upgrades the draft to schema 1.20, which supports up to four sequential blocks while leaving earlier schema definitions limited to one.

To add another block, select a string connection after the previous block's selector and insert **Parallel block** again. Each block has its own join, progress events, and JSON result. Selecting a fork, branch, or join and choosing **Remove parallel block…** previews and removes only that block, preserving the other blocks and reconnecting the surrounding path.

Add deterministic text operations, agent nodes, or MCP tools after either branch anchor. The branch anchors must remain the first nodes after the fork, and each path must connect to the join input with the same branch name. Branch paths are acyclic and cannot contain conditions, approvals, merges, another parallel block, or terminals. Existing compatible pin rewiring is available; drafts with incomplete or mismatched paths show validation issues and cannot be published or run. The JSON selector can turn the complete result or one named field back into text.

The runner maps the fork to explicit Agent Framework messages and uses the framework's concurrent in-process execution environment. It maps the join to a fan-in barrier that waits for one result from both branch endpoints. Join output is a deterministic JSON object with `branch_1` and `branch_2`, capped at 16,384 serialized characters. Node, transfer, join-progress, completion, and failure events preserve the values and actual connections for playback; replay performs no agent or tool calls.

If a branch fails or the run is cancelled, the workflow does not produce a joined result. External effects already completed by another branch are not rolled back. Agent/MCP services must tolerate concurrent calls when authors place them in separate branches. The current bound is four sequential parallel blocks per workflow, two branches per block, 256 total graph nodes, and 16,384 characters per joined result. There are no loops, dynamic branch counts, nested joins, branch-local conditional routing, or distributed transactions.

Each block can be removed by selecting any of its fork, branch, or join nodes and choosing **Remove parallel block…**. Studio previews the complete affected region before confirmation, reconnects the original incoming path to the following node, and makes the edit undoable. Published workflow revisions and recorded runs remain immutable.

See [architecture](architecture.md), [the user guide](user-guide.md#run-two-paths-in-parallel), and [roadmap](roadmap.md) for implementation status.
