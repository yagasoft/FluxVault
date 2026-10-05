using System.IO;
using System.Text.Json;
using FluxVault.Abstractions.Configuration;
using FluxVault.Abstractions.Ipc;
using FluxVault.Abstractions.Policies;
using FluxVault.Abstractions.Security;
using FluxVault.App.Services;
using FluxVault.App.ViewModels;
using FluxVault.Core.Configuration;
using FluxVault.Core.Ipc;

namespace FluxVault.App.Tests;

public sealed class UnsentProtectionDraftContractTests
{
    [Fact]
    public async Task Exact_draft_and_linked_receipt_are_durable_before_save_dispatch_and_retired_after_acknowledgement()
    {
        using var files = new Files(); await files.InitialiseAsync();
        var model = files.Model();
        try
        {
            await model.RefreshAsync(); model.RepositoryPath = Path.Combine(files.Root,"submitted");
            files.Client.BeforeSave = request =>
            {
                var draft = files.Drafts.Read()!; var pending = files.Saves.Read()!;
                Assert.NotNull(draft); Assert.Equal(request.OperationId,draft.SaveOperationId);
                Assert.Equal(draft.DraftId,pending.ProtectionDraftId);
                Assert.Equal(draft.BaselineFingerprint,pending.ProtectionDraftBaselineFingerprint);
                Assert.Equal(Json(request.Configuration!),Json(draft.Configuration));
                Assert.Equal(Json(request.Configuration!),Json(pending.Configuration));
            };
            await model.SaveConfigurationCommand.ExecuteAsync(null);
            Assert.Equal(ProtectionSaveState.Saved,model.ProtectionSaveState);
            Assert.Null(files.Drafts.Read()); Assert.Null(files.Saves.Read());
            Assert.True(await model.PrepareForExitAsync());
            Assert.DoesNotContain(files.Client.Requests,r=>r.Command==FluxVaultIpcCommand.RunBackupNow);
        }
        finally { await StopAsync(model); }
    }

