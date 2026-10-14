using System.IO;
using System.Text.Json;
using FluxVault.Core.Security;
using Npgsql;

namespace FluxVault.TestHost;

/// <summary>Executes the exact operational marker SQL only on the authenticated owned fixture connection.</summary>
internal static class AutomaticRollbackSqlProbe
{
    internal static async Task VerifyUninitialisedAsync(NpgsqlConnection connection,VaultBinding binding,string creatorSid,List<string> checks,CancellationToken ct)
    {
        var absent=await ApplyAsync(connection,binding,creatorSid,ct);
        if(!absent.GetProperty("AutomaticExtensionAbsent").GetBoolean())throw new InvalidOperationException("Absent migration rollback was not accepted.");
        checks.Add("actual operational rollback SQL accepts a wholly absent first-start extension without creating consent or work");
        await using var partial=new NpgsqlCommand("CREATE TABLE fv_control.automatic_identity(test integer)",connection);
        await partial.ExecuteNonQueryAsync(ct);
        try
        {
            try{await ApplyAsync(connection,binding,creatorSid,ct);throw new InvalidOperationException("Partial extension rollback was accepted.");}
            catch(PostgresException exception) when(exception.SqlState=="P0001")
            { checks.Add("actual operational rollback SQL refuses a partial extension before downgrade"); }
        }
        finally
        {
            partial.CommandText="ROLLBACK; DROP TABLE fv_control.automatic_identity";
            await partial.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    internal static async Task<JsonElement> ApplyAsync(NpgsqlConnection connection,VaultBinding binding,string creatorSid,CancellationToken ct)
    {
        using var resource=typeof(AutomaticRollbackSqlProbe).Assembly.GetManifestResourceStream("automatic-rollback-worker.ps1")!;
        using var reader=new StreamReader(resource);
        var worker=(await reader.ReadToEndAsync(ct)).Replace("\r", "");
        var start=worker.IndexOf("$sql=@'\n",StringComparison.Ordinal)+8;
        var end=worker.IndexOf("\n'@",start,StringComparison.Ordinal);
        if(start<8 || end<=start)throw new InvalidDataException("The operational SQL boundary changed.");
        var sql=worker[start..end].Replace("67539278-9292-4870-93cd-a011419228dc",binding.Id.Value.ToString(),StringComparison.Ordinal)
            .Replace("S-1-5-21-136112424-624261118-1239521417-1001",creatorSid,StringComparison.Ordinal);
        await using var command=new NpgsqlCommand(sql,connection);
        var value=(string)(await command.ExecuteScalarAsync(ct))!;
        using var result=JsonDocument.Parse(value);
        return result.RootElement.Clone();
    }
}
