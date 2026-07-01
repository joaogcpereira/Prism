// ============================================================
//  PipeFraming.cs  (Prism.Agent.Contracts)
//  Length-prefixed framing over a byte-mode stream:
//      [4-byte little-endian length N][N bytes payload]
//  Robust for arbitrary sizes; bounded by maxBytes (anti-DoS).
// ============================================================
using System.Buffers.Binary;

namespace Prism.Agent.Contracts;

public static class PipeFraming
{
    /// <summary>Reads one frame. Returns null on a clean end-of-stream (no frame).</summary>
    public static async Task<byte[]?> ReadFrameAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        var header = new byte[4];

        // Peek the first byte: 0 bytes read => connection closed cleanly, no frame.
        int first = await stream.ReadAsync(header.AsMemory(0, 1), ct).ConfigureAwait(false);
        if (first == 0) return null;

        await stream.ReadExactlyAsync(header.AsMemory(1, 3), ct).ConfigureAwait(false);
        int len = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (len < 0 || len > maxBytes)
            throw new InvalidDataException($"Frame length {len} is out of bounds (max {maxBytes}).");
        if (len == 0) return Array.Empty<byte>();

        var payload = new byte[len];
        await stream.ReadExactlyAsync(payload.AsMemory(0, len), ct).ConfigureAwait(false);
        return payload;
    }

    /// <summary>Writes one frame.</summary>
    public static async Task WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> payload, int maxBytes, CancellationToken ct)
    {
        if (payload.Length > maxBytes)
            throw new InvalidDataException($"Payload {payload.Length} exceeds max frame {maxBytes}.");

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }
}
