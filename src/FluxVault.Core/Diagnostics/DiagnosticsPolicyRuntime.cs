using FluxVault.Abstractions.Configuration;

namespace FluxVault.Core.Diagnostics;

public sealed class DiagnosticsPolicyRuntime(string programDataPath)
{
    private readonly Lock gate = new();
    private DiagnosticsPolicy current = DiagnosticsPolicy.CreateDefault(programDataPath);

    public DiagnosticsPolicy Current
    {
        get
        {
            lock (gate)
            {
                return current;
            }
        }
    }

    public void Update(DiagnosticsPolicy? policy)
    {
        lock (gate)
        {
            current = (policy ?? DiagnosticsPolicy.CreateDefault(programDataPath)).Normalise(programDataPath);
        }
    }
}
