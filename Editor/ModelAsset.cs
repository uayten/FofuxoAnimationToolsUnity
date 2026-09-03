using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FofuxoAnimationTools.Editor
{
    /// <summary>
    /// What counts as a model, now that not every model file is a model to Unity.
    ///
    /// FBX, OBJ, DAE and the rest arrive through the built-in ModelImporter, which is
    /// what `t:Model` searches for and what `is ModelImporter` tests. A `.glb` arrives
    /// through a ScriptedImporter that some package installed — UnityGLTF here — and
    /// to the asset database it is a GameObject like any other. It is invisible to
    /// every `t:Model` search and fails every importer test, while holding the same
    /// skeleton, meshes, materials and named animations the FBX beside it holds.
    ///
    /// So the question is asked of the result rather than of the importer: an asset
    /// whose main object is a GameObject, and that came from a file rather than being
    /// authored in Unity, is a model. Prefabs are what has to be kept out — they are
    /// GameObjects from a file too, but a prefab is a scene fragment, not an import.
    ///
    /// Nothing here names UnityGLTF. Another glTF importer, or a USD one, drops into
    /// the same hole and comes out the same way.
    /// </summary>
    public static class ModelAsset
    {
        /// <summary>True for a file Unity imported into a GameObject, prefabs aside.</summary>
        public static bool Is(string path)
        {
            if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path))
            {
                return false;
            }

            if (path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            AssetImporter importer = AssetImporter.GetAtPath(path);
            if (importer == null)
            {
                return false;
            }

            if (importer is ModelImporter)
            {
                return true;
            }

            return typeof(GameObject).IsAssignableFrom(AssetDatabase.GetMainAssetTypeAtPath(path));
        }

        public static bool Is(Object asset)
        {
            return asset != null && Is(AssetDatabase.GetAssetPath(asset));
        }

        /// <summary>
        /// The models at a path: the file itself, or every model in the folder.
        ///
        /// Searched as `t:GameObject` rather than `t:Model`, because that is the only
        /// filter both an FBX and a glb answer to, and then narrowed by <see cref="Is"/>
        /// to drop the prefabs that come along with it.
        /// </summary>
        public static List<string> Under(string path)
        {
            var models = new List<string>();

            if (string.IsNullOrEmpty(path))
            {
                return models;
            }

            if (!AssetDatabase.IsValidFolder(path))
            {
                if (Is(path))
                {
                    models.Add(path);
                }

                return models;
            }

            foreach (string guid in AssetDatabase.FindAssets("t:GameObject", new[] { path }))
            {
                string found = AssetDatabase.GUIDToAssetPath(guid);

                if (Is(found))
                {
                    models.Add(found);
                }
            }

            models.Sort(string.CompareOrdinal);
            return models;
        }

        /// <summary>
        /// The models sitting directly in a folder, without descending into it.
        ///
        /// The difference from <see cref="Under"/> matters when walking up a tree: a
        /// recursive search from a character's own folder reaches its neighbours' too,
        /// and answers with whichever it happened to find first.
        /// </summary>
        public static List<string> InFolder(string folder)
        {
            var models = new List<string>();

            if (string.IsNullOrEmpty(folder) || !AssetDatabase.IsValidFolder(folder))
            {
                return models;
            }

            foreach (string guid in AssetDatabase.FindAssets("t:GameObject", new[] { folder }))
            {
                string found = AssetDatabase.GUIDToAssetPath(guid);

                if (Is(found) &&
                    System.IO.Path.GetDirectoryName(found).Replace('\\', '/') == folder)
                {
                    models.Add(found);
                }
            }

            models.Sort(string.CompareOrdinal);
            return models;
        }

        /// <summary>
        /// Whether this model brings materials in at all. A model importing none has no
        /// material slots to remap, and offering to match its materials by name is
        /// offering nothing.
        ///
        /// The built-in importer says so through materialImportMode. A scripted one is
        /// asked through its serialised fields, by the name UnityGLTF happens to use;
        /// an importer that does not have it is taken at its word that it imports them,
        /// which is the harmless answer — the match then simply finds no slots.
        /// </summary>
        public static bool ImportsMaterials(string path)
        {
            AssetImporter importer = AssetImporter.GetAtPath(path);

            if (importer == null)
            {
                return false;
            }

            if (importer is ModelImporter model)
            {
                return model.materialImportMode != ModelImporterMaterialImportMode.None;
            }

            using (var serialized = new SerializedObject(importer))
            {
                SerializedProperty property = serialized.FindProperty("_importMaterials");
                return property == null || property.boolValue;
            }
        }

        /// <summary>
        /// The built-in importer, or null for a model that came in through a scripted
        /// one. Callers that need it are the ones reaching for settings only it has --
        /// avatar setup, clip definitions, rig type -- and those have no counterpart on
        /// a scripted importer, so they have to say so rather than fail quietly.
        /// </summary>
        public static ModelImporter Importer(string path)
        {
            return AssetImporter.GetAtPath(path) as ModelImporter;
        }

        /// <summary>
        /// A sentence naming the models a built-in-importer-only operation cannot touch,
        /// or empty when they all can. For putting in front of the user instead of
        /// silently doing nothing to half the selection.
        /// </summary>
        public static string Unsupported(IEnumerable<string> paths)
        {
            var scripted = new List<string>();

            foreach (string path in paths)
            {
                if (Is(path) && Importer(path) == null)
                {
                    scripted.Add(System.IO.Path.GetFileName(path));
                }
            }

            if (scripted.Count == 0)
            {
                return string.Empty;
            }

            string names = scripted.Count <= 3
                ? string.Join(", ", scripted)
                : $"{scripted[0]}, {scripted[1]} and {scripted.Count - 2} more";

            return $"{names} {(scripted.Count == 1 ? "is" : "are")} not imported by Unity's " +
                   "own model importer, which is where this setting lives.";
        }
    }
}
