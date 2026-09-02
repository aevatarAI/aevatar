using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Aevatar.BackendConsole.Hosting;

/// <summary>
/// A console page rendered once per process: final UTF-8 bytes, a strong content hash
/// for conditional requests, and precompressed variants negotiated at serve time.
/// </summary>
public sealed class BackendConsoleRenderedAsset
{
    private BackendConsoleRenderedAsset(
        string contentType,
        string etag,
        byte[] identityBytes,
        byte[]? gzipBytes,
        byte[]? brotliBytes)
    {
        ContentType = contentType;
        ETag = etag;
        IdentityBytes = identityBytes;
        GzipBytes = gzipBytes;
        BrotliBytes = brotliBytes;
    }

    public string ContentType { get; }

    public string ETag { get; }

    public byte[] IdentityBytes { get; }

    public byte[]? GzipBytes { get; }

    public byte[]? BrotliBytes { get; }

    public static BackendConsoleRenderedAsset Create(string content, string contentType)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(contentType);

        var identityBytes = Encoding.UTF8.GetBytes(content);
        var etag = "\"" + Convert.ToHexStringLower(SHA256.HashData(identityBytes)) + "\"";
        var gzipBytes = CompressSmaller(identityBytes, static stream => new GZipStream(stream, CompressionLevel.SmallestSize, leaveOpen: true));
        var brotliBytes = CompressSmaller(identityBytes, static stream => new BrotliStream(stream, CompressionLevel.SmallestSize, leaveOpen: true));
        return new BackendConsoleRenderedAsset(contentType, etag, identityBytes, gzipBytes, brotliBytes);
    }

    private static byte[]? CompressSmaller(byte[] identityBytes, Func<Stream, Stream> compressorFactory)
    {
        using var buffer = new MemoryStream();
        using (var compressor = compressorFactory(buffer))
        {
            compressor.Write(identityBytes);
        }

        return buffer.Length < identityBytes.Length ? buffer.ToArray() : null;
    }
}
