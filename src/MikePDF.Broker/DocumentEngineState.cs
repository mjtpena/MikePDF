namespace MikePDF.Broker;

/// <summary>The life of a document engine as the <see cref="EngineBudget"/> manages it.</summary>
public enum DocumentEngineState
{
    /// <summary>An engine process is running (or being restarted after a crash) for this document.</summary>
    Running,

    /// <summary>No process: it gave way to a more recently used document. The next call starts one.</summary>
    Suspended,

    /// <summary>A call has asked for it and a process is being started and the file reopened.</summary>
    Resuming,

    /// <summary>The engine could not be brought back; every call says so.</summary>
    Failed,
}
