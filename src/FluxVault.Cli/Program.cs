using FluxVault.Cli;

using var cancellation = new CancellationTokenSource();
ConsoleCancelEventHandler? cancel = null;
if (args.Length > 0 && string.Equals(args[0], "setup-confirm", StringComparison.OrdinalIgnoreCase))
{
    cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    Console.CancelKeyPress += cancel;
}
try { return await FluxVaultCli.RunAsync(args, Console.Out, Console.Error, cancellation.Token); }
finally { if (cancel is not null) Console.CancelKeyPress -= cancel; }
