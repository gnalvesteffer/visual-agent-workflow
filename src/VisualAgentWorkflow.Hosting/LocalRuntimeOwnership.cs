namespace VisualAgentWorkflow.Hosting;

/// <summary>Local process ownership only. No distributed lease, fencing token or failover guarantee.</summary>
public interface ILocalRuntimeOwnershipStore
{
    IDisposable AcquireRuntimeOwnership();
}

internal static class LocalRuntimeOwnership
{
    public static IDisposable Acquire(string path)
    {
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new InvalidOperationException("Another local runtime owns this run store, or its ownership file is unavailable."); }
        // Never delete the ownership file: deleting/recreating it can split ownership across file identities.
    }
}
