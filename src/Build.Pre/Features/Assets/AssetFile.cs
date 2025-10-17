using Build.Shared;

namespace Build.Pre.Features.Assets;

internal readonly record struct VariantData(int Start, int End)
{
    public int Count => End - Start + 1;
}

internal readonly record struct AssetFile(string Name, string Path, IAssetReference Reference, ProjectFile File, VariantData? Variants = null);
