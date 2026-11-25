using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Scoreboard.Core;

namespace Scoreboard.Desktop;

/// <summary>
/// The on-the-wire node package. A JSON manifest is followed by each asset's raw bytes, so a
/// multi-gigabyte show video is streamed to disk instead of becoming a base64 string inside JSON.
/// The format is deliberately small: magic, format version, manifest length, manifest, assets.
/// </summary>
internal static class NodePackageBinary
{
    public const string ContentType = "application/vnd.scoreplus.node-package";
    public const long ManifestLimit = 16L * 1024 * 1024;
    const int FormatVersion = 2;
    static readonly byte[] Magic = "SPN2"u8.ToArray();
    static readonly JsonSerializerOptions ManifestJsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static void Write(string path, NodePackage package)
    {
        var manifest = JsonSerializer.SerializeToUtf8Bytes(package, ManifestJsonOptions);
        if (manifest.Length > ManifestLimit)
            throw new InvalidOperationException($"The node package manifest is larger than {ManifestLimit / 1024 / 1024} MB.");

        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            1024 * 1024, FileOptions.SequentialScan);
        output.Write(Magic);
        var header = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(header, FormatVersion);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(4), manifest.Length);
        output.Write(header);
        output.Write(manifest);

        foreach (var asset in package.Assets ?? [])
        {
            using var input = new FileStream(asset.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.SequentialScan);
            if (input.Length != asset.Size)
                throw new IOException($"{asset.SourcePath} changed while the package was being built.");
            CopyExactly(input, output, asset.Size);
        }
    }

    public static NodePackage ReadManifest(Stream stream)
    {
        var magic = new byte[4];
        ReadExactly(stream, magic, "package header");
        if (!magic.AsSpan().SequenceEqual(Magic))
            throw new InvalidDataException("This is not a ScorePlus node package. Rebuild and send the package again.");

        var header = new byte[12];
        ReadExactly(stream, header, "package header");
        var version = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (version != FormatVersion)
            throw new InvalidDataException($"Node package format {version} is not supported by this agent.");
        var manifestLength = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(4));
        if (manifestLength <= 0 || manifestLength > ManifestLimit)
            throw new InvalidDataException("The node package manifest length is invalid.");

        var manifest = new byte[manifestLength];
        ReadExactly(stream, manifest, "package manifest");
        return JsonSerializer.Deserialize<NodePackage>(manifest, ManifestJsonOptions)
            ?? throw new InvalidDataException("The node package manifest is empty.");
    }

    public static async Task CopyExactlyAsync(Stream source, Stream destination, long length,
        IncrementalHash? hash, CancellationToken token)
    {
        var buffer = new byte[1024 * 1024];
        var remaining = length;
        while (remaining > 0)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token)
                .ConfigureAwait(false);
            if (read <= 0) throw new EndOfStreamException("The package body ended before its declared length.");
            hash?.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            remaining -= read;
        }
    }

    static void CopyExactly(Stream source, Stream destination, long length)
    {
        var buffer = new byte[1024 * 1024];
        var remaining = length;
        while (remaining > 0)
        {
            var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read <= 0) throw new EndOfStreamException("An asset ended before its declared length.");
            destination.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    static void ReadExactly(Stream stream, byte[] buffer, string description)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = stream.Read(buffer, read, buffer.Length - read);
            if (count <= 0) throw new EndOfStreamException($"The {description} ended early.");
            read += count;
        }
    }
}