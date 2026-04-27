namespace FluxVault.Cli;

public sealed record CliResult(int ExitCode, string StandardOutput, string StandardError);
