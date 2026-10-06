using System.IO.Compression;
using TubaWinUi3.Models;
using TubaWinUi3.Services;

namespace TubaWinUi3.Tests;

public sealed class DownloadArchiveValidationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zxai-archive-" + Guid.NewGuid().ToString("N"));
    public DownloadArchiveValidationTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    private string Zip(params (string Name, byte[] Bytes)[] entries)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, bytes) in entries)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
            using var output = entry.Open();
            output.Write(bytes);
        }
        return path;
    }

    [Fact]
    public async Task CompleteArchive_ReadsAllFilesAndDirectories()
    {
        var path = Zip(("runtime/", []), ("runtime/dependency/data.txt", "data"u8.ToArray()),
            ("主程序/readme.txt", "说明"u8.ToArray()));
        await DownloadQueueService.ValidateDownloadedFileAsync(path, new FileInfo(path).Length);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task CorruptStoredContent_IsRejectedEvenWithValidCentralDirectory()
    {
        var payload = "UNIQUE-PAYLOAD-CRC-CHECK"u8.ToArray();
        var path = Zip(("entry.txt", payload));
        var bytes = File.ReadAllBytes(path);
        var offset = bytes.AsSpan().IndexOf(payload);
        Assert.True(offset >= 0);
        bytes[offset] ^= 1;
        File.WriteAllBytes(path, bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => DownloadQueueService.ValidateDownloadedFileAsync(path));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task WebPageNamedZip_IsRejectedAndCannotReachPostProcessing()
    {
        var path = Path.Combine(_root, "tool.zip");
        File.WriteAllText(path, "<!doctype html><title>not a tool</title>");
        await Assert.ThrowsAsync<InvalidDataException>(() => DownloadQueueService.ValidateDownloadedFileAsync(path));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task DeclaredSizeMismatch_IsRejected()
    {
        var path = Zip(("entry.txt", "data"u8.ToArray()));
        await Assert.ThrowsAsync<InvalidDataException>(() => DownloadQueueService.ValidateDownloadedFileAsync(path,
            new FileInfo(path).Length + 1));
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("../out-other/entry.txt")]
    [InlineData("..\\out-other\\entry.txt")]
    [InlineData("C:/entry.txt")]
    [InlineData("entry.txt:payload")]
    public async Task UnsafeArchivePaths_AreRejectedBeforeExtraction(string name)
    {
        var path = Zip((name, "data"u8.ToArray()));
        await Assert.ThrowsAsync<InvalidDataException>(() => DownloadArchiveValidation.ValidateAsync(path));
        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FileDirectoryCollision_IsRejectedInEitherOrder(bool fileFirst)
    {
        (string, byte[])[] entries = [("same", "file"u8.ToArray()), ("same/child.txt", "child"u8.ToArray())];
        if (!fileFirst) Array.Reverse(entries);
        var path = Zip(entries);
        await Assert.ThrowsAsync<InvalidDataException>(() => DownloadArchiveValidation.ValidateAsync(path));
    }

    [Fact]
    public async Task CancelledVerification_KeepsTheDownloadAndPropagatesCancellation()
    {
        var path = Zip(("entry.txt", "data"u8.ToArray()));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DownloadQueueService.ValidateDownloadedFileAsync(
            path, ct: cancellation.Token));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void TolerantExtractor_CannotWriteToSiblingWithTheSamePrefix()
    {
        var path = Zip(("../out-other/escaped.txt", "must stay inside"u8.ToArray()));
        var skipped = ZipExtractHelper.ExtractTolerant(path, Path.Combine(_root, "out"));
        Assert.Single(skipped);
        Assert.False(File.Exists(Path.Combine(_root, "out-other", "escaped.txt")));
    }
}
