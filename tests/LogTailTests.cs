using System.Net;
using Microsoft.Extensions.Options;

namespace Snail.Toolkit.Nomad.Tests;

/// <summary>
/// The tail of a log reads as it did through a <see cref="StreamReader"/> of <c>tailBytes</c> characters,
/// whatever bytes the body holds: the reader is the oracle every case is compared with.
/// </summary>
public sealed class LogTailTests
{
    public static TheoryData<string, byte[], int> Bodies() => new()
    {
        { "empty", [], 16 },
        { "ascii", "last lines\nof the log\n"u8.ToArray(), 64 },
        { "ascii, exactly the tail", "0123456789abcdef"u8.ToArray(), 16 },
        { "multi-byte", "Ünïcödé — 日本語 🚀\n"u8.ToArray(), 64 },
        { "utf-8 mark", [0xEF, 0xBB, 0xBF, .. "marked"u8.ToArray()], 64 },
        { "utf-8 mark alone", [0xEF, 0xBB, 0xBF], 8 },
        { "part of a utf-8 mark", [0xEF, 0xBB], 8 },
        { "first byte of a utf-8 mark", [0xEF], 8 },
        { "cut mid-character", [.. "end ü"u8.ToArray()[..^1]], 64 },
        { "cut at the start", [.. "🚀 rest"u8.ToArray()[2..]], 64 },
        { "invalid bytes", [0x61, 0xC0, 0xFF, 0x80, 0x62], 64 },
        { "utf-16 little-endian mark", [0xFF, 0xFE, 0x61, 0x00, 0x62, 0x00], 64 },
        { "utf-16 big-endian mark", [0xFE, 0xFF, 0x00, 0x61, 0x00, 0x62], 64 },
        { "utf-32 little-endian mark", [0xFF, 0xFE, 0x00, 0x00, 0x61, 0x00, 0x00, 0x00], 64 },
        { "utf-32 big-endian mark", [0x00, 0x00, 0xFE, 0xFF, 0x00, 0x00, 0x00, 0x61], 64 },
        { "one byte of a utf-16 mark", [0xFF], 8 },
        { "more than was asked, ascii", "0123456789abcdefXYZ"u8.ToArray(), 16 },
        { "more than was asked, multi-byte", "ééééééééééé"u8.ToArray(), 8 },
        { "more than was asked, with a mark", [0xEF, 0xBB, 0xBF, .. "0123456789"u8.ToArray()], 8 },
        { "more than was asked, a pair split at the end", "abcdefg🚀🚀"u8.ToArray(), 8 }
    };

    [Theory]
    [MemberData(nameof(Bodies))]
    public async Task ReadLogs_AnyBody_ReadsAsAStreamReaderDid(string body, byte[] bytes, int tailBytes)
    {
        var client = new NomadClient(
            new HttpClient(new BytesHandler(bytes)) { BaseAddress = new Uri("http://nomad.test:4646/") },
            Options.Create(new NomadOptions { Address = "http://nomad.test:4646" }));

        var text = await client.ReadLogsAsync("a-1", "mcp", tailBytes: tailBytes);

        Assert.True(await ReadAsBeforeAsync(bytes, tailBytes) == text, body);
    }

    private static async Task<string> ReadAsBeforeAsync(byte[] bytes, int tailBytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes));
        var buffer = new char[tailBytes];
        var read = await reader.ReadBlockAsync(buffer, CancellationToken.None);

        return new string(buffer, 0, read);
    }

    private sealed class BytesHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
                RequestMessage = request
            });
    }
}
