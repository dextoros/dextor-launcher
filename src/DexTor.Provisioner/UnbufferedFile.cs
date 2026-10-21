using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace DexTor.Provisioner;

/// <summary>
/// Copies a file to the stick and reads it back without the Windows file cache.
/// </summary>
/// <remarks>
/// <para>
/// The copy for Windows went through the cache: Windows took the writes at memory speed and
/// wrote them to the stick later, and the check afterwards read the file back from memory -
/// 1299 MB/s on a stick that writes 20. The figures on step 5 showed the cache, and for a
/// product about security a check of the cache proves nothing about the stick.
/// </para>
/// <para>
/// Both directions use FILE_FLAG_NO_BUFFERING (and write-through for the copy): every byte
/// counted as written has reached the device, and every byte checked was read from it.
/// Unbuffered I/O needs sector-aligned buffers, offsets and lengths; buffers are allocated
/// aligned to 4 KiB, which covers 512-byte and 4K-native sticks, and the last block of the
/// copy is padded and the file then cut to its real length.
/// </para>
/// </remarks>
internal static unsafe partial class UnbufferedFile
{
    /// <summary>FILE_FLAG_NO_BUFFERING, which <see cref="FileOptions"/> has no name for.</summary>
    internal const FileOptions NoBuffering = (FileOptions)0x20000000;

    private const int Alignment = 4096;
    private const int ChunkBytes = 4 * 1024 * 1024;

    /// <summary>Copies <paramref name="source"/> to <paramref name="destination"/>, reporting bytes that reached the device.</summary>
    internal static void Copy(string source, string destination, Action<long>? progress, CancellationToken cancellationToken)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        var length = input.Length;
        using var buffer = new AlignedBuffer(ChunkBytes);
        using (var output = File.OpenHandle(destination, FileMode.Create, FileAccess.ReadWrite, FileShare.None,
                   FileOptions.WriteThrough | NoBuffering, preallocationSize: length))
        {
            long done = 0;
            progress?.Invoke(0);
            while (done < length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = (int)Math.Min(ChunkBytes, length - done);
                input.ReadExactly(buffer.Span[..count]);
                // Unbuffered writes are whole sectors; the tail is padded and cut off below.
                var padded = (count + Alignment - 1) / Alignment * Alignment;
                buffer.Span[count..padded].Clear();
                RandomAccess.Write(output, buffer.Span[..padded], done);
                done += count;
                progress?.Invoke(done);
            }

            RandomAccess.SetLength(output, length);
            FlushFileBuffers(output);
        }
    }

    /// <summary>The SHA-256 of what the device holds for <paramref name="path"/>, upper-case hex.</summary>
    internal static string Sha256(string path, Action<long, long>? progress, CancellationToken cancellationToken = default)
    {
        using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan | NoBuffering);
        var length = RandomAccess.GetLength(file);
        using var buffer = new AlignedBuffer(ChunkBytes);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long done = 0;
        progress?.Invoke(0, length);
        while (done < length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Whole aligned chunks are asked for; at the end of the file fewer bytes come back.
            var read = RandomAccess.Read(file, buffer.Span, done);
            if (read <= 0) throw new IOException($"{path} ended early while it was read back.");
            read = (int)Math.Min(read, length - done);
            hash.AppendData(buffer.Span[..read]);
            done += read;
            progress?.Invoke(done, length);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private sealed class AlignedBuffer : IDisposable
    {
        private readonly byte* _pointer;
        private readonly int _length;

        public AlignedBuffer(int length)
        {
            _length = length;
            _pointer = (byte*)NativeMemory.AlignedAlloc((nuint)length, Alignment);
        }

        public Span<byte> Span => new(_pointer, _length);

        public void Dispose() => NativeMemory.AlignedFree(_pointer);
    }

    private static void FlushFileBuffers(SafeFileHandle file)
    {
        // Write-through has already put the data on the device; this also asks the stick
        // to empty its own cache. Sticks that cannot be asked answer "Incorrect function".
        if (!NativeFlushFileBuffers(file) && Marshal.GetLastPInvokeError() != 1)
        {
            throw new IOException("The copy could not be flushed to the USB drive.", Marshal.GetLastPInvokeError());
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "FlushFileBuffers", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool NativeFlushFileBuffers(SafeFileHandle file);
}

/// <summary>
/// A read-only stream over a handle opened with FILE_FLAG_NO_BUFFERING: each read goes to the
/// device, through a sector-aligned buffer, whatever offset and length the caller asks for.
/// </summary>
/// <remarks>
/// The raw boot image is checked by reading the disk back. A disk handle is not served from
/// the file cache to begin with, but testers asked for the flag to be explicit, and with it
/// no layer can answer from memory.
/// </remarks>
internal sealed unsafe class UnbufferedReadStream : Stream
{
    private const int Alignment = 4096;
    private readonly SafeFileHandle _handle;
    private readonly byte* _buffer;
    private readonly int _bufferLength;

    internal UnbufferedReadStream(SafeFileHandle handle, int bufferLength = 4 * 1024 * 1024)
    {
        _handle = handle;
        _bufferLength = bufferLength + Alignment;
        _buffer = (byte*)NativeMemory.AlignedAlloc((nuint)_bufferLength, Alignment);
    }

    internal static UnbufferedReadStream OpenDevice(string path) =>
        new(File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, UnbufferedFile.NoBuffering));

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => RandomAccess.GetLength(_handle);
    public override long Position { get; set; }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> destination)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var start = (Position / Alignment) * Alignment;
            var skip = (int)(Position - start);
            var want = Math.Min(destination.Length - total + skip, _bufferLength);
            want = (want + Alignment - 1) / Alignment * Alignment;
            var read = RandomAccess.Read(_handle, new Span<byte>(_buffer, want), start);
            if (read <= skip) break;
            var useful = Math.Min(read - skip, destination.Length - total);
            new Span<byte>(_buffer + skip, useful).CopyTo(destination[total..]);
            total += useful;
            Position += useful;
            if (read < want) break;
        }

        return total;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(buffer.Span));
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
    {
        SeekOrigin.Begin => offset,
        SeekOrigin.Current => Position + offset,
        _ => Length + offset
    };

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _handle.Dispose();
        NativeMemory.AlignedFree(_buffer);
        base.Dispose(disposing);
    }
}
