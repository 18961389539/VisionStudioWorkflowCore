using System.Buffers.Binary;

namespace VisionStudio.Engine.Nodes;

internal static class TiffPageSelection
{
    // Redirect the private image buffer to the requested IFD. OpenCV decodes only that page,
    // using the original absolute strip/tile offsets. The on-disk file stays intact.
    // https://libtiff.gitlab.io/libtiff/specification/bigtiff.html
    public static void Select(Span<byte> buffer, int pageIndex, string fileName, CancellationToken ct)
    {
        if (buffer.Length < 8 || (buffer[0], buffer[1]) is not ((0x49, 0x49) or (0x4D, 0x4D)))
            throw InvalidDirectory(fileName);
        var littleEndian = buffer[0] == 0x49;
        var magic = ReadUInt(buffer[2..], 2, littleEndian);
        var wide = magic == 43 && buffer.Length >= 16 && ReadUInt(buffer[4..], 2, littleEndian) == 8
            && ReadUInt(buffer[6..], 2, littleEndian) == 0;
        if (magic != 42 && !wide) throw InvalidDirectory(fileName);

        var headerOffset = wide ? 8 : 4;
        var offsetLength = wide ? 8 : 4;
        var countLength = wide ? 8 : 2;
        var entryLength = wide ? 20 : 12;
        var visited = new HashSet<ulong>();
        var directory = ReadUInt(buffer[headerOffset..], offsetLength, littleEndian);
        for (var page = 0; ; page++)
        {
            ct.ThrowIfCancellationRequested();
            if (directory == 0)
                throw new InvalidDataException($"image.load TIFF '{fileName}' has {page} pages. Page index {pageIndex} is out of range.");
            if (directory < (ulong)(headerOffset + offsetLength) ||
                directory > (ulong)(buffer.Length - countLength - offsetLength) || !visited.Add(directory))
                throw InvalidDirectory(fileName);
            var offset = (int)directory;
            var count = ReadUInt(buffer[offset..], countLength, littleEndian);
            if (count > (ulong)((buffer.Length - offset - countLength - offsetLength) / entryLength))
                throw InvalidDirectory(fileName);
            if (page == pageIndex) break;
            directory = ReadUInt(buffer[(offset + countLength + (int)count * entryLength)..], offsetLength, littleEndian);
        }

        var firstDirectory = buffer[headerOffset..];
        if (wide && littleEndian) BinaryPrimitives.WriteUInt64LittleEndian(firstDirectory, directory);
        else if (wide) BinaryPrimitives.WriteUInt64BigEndian(firstDirectory, directory);
        else if (littleEndian) BinaryPrimitives.WriteUInt32LittleEndian(firstDirectory, (uint)directory);
        else BinaryPrimitives.WriteUInt32BigEndian(firstDirectory, (uint)directory);
    }

    private static ulong ReadUInt(ReadOnlySpan<byte> bytes, int length, bool littleEndian) => (length, littleEndian) switch
    {
        (2, true) => BinaryPrimitives.ReadUInt16LittleEndian(bytes),
        (2, false) => BinaryPrimitives.ReadUInt16BigEndian(bytes),
        (4, true) => BinaryPrimitives.ReadUInt32LittleEndian(bytes),
        (4, false) => BinaryPrimitives.ReadUInt32BigEndian(bytes),
        (8, true) => BinaryPrimitives.ReadUInt64LittleEndian(bytes),
        _ => BinaryPrimitives.ReadUInt64BigEndian(bytes)
    };

    private static InvalidDataException InvalidDirectory(string fileName)
        => new($"image.load TIFF '{fileName}' has an invalid page directory.");
}