    [Fact]
    public async Task Failed_operation_association_preserves_both_records_and_prevents_save_and_backup_dispatch()
    {
        using var files = new Files(); await files.InitialiseAsync();
        var model = files.Model(new AssociationFailureStore(files.Drafts));
        try
        {
            await model.RefreshAsync(); model.RepositoryPath = Path.Combine(files.Root,"unsent");
            await model.RunBackupNowCommand.ExecuteAsync(null);
            var draft = files.Drafts.Read()!; var pending = files.Saves.Read()!;
            Assert.NotNull(draft); Assert.NotNull(pending); Assert.Null(draft.SaveOperationId);
            Assert.Equal(draft.DraftId,pending.ProtectionDraftId);
            Assert.True(model.HasUnconfirmedProtectionSave); Assert.Equal(draft.Configuration.RepositoryPath,model.RepositoryPath);
            Assert.Contains("kept",model.ProtectionSaveMessage,StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(files.Client.Requests,r=>r.Command is FluxVaultIpcCommand.SaveConfiguration or FluxVaultIpcCommand.RunBackupNow);
        }
        finally { await StopAsync(model); }
    }

    [Fact]
    public async Task Acknowledgement_preserves_newer_edits_in_a_rebased_draft_before_clearing_the_receipt()
    {
        using var files = new Files(); await files.InitialiseAsync(5000);
        files.Client.HoldSave = true;
        var observedSaves = new ObservedSaveStore(files.Saves);
        var model = files.Model(saves:observedSaves);
        try
        {
            await model.RefreshAsync(); model.RepositoryPath = Path.Combine(files.Root,"submitted");
            var save = model.RunBackupNowCommand.ExecuteAsync(null);
            await files.Client.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var id = files.Drafts.Read()!.DraftId;
            model.RepositoryPath = Path.Combine(files.Root,"newer");
            observedSaves.BeforeClear = pending =>
            {
                var draft = files.Drafts.Read()!;
                Assert.Equal(id,draft.DraftId); Assert.Equal(2,draft.BaselineRevision);
                Assert.Equal(PendingProtectionDraft.Fingerprint(pending.Configuration),draft.BaselineFingerprint);
                Assert.Null(draft.SaveOperationId); Assert.Equal(model.RepositoryPath,draft.Configuration.RepositoryPath);
            };
            files.Client.ReleaseSave.TrySetResult(); await save.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(files.Saves.Read()); Assert.NotNull(files.Drafts.Read());
            Assert.DoesNotContain(files.Client.Requests,r=>r.Command==FluxVaultIpcCommand.RunBackupNow);
            Assert.True(await model.PrepareForExitAsync());
            var reopened=files.Model();
            try { await reopened.RefreshAsync(); Assert.Equal(model.RepositoryPath,reopened.RepositoryPath); Assert.False(reopened.RequiresLocalProtectionDraftReview); }
            finally { await StopAsync(reopened); }
        }
        finally { files.Client.ReleaseSave.TrySetResult(); await StopAsync(model); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restart_correlates_both_interrupted_receipt_phases_without_overwriting_newer_edits_or_replaying(bool rebased)
    {
        using var files = new Files(); await files.InitialiseAsync(5000);
        var original = await files.Configuration.LoadAsync();
        var submitted = original with { RepositoryPath=Path.Combine(files.Root,"submitted") };
        var pending = new PendingConfigurationSave(files.Client.Id.Value,Guid.NewGuid(),1,submitted,false,[],[],
            ProtectionDraftId:Guid.NewGuid(),ProtectionDraftBaselineFingerprint:PendingProtectionDraft.Fingerprint(original));
        files.Saves.Reserve(pending);
        await files.Client.SendAsync(pending.Request); files.Client.Requests.Clear();
        var newer=submitted with { RepositoryPath=Path.Combine(files.Root,"newer") };
        files.Drafts.Write(null,new(pending.ProtectionDraftId!.Value,Guid.NewGuid(),files.Client.Id.Value,rebased?2:1,
            PendingProtectionDraft.Fingerprint(rebased?submitted:original),newer,rebased?null:pending.OperationId));
        var model=files.Model();
        try
        {
            await model.RefreshAsync(); Assert.Equal(newer.RepositoryPath,model.RepositoryPath);
            Assert.False(model.RequiresLocalProtectionDraftReview); Assert.True(model.HasUnconfirmedProtectionSave);
            Assert.False(model.CanSaveProtection);
            await model.CheckProtectionSaveOutcomeCommand.ExecuteAsync(null);
            Assert.Null(files.Saves.Read()); var kept=files.Drafts.Read()!;
            Assert.Equal(newer.RepositoryPath,kept.Configuration.RepositoryPath); Assert.Equal(2,kept.BaselineRevision); Assert.Null(kept.SaveOperationId);
            Assert.Single(files.Client.Requests,r=>r.Command==FluxVaultIpcCommand.GetOperationStatus);
            Assert.DoesNotContain(files.Client.Requests,r=>r.Command is FluxVaultIpcCommand.SaveConfiguration or FluxVaultIpcCommand.RunBackupNow);
        }
        finally { await StopAsync(model); }
    }

    [Fact]
    public async Task Explicit_discard_retires_exact_local_draft_and_a_new_edit_gets_a_new_identity()
    {
        using var files=new Files(); await files.InitialiseAsync(5000);
        var model=files.Model();
        try
        {
            await model.RefreshAsync(); var saved=model.RepositoryPath;
            model.RepositoryPath=Path.Combine(files.Root,"discard-me");
            Assert.True(await model.PrepareForExitAsync()); var first=files.Drafts.Read()!;
        }
        finally { await StopAsync(model); }
        var reopened=files.Model();
        try
        {
            var first=files.Drafts.Read()!; await reopened.RefreshAsync();
            await reopened.DiscardConfigurationChangesCommand.ExecuteAsync(null);
            Assert.Null(files.Drafts.Read()); Assert.Equal((await files.Configuration.LoadAsync()).RepositoryPath,reopened.RepositoryPath);
            reopened.RepositoryPath=Path.Combine(files.Root,"fresh-edit"); Assert.True(await reopened.PrepareForExitAsync());
            Assert.NotEqual(first.DraftId,files.Drafts.Read()!.DraftId);
        }
        finally { await StopAsync(reopened); }
    }

    private static async Task StopAsync(MainWindowViewModel model)
    {
        await model.CancelAndJoinLocalProtectionDraftWriterAsync(); await model.StopRepositoryReadsAsync();
    }

    [Fact]
    public async Task Receipt_cleanup_failure_keeps_rebased_newer_draft_and_original_receipt_for_restart_check()
    {
        using var files=new Files(); await files.InitialiseAsync(5000); files.Client.HoldSave=true;
        var saves=new ObservedSaveStore(files.Saves){BeforeClear=_=>throw new IOException("Fixture ledger cleanup failure")};
        var model=files.Model(saves:saves);
        try
        {
            await model.RefreshAsync(); model.RepositoryPath=Path.Combine(files.Root,"submitted");
            var save=model.RunBackupNowCommand.ExecuteAsync(null);
            await files.Client.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); model.RepositoryPath=Path.Combine(files.Root,"newer");
            files.Client.ReleaseSave.TrySetResult(); await save.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ProtectionSaveState.Unknown,model.ProtectionSaveState); Assert.NotNull(files.Saves.Read());
            Assert.Equal(2,files.Drafts.Read()!.BaselineRevision); Assert.Null(files.Drafts.Read()!.SaveOperationId);
            Assert.True(await model.PrepareForExitAsync());
            var reopened=files.Model();
            try
            {
                await reopened.RefreshAsync(); Assert.Equal(model.RepositoryPath,reopened.RepositoryPath); Assert.True(reopened.HasUnconfirmedProtectionSave);
                await reopened.CheckProtectionSaveOutcomeCommand.ExecuteAsync(null); Assert.Null(files.Saves.Read());
                Assert.Equal(model.RepositoryPath,files.Drafts.Read()!.Configuration.RepositoryPath);
            }
            finally { await StopAsync(reopened); }
            Assert.Single(files.Client.Requests,r=>r.Command==FluxVaultIpcCommand.SaveConfiguration);
            Assert.DoesNotContain(files.Client.Requests,r=>r.Command==FluxVaultIpcCommand.RunBackupNow);
        }
        finally { files.Client.ReleaseSave.TrySetResult(); await StopAsync(model); }
    }

    [Fact]
    public async Task Acknowledgement_retains_edits_arriving_during_exact_draft_retirement_before_receipt_release()
    {
        using var files=new Files(); await files.InitialiseAsync(5000);
        var drafts=new HeldRetirementStore(files.Drafts); var saves=new ObservedSaveStore(files.Saves);
        var model=files.Model(drafts,saves);
        try
        {
            await model.RefreshAsync(); model.RepositoryPath=Path.Combine(files.Root,"submitted");
            var save=model.RunBackupNowCommand.ExecuteAsync(null);
            await drafts.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); var original=files.Drafts.Read()!;
            model.RepositoryPath=Path.Combine(files.Root,"during-retirement");
            saves.BeforeClear=_=>
            {
                var draft=files.Drafts.Read()!; Assert.Equal(original.DraftId,draft.DraftId);
                Assert.Equal(2,draft.BaselineRevision); Assert.Null(draft.SaveOperationId);
                Assert.Equal(model.RepositoryPath,draft.Configuration.RepositoryPath);
            };
            drafts.Release.TrySetResult(); await save.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(files.Saves.Read()); Assert.Equal(model.RepositoryPath,files.Drafts.Read()!.Configuration.RepositoryPath);
            Assert.DoesNotContain(files.Client.Requests,r=>r.Command==FluxVaultIpcCommand.RunBackupNow);
        }
        finally { drafts.Release.TrySetResult(); await StopAsync(model); }
    }

