using System.Buffers.Binary;
using System.Text.Json;

namespace VisionStudio.Abstractions;

public static class PluginWorkerWire
{
    private const int MaxFrameBytes = 256 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static async ValueTask WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (payload.Length > MaxFrameBytes) throw new InvalidOperationException($"Plugin worker IPC frame is too large: {payload.Length} bytes.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async ValueTask<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await ReadExactlyAsync(stream, header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxFrameBytes) throw new InvalidOperationException($"Invalid plugin worker IPC frame length {length}.");
        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken);
        return JsonSerializer.Deserialize<T>(payload, Json) ?? throw new InvalidOperationException($"Could not deserialize plugin worker IPC {typeof(T).Name}.");
    }

    private static async ValueTask ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0) throw new EndOfStreamException("Plugin worker pipe closed unexpectedly.");
            offset += read;
        }
    }
}
