// Preserve the persisted CLR type for installations still using the original
// development-plugin identity. No saved footprint settings need to be deleted.
namespace XivFloorMap.Plugin;

public sealed class Configuration : XivRug.Plugin.Configuration;
