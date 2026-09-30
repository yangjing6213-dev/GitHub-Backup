using System.Globalization;
using System.Security.Cryptography;
using System.Security.Principal;

namespace GitHubBackup.App;

internal static class ContentIntegrity
{
    internal static ReleaseAssetExpectation Expect(ReleaseAsset asset)
    {
        if (asset.Size < 0) throw new ReleaseException("RELEASE_INVENTORY_INVALID");
        if (asset.Digest is null) return new(asset.Size, null);
        if (!asset.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            || asset.Digest.Length != 71 || !asset.Digest.AsSpan(7).ToArray().All(Uri.IsHexDigit))
            throw new ReleaseException("RELEASE_INVENTORY_INVALID");
        return new(asset.Size, asset.Digest[7..].ToUpperInvariant());
    }

    internal static async Task<bool> MatchesFileAsync(string path, ReleaseAssetExpectation expected, CancellationToken token)
    {
        try
        {
            using var handle = NativeFileSystem.Open(path);
            NativeFileSystem.Inspect(handle, path, directory: false);
            if (AclPolicy.Evaluate(AclPolicy.ReadDescriptor(handle), WindowsIdentity.GetCurrent().User!, true) != AclRisk.Safe)
                return false;
            await using var file = new FileStream(handle, FileAccess.Read);
            if (file.Length != expected.Size) return false;
            return expected.Sha256 is null || string.Equals(
                Convert.ToHexString(await SHA256.HashDataAsync(file, token).ConfigureAwait(false)), expected.Sha256,
                StringComparison.Ordinal);
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    internal static FileStream OpenPinnedRead(string path)
    {
        var handle = NativeFileSystem.Open(path, shareWrite: false);
        try
        {
            NativeFileSystem.Inspect(handle, path, directory: false);
            if (AclPolicy.Evaluate(AclPolicy.ReadDescriptor(handle), WindowsIdentity.GetCurrent().User!, true) != AclRisk.Safe)
                throw new ReleaseException("RELEASE_GENERATION_INCOMPLETE");
            return new FileStream(handle, FileAccess.Read);
        }
        catch { handle.Dispose(); throw; }
    }

    internal static async Task<FileStream> PinVerifiedAsync(string path, ReleaseAssetExpectation expected, CancellationToken token)
    {
        FileStream stream = OpenPinnedRead(path);
        try
        {
            if (stream.Length != expected.Size) throw new ReleaseException("RELEASE_GENERATION_INCOMPLETE");
            if (expected.Sha256 is not null && !string.Equals(
                Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)), expected.Sha256,
                StringComparison.Ordinal)) throw new ReleaseException("RELEASE_GENERATION_INCOMPLETE");
            NativeFileSystem.Inspect(stream.SafeFileHandle, path, directory: false);
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    internal static async Task<AssetDownloadResult> CopyAsync(GitHubResponse response, FileStream output,
        string path, ReleaseAssetExpectation expected, CancellationToken token)
    {
        if (response.StatusCode != 200) throw new ReleaseException("RELEASE_ASSET_HTTP_INVALID");
        if (response.Headers.TryGetValue("Content-Length", out string? lengthText)
            && (!long.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out long declared)
                || declared != expected.Size)) throw new ReleaseException("RELEASE_ASSET_SIZE_MISMATCH");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        long total = 0;
        int count;
        while ((count = await response.Body.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (total > expected.Size - count) throw new ReleaseException("RELEASE_ASSET_SIZE_MISMATCH");
            await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            hash.AppendData(buffer, 0, count);
            total += count;
        }
        if (total != expected.Size) throw new ReleaseException("RELEASE_ASSET_SIZE_MISMATCH");
        string actual = Convert.ToHexString(hash.GetHashAndReset());
        if (expected.Sha256 is not null && actual != expected.Sha256)
            throw new ReleaseException("RELEASE_ASSET_DIGEST_MISMATCH");
        return new(path, total, actual, expected.Sha256 is not null);
    }
}
