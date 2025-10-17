using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Build.Shared;

namespace Build.Pre.Features.Assets;

// Some parts of this are based on:
// https://github.com/LucilleKarma/WotG/blob/main/src/WotGAssetRebuilder/Program.cs
// Mostly to do with handling sound variants and some prettier formatting for
// the generated code.
internal sealed partial class AssetGenerator : BuildTask
{
    private static readonly Regex end_number_regex = EndNumberFinder();

    private static readonly char[] number_chars = ['0', '1', '2', '3', '4', '5', '6', '7', '8', '9'];

    private static readonly IAssetReference[] asset_references =
    [
        new TextureReference(),
        new SoundReference(),
        new EffectReference(),
    ];

    public override void Run(ProjectContext ctx)
    {
        GenerateCommonFiles(ctx);

        var root = CreateAssetTree(ctx.EnumerateProjectFiles());
        GenerateAssetFile(ctx, root);

        return;

        static PathNode CreateAssetTree(IEnumerable<ProjectFile> files)
        {
            var rootNode = new PathNode("Root", [], []);

            foreach (var file in files)
            {
                if (!Eligible(file, out var reference))
                {
                    continue;
                }

                var pathNodes = file.RelativePath.Split('/');
                var fileName = Path.GetFileNameWithoutExtension(file.RelativePath);

                var currentNode = rootNode;
                for (var i = 0; i < pathNodes.Length - 1; i++) // -1 to exclude the file name
                {
                    var directoryName = pathNodes[i];

                    if (!currentNode.Nodes.TryGetValue(directoryName, out var value))
                    {
                        value = new PathNode(directoryName, [], []);
                        currentNode.Nodes[directoryName] = value;
                    }

                    currentNode = value;
                }

                var assetFile = new AssetFile(fileName, file.RelativePath, reference, file);
                currentNode.Files.Add(assetFile);
            }

            return rootNode;
        }

        static bool Eligible(ProjectFile file, out IAssetReference reference)
        {
            foreach (var assetReference in asset_references)
            {
                if (!assetReference.Eligible(file))
                {
                    continue;
                }

                reference = assetReference;
                return true;
            }

            reference = null!;
            return false;
        }
    }

    private static void GenerateAssetFile(ProjectContext ctx, PathNode root)
    {
        ctx.WriteFile(
            "Core/AssetReferences.cs",
            $$"""
              #pragma warning disable CS8981

              namespace {{ctx.ProjectNamespace}}.Core;

              // ReSharper disable InconsistentNaming
              internal static class AssetReferences
              {
              {{GenerateTextFromPathNode(ctx, root)}}
              }
              """
        );

        return;

        static string GenerateTextFromPathNode(ProjectContext ctx, PathNode root, int depth = 0)
        {
            var sb = new StringBuilder();

            var indent = new string(' ', depth * 4);

            if (depth != 0)
            {
                sb.AppendLine($"{indent}public static class {NormalizeName(root.Name)}");
                sb.AppendLine($"{indent}{{");
            }

            var seenVariantBases = new HashSet<string>();

            for (var i = 0; i < root.Files.Count; i++)
            {
                var file = root.Files[i];

                if (file.Reference.IsVariant)
                {
                    var numberMatch = end_number_regex.Match(file.Name);
                    if (numberMatch.Success)
                    {
                        var trimmedName = numberMatch.Groups[1].Value;

                        if (seenVariantBases.Add(trimmedName))
                        {
                            var pathExt = Path.GetExtension(file.Path);
                            
                            var variantFile = file with
                            {
                                Name = trimmedName,
                                Path = Path.ChangeExtension(file.Path, null).TrimEnd(number_chars) + pathExt,
                                Variants = GetVariantCount(root.Files.Select(x => x.Name), trimmedName),
                            };

                            sb.AppendLine($"{indent}    public static class {NormalizeName(variantFile.Name)}");
                            sb.AppendLine($"{indent}    {{");

                            sb.AppendLine(variantFile.Reference.GenerateCode(ctx, variantFile, $"{indent}        "));

                            sb.AppendLine($"{indent}    }}");
                            sb.AppendLine();
                        }
                    }
                }

                sb.AppendLine($"{indent}    public static class {NormalizeName(file.Name)}");
                sb.AppendLine($"{indent}    {{");

                sb.AppendLine(file.Reference.GenerateCode(ctx, file, $"{indent}        "));

                sb.AppendLine($"{indent}    }}");

                if (i != root.Files.Count - 1)
                {
                    sb.AppendLine();
                }
            }

            if (root.Files.Count > 0 && root.Nodes.Count > 0)
            {
                sb.AppendLine();
            }

            var j = 0;
            foreach (var node in root.Nodes.Values)
            {
                if (j++ != 0)
                {
                    sb.AppendLine();
                }

                sb.AppendLine(GenerateTextFromPathNode(ctx, node, depth + 1));
            }

            if (depth != 0)
            {
                sb.AppendLine($"{indent}}}");
            }

            return sb.ToString().TrimEnd();
        }

        static string NormalizeName(string name)
        {
            // - Replace any non-alphanumeric characters with underscores,
            // - trim trailing underscores as a result of variant number slices
            //   or simply poor naming.
            return NonAlphanumeric()
                  .Replace(name, "_")
                  .TrimEnd('_');
        }
    }

    private static int GetVariantCount(IEnumerable<string> fileNames, string assetName)
    {
        var variants = 1;

        foreach (var name in fileNames)
        {
            if (!name.Contains(assetName))
            {
                continue;
            }

            var numberResult = end_number_regex.Match(Path.GetFileNameWithoutExtension(name));
            if (numberResult.Success)
            {
                variants = Math.Max(variants, int.Parse(numberResult.Groups[2].Value));
            }
        }

        return variants;
    }

    private static void GenerateCommonFiles(ProjectContext ctx)
    {
        ctx.WriteFile(
            "Core/IShaderParameters.cs",
            $$"""
              using Microsoft.Xna.Framework.Graphics;

              namespace {{ctx.ProjectNamespace}}.Core;

              internal interface IShaderParameters
              {
                  void Apply(EffectParameterCollection parameters);
              }
              """
        );

        ctx.WriteFile(
            "Core/WrappedShaderData.cs",
            $$"""
              using Microsoft.Xna.Framework.Graphics;

              using ReLogic.Content;

              using Terraria.Graphics.Shaders;

              namespace {{ctx.ProjectNamespace}}.Core;

              internal sealed class WrapperShaderData<TParameters>(Asset<Effect> shader, string passName) : ShaderData(shader, passName)
                  where TParameters : IShaderParameters, new()
              {
                  public TParameters Parameters { get; } = new();

                  public override void Apply()
                  {
                      Parameters.Apply(Shader.Parameters);

                      base.Apply();
                  }
              }
              """
        );
    }

    [GeneratedRegex(@"([^\d]+)([\d]+)$")]
    private static partial Regex EndNumberFinder();

    [GeneratedRegex(@"[^\w]")]
    private static partial Regex NonAlphanumeric();
}
