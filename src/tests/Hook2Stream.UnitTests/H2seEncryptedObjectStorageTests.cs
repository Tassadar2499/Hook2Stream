using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hook2Stream.Application;
using Hook2Stream.Infrastructure;
using Hook2Stream.Infrastructure.Storage;
using Microsoft.Extensions.Options;

namespace Hook2Stream.UnitTests;

public sealed class H2seEncryptedObjectStorageTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1024 * 1024)]
    [InlineData(10 * 1024 * 1024)]
    [InlineData(10 * 1024 * 1024 + 73)]
    public async Task RoundTrips_without_persisting_plaintext_and_publishes_manifest_last(int size)
    {
        var raw = new MemoryRawStorage();
        using var keyring = TestKeyring.Create();
        var storage = Create(raw, keyring.Path);
        var source = TempFile(RandomNumberGenerator.GetBytes(size));
        var destination = NewTempPath();
        try
        {
            await storage.UploadAsync("w/one/p/two/assets/three/original", source, "audio/mpeg", default);
            var info = await storage.HeadAsync("w/one/p/two/assets/three/original", default);
            Assert.Equal(size, info!.SizeBytes);
            Assert.EndsWith(".h2se/manifest", raw.UploadOrder[^1], StringComparison.Ordinal);

            await storage.DownloadAsync("w/one/p/two/assets/three/original", destination, default);
            Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(destination));
        }
        finally { File.Delete(source); File.Delete(destination); }
    }

    [Fact]
    public async Task Long_plaintext_sentinel_is_absent_from_every_physical_object()
    {
        var raw = new MemoryRawStorage();
        using var keyring = TestKeyring.Create();
        var marker = System.Text.Encoding.UTF8.GetBytes("HOOK2STREAM-PLAINTEXT-MUST-NEVER-REACH-S3-0123456789-ABCDEFGHIJKLMNOPQRSTUVWXYZ");
        var bytes = Enumerable.Range(0, 4096).SelectMany(_ => marker).ToArray();
        var source = TempFile(bytes);
        try
        {
            await Create(raw, keyring.Path).UploadAsync("sentinel", source, "audio/mpeg", default);
            Assert.DoesNotContain(raw.Objects.Values, value => Contains(value, marker));
        }
        finally { File.Delete(source); }
    }

    [Fact]
    public async Task RoundTrips_250MiB_without_buffering_the_whole_object_in_memory()
    {
        const long size = 250L * 1024 * 1024;
        using var raw = new DiskRawStorage();
        using var keyring = TestKeyring.Create();
        var storage = Create(raw, keyring.Path);
        var source = NewTempPath();
        try
        {
            await using (var file = new FileStream(source, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                file.SetLength(size);

            byte[] expectedHash;
            await using (var file = File.OpenRead(source))
            using (var sha = SHA256.Create())
                expectedHash = await sha.ComputeHashAsync(file);

            await storage.UploadAsync("large-object", source, "application/octet-stream", default);
            var info = await storage.HeadAsync("large-object", default);
            Assert.Equal(size, info!.SizeBytes);

            await using var output = new HashingWriteStream();
            await storage.CopyToAsync("large-object", output, 0, null, default);
            Assert.Equal(size, output.BytesWritten);
            Assert.Equal(expectedHash, output.GetHashAndReset());
        }
        finally { File.Delete(source); }
    }

    [Fact]
    public async Task Every_object_write_uses_unique_manifest_wrap_and_chunk_nonces()
    {
        var raw = new MemoryRawStorage();
        using var keyring = TestKeyring.Create();
        var storage = Create(raw, keyring.Path);
        var source = TempFile(RandomNumberGenerator.GetBytes(1024 * 1024 + 1));
        try
        {
            await storage.UploadAsync("object", source, "application/octet-stream", default);
            var manifestKey = raw.Objects.Keys.Single(key => key.EndsWith(".h2se/manifest", StringComparison.Ordinal));
            var first = ReadNonces(raw.Objects[manifestKey], "object", keyring.ActiveKey);

            await storage.UploadAsync("object", source, "application/octet-stream", default);
            var second = ReadNonces(raw.Objects[manifestKey], "object", keyring.ActiveKey);

            Assert.NotEqual(first.ManifestNonce, second.ManifestNonce);
            Assert.NotEqual(first.WrapNonce, second.WrapNonce);
            Assert.NotEqual(first.ChunkNoncePrefix, second.ChunkNoncePrefix);
        }
        finally { File.Delete(source); }
    }

    [Fact]
    public async Task Reads_only_ciphertext_chunks_intersecting_a_plaintext_range()
    {
        var raw = new MemoryRawStorage();
        using var keyring = TestKeyring.Create();
        var storage = Create(raw, keyring.Path);
        var bytes = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 17);
        var source = TempFile(bytes);
        try
        {
            await storage.UploadAsync("object", source, "application/octet-stream", default);
            raw.RangeReads.Clear();
            await using var output = new MemoryStream();
            await storage.CopyToAsync("object", output, 1024 * 1024 - 5, 20, default);
            Assert.Equal(bytes.AsSpan(1024 * 1024 - 5, 20).ToArray(), output.ToArray());
            Assert.Equal(2, raw.RangeReads.Count);
            Assert.All(raw.RangeReads, read => Assert.True(read.Length <= 1024 * 1024 + 16));
        }
        finally { File.Delete(source); }
    }

    [Fact]
    public async Task Rejects_tampered_ciphertext_and_wrong_key()
    {
        var raw = new MemoryRawStorage();
        using var keyring = TestKeyring.Create();
        var storage = Create(raw, keyring.Path);
        var source = TempFile(RandomNumberGenerator.GetBytes(2048));
        try
        {
            await storage.UploadAsync("object", source, "audio/mpeg", default);
            using var wrong = TestKeyring.Create(activeKeyId: "k1");
            await Assert.ThrowsAsync<AuthenticationTagMismatchException>(() => Create(raw, wrong.Path).HeadAsync("object", default));

            var dataKey = raw.Objects.Keys.Single(key => key.Contains(".h2se/data/", StringComparison.Ordinal));
            raw.Objects[dataKey][0] ^= 0x80;
            await Assert.ThrowsAsync<AuthenticationTagMismatchException>(() => storage.CopyToAsync("object", new MemoryStream(), 0, null, default));
        }
        finally { File.Delete(source); }
    }

    [Fact]
    public async Task Rejects_tampered_manifest_truncation_and_reordered_chunks()
    {
        var raw = new MemoryRawStorage();
        using var keyring = TestKeyring.Create();
        var storage = Create(raw, keyring.Path);
        var source = TempFile(RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 99));
        try
        {
            await storage.UploadAsync("object", source, "application/octet-stream", default);
            var manifestKey = raw.Objects.Keys.Single(key => key.EndsWith(".h2se/manifest", StringComparison.Ordinal));
            var originalManifest = raw.Objects[manifestKey].ToArray();
            var node = System.Text.Json.Nodes.JsonNode.Parse(originalManifest)!.AsObject();
            var tag = Convert.FromBase64String(node["Tag"]!.GetValue<string>());
            tag[0] ^= 0x80;
            node["Tag"] = Convert.ToBase64String(tag);
            raw.Objects[manifestKey] = Encoding.UTF8.GetBytes(node.ToJsonString());
            await Assert.ThrowsAsync<AuthenticationTagMismatchException>(() => storage.HeadAsync("object", default));

            node = System.Text.Json.Nodes.JsonNode.Parse(originalManifest)!.AsObject();
            node["KeyId"] = "missing";
            raw.Objects[manifestKey] = Encoding.UTF8.GetBytes(node.ToJsonString());
            await Assert.ThrowsAsync<CryptographicException>(() => storage.HeadAsync("object", default));
            raw.Objects[manifestKey] = originalManifest;

            var dataKey = raw.Objects.Keys.Single(key => key.Contains(".h2se/data/", StringComparison.Ordinal));
            var originalData = raw.Objects[dataKey].ToArray();
            var block = 1024 * 1024 + 16;
            originalData.AsSpan(0, block).CopyTo(raw.Objects[dataKey].AsSpan(block, block));
            originalData.AsSpan(block, block).CopyTo(raw.Objects[dataKey].AsSpan(0, block));
            await Assert.ThrowsAsync<AuthenticationTagMismatchException>(() => storage.CopyToAsync("object", new MemoryStream(), 0, null, default));
            raw.Objects[dataKey] = originalData[..^1];
            await Assert.ThrowsAnyAsync<Exception>(() => storage.CopyToAsync("object", new MemoryStream(), 0, null, default));
        }
        finally { File.Delete(source); }
    }

    [Fact]
    public async Task Rotation_keeps_old_keys_readable_and_replaces_data_immutably()
    {
        var raw = new MemoryRawStorage();
        using var keyring = TestKeyring.Create();
        var source = TempFile("first"u8.ToArray());
        var second = TempFile("second"u8.ToArray());
        try
        {
            var storage = Create(raw, keyring.Path);
            await storage.UploadAsync("object", source, "text/plain", default);
            var oldDataKey = raw.Objects.Keys.Single(key => key.Contains(".h2se/data/", StringComparison.Ordinal));
            keyring.Rotate();
            var rotated = Create(raw, keyring.Path);
            await using var oldOutput = new MemoryStream();
            await rotated.CopyToAsync("object", oldOutput, 0, null, default);
            Assert.Equal("first", System.Text.Encoding.UTF8.GetString(oldOutput.ToArray()));

            await rotated.UploadAsync("object", second, "text/plain", default);
            Assert.Contains(oldDataKey, raw.Objects.Keys);
            Assert.Equal(2, raw.Objects.Keys.Count(key => key.Contains(".h2se/data/", StringComparison.Ordinal)));
        }
        finally { File.Delete(source); File.Delete(second); }
    }

    private static H2seEncryptedObjectStorage Create(IRawObjectStorage raw, string keyringPath) =>
        new(raw, Options.Create(new StorageEncryptionOptions { KeyringPath = keyringPath }));
    private static string TempFile(byte[] bytes) { var path = NewTempPath(); File.WriteAllBytes(path, bytes); return path; }
    private static string NewTempPath() => Path.Combine(Path.GetTempPath(), $"h2se-test-{Guid.NewGuid():N}");
    private static bool Contains(byte[] haystack, ReadOnlySpan<byte> needle) => haystack.AsSpan().IndexOf(needle) >= 0;
    private static (string ManifestNonce, string WrapNonce, string ChunkNoncePrefix) ReadNonces(byte[] envelopeBytes, string objectKey, byte[] kek)
    {
        using var envelope = JsonDocument.Parse(envelopeBytes);
        var root = envelope.RootElement;
        var nonce = root.GetProperty("Nonce").GetString()!;
        var ciphertext = Convert.FromBase64String(root.GetProperty("Ciphertext").GetString()!);
        var payload = new byte[ciphertext.Length];
        var objectHash = SHA256.HashData(Encoding.UTF8.GetBytes(objectKey));
        var aad = Encoding.ASCII.GetBytes("H2SE manifest v1").Concat(objectHash).ToArray();
        using (var aes = new AesGcm(kek, 16))
            aes.Decrypt(
                Convert.FromBase64String(nonce),
                ciphertext,
                Convert.FromBase64String(root.GetProperty("Tag").GetString()!),
                payload,
                aad);
        try
        {
            using var document = JsonDocument.Parse(payload);
            var payloadRoot = document.RootElement;
            return (
                nonce,
                payloadRoot.GetProperty("WrapNonce").GetString()!,
                payloadRoot.GetProperty("Manifest").GetProperty("NoncePrefix").GetString()!);
        }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }

    private sealed class MemoryRawStorage : IRawObjectStorage
    {
        public Dictionary<string, byte[]> Objects { get; } = [];
        public List<string> UploadOrder { get; } = [];
        public List<(string Key, long Offset, long Length)> RangeReads { get; } = [];
        public Task EnsureBucketAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<StorageObjectInfo?> HeadAsync(string key, CancellationToken cancellationToken) => Task.FromResult(Objects.TryGetValue(key, out var value) ? new StorageObjectInfo(value.Length, null, null) : null);
        public async Task DownloadAsync(string key, string path, CancellationToken cancellationToken) => await File.WriteAllBytesAsync(path, Objects[key], cancellationToken);
        public async Task UploadAsync(string key, string path, string contentType, CancellationToken cancellationToken) { Objects[key] = await File.ReadAllBytesAsync(path, cancellationToken); UploadOrder.Add(key); }
        public Task CopyToAsync(string key, Stream destination, long offset, long length, CancellationToken cancellationToken) { RangeReads.Add((key, offset, length)); return destination.WriteAsync(Objects[key].AsMemory((int)offset, (int)length), cancellationToken).AsTask(); }
        public Task DeleteAsync(string key, CancellationToken cancellationToken) { Objects.Remove(key); return Task.CompletedTask; }
        public Task DeleteProjectObjectsAsync(ProjectStorageScope scope, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAssetObjectsAsync(AssetStorageScope scope, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class DiskRawStorage : IRawObjectStorage, IDisposable
    {
        private readonly string _root = NewTempPath();
        private readonly Dictionary<string, string> _objects = [];

        public DiskRawStorage() => Directory.CreateDirectory(_root);
        public Task EnsureBucketAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<StorageObjectInfo?> HeadAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(_objects.TryGetValue(key, out var path)
                ? new StorageObjectInfo(new FileInfo(path).Length, null, null)
                : null);
        public Task DownloadAsync(string key, string path, CancellationToken cancellationToken) =>
            CopyFileAsync(_objects[key], path, cancellationToken);
        public Task UploadAsync(string key, string path, string contentType, CancellationToken cancellationToken)
        {
            var destination = Path.Combine(_root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))));
            File.Move(path, destination, overwrite: true);
            _objects[key] = destination;
            return Task.CompletedTask;
        }
        public async Task CopyToAsync(string key, Stream destination, long offset, long length, CancellationToken cancellationToken)
        {
            await using var source = new FileStream(_objects[key], FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            source.Position = offset;
            var buffer = new byte[128 * 1024];
            var remaining = length;
            while (remaining > 0)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
                if (read == 0) throw new EndOfStreamException();
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                remaining -= read;
            }
        }
        public Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            if (_objects.Remove(key, out var path)) File.Delete(path);
            return Task.CompletedTask;
        }
        public Task DeleteProjectObjectsAsync(ProjectStorageScope scope, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAssetObjectsAsync(AssetStorageScope scope, CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() => Directory.Delete(_root, recursive: true);

        private static async Task CopyFileAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
        {
            await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(destination, cancellationToken);
        }
    }

    private sealed class HashingWriteStream : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public long BytesWritten { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => BytesWritten;
        public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }
        public byte[] GetHashAndReset() => _hash.GetHashAndReset();
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Write(byte[] buffer, int offset, int count)
        {
            _hash.AppendData(buffer, offset, count);
            BytesWritten += count;
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _hash.AppendData(buffer.Span);
            BytesWritten += buffer.Length;
            return ValueTask.CompletedTask;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _hash.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class TestKeyring : IDisposable
    {
        private readonly byte[] _first = RandomNumberGenerator.GetBytes(32);
        public byte[] ActiveKey => _first;
        public string Path { get; } = NewTempPath();
        private TestKeyring(string activeKeyId) => Write(activeKeyId, new Dictionary<string, byte[]> { [activeKeyId] = _first });
        public static TestKeyring Create(string activeKeyId = "k1") => new(activeKeyId);
        public void Rotate() => Write("k2", new Dictionary<string, byte[]> { ["k1"] = _first, ["k2"] = RandomNumberGenerator.GetBytes(32) });
        private void Write(string active, Dictionary<string, byte[]> keys) => File.WriteAllText(Path, System.Text.Json.JsonSerializer.Serialize(new { activeKeyId = active, keys = keys.ToDictionary(pair => pair.Key, pair => Convert.ToBase64String(pair.Value)) }));
        public void Dispose() => File.Delete(Path);
    }
}
