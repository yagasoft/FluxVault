using FluxVault.App.Services;

namespace FluxVault.App.Tests;

public sealed class AppStartupRequestRouterTests
{
    [Fact]
    public void Restore_path_argument_parser_accepts_file_or_folder_path()
    {
        var request = AppStartupRequest.Parse(["--restore-path", @"D:\Work\Drafts"]);

        Assert.Equal(@"D:\Work\Drafts", request.RestorePath);
    }

    [Fact]
    public async Task Existing_instance_receives_restore_path_request_from_second_launch()
    {
        var instanceKey = $"FluxVault.App.Tests.{Guid.NewGuid():N}";
        await using var primary = new AppStartupRequestRouter(instanceKey);
        var received = new TaskCompletionSource<AppStartupRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        await primary.StartListeningAsync(request =>
        {
            received.TrySetResult(request);
            return Task.CompletedTask;
        });
        await using var secondary = new AppStartupRequestRouter(instanceKey);

        var forwarded = await secondary.TryForwardToExistingInstanceAsync(
            new AppStartupRequest(@"D:\Work\Drafts\brief.docx"));

        Assert.True(primary.IsPrimaryInstance);
        Assert.False(secondary.IsPrimaryInstance);
        Assert.True(forwarded);
        var request = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(@"D:\Work\Drafts\brief.docx", request.RestorePath);
    }
}
