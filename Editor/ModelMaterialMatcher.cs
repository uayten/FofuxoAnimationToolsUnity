using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace FofuxoAnimationTools.Editor
{
    /// <summary>
    /// Finds the material a model is asking for among the ones the project already
    /// has, and points the importer at it.
    ///
    /// Unity has a version of this: the Materials tab has a Search and Remap button
    /// that looks for a material file of the same name. It matches on the name
    /// exactly, which is precisely where a model arriving from another engine falls
    /// down. Unreal names a material instance MI_GrantClothes; the same material,
    /// built in Unity by hand, is called M_GrantClothes. Two conventions for the same
    /// surface, and no exact match between them, so the button comes back with
    /// nothing and every slot is filled with a fresh grey material instead.
    ///
    /// The fix is to compare the names with the convention taken off both: strip the
    /// prefix that says "this is a material" from each side and both read
    /// GrantClothes.
    ///
    /// What gets written is the importer's external object map -- the same thing the
    /// Search and Remap button writes -- so the result is an ordinary remap, visible
    /// in the Materials tab and undone from there.
    /// </summary>
    public static class ModelMaterialMatcher
    {
        public sealed class Slot
        {
            /// <summary>The material name baked into the model file.</summary>
            public string Name;

            /// <summary>What the importer currently resolves it to, if anything.</summary>
            public Material Current;

            /// <summary>The project material that matches, if exactly one does.</summary>
            public Material Suggestion;

            public string Note = string.Empty;

            public bool WouldChange => Suggestion != null && Suggestion != Current;
        }

        public sealed class Model
        {
            public string Path;

            /// <summary>
            /// The importer, whichever kind it is. Remapping a material slot is
            /// AssetImporter's own mechanism rather than the model importer's, so a glb
            /// coming in through a scripted importer takes the same remaps an FBX does.
            /// </summary>
            public AssetImporter Importer;

            public readonly List<Slot> Slots = new List<Slot>();
            public string Warning = string.Empty;

            public int Changes
            {
                get
                {
                    int count = 0;
                    foreach (Slot slot in Slots)
                    {
                        if (slot.WouldChange)
                        {
                            count++;
                        }
                    }

                    return count;
                }
            }
        }

        public static string Normalize(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return string.Empty;
            }

            string result = name;

            foreach (string prefix in FofuxoToolsSettings.Split(FofuxoToolsSettings.MaterialPrefixes))
            {
                if (result.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase))
                {
                    result = result.Substring(prefix.Length);
                    break;
                }
            }

            foreach (string suffix in FofuxoToolsSettings.Split(FofuxoToolsSettings.MaterialSuffixes))
            {
                if (result.EndsWith(suffix, System.StringComparison.OrdinalIgnoreCase))
                {
                    result = result.Substring(0, result.Length - suffix.Length);
                    break;
                }
            }

            return result.Replace(" ", string.Empty).ToLowerInvariant();
        }

        /// <summary>Every material under a folder, bucketed by its stripped name.</summary>
        public static Dictionary<string, List<Material>> Index(string folder)
        {
            var index = new Dictionary<string, List<Material>>();

            string[] folders = AssetDatabase.IsValidFolder(folder)
                ? new[] { folder }
                : new[] { "Assets" };

            foreach (string guid in AssetDatabase.FindAssets("t:Material", folders))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);

                // The search also returns the materials living inside model files, and
                // those are the very things being matched away from. Left in, a model
                // matches its own embedded material, the remap points the file at
                // itself, and every prefab built from it stays tied to the FBX for a
                // material that was supposed to have been replaced.
                if (!path.EndsWith(".mat", System.StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var material = AssetDatabase.LoadAssetAtPath<Material>(path);

                if (material == null)
                {
                    continue;
                }

                string key = Normalize(material.name);

                if (!index.TryGetValue(key, out List<Material> bucket))
                {
                    bucket = new List<Material>();
                    index[key] = bucket;
                }

                bucket.Add(material);
            }

            return index;
        }

        public static Model Inspect(string modelPath, Dictionary<string, List<Material>> index)
        {
            var model = new Model
            {
                Path = modelPath,
                Importer = AssetImporter.GetAtPath(modelPath)
            };

            if (model.Importer == null)
            {
                return model;
            }

            if (!ModelAsset.ImportsMaterials(modelPath))
            {
                model.Warning = "the importer has materials turned off";
                return model;
            }

            Dictionary<AssetImporter.SourceAssetIdentifier, Object> remaps =
                model.Importer.GetExternalObjectMap();

            foreach (string name in SlotNames(model.Importer, modelPath, remaps))
            {
                var slot = new Slot { Name = name };

                var identifier = new AssetImporter.SourceAssetIdentifier(typeof(Material), name);
                if (remaps.TryGetValue(identifier, out Object current))
                {
                    slot.Current = current as Material;
                }

                Resolve(slot, index, modelPath);
                model.Slots.Add(slot);
            }

            return model;
        }

        public static void Resolve(Slot slot, Dictionary<string, List<Material>> index, string modelPath)
        {
            if (!index.TryGetValue(Normalize(slot.Name), out List<Material> candidates) || candidates.Count == 0)
            {
                slot.Note = "no material in the project matches this name";
                return;
            }

            if (candidates.Count == 1)
            {
                slot.Suggestion = candidates[0];
                slot.Note = candidates[0].name == slot.Name ? "exact name" : $"matches {candidates[0].name}";
                return;
            }

            // Several materials strip down to the same word. The one spelled exactly
            // like the slot wins; failing that, the one living nearest the model does,
            // because a character folder is a stronger signal than the project is.
            Material best = null;
            int bestScore = -1;
            bool tied = false;

            foreach (Material candidate in candidates)
            {
                int score = candidate.name == slot.Name ? 10_000 : 0;
                score += SharedPathLength(modelPath, AssetDatabase.GetAssetPath(candidate));

                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                    tied = false;
                }
                else if (score == bestScore)
                {
                    tied = true;
                }
            }

            if (tied)
            {
                slot.Note = $"{candidates.Count} materials match this name equally well";
                return;
            }

            slot.Suggestion = best;
            slot.Note = $"{candidates.Count} candidates, nearest is {best.name}";
        }

        private static int SharedPathLength(string a, string b)
        {
            int length = Mathf.Min(a.Length, b.Length);
            int shared = 0;

            while (shared < length && a[shared] == b[shared])
            {
                shared++;
            }

            return shared;
        }

        /// <summary>
        /// The material names the model file itself carries.
        ///
        /// There is no single API for this. The importer keeps the authoritative list
        /// in a serialised field, which is what the Materials tab lists; the remaps
        /// already in place name the ones that have been resolved; and the ones that
        /// have not are sitting in the model as embedded sub-assets. All three are
        /// read and merged, because which of them has the answer depends on how far
        /// along the model already is.
        /// </summary>
        private static IEnumerable<string> SlotNames(
            AssetImporter importer,
            string modelPath,
            Dictionary<AssetImporter.SourceAssetIdentifier, Object> remaps)
        {
            var names = new List<string>();
            var seen = new HashSet<string>();

            var serialized = new SerializedObject(importer);
            SerializedProperty materials = serialized.FindProperty("m_Materials");

            if (materials != null && materials.isArray)
            {
                for (int i = 0; i < materials.arraySize; i++)
                {
                    SerializedProperty entry = materials.GetArrayElementAtIndex(i);
                    SerializedProperty name = entry.FindPropertyRelative("name");

                    // The model importer keeps a struct with a name in it. A scripted
                    // importer is free to keep the materials themselves -- UnityGLTF
                    // does -- in which case the name is on the object.
                    string slot = name != null
                        ? name.stringValue
                        : entry.objectReferenceValue != null
                            ? entry.objectReferenceValue.name
                            : string.Empty;

                    if (!string.IsNullOrEmpty(slot) && seen.Add(slot))
                    {
                        names.Add(slot);
                    }
                }
            }

            foreach (AssetImporter.SourceAssetIdentifier identifier in remaps.Keys)
            {
                if (identifier.type == typeof(Material) && seen.Add(identifier.name))
                {
                    names.Add(identifier.name);
                }
            }

            foreach (Object member in AssetDatabase.LoadAllAssetRepresentationsAtPath(modelPath))
            {
                if (member is Material material && seen.Add(material.name))
                {
                    names.Add(material.name);
                }
            }

            names.Sort(string.CompareOrdinal);
            return names;
        }

        /// <summary>
        /// Writes the accepted suggestions to the importer and reimports. Returns how
        /// many slots were remapped.
        /// </summary>
        public static int Apply(Model model)
        {
            if (model.Importer == null)
            {
                return 0;
            }

            int changed = 0;

            foreach (Slot slot in model.Slots)
            {
                if (!slot.WouldChange)
                {
                    continue;
                }

                model.Importer.AddRemap(
                    new AssetImporter.SourceAssetIdentifier(typeof(Material), slot.Name),
                    slot.Suggestion);

                changed++;
            }

            if (changed > 0)
            {
                model.Importer.SaveAndReimport();
            }

            return changed;
        }
    }
}
