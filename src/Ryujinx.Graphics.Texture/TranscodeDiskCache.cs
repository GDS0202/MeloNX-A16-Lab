using Ryujinx.Common.Configuration;
using Ryujinx.Common.Memory;
using Ryujinx.Memory;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace Ryujinx.Graphics.Texture
{
    /// <summary>Optional content-addressed cache. Misses and failures always fall back to normal conversion.</summary>
    public sealed class TranscodeDiskCache : IDisposable
    {
        private const int HeaderSize = 48;
        private const ulong Magic = 0x3345484341435854; // TXCACHE3
        private const int MaximumEntryBytes = 4 * 1024 * 1024;
        private const long MaximumQueuedBytes = 8 * 1024 * 1024;
        private readonly string _directory;
        private readonly long _diskBudget;
        private readonly Queue<PendingWrite> _pending = new();
        private readonly HashSet<string> _pendingKeys = [];
        private readonly object _queueLock = new();
        private readonly AutoResetEvent _wake = new(false);
        private readonly ManualResetEventSlim _idle = new(true);
        private readonly Thread _writer;
        private long _queuedBytes;
        private long _hits;
        private static long _totalHits;
        public static long TotalHits => Interlocked.Read(ref _totalHits);
        private long _diskBytes;
        private int _fileCount;
        private bool _stopping;

        private sealed record PendingWrite(string Key, byte[] Bytes, int Length);
        public long Hits => Interlocked.Read(ref _hits);

        public TranscodeDiskCache(string directory, long diskBudget = 256L * 1024 * 1024)
        {
            _directory = directory;
            _diskBudget = Math.Max(4096, diskBudget);
            Directory.CreateDirectory(directory);
            _writer = new Thread(WriterLoop) { IsBackground = true, Name = "Texture cache writer" };
            _writer.Priority = ThreadPriority.BelowNormal;
            _writer.Start();
        }

        public static TranscodeDiskCache CreateForCurrentProcess()
        {
            if (!OperatingSystem.IsIOS() || Environment.GetEnvironmentVariable("MELONX_TEXTURE_CACHE") != "1" ||
                string.IsNullOrEmpty(AppDataManager.BaseDirPath)) return null;
            try { return new(Path.Combine(AppDataManager.BaseDirPath, "cache", "texture-transcode-v3")); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        }

        public static string CreateKey(ReadOnlySpan<byte> input, int format, int width = 0, int height = 0,
            int depth = 0, int levels = 0, int layers = 0)
        {
            Span<byte> digest = stackalloc byte[32];
            SHA256.HashData(input, digest);
            return FormattableString.Invariant($"{format}-{width}-{height}-{depth}-{levels}-{layers}-{Convert.ToHexString(digest)}");
        }

        public bool TryRead(string key, int length, out MemoryOwner<byte> result)
        {
            result = null;
            if (length < 4096 || length > MaximumEntryBytes) return false;
            try
            {
                using FileStream stream = new(Path.Combine(_directory, key + ".bin"), FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length != HeaderSize + length) return false;
                Span<byte> header = stackalloc byte[HeaderSize];
                stream.ReadExactly(header);
                if (BinaryPrimitives.ReadUInt64LittleEndian(header) != Magic ||
                    BinaryPrimitives.ReadInt32LittleEndian(header[8..]) != length ||
                    BinaryPrimitives.ReadInt32LittleEndian(header[12..]) != 3) return false;
                MemoryOwner<byte> output = MemoryOwner<byte>.Rent(length);
                try
                {
                    stream.ReadExactly(output.Span);
                    Span<byte> checksum = stackalloc byte[32];
                    SHA256.HashData(output.Span, checksum);
                    if (!CryptographicOperations.FixedTimeEquals(checksum, header[16..])) return false;
                    result = output;
                    output = null;
                    Interlocked.Increment(ref _hits);
                    Interlocked.Increment(ref _totalHits);
                    return true;
                }
                finally { output?.Dispose(); }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        }

        public bool TryStore(string key, ReadOnlySpan<byte> output)
        {
            if (output.Length < 4096 || output.Length > MaximumEntryBytes) return false;
            // Never add a duplicate copy when this process has little headroom.
            if (HostMemoryBudget.IsConstrained && HostMemoryBudget.AvailableBytes < 256UL * 1024 * 1024) return false;
            lock (_queueLock)
            {
                if (_stopping || _pending.Count >= 2 || _queuedBytes + output.Length > MaximumQueuedBytes ||
                    !_pendingKeys.Add(key)) return false;
                byte[] copy;
                try { copy = ArrayPool<byte>.Shared.Rent(output.Length); }
                catch (OutOfMemoryException) { _pendingKeys.Remove(key); return false; }
                output.CopyTo(copy);
                _pending.Enqueue(new(key, copy, output.Length));
                _queuedBytes += output.Length;
                _idle.Reset();
                _wake.Set();
                return true;
            }
        }

        public bool WaitForIdle(TimeSpan timeout) => _idle.Wait(timeout);

        private void WriterLoop()
        {
            try
            {
                foreach (FileInfo info in new DirectoryInfo(_directory).EnumerateFiles("*.bin"))
                { _diskBytes += info.Length; _fileCount++; }
                Prune();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            while (true)
            {
                PendingWrite item;
                lock (_queueLock)
                {
                    if (_pending.Count == 0) { if (_stopping) return; item = null; }
                    else item = _pending.Dequeue();
                }
                if (item == null) { _wake.WaitOne(); continue; }
                try { Write(item); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                finally
                {
                    ArrayPool<byte>.Shared.Return(item.Bytes);
                    lock (_queueLock)
                    {
                        _queuedBytes -= item.Length;
                        _pendingKeys.Remove(item.Key);
                        if (_queuedBytes == 0) _idle.Set();
                    }
                }
            }
        }

        private void Write(PendingWrite item)
        {
            string path = Path.Combine(_directory, item.Key + ".bin");
            string temporary = path + ".tmp";
            long previous = File.Exists(path) ? new FileInfo(path).Length : 0;
            try
            {
                Span<byte> header = stackalloc byte[HeaderSize];
                BinaryPrimitives.WriteUInt64LittleEndian(header, Magic);
                BinaryPrimitives.WriteInt32LittleEndian(header[8..], item.Length);
                BinaryPrimitives.WriteInt32LittleEndian(header[12..], 3);
                SHA256.HashData(item.Bytes.AsSpan(0, item.Length), header[16..]);
                using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(header);
                    stream.Write(item.Bytes.AsSpan(0, item.Length));
                    // No flush-to-disk on the graphics thread; this cache is disposable.
                }
                File.Move(temporary, path, overwrite: true);
                _diskBytes += HeaderSize + item.Length - previous;
                if (previous == 0) _fileCount++;
                Prune();
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }

        private void Prune()
        {
            if (_diskBytes <= _diskBudget && _fileCount <= 4096) return;
            foreach (FileInfo info in new DirectoryInfo(_directory).EnumerateFiles("*.bin").OrderBy(i => i.LastWriteTimeUtc))
            {
                if (_diskBytes <= _diskBudget * 9 / 10 && _fileCount <= 4096) break;
                long size = info.Length;
                info.Delete();
                _diskBytes -= size;
                _fileCount--;
            }
        }

        public void Dispose()
        {
            lock (_queueLock) { if (_stopping) return; _stopping = true; _wake.Set(); }
            _writer.Join();
            _wake.Dispose();
            _idle.Dispose();
        }
    }
}
