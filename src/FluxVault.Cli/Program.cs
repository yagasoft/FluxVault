using FluxVault.Cli;

var exitCode = await FluxVaultCli.RunAsync(args, Console.Out, Console.Error);
return exitCode;
