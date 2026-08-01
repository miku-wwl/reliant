namespace Reliant.Infrastructure.Provider;

/// <summary>
/// Test / development-only control surface for the sandbox provider, letting
/// tests switch the provider mode at runtime (e.g. simulate a timeout on the
/// first attempt, then succeed on the retry). Not intended for production use.
/// </summary>
public interface ISandboxProviderControl
{
    /// <summary>Switches the effective provider mode (e.g. "Success", "TimeoutBeforeProcessing").</summary>
    void SetMode(string mode);

    /// <summary>Number of provider-side operations created so far.</summary>
    int OperationCount { get; }
}