    [Theory]
    [InlineData("mirror")]
    [InlineData("selection")]
    [InlineData("regex")]
    public async Task Structurally_corrupt_editable_collections_are_preserved_without_partial_UI_application(string malformed)
    {
        using var files=new Files(); await files.InitialiseAsync(); var saved=await files.Configuration.LoadAsync();
        var configuration=saved with{RepositoryPath=Path.Combine(files.Root,"must-not-apply")};
        configuration=malformed switch
        {
            "mirror"=>configuration with{MirrorSet=new([null!])},
            "selection"=>configuration with{SelectionRules=[null!]},
            "regex"=>configuration with{SelectionRules=[new("r",files.Root,ProtectionSelectionMode.RecursiveFolder,CompressionPreference.Zstd,ResourceProfile.Balanced,true,IncludeRegexRules:[null!])]},
            _=>throw new InvalidOperationException()
        };
        File.WriteAllText(files.DraftPath,JsonSerializer.Serialize(new PendingProtectionDraft(Guid.NewGuid(),Guid.NewGuid(),files.Client.Id.Value,1,
            PendingProtectionDraft.Fingerprint(saved),configuration),FileConfigurationSaveOperationStore.JsonOptions));
        var bytes=File.ReadAllBytes(files.DraftPath); var model=files.Model();
        try
        {
            await model.RefreshAsync(); Assert.True(model.RequiresLocalProtectionDraftReview); Assert.Equal(saved.RepositoryPath,model.RepositoryPath);
            Assert.Equal(bytes,File.ReadAllBytes(files.DraftPath)); await model.RunBackupNowCommand.ExecuteAsync(null);
            Assert.DoesNotContain(files.Client.Requests,r=>r.Command is FluxVaultIpcCommand.SaveConfiguration or FluxVaultIpcCommand.RunBackupNow);
        }
        finally { await StopAsync(model); }
    }

