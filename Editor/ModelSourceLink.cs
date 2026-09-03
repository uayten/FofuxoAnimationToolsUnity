using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace FofuxoAnimationTools.Editor
{
    /// <summary>
    /// Remembers the file outside the project a model was brought in from, and pulls it
    /// in again on demand.
    ///
    /// Unreal keeps the source path of everything it imports, and Reimport goes back to
    /// that path, reads the file again and rebuilds what was made from it. Unity has no
    /// equivalent, because in Unity the imported file *is* the asset: its Reimport
    /// re-reads the copy already in Assets, which is the one thing that has not changed.
    /// The export sitting in the output folder, the one that actually is newer, it knows
    /// nothing about.
    ///
    /// So the path is kept here, on the importer's userData, which travels with the .meta
    /// file and therefore with the repository. Updating is then a file copy over the same
    /// asset path: the GUID does not move, so every prefab, clip and material remapping
    /// built on it stays pointed at it. Same outcome as delete-and-replace, without the
    /// deleting or the replacing.
    /// </summary>
    public static class ModelSourceLink
    {
        [Serializable]
        private sealed class Link
        {
            public string fofuxoSource;
        }

        /// <summary>The external file this asset was last updated from, or empty.</summary>
        public static string SourceOf(string assetPath)
        {
            AssetImporter importer = AssetImporter.GetAtPath(assetPath);

            if (importer == null || string.IsNullOrEmpty(importer.userData))
            {
                return string.Empty;
            }

            try
            {
                Link link = JsonUtility.FromJson<Link>(importer.userData);
                return link?.fofuxoSource ?? string.Empty;
            }
            catch (ArgumentException)
            {
                // userData belongs to whoever writes it and another tool may have put
                // something else there. Not ours, not our business.
                return string.Empty;
            }
        }

        public static void Remember(string assetPath, string sourceFile)
        {
            AssetImporter importer = AssetImporter.GetAtPath(assetPath);

            if (importer == null)
            {
                return;
            }

            importer.userData = JsonUtility.ToJson(new Link { fofuxoSource = sourceFile });
            importer.SaveAndReimport();
        }

        /// <summary>
        /// Copies the remembered file over the asset and reimports it.
        ///
        /// Returns what happened, for a caller that has to report on a batch. Writing
        /// over the asset path rather than importing beside it is the whole point: the
        /// GUID is what everything in the project is holding on to.
        /// </summary>
        public static string Update(string assetPath)
        {
            string source = SourceOf(assetPath);

            if (string.IsNullOrEmpty(source))
            {
                return "no source file remembered";
            }

            if (!File.Exists(source))
            {
                return $"the source file is gone: {source}";
            }

            if (!string.Equals(Path.GetExtension(source), Path.GetExtension(assetPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                return "the source file is a different format now";
            }

            try
            {
                File.Copy(source, assetPath, true);
            }
            catch (IOException error)
            {
                return "could not copy: " + error.Message;
            }

            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            return string.Empty;
        }

        /// <summary>
        /// The models in the selection, with the file each was brought in from. Models
        /// with nothing remembered come back with an empty string, so a caller can ask
        /// for one rather than skipping the asset in silence.
        /// </summary>
        public static List<KeyValuePair<string, string>> Selected()
        {
            var found = new List<KeyValuePair<string, string>>();

            foreach (UnityEngine.Object selected in
                     Selection.GetFiltered(typeof(UnityEngine.Object), SelectionMode.Assets))
            {
                string path = AssetDatabase.GetAssetPath(selected);

                foreach (string model in ModelAsset.Under(path))
                {
                    found.Add(new KeyValuePair<string, string>(model, SourceOf(model)));
                }
            }

            return found;
        }
    }
}
