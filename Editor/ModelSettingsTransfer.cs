using System.Linq;
using UnityEditor;
using UnityEditor.Presets;
using Object = UnityEngine.Object;

namespace FofuxoAnimationTools.Editor
{
    /// <summary>
    /// Hands a model's import settings to the model replacing it.
    ///
    /// A replacement arrives as whatever the exporter happened to write, which is
    /// almost never how the file it is standing in for was set up. The character
    /// being replaced was Generic with an avatar built from itself, compressed
    /// optimally, with four materials remapped onto the project's own; the new file
    /// has no avatar, different compression, and every material embedded. Setting
    /// that up again by hand is the part of a reimport that gets forgotten, and the
    /// consequences -- a missing avatar, a pink character -- turn up later looking
    /// like something else.
    ///
    /// Unity already has the mechanism for copying importer settings between assets:
    /// a Preset. It carries everything the inspector shows, the external object map
    /// included, and it knows which fields identify the asset rather than describe
    /// how to read it.
    ///
    /// The one thing not inherited is whether to import animation. That is not a
    /// setting so much as a statement about what the file is for, and the answer
    /// comes from the file being replaced: a character model that carried no takes
    /// is being replaced by a character model, whatever else happens to be inside
    /// the new file.
    /// </summary>
    public static class ModelSettingsTransfer
    {
        public static bool Applies(string fromPath, string toPath)
        {
            return fromPath != toPath &&
                   AssetImporter.GetAtPath(fromPath) is ModelImporter &&
                   AssetImporter.GetAtPath(toPath) is ModelImporter;
        }

        /// <summary>
        /// Where a copy of the replacement would land: beside the model it replaces.
        /// </summary>
        public static string CopyTarget(string modelPath, string replacementPath)
        {
            string folder = System.IO.Path.GetDirectoryName(modelPath).Replace('\\', '/');
            string name = System.IO.Path.GetFileName(replacementPath);

            return AssetDatabase.GenerateUniqueAssetPath($"{folder}/{name}");
        }

        /// <summary>
        /// Copies the replacement in beside the model it is replacing, so the
        /// character ends up living in a file of its own.
        ///
        /// Pointing the project straight at the file the animations came out of works
        /// until that file is deleted, and it is going to be -- an export carrying a
        /// hundred takes is a quarter of a gigabyte and exists to be thrown away. The
        /// character would leave with it. Copying first separates the two questions:
        /// what the project uses, and what the exporter happened to hand over.
        /// </summary>
        public static string Copy(string modelPath, string replacementPath)
        {
            string target = CopyTarget(modelPath, replacementPath);

            return AssetDatabase.CopyAsset(replacementPath, target) ? target : null;
        }

        /// <summary>
        /// The folder the model's own materials live in, which is where any material
        /// still stuck inside it belongs. Falls back to the model's folder when there
        /// is nothing to learn from.
        /// </summary>
        public static string MaterialFolder(string modelPath)
        {
            if (AssetImporter.GetAtPath(modelPath) is ModelImporter importer)
            {
                foreach (Object material in importer.GetExternalObjectMap().Values)
                {
                    string path = AssetDatabase.GetAssetPath(material);

                    if (!string.IsNullOrEmpty(path) && path.EndsWith(".mat"))
                    {
                        return System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
                    }
                }
            }

            return System.IO.Path.GetDirectoryName(modelPath).Replace('\\', '/');
        }

        /// <summary>
        /// What the replacement would gain, in the words of the Rig and Materials
        /// tabs, so it can be read before it is done rather than found afterwards.
        /// </summary>
        public static string Describe(string fromPath, string toPath)
        {
            if (!(AssetImporter.GetAtPath(fromPath) is ModelImporter from) ||
                !(AssetImporter.GetAtPath(toPath) is ModelImporter to))
            {
                return string.Empty;
            }

            var changes = new System.Collections.Generic.List<string>();

            if (from.animationType != to.animationType)
            {
                changes.Add($"rig {to.animationType} to {from.animationType}");
            }

            if (from.avatarSetup != to.avatarSetup)
            {
                changes.Add($"avatar {to.avatarSetup} to {from.avatarSetup}");
            }

            int remaps = from.GetExternalObjectMap().Count;
            if (remaps > to.GetExternalObjectMap().Count)
            {
                changes.Add($"{remaps} material remap(s)");
            }

            if (KeepsAnimation(from) != to.importAnimation)
            {
                changes.Add(KeepsAnimation(from)
                    ? "animation import on"
                    : "animation import off, since the model it replaces held no takes");
            }

            return changes.Count == 0
                ? "the replacement is already set up the same way"
                : string.Join(", ", changes);
        }

        public static void Adopt(string fromPath, string toPath)
        {
            if (!(AssetImporter.GetAtPath(fromPath) is ModelImporter from) ||
                !(AssetImporter.GetAtPath(toPath) is ModelImporter to))
            {
                return;
            }

            bool animation = KeepsAnimation(from);

            new Preset(from).ApplyTo(to);

            // The clip definitions describe takes in the old file by name and frame
            // range, and the new file's takes are not those. Cleared, the importer
            // reads the ones actually in front of it.
            to.clipAnimations = new ModelImporterClipAnimation[0];
            to.importAnimation = animation;

            to.SaveAndReimport();
        }

        /// <summary>
        /// Whether the model being replaced was an animation source at all. Read from
        /// the takes in the file rather than from the importer's switch, because a
        /// file with nothing to import says nothing by having the switch on.
        /// </summary>
        private static bool KeepsAnimation(ModelImporter importer)
        {
            return importer.importAnimation && importer.defaultClipAnimations.Length > 0;
        }
    }
}