    [Fact]
    public async Task A_new_edit_during_discard_retirement_is_durably_retained_without_applying_saved_settings()
    {
        using var files=new Files(); await files.InitialiseAsync(5000); var saved=await files.Configuration.LoadAsync();
        files.Drafts.Write(null,new(Guid.NewGuid(),Guid.NewGuid(),files.Client.Id.Value,1,PendingProtectionDraft.Fingerprint(saved),
            saved with{RepositoryPath=Path.Combine(files.Root,"discard-me")}));
        var drafts=new HeldRetirementStore(files.Drafts); var model=files.Model(drafts);
        try
        {
            await model.RefreshAsync(); var discard=model.DiscardConfigurationChangesCommand.ExecuteAsync(null);
            await drafts.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); model.RepositoryPath=Path.Combine(files.Root,"newer-edit");
            drafts.Release.TrySetResult(); await discard.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(Path.Combine(files.Root,"newer-edit"),model.RepositoryPath);
            Assert.Equal(model.RepositoryPath,files.Drafts.Read()!.Configuration.RepositoryPath);
            Assert.Contains("kept",model.ProtectionSaveMessage,StringComparison.OrdinalIgnoreCase);
        }
        finally { drafts.Release.TrySetResult(); await StopAsync(model); }
    }

    [Fact]
    public async Task Two_edits_during_successive_acknowledgement_publications_keep_exact_correlation_for_restart()
    {
        using var files=new Files(); await files.InitialiseAsync(5000);
        var drafts=new HeldRetirementStore(files.Drafts){HoldPublicationsAfterRetirement=true};
        var saves=new ObservedSaveStore(files.Saves){BeforeClear=_=>throw new IOException("Fixture receipt cleanup interruption")};
        var model=files.Model(drafts,saves); Task? save=null;
        try
        {
            await model.RefreshAsync(); model.RepositoryPath=Path.Combine(files.Root,"submitted");
            save=model.RunBackupNowCommand.ExecuteAsync(null);
            await drafts.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); var identity=files.Drafts.Read()!.DraftId;
            model.RepositoryPath=Path.Combine(files.Root,"newer-1"); drafts.Release.TrySetResult();
            await drafts.PublicationStarted[0].Task.WaitAsync(TimeSpan.FromSeconds(5));
            model.RepositoryPath=Path.Combine(files.Root,"newer-2"); drafts.ReleasePublication[0].TrySetResult();
            await drafts.PublicationStarted[1].Task.WaitAsync(TimeSpan.FromSeconds(5));
            model.RepositoryPath=Path.Combine(files.Root,"newer-3"); drafts.ReleasePublication[1].TrySetResult();
            await save.WaitAsync(TimeSpan.FromSeconds(5));
            var kept=files.Drafts.Read()!; var pending=files.Saves.Read()!;
            Assert.Equal(identity,kept.DraftId); Assert.Equal(identity,pending.ProtectionDraftId);
            Assert.Equal(model.RepositoryPath,kept.Configuration.RepositoryPath); Assert.Equal(2,kept.BaselineRevision);
            var reopened=files.Model();
            try
            {
                await reopened.RefreshAsync(); Assert.Equal(model.RepositoryPath,reopened.RepositoryPath);
                Assert.False(reopened.RequiresLocalProtectionDraftReview); Assert.True(reopened.HasUnconfirmedProtectionSave);
                await reopened.CheckProtectionSaveOutcomeCommand.ExecuteAsync(null); Assert.Null(files.Saves.Read());
                Assert.Equal(model.RepositoryPath,files.Drafts.Read()!.Configuration.RepositoryPath);
            }
            finally { await StopAsync(reopened); }
            Assert.Single(files.Client.Requests,r=>r.Command==FluxVaultIpcCommand.SaveConfiguration);
            Assert.DoesNotContain(files.Client.Requests,r=>r.Command==FluxVaultIpcCommand.RunBackupNow);
        }
        finally
        {
            drafts.Release.TrySetResult(); foreach(var release in drafts.ReleasePublication)release.TrySetResult();
            if(save is not null)await save.WaitAsync(TimeSpan.FromSeconds(5)); await StopAsync(model);
        }
    }

    [Fact]
    public async Task Restart_after_draft_retirement_keeps_original_correlation_when_exit_retains_an_edit_before_receipt_check()
    {
        using var files=new Files(); await files.InitialiseAsync(5000); var original=await files.Configuration.LoadAsync();
        var submitted=original with{RepositoryPath=Path.Combine(files.Root,"submitted")};
        var pending=new PendingConfigurationSave(files.Client.Id.Value,Guid.NewGuid(),1,submitted,false,[],[],
            ProtectionDraftId:Guid.NewGuid(),ProtectionDraftBaselineFingerprint:PendingProtectionDraft.Fingerprint(original));
        files.Saves.Reserve(pending); await files.Client.SendAsync(pending.Request); files.Client.Requests.Clear();
        var model=files.Model();
        try
        {
            await model.RefreshAsync(); Assert.Null(files.Drafts.Read()); Assert.True(model.HasUnconfirmedProtectionSave);
            model.RepositoryPath=Path.Combine(files.Root,"edit-before-check"); Assert.True(await model.PrepareForExitAsync());
            Assert.Equal(pending.ProtectionDraftId,files.Drafts.Read()!.DraftId);
            var reopened=files.Model();
            try
            {
                await reopened.RefreshAsync(); Assert.Equal(model.RepositoryPath,reopened.RepositoryPath); Assert.False(reopened.RequiresLocalProtectionDraftReview);
                await reopened.CheckProtectionSaveOutcomeCommand.ExecuteAsync(null); Assert.Null(files.Saves.Read());
                Assert.Equal(model.RepositoryPath,files.Drafts.Read()!.Configuration.RepositoryPath);
            }
            finally { await StopAsync(reopened); }
            Assert.DoesNotContain(files.Client.Requests,r=>r.Command is FluxVaultIpcCommand.SaveConfiguration or FluxVaultIpcCommand.RunBackupNow);
        }
        finally { await StopAsync(model); }
    }

    [Fact]
    public async Task Exit_refuses_an_in_flight_receipt_check_that_can_publish_local_draft_state()
    {
        using var files=new Files(); await files.InitialiseAsync(5000); var original=await files.Configuration.LoadAsync();
        var pending=new PendingConfigurationSave(files.Client.Id.Value,Guid.NewGuid(),1,original,false,[],[]);
        files.Saves.Reserve(pending); await files.Client.SendAsync(pending.Request); files.Client.HoldReceipt=true;
        var model=files.Model(); Task? check=null;
        try
        {
            await model.RefreshAsync(); check=model.CheckProtectionSaveOutcomeCommand.ExecuteAsync(null);
            await files.Client.ReceiptStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(await model.PrepareForExitAsync()); Assert.False(model.IsPreparingForExit);
            Assert.Contains("finish",model.LocalProtectionDraftMessage,StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(files.Saves.Read());
            files.Client.ReleaseReceipt.TrySetResult(); await check.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(files.Saves.Read()); Assert.True(await model.PrepareForExitAsync());
        }
        finally
        {
            files.Client.ReleaseReceipt.TrySetResult(); if(check is not null)await check.WaitAsync(TimeSpan.FromSeconds(5)); await StopAsync(model);
        }
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("record")]
    [InlineData("receipt")]
    public async Task Discard_rechecks_edits_draft_and_receipt_after_status_await(string change)
    {
        using var files=new Files(); await files.InitialiseAsync(5000);
        var original=await files.Configuration.LoadAsync();
        var draft=files.Drafts.Write(null,new(Guid.NewGuid(),Guid.NewGuid(),files.Client.Id.Value,1,
            PendingProtectionDraft.Fingerprint(original),original with{RepositoryPath=Path.Combine(files.Root,"local-draft")}));
        var model=files.Model();
        try
        {
            await model.RefreshAsync();
            files.Client.BeforeStatus=()=>
            {
                files.Client.BeforeStatus=null;
                if(change=="edit")model.RepositoryPath=Path.Combine(files.Root,"newer-edit");
                if(change=="record")files.Drafts.Write(draft,draft with{RecordId=Guid.NewGuid(),Configuration=draft.Configuration with{RepositoryPath=Path.Combine(files.Root,"another-session")}});
                if(change=="receipt")files.Saves.Reserve(new(files.Client.Id.Value,Guid.NewGuid(),1,original,false,[],[]));
            };
            await model.DiscardConfigurationChangesCommand.ExecuteAsync(null);
            Assert.NotNull(files.Drafts.Read());
            Assert.Equal(change=="edit"?Path.Combine(files.Root,"newer-edit"):draft.Configuration.RepositoryPath,model.RepositoryPath);
            Assert.Contains("kept",model.ProtectionSaveMessage,StringComparison.OrdinalIgnoreCase);
            if(change=="receipt")Assert.NotNull(files.Saves.Read());
            Assert.DoesNotContain(files.Client.Requests,r=>r.Command is FluxVaultIpcCommand.SaveConfiguration or FluxVaultIpcCommand.RunBackupNow);
        }
        finally { await StopAsync(model); }
    }

    [Fact]
    public async Task Explicit_discard_quarantines_corrupt_unsent_bytes_and_restores_verified_settings()
    {
        using var files=new Files(); await files.InitialiseAsync();
        File.WriteAllText(files.DraftPath,"{ interrupted"); var bytes=File.ReadAllBytes(files.DraftPath);
        var model=files.Model();
        try
        {
            await model.RefreshAsync(); Assert.True(model.RequiresLocalProtectionDraftReview);
            await model.DiscardConfigurationChangesCommand.ExecuteAsync(null);
            Assert.False(model.RequiresLocalProtectionDraftReview); Assert.False(File.Exists(files.DraftPath));
            var quarantine=Assert.Single(Directory.GetFiles(files.Root,"draft.json.*.quarantine"));
            Assert.Equal(bytes,File.ReadAllBytes(quarantine)); Assert.Contains(quarantine,model.LocalProtectionDraftMessage);
            Assert.Equal((await files.Configuration.LoadAsync()).RepositoryPath,model.RepositoryPath);
        }
        finally { await StopAsync(model); }
    }

    [Fact]
    public async Task Failed_exit_flush_keeps_edits_and_allows_a_later_retry_in_the_same_window()
    {
        using var files=new Files(); await files.InitialiseAsync(5000);
        var model=files.Model();
        try
        {
            await model.RefreshAsync(); model.RepositoryPath=Path.Combine(files.Root,"retry-me");
            using(var held=new FileStream(files.DraftPath+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
            {
                Assert.False(await model.PrepareForExitAsync());
                Assert.False(model.IsPreparingForExit); Assert.True(model.CanSaveProtection);
                Assert.Contains("Exit stopped",model.LocalProtectionDraftMessage); Assert.False(File.Exists(files.DraftPath));
            }
            Assert.Equal(Path.Combine(files.Root,"retry-me"),model.RepositoryPath);
            Assert.True(await model.PrepareForExitAsync()); Assert.Equal(model.RepositoryPath,files.Drafts.Read()!.Configuration.RepositoryPath);
            Assert.True(model.IsPreparingForExit); Assert.False(model.CanSaveProtection); Assert.False(model.CanOpenOptions);
            Assert.DoesNotContain(files.Client.Requests,r=>r.Command is FluxVaultIpcCommand.SaveConfiguration or FluxVaultIpcCommand.RunBackupNow);
        }
        finally { await StopAsync(model); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rejected_or_lost_acknowledgement_keeps_edits_and_prevents_dependent_backup(bool lost)
    {
        using var files=new Files(); await files.InitialiseAsync(5000);
        files.Client.RejectSave=!lost; files.Client.LoseSaveAcknowledgement=lost;
        var model=files.Model();
        try
        {
            await model.RefreshAsync(); model.RepositoryPath=Path.Combine(files.Root,"pending");
            await model.RunBackupNowCommand.ExecuteAsync(null);
            Assert.Equal(model.RepositoryPath,files.Drafts.Read()!.Configuration.RepositoryPath);
            Assert.Contains("kept",model.ProtectionSaveMessage,StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(files.Client.Requests,r=>r.Command==FluxVaultIpcCommand.RunBackupNow);
            if(lost)
            {
                Assert.NotNull(files.Saves.Read()); Assert.NotNull(files.Drafts.Read()!.SaveOperationId);
                Assert.True(await model.PrepareForExitAsync()); var reopened=files.Model();
                try
                {
                    await reopened.RefreshAsync(); Assert.Equal(model.RepositoryPath,reopened.RepositoryPath);
                    await reopened.CheckProtectionSaveOutcomeCommand.ExecuteAsync(null);
                    Assert.Null(files.Saves.Read()); Assert.Null(files.Drafts.Read());
                }
                finally { await StopAsync(reopened); }
            }
            else { Assert.Null(files.Saves.Read()); Assert.Null(files.Drafts.Read()!.SaveOperationId); }
            Assert.Single(files.Client.Requests,r=>r.Command==FluxVaultIpcCommand.SaveConfiguration);
        }
        finally { await StopAsync(model); }
    }

    [Fact]
    public async Task Local_preparation_failure_reports_local_retention_and_allows_retry_without_false_service_uncertainty()
    {
        using var files=new Files(); await files.InitialiseAsync(5000); var model=files.Model();
        try
        {
            await model.RefreshAsync(); model.RepositoryPath=Path.Combine(files.Root,"pending");
            using(var held=new FileStream(files.DraftPath+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
            {
                await model.RunBackupNowCommand.ExecuteAsync(null);
                Assert.Equal(ProtectionSaveState.Failed,model.ProtectionSaveState);
                Assert.Contains("local",model.ProtectionSaveMessage,StringComparison.OrdinalIgnoreCase);
                Assert.False(model.HasUnconfirmedProtectionSave); Assert.Null(files.Saves.Read());
                Assert.DoesNotContain(files.Client.Requests,r=>r.Command is FluxVaultIpcCommand.SaveConfiguration or FluxVaultIpcCommand.RunBackupNow);
            }
            await model.SaveConfigurationCommand.ExecuteAsync(null); Assert.Equal(ProtectionSaveState.Saved,model.ProtectionSaveState);
            Assert.Null(files.Drafts.Read()); Assert.Single(files.Client.Requests,r=>r.Command==FluxVaultIpcCommand.SaveConfiguration);
        }
        finally { await StopAsync(model); }
    }

    [Fact]
    public async Task Automatic_local_writes_coalesce_rapid_edits_and_use_the_saved_delay_policy()
    {
        using var files=new Files();await files.InitialiseAsync(1300);
        var observed=new ObservedDraftStore(files.Drafts);
        var model=files.Model(observed);
        try
        {
            await model.RefreshAsync();var clock=System.Diagnostics.Stopwatch.StartNew();
            model.RepositoryPath=Path.Combine(files.Root,"typing-1");
            model.RepositoryPath=Path.Combine(files.Root,"typing-2");
            model.RepositoryPath=Path.Combine(files.Root,"final-draft");
            await observed.Published.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(clock.ElapsedMilliseconds>=1200,"The saved draft delay was bypassed.");
            Assert.Equal(1,observed.WriteCount);
            Assert.Equal(model.RepositoryPath,files.Drafts.Read()!.Configuration.RepositoryPath);
            Assert.DoesNotContain(files.Client.Requests,r=>r.Command is FluxVaultIpcCommand.SaveConfiguration or FluxVaultIpcCommand.RunBackupNow);
        }
        finally{await model.PrepareForExitAsync();await model.StopRepositoryReadsAsync();}
    }

    [Fact]
    public async Task Never_dispatched_edits_survive_joined_exit_and_restart_without_saving_or_backing_up()
    {
        using var files = new Files();
        await files.InitialiseAsync();
        var before = await files.Configuration.LoadAsync();
        var model = files.Model();
        await model.RefreshAsync();
        model.RepositoryPath = Path.Combine(files.Root,"pending-repository");
        model.FileBrowser.ReplaceSelectionRule(new("pending",files.Root,ProtectionSelectionMode.RecursiveFolder,CompressionPreference.Zstd,ResourceProfile.Balanced,true));
        Assert.True(await model.PrepareForExitAsync());
        await model.StopRepositoryReadsAsync();
        var persisted = files.Drafts.Read()!;
        Assert.Equal(files.Client.Id.Value,persisted.RepositoryId);
        Assert.Equal(1,persisted.BaselineRevision);
        Assert.Equal(PendingProtectionDraft.Fingerprint(before),persisted.BaselineFingerprint);
        Assert.Equal(model.RepositoryPath,persisted.Configuration.RepositoryPath);
        Assert.Equal(Json(before),Json(await files.Configuration.LoadAsync()));
        var reopened = files.Model();
        try
        {
            await reopened.RefreshAsync();
            Assert.Equal(model.RepositoryPath,reopened.RepositoryPath);
            Assert.Equal("pending",Assert.Single(reopened.FileBrowser.GetSelectionRules()).Id);
            Assert.Single(reopened.FileBrowser.PendingChanges);
            Assert.False(reopened.RequiresLocalProtectionDraftReview);
            Assert.Contains("recover",reopened.LocalProtectionDraftMessage,StringComparison.OrdinalIgnoreCase);
            Assert.All(files.Client.Requests,r=>Assert.Equal(FluxVaultIpcCommand.GetStatus,r.Command));
            Assert.True(await reopened.PrepareForExitAsync());
        }
        finally{await reopened.StopRepositoryReadsAsync();}
    }

    [Theory]
    [InlineData("repository")]
    [InlineData("revision")]
    [InlineData("fingerprint")]
    [InlineData("corrupt")]
    public async Task Unrelated_or_unreadable_draft_is_preserved_and_blocks_save_and_dependent_backup(string conflict)
    {
        using var files = new Files();
        await files.InitialiseAsync();
        var before = await files.Configuration.LoadAsync();
        var draft = new PendingProtectionDraft(Guid.NewGuid(),Guid.NewGuid(),files.Client.Id.Value,1,
            PendingProtectionDraft.Fingerprint(before),before with{RepositoryPath=Path.Combine(files.Root,"old-draft")});
        if(conflict=="repository")draft=draft with{RepositoryId=Guid.NewGuid()};
        if(conflict=="revision")draft=draft with{BaselineRevision=2};
        if(conflict=="fingerprint")draft=draft with{BaselineFingerprint=new string('A',64)};
        files.Drafts.Write(null,draft);
        if(conflict=="corrupt")File.WriteAllText(files.DraftPath,"{ interrupted");
        var bytes=File.ReadAllBytes(files.DraftPath);
        var model=files.Model();
        try
        {
            await model.RefreshAsync();
            Assert.True(model.RequiresLocalProtectionDraftReview);
            Assert.False(model.CanSaveProtection);
            await model.RunBackupNowCommand.ExecuteAsync(null);
            Assert.DoesNotContain(files.Client.Requests,r=>r.Command is FluxVaultIpcCommand.SaveConfiguration or FluxVaultIpcCommand.RunBackupNow);
            Assert.Equal(bytes,File.ReadAllBytes(files.DraftPath));
            Assert.Equal(Json(before),Json(await files.Configuration.LoadAsync()));
            Assert.Contains("kept",model.LocalProtectionDraftMessage,StringComparison.OrdinalIgnoreCase);
        }
        finally{await model.StopRepositoryReadsAsync();}
    }

    [Fact]
    public async Task Incomplete_repository_edit_survives_restart_as_a_draft()
    {
        using var files=new Files();await files.InitialiseAsync();
        var model=files.Model();await model.RefreshAsync();model.RepositoryPath=string.Empty;
        Assert.True(await model.PrepareForExitAsync());await model.StopRepositoryReadsAsync();
        var reopened=files.Model();
        try
        {
            await reopened.RefreshAsync();Assert.Equal(string.Empty,reopened.RepositoryPath);
            Assert.False(reopened.RequiresLocalProtectionDraftReview);
            Assert.True(await reopened.PrepareForExitAsync());
            Assert.DoesNotContain(files.Client.Requests,r=>r.Command is FluxVaultIpcCommand.SaveConfiguration or FluxVaultIpcCommand.RunBackupNow);
        }
        finally{await reopened.StopRepositoryReadsAsync();}
    }

    private static string Json(FluxVaultConfiguration configuration)=>JsonSerializer.Serialize(configuration);
    private sealed class Files:IDisposable
    {
        public string Root{get;}=Path.Combine(Path.GetTempPath(),"FluxVault-unsent-flow-"+Guid.NewGuid().ToString("N"));
        public string DraftPath=>Path.Combine(Root,"draft.json");
        public FileProtectionDraftStore Drafts=>new(DraftPath);
        public FileConfigurationSaveOperationStore Saves=>new(Path.Combine(Root,"pending-save.json"));
        public FileFluxVaultConfigurationStore Configuration=>new(Path.Combine(Root,"saved.json"),Root);
        public Client Client{get;}
        public Files(){Directory.CreateDirectory(Root);Client=new(Configuration);}
        public Task InitialiseAsync(int delay=500)=>Configuration.SaveAsync(FluxVaultConfiguration.CreateDefault(Root) with{ProtectionDraft=new(delay)});
        public MainWindowViewModel Model(IProtectionDraftStore? drafts=null,IConfigurationSaveOperationStore? saves=null)=>new(Client,TimeSpan.FromHours(1),new FileBrowserViewModel(new EmptyFiles()),new Controller(),new Destination(),new Overwrite(),
            saveOperationStore:saves??Saves,protectionDraftStore:drafts??Drafts);
        public void Dispose()=>Directory.Delete(Root,true);
    }
    private sealed class ObservedDraftStore(IProtectionDraftStore inner):IProtectionDraftStore
    {
        public int WriteCount{get;private set;}
        public TaskCompletionSource Published{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public PendingProtectionDraft? Read()=>inner.Read();
        public PendingProtectionDraft Write(PendingProtectionDraft? expected,PendingProtectionDraft next,CancellationToken cancellationToken=default)
        {
            var result=inner.Write(expected,next,cancellationToken);WriteCount++;Published.TrySetResult();return result;
        }
        public void Clear(PendingProtectionDraft expected)=>inner.Clear(expected);
    }
    private sealed class AssociationFailureStore(IProtectionDraftStore inner):IProtectionDraftStore
    {
        public PendingProtectionDraft? Read()=>inner.Read();
        public PendingProtectionDraft Write(PendingProtectionDraft? expected,PendingProtectionDraft next,CancellationToken cancellationToken=default)
        {
            if(next.SaveOperationId is not null)throw new IOException("Fixture association failure");
            return inner.Write(expected,next,cancellationToken);
        }
        public void Clear(PendingProtectionDraft expected)=>inner.Clear(expected);
    }
    private sealed class HeldRetirementStore(IProtectionDraftStore inner):IProtectionDraftStore
    {
        public TaskCompletionSource Started{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HoldPublicationsAfterRetirement{get;init;}
        public TaskCompletionSource[] PublicationStarted{get;}=[new(TaskCreationOptions.RunContinuationsAsynchronously),new(TaskCreationOptions.RunContinuationsAsynchronously)];
        public TaskCompletionSource[] ReleasePublication{get;}=[new(TaskCreationOptions.RunContinuationsAsynchronously),new(TaskCreationOptions.RunContinuationsAsynchronously)];
        private bool retired;
        private int publications;
        public PendingProtectionDraft? Read()=>inner.Read();
        public PendingProtectionDraft Write(PendingProtectionDraft? expected,PendingProtectionDraft next,CancellationToken cancellationToken=default)
        {
            if(retired && HoldPublicationsAfterRetirement && publications<2)
            {
                var index=publications++; PublicationStarted[index].TrySetResult();
                if(!ReleasePublication[index].Task.Wait(TimeSpan.FromSeconds(5)))throw new TimeoutException("Fixture publication timeout");
            }
            return inner.Write(expected,next,cancellationToken);
        }
        public void Clear(PendingProtectionDraft expected)
        {
            Started.TrySetResult(); if(!Release.Task.Wait(TimeSpan.FromSeconds(5)))throw new TimeoutException("Fixture retirement timeout");
            inner.Clear(expected); retired=true;
        }
    }
    private sealed class ObservedSaveStore(IConfigurationSaveOperationStore inner):IConfigurationSaveOperationStore
    {
        public Action<PendingConfigurationSave>? BeforeClear{get;set;}
        public PendingConfigurationSave? Read()=>inner.Read();
        public void Reserve(PendingConfigurationSave save)=>inner.Reserve(save);
        public void Clear(PendingConfigurationSave save){BeforeClear?.Invoke(save);inner.Clear(save);}
    }
    private sealed class Client(IFluxVaultConfigurationStore configuration):IFluxVaultServiceClient
    {
        public VaultId Id{get;}=VaultId.New();
        public List<FluxVaultIpcRequest> Requests{get;}=[];
        private long revision=1;
        private FluxVaultIpcResponse? receipt;
        public bool HoldSave{get;set;}
        public bool RejectSave{get;set;}
        public bool LoseSaveAcknowledgement{get;set;}
        public bool HoldReceipt{get;set;}
        public TaskCompletionSource ReceiptStarted{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseReceipt{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action<FluxVaultIpcRequest>? BeforeSave{get;set;}
        public Action? BeforeStatus{get;set;}
        public TaskCompletionSource SaveStarted{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSave{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<FluxVaultIpcResponse> SendAsync(FluxVaultIpcRequest request,CancellationToken cancellationToken=default)
        {
            Requests.Add(request);
            if(request.Command==FluxVaultIpcCommand.GetStatus)
            {
                BeforeStatus?.Invoke();
                return FluxVaultIpcResponse.WithStatus(new(true,await configuration.LoadAsync(cancellationToken),"Fixture",null,[],[],TrackedEntries:[]))
                    with{VaultId=Id,VaultRevision=revision};
            }
            if(request.Command==FluxVaultIpcCommand.SaveConfiguration)
            {
                BeforeSave?.Invoke(request); SaveStarted.TrySetResult();
                if(HoldSave)await ReleaseSave.Task.WaitAsync(cancellationToken);
                if(RejectSave)return FluxVaultIpcResponse.Failure("Fixture save rejection") with{ErrorCode=FluxVaultIpcErrorCode.InvalidRequest};
                await configuration.SaveAsync(request.Configuration!,cancellationToken); revision++;
                receipt=FluxVaultIpcResponse.Ok() with{VaultId=Id,VaultRevision=revision,OperationId=request.OperationId};
                return LoseSaveAcknowledgement ? FluxVaultIpcResponse.Failure("Fixture lost acknowledgement") with{ErrorCode=FluxVaultIpcErrorCode.OutcomeUnknown} : receipt;
            }
            if(request.Command==FluxVaultIpcCommand.GetOperationStatus && receipt?.OperationId==request.OperationId)
            {
                ReceiptStarted.TrySetResult(); if(HoldReceipt)await ReleaseReceipt.Task.WaitAsync(cancellationToken); return receipt!;
            }
            if(request.Command==FluxVaultIpcCommand.GetOperationStatus)return FluxVaultIpcResponse.Failure("Fixture receipt not found") with{ErrorCode=FluxVaultIpcErrorCode.Unavailable};
            throw new InvalidOperationException("Unexpected mutation or receipt request: "+request.Command);
        }
    }
    private sealed class EmptyFiles:IFileBrowserFileSystem
    {
        public IReadOnlyList<FileBrowserFolderInfo> GetRoots()=>[];
        public IReadOnlyList<FileBrowserFolderInfo> GetChildFolders(string path)=>[];
        public IReadOnlyList<FileBrowserFileInfo> GetFiles(string path)=>[];
    }
    private sealed class Controller:IFluxVaultWindowsServiceController
    {
        public Task<FluxVaultWindowsServiceStatus> GetStatusAsync(CancellationToken cancellationToken=default)=>Task.FromResult(new FluxVaultWindowsServiceStatus("Fixture",FluxVaultWindowsServiceState.Running,"Fixture"));
        public Task<FluxVaultWindowsServiceActionResult> StartAsync(CancellationToken cancellationToken=default)=>throw new InvalidOperationException();
        public Task<FluxVaultWindowsServiceActionResult> StopAsync(CancellationToken cancellationToken=default)=>throw new InvalidOperationException();
    }
    private sealed class Destination:IRestoreDestinationPicker{public string? PickDestination(VersionRow version)=>throw new InvalidOperationException();}
    private sealed class Overwrite:IRestoreOverwriteConfirmation{public bool ConfirmOverwrite(string destinationPath)=>throw new InvalidOperationException();}
}
