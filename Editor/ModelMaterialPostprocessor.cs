using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace FofuxoAnimationTools.Editor
{
    /// <summary>
    /// Runs the material match the moment a model lands in the project, so a new FBX
    /// arrives already wearing the project's materials instead of a set of fresh grey
    /// ones that have to be replaced by hand.
    ///
    /// Off by default, and deliberately so. Applying a remap means importing the
    /// model a second time, and a guess made from a name is worth looking at before
    /// it is made on your behalf -- which is what the window is for. This is for once
    /// the naming rules have been tuned and the guesses stopped being interesting.
    ///
    /// It only ever acts on a model with no material remaps at all. That makes it a
    /// first-import behaviour rather than something that keeps re-deciding, and it is
    /// also what stops the second import from starting a third.
    /// </summary>
    internal sealed class ModelMaterialPostprocessor : AssetPostprocessor
    {
        private static readonly HashSet<string> Seen = new HashSet<string>();

        private void OnPostprocessModel(GameObject root)
        {
            if (!FofuxoToolsSettings.RemapMaterialsOnImport)
            {
                return;
            }

            if (!(assetImporter is ModelImporter importer) ||
                importer.materialImportMode == ModelImporterMaterialImportMode.None)
            {
                return;
            }

            if (!Seen.Add(assetPath) || HasMaterialRemaps(importer))
            {
                return;
            }

            Dictionary<string, List<Material>> index =
                ModelMaterialMatcher.Index(FofuxoToolsSettings.MaterialSearchFolder);

            int matched = 0;

            foreach (string name in SlotNames(root))
            {
                if (!index.TryGetValue(ModelMaterialMatcher.Normalize(name), out List<Material> candidates) ||
                    candidates.Count != 1)
                {
                    continue;
                }

                importer.AddRemap(
                    new AssetImporter.SourceAssetIdentifier(typeof(Material), name),
                    candidates[0]);

                matched++;
            }

            if (matched == 0)
            {
                return;
            }

            // The remaps only take effect on the next import, and reimporting from
            // inside an import is not allowed. This is that next import.
            string path = assetPath;
            EditorApplication.delayCall += () =>
            {
                AssetDatabase.WriteImportSettingsIfDirty(path);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                Debug.Log($"Matched {matched} material(s) by name on '{path}'.");
            };
        }

        private static bool HasMaterialRemaps(ModelImporter importer)
        {
            foreach (AssetImporter.SourceAssetIdentifier identifier in importer.GetExternalObjectMap().Keys)
            {
                if (identifier.type == typeof(Material))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// On a first import nothing is remapped yet, so the materials hanging off
        /// the renderers are the ones the model file named.
        /// </summary>
        private static IEnumerable<string> SlotNames(GameObject root)
        {
            var names = new HashSet<string>();

            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                foreach (Material material in renderer.sharedMaterials)
                {
                    if (material != null)
                    {
                        names.Add(material.name);
                    }
                }
            }

            return names;
        }
    }
}
