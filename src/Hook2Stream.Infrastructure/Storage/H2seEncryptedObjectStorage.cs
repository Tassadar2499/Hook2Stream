using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hook2Stream.Application;
using Microsoft.Extensions.Options;

namespace Hook2Stream.Infrastructure.Storage;

/// <summary>
/// H2SE v1 logical object storage. The data object is uploaded before the
/// authenticated manifest, so an object is never visible until it is complete.
/// Object keys are server generated; neither filenames nor bearer credentials
/// are written into storage metadata.
/// </summary>
internal sealed class H2seEncryptedObjectStorage(
    IRawObjectStorage raw,
    IOptions<StorageEncryptionOptions> options,
    IOptions<StorageOptions> storageOptions,
    IOptions<OperationalPolicyOptions> policyOptions,
    TimeProvider timeProvider,
    IH2seConcurrencyGate? concurrencyGate = null) : IObjectStorage
{
    public const int Version = 1;
    private const string Format = "H2SE";
    private readonly StorageEncryptionOptions _options = options.Value;
    private readonly IH2seConcurrencyGate _concurrencyGate = concurrencyGate ??
        new ProcessH2seConcurrencyGate(options.Value.MaxConcurrentEncryptions, options.Value.MaxConcurrentDownloads);
    private H2seKeyring? _keyring;

    public Task EnsureBucketAsync(CancellationToken cancellationToken) => raw.EnsureBucketAsync(cancellationToken);

    public Task<Uri> CreateUploadUrlAsync(string objectKey, string contentType, TimeSpan lifetime, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Browser presigned URLs are disabled. Use the same-origin upload gateway.");

    public Task<Uri> CreateReadUrlAsync(string objectKey, TimeSpan lifetime, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Browser presigned URLs are disabled. Use the same-origin content gateway.");

    public Task<MultipartUpload> CreateMultipartUploadAsync(string objectKey, string contentType, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Raw multipart uploads are disabled. Use encrypted upload parts.");

    public Task<Uri> CreateMultipartPartUploadUrlAsync(string objectKey, string uploadId, int partNumber, TimeSpan lifetime, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Browser presigned URLs are disabled. Use the same-origin upload gateway.");

    public Task CompleteMultipartUploadAsync(string objectKey, string uploadId, IReadOnlyList<MultipartPart> parts, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Raw multipart uploads are disabled. Use encrypted upload parts.");

    public Task AbortMultipartUploadAsync(string objectKey, string uploadId, CancellationToken cancellationToken) =>
        DeleteAsync(objectKey, cancellationToken);

    public async Task<StorageObjectInfo?> HeadAsync(string objectKey, CancellationToken cancellationToken)
    {
        var envelope = await ReadManifestAsync(objectKey, cancellationToken);
        if (envelope is null) return null;
        try
        {
            return new StorageObjectInfo(envelope.Value.Manifest.PlaintextLength, envelope.Value.Manifest.PlaintextSha256, envelope.Value.Manifest.ContentType);
        }
        finally { CryptographicOperations.ZeroMemory(envelope.Value.Dek); }
    }

    public async Task DownloadAsync(string objectKey, string destinationPath, CancellationToken cancellationToken)
    {
        await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous);
        await CopyToAsync(objectKey, destination, 0, null, cancellationToken);
    }

    public async Task CopyToAsync(string objectKey, Stream destination, long offset, long? length, CancellationToken cancellationToken)
    {
        await using (await _concurrencyGate.AcquireDownloadAsync(cancellationToken))
        {
            var envelope = await ReadManifestAsync(objectKey, cancellationToken)
                ?? throw new FileNotFoundException("Encrypted object manifest was not found.");
            try
            {
                if (offset < 0 || offset > envelope.Manifest.PlaintextLength ||
                    length is < 0 || length is not null && offset + length > envelope.Manifest.PlaintextLength)
                    throw new ArgumentOutOfRangeException(nameof(offset));
                var wantedLength = length ?? envelope.Manifest.PlaintextLength - offset;
                if (wantedLength == 0) return;
                await DecryptRangeAsync(objectKey, raw, envelope.Manifest, envelope.Dek, destination, offset, wantedLength, cancellationToken);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(envelope.Dek);
            }
        }
    }

    public async Task UploadAsync(string objectKey, string sourcePath, string contentType, CancellationToken cancellationToken)
    {
        await using var encryptionLease = await _concurrencyGate.AcquireEncryptionAsync(cancellationToken);
        var expiresAt = StorageObjectExpirationPolicy.GetExpiration(
            objectKey,
            storageOptions.Value,
            policyOptions.Value,
            timeProvider);
        var dataPath = TempPath();
        var manifestPath = TempPath();
        try
        {
            var keyring = Keyring();
            var kek = keyring.ActiveKey;
            var dek = RandomNumberGenerator.GetBytes(32);
            try
            {
                var physicalDataKey = objectKey + ".h2se/data/" + Guid.NewGuid().ToString("N");
                var manifest = await EncryptFileAsync(objectKey, sourcePath, dataPath, contentType, physicalDataKey, dek, cancellationToken);
                var envelope = ProtectManifest(objectKey, keyring.ActiveKeyId, kek, dek, manifest);
                await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(envelope), cancellationToken);
                await raw.UploadAsync(
                    physicalDataKey,
                    dataPath,
                    "application/octet-stream",
                    expiresAt,
                    cancellationToken);
                try
                {
                    await raw.UploadAsync(
                        ManifestKey(objectKey),
                        manifestPath,
                        "application/vnd.hook2stream.h2se+json",
                        expiresAt,
                        cancellationToken);
                }
                catch
                {
                    // PutObject can commit remotely and still lose its response.
                    // Retain the immutable data object on an ambiguous manifest
                    // failure: deleting it could corrupt a manifest that was in
                    // fact published. Inventory cleanup removes unreferenced
                    // physical objects after a grace period.
                    throw;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dek);
            }
        }
        finally
        {
            TryDelete(dataPath);
            TryDelete(manifestPath);
        }
    }

    public async Task DeleteAsync(string objectKey, CancellationToken cancellationToken)
    {
        var existing = await ReadManifestAsync(objectKey, cancellationToken);
        try
        {
            if (existing is not null)
                await raw.DeleteAsync(existing.Value.Manifest.PhysicalDataKey, cancellationToken);
            await raw.DeleteAsync(ManifestKey(objectKey), cancellationToken);
        }
        finally { if (existing is not null) CryptographicOperations.ZeroMemory(existing.Value.Dek); }
    }

    public Task DeleteProjectObjectsAsync(ProjectStorageScope scope, CancellationToken cancellationToken) =>
        raw.DeleteProjectObjectsAsync(scope, cancellationToken);

    public Task DeleteAssetObjectsAsync(AssetStorageScope scope, CancellationToken cancellationToken) =>
        raw.DeleteAssetObjectsAsync(scope, cancellationToken);

    private async Task<H2seManifest> EncryptFileAsync(string objectKey, string sourcePath, string destinationPath, string contentType, string physicalDataKey, byte[] dek, CancellationToken cancellationToken)
    {
        var noncePrefix = RandomNumberGenerator.GetBytes(8);
        var objectHash = SHA256.HashData(Encoding.UTF8.GetBytes(objectKey));
        using var plaintextHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[_options.ChunkSizeBytes];
        try
        {
            var chunks = new List<H2seChunk>();
            long plaintextLength = 0;
            long ciphertextOffset = 0;
            for (var index = 0; ; index++)
            {
                var read = await ReadChunkAsync(source, buffer, cancellationToken);
                if (read == 0) break;
                var ciphertext = new byte[read];
                var tag = new byte[16];
                using (var aes = new AesGcm(dek, 16))
                    aes.Encrypt(ChunkNonce(noncePrefix, index), buffer.AsSpan(0, read), ciphertext, tag, ChunkAad(objectHash, index, read));
                await destination.WriteAsync(ciphertext, cancellationToken);
                await destination.WriteAsync(tag, cancellationToken);
                plaintextHash.AppendData(buffer, 0, read);
                chunks.Add(new H2seChunk(index, read, ciphertextOffset));
                plaintextLength += read;
                ciphertextOffset += read + tag.Length;
            }
            return new H2seManifest(physicalDataKey, plaintextLength, contentType, Convert.ToHexString(plaintextHash.GetHashAndReset()).ToLowerInvariant(), Convert.ToBase64String(noncePrefix), chunks);
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private H2seEnvelope ProtectManifest(string objectKey, string keyId, byte[] kek, byte[] dek, H2seManifest manifest)
    {
        var objectHash = SHA256.HashData(Encoding.UTF8.GetBytes(objectKey));
        var wrapNonce = RandomNumberGenerator.GetBytes(12);
        var wrappedDek = new byte[dek.Length];
        var wrapTag = new byte[16];
        using (var aes = new AesGcm(kek, 16))
            aes.Encrypt(wrapNonce, dek, wrappedDek, wrapTag, WrapAad(keyId, objectHash));
        var payload = JsonSerializer.SerializeToUtf8Bytes(new H2seManifestPayload(manifest, Convert.ToBase64String(wrapNonce), Convert.ToBase64String(wrappedDek), Convert.ToBase64String(wrapTag)));
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var encrypted = new byte[payload.Length];
            var tag = new byte[16];
            using (var aes = new AesGcm(kek, 16))
                aes.Encrypt(nonce, payload, encrypted, tag, ManifestAad(objectHash));
            return new H2seEnvelope(Format, Version, keyId, Convert.ToBase64String(nonce), Convert.ToBase64String(encrypted), Convert.ToBase64String(tag));
        }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }

    private async Task<(H2seManifest Manifest, byte[] Dek)?> ReadManifestAsync(string objectKey, CancellationToken cancellationToken)
    {
        var manifestInfo = await raw.HeadAsync(ManifestKey(objectKey), cancellationToken);
        if (manifestInfo is null)
        {
            if (_options.AllowLegacyPlaintextReads)
                throw new NotSupportedException("Legacy plaintext reads are intentionally unsupported in the greenfield H2SE deployment.");
            return null;
        }
        var path = TempPath();
        try
        {
            await raw.DownloadAsync(ManifestKey(objectKey), path, cancellationToken);
            var envelope = JsonSerializer.Deserialize<H2seEnvelope>(await File.ReadAllTextAsync(path, cancellationToken))
                ?? throw new CryptographicException("Invalid H2SE manifest envelope.");
            if (envelope.Format != Format || envelope.Version != Version)
                throw new CryptographicException("Unsupported H2SE object format.");
            var keyring = Keyring();
            var kek = keyring.Get(envelope.KeyId);
            var objectHash = SHA256.HashData(Encoding.UTF8.GetBytes(objectKey));
            var encrypted = Convert.FromBase64String(envelope.Ciphertext);
            var payloadBytes = new byte[encrypted.Length];
            try
            {
                using (var aes = new AesGcm(kek, 16))
                    aes.Decrypt(Convert.FromBase64String(envelope.Nonce), encrypted, Convert.FromBase64String(envelope.Tag), payloadBytes, ManifestAad(objectHash));
                var payload = JsonSerializer.Deserialize<H2seManifestPayload>(payloadBytes)
                    ?? throw new CryptographicException("Invalid H2SE manifest payload.");
                var wrappedDek = Convert.FromBase64String(payload.WrappedDek);
                try
                {
                    var dek = new byte[wrappedDek.Length];
                    try
                    {
                        using (var aes = new AesGcm(kek, 16))
                            aes.Decrypt(Convert.FromBase64String(payload.WrapNonce), wrappedDek, Convert.FromBase64String(payload.WrapTag), dek, WrapAad(envelope.KeyId, objectHash));
                        return (payload.Manifest, dek);
                    }
                    catch
                    {
                        CryptographicOperations.ZeroMemory(dek);
                        throw;
                    }
                }
                finally { CryptographicOperations.ZeroMemory(wrappedDek); }
            }
            finally { CryptographicOperations.ZeroMemory(payloadBytes); }
        }
        finally { TryDelete(path); }
    }

    private static async Task DecryptRangeAsync(string objectKey, IRawObjectStorage raw, H2seManifest manifest, byte[] dek, Stream destination, long offset, long length, CancellationToken cancellationToken)
    {
        var objectHash = SHA256.HashData(Encoding.UTF8.GetBytes(objectKey));
        var prefix = Convert.FromBase64String(manifest.NoncePrefix);
        long plainPosition = 0;
        var wantedEnd = offset + length;
        foreach (var chunk in manifest.Chunks)
        {
            var chunkEnd = plainPosition + chunk.PlaintextLength;
            if (chunkEnd <= offset) { plainPosition = chunkEnd; continue; }
            if (plainPosition >= wantedEnd) break;
            await using var encryptedChunk = new MemoryStream(chunk.PlaintextLength + 16);
            await raw.CopyToAsync(manifest.PhysicalDataKey, encryptedChunk, chunk.CiphertextOffset, chunk.PlaintextLength + 16, cancellationToken);
            var bytes = encryptedChunk.GetBuffer();
            var plaintext = new byte[chunk.PlaintextLength];
            try
            {
                using (var aes = new AesGcm(dek, 16))
                    aes.Decrypt(ChunkNonce(prefix, chunk.Index), bytes.AsSpan(0, chunk.PlaintextLength), bytes.AsSpan(chunk.PlaintextLength, 16), plaintext, ChunkAad(objectHash, chunk.Index, chunk.PlaintextLength));
                var start = (int)Math.Max(0, offset - plainPosition);
                var end = (int)Math.Min(chunk.PlaintextLength, wantedEnd - plainPosition);
                await destination.WriteAsync(plaintext.AsMemory(start, end - start), cancellationToken);
            }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
            plainPosition = chunkEnd;
        }
    }

    private H2seKeyring Keyring() => _keyring ??= H2seKeyring.Load(_options.KeyringPath);
    private static string ManifestKey(string key) => key + ".h2se/manifest";
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"h2se-{Guid.NewGuid():N}");
    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }
    private static byte[] ChunkNonce(byte[] prefix, int index) { var nonce = new byte[12]; prefix.CopyTo(nonce, 0); BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(8), checked((uint)index)); return nonce; }
    private static byte[] ChunkAad(byte[] objectHash, int index, int length) { var aad = new byte[40]; objectHash.CopyTo(aad, 0); BinaryPrimitives.WriteInt32BigEndian(aad.AsSpan(32), index); BinaryPrimitives.WriteInt32BigEndian(aad.AsSpan(36), length); return aad; }
    private static byte[] ManifestAad(byte[] objectHash) => Encoding.ASCII.GetBytes("H2SE manifest v1").Concat(objectHash).ToArray();
    private static byte[] WrapAad(string keyId, byte[] objectHash) => Encoding.UTF8.GetBytes("H2SE DEK v1\0" + keyId).Concat(objectHash).ToArray();
    private static async Task<int> ReadChunkAsync(Stream stream, byte[] buffer, CancellationToken token) { var total = 0; while (total < buffer.Length) { var read = await stream.ReadAsync(buffer.AsMemory(total), token); if (read == 0) break; total += read; } return total; }

    private sealed record H2seEnvelope(string Format, int Version, string KeyId, string Nonce, string Ciphertext, string Tag);
    private sealed record H2seManifestPayload(H2seManifest Manifest, string WrapNonce, string WrappedDek, string WrapTag);
    private sealed record H2seManifest(string PhysicalDataKey, long PlaintextLength, string ContentType, string PlaintextSha256, string NoncePrefix, IReadOnlyList<H2seChunk> Chunks);
    private sealed record H2seChunk(int Index, int PlaintextLength, long CiphertextOffset);
}

internal sealed class H2seKeyring
{
    private readonly Dictionary<string, byte[]> _keys;
    public string ActiveKeyId { get; }
    public byte[] ActiveKey => Get(ActiveKeyId);
    private H2seKeyring(string activeKeyId, Dictionary<string, byte[]> keys) { ActiveKeyId = activeKeyId; _keys = keys; }
    public byte[] Get(string keyId) => _keys.TryGetValue(keyId, out var key) ? key : throw new CryptographicException($"H2SE key '{keyId}' is unavailable.");
    public static H2seKeyring Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new InvalidOperationException("StorageEncryption:KeyringPath must point to a mounted keyring.");
        var document = JsonSerializer.Deserialize<KeyringDocument>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("The H2SE keyring is invalid.");
        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            foreach (var pair in document.Keys)
            {
                if (!keys.TryAdd(pair.Key, Convert.FromBase64String(pair.Value)))
                    throw new InvalidOperationException("The H2SE keyring contains a duplicate key ID.");
            }
            if (!keys.TryGetValue(document.ActiveKeyId, out var active) || keys.Values.Any(value => value.Length != 32) || active.Length != 32)
                throw new InvalidOperationException("Every H2SE KEK must be exactly 256 bits and activeKeyId must exist.");
            return new H2seKeyring(document.ActiveKeyId, keys);
        }
        catch
        {
            foreach (var key in keys.Values) CryptographicOperations.ZeroMemory(key);
            throw;
        }
    }
    public static bool IsValid(string path)
    {
        H2seKeyring? keyring = null;
        try { keyring = Load(path); return true; }
        catch { return false; }
        finally
        {
            if (keyring is not null)
                foreach (var key in keyring._keys.Values) CryptographicOperations.ZeroMemory(key);
        }
    }
    private sealed record KeyringDocument(string ActiveKeyId, Dictionary<string, string> Keys);
}
