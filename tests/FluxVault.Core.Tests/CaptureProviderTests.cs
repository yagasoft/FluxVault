using System.Text;
using FluxVault.Abstractions.Capture;
using FluxVault.Abstractions.Storage;
using FluxVault.Core.Capture;

namespace FluxVault.Core.Tests;

public sealed class CaptureProviderTests
{
    [Fact]
    public async Task Normal_file_capture_reads_readable_file()
    {
        using var workspace = TemporaryWorkspace.Create();
        var source = Path.Combine(workspace.RootPath, "source.txt");
        await File.WriteAllTextAsync(source, "readable content");
        var provider = new NormalFileCaptureProvider();

        await using var capture = await provider.CaptureAsync(new FileCaptureRequest(source));

        Assert.True(capture.Success);
        Assert.Equal(CaptureConsistency.BestEffort, capture.Consistency);
        using var reader = new StreamReader(capture.Content!, Encoding.UTF8);
        Assert.Equal("readable content", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Fallback_capture_uses_vss_when_normal_read_fails()
    {
        var normal = new StubCaptureProvider(FileCaptureResult.Failed("locked"));
        var vss = new StubCaptureProvider(FileCaptureResult.Captured(
            new MemoryStream(Encoding.UTF8.GetBytes("from vss")),
            CaptureConsistency.CrashConsistent,
            "captured from VSS snapshot"));
        var provider = new FallbackFileCaptureProvider(normal, vss);

        await using var capture = await provider.CaptureAsync(new FileCaptureRequest(@"C:\Work\locked.bin"));

        Assert.True(capture.Success);
        Assert.Equal(CaptureConsistency.CrashConsistent, capture.Consistency);
        Assert.Equal(1, normal.CallCount);
        Assert.Equal(1, vss.CallCount);
    }

    private sealed class StubCaptureProvider(FileCaptureResult result) : IFileCaptureProvider
    {
        public int CallCount { get; private set; }

        public Task<FileCaptureResult> CaptureAsync(FileCaptureRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(result);
        }
    }
}
