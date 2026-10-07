namespace XivRug.Plugin;

internal readonly record struct TerrainArchiveHeader(uint Size, uint Type, uint RawBytes, uint Blocks);

/// <summary>Admission before the reviewed Lumina decoder allocates RawFileSize.
/// This is for immutable installed SqPack archives, not a hostile file service.</summary>
internal static class TerrainArchiveReadPolicy
{
    internal const int MaximumBytes = 96 * 1024 * 1024;
    internal const uint MaximumStandardBlocks = 16384;

    internal static bool Allows(TerrainArchiveHeader header, int maximumBytes, bool model)
    {
        if (maximumBytes is <= 0 or > MaximumBytes || header.RawBytes == 0
            || header.RawBytes > maximumBytes || header.Size is < 24 or > 1024 * 1024
            || header.Type != (model ? 3u : 2u)) return false;
        // Model's common-header Blocks slot is NOT its standard block count.
        // Its separate read table sums eleven ushort counts (bounded <1.5MiB).
        // Standard's table count is uint and therefore needs a separate cap.
        return model || header.Blocks is > 0 and <= MaximumStandardBlocks
            && 24UL + header.Blocks * 8UL <= header.Size;
    }

    internal static bool Matches(TerrainArchiveHeader before, TerrainArchiveHeader after,
        uint decodedType, uint decodedRawBytes, int actualLength) => before == after
        && decodedType == before.Type && decodedRawBytes == before.RawBytes
        && actualLength >= 0 && (uint)actualLength == before.RawBytes;
}
