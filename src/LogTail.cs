using System.Buffers;
using System.Text;

namespace Snail.Toolkit.Nomad;

/// <summary>Reads the tail of a task's log Nomad answers with, as text.</summary>
/// <remarks>
/// The text is what a <see cref="StreamReader"/> gives for the first <c>tailBytes</c> characters of the
/// body: UTF-8 unless a byte order mark says otherwise, the mark itself dropped. Nomad sends at most the
/// bytes asked for, so the whole body is read once into a pooled buffer and decoded in one call, without a
/// reader's buffers or a character array of the tail's size. A body that says it is UTF-16 or UTF-32, or
/// that is longer than was asked for, is read as before by a reader.
/// </remarks>
internal static class LogTail
{
    public static async Task<string> ReadAsync(Stream stream, int tailBytes, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(tailBytes + 1);

        try
        {
            var read = await stream
                .ReadAtLeastAsync(buffer.AsMemory(0, tailBytes + 1), tailBytes + 1, throwOnEndOfStream: false, cancellationToken)
                .ConfigureAwait(false);

            if (read <= tailBytes && TryDecode(buffer.AsSpan(0, read), out var text))
            {
                return text;
            }

            using var reader = new StreamReader(new ReplayedStream(buffer, read, stream));

            return await ReadByReaderAsync(reader, tailBytes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>The way the tail was read before: up to <paramref name="tailBytes"/> characters by a reader.</summary>
    private static async Task<string> ReadByReaderAsync(StreamReader reader, int tailBytes, CancellationToken cancellationToken)
    {
        var characters = new char[tailBytes];
        var read = await reader.ReadBlockAsync(characters, cancellationToken).ConfigureAwait(false);

        return new string(characters, 0, read);
    }

    /// <summary>Decodes a whole body as UTF-8, unless a byte order mark names another encoding.</summary>
    private static bool TryDecode(ReadOnlySpan<byte> body, out string text)
    {
        ReadOnlySpan<byte> bigEndian = [0xFE, 0xFF];
        ReadOnlySpan<byte> littleEndian = [0xFF, 0xFE];
        ReadOnlySpan<byte> bigEndianWide = [0x00, 0x00, 0xFE, 0xFF];

        if (body.StartsWith(bigEndian) || body.StartsWith(littleEndian) || body.StartsWith(bigEndianWide))
        {
            text = string.Empty;

            return false;
        }

        if (body.StartsWith(Encoding.UTF8.Preamble))
        {
            body = body[Encoding.UTF8.Preamble.Length..];
        }

        text = Encoding.UTF8.GetString(body);

        return true;
    }

    /// <summary>The bytes already read, then the rest of the body, so a reader sees the body whole.</summary>
    private sealed class ReplayedStream(byte[] head, int headLength, Stream rest) : Stream
    {
        private int _replayed;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_replayed < headLength)
            {
                return Replay(buffer);
            }

            return rest.Read(buffer);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_replayed < headLength)
            {
                return ValueTask.FromResult(Replay(buffer.Span));
            }

            return rest.ReadAsync(buffer, cancellationToken);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Replay(Span<byte> buffer)
        {
            var count = Math.Min(buffer.Length, headLength - _replayed);
            head.AsSpan(_replayed, count).CopyTo(buffer);
            _replayed += count;

            return count;
        }
    }
}
