using FluxVault.Abstractions.Ipc;

namespace FluxVault.App.Services;

internal static class AutomaticProtectionText
{
    internal static string Label(AutomaticProtectionState state) => state switch
    {
        AutomaticProtectionState.WaitingForChanges=>"Waiting for changes",
        AutomaticProtectionState.OutcomeUncertain=>"Outcome uncertain",
        _=>state.ToString()
    };
}
