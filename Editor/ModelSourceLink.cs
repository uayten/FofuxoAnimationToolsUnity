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
        private const string MetadataMarker = "\n[FofuxoSource]\n";

        [Serializable]
        private sealed class Link
        {
            public string fofuxoSource;
        }

        /// <summary>The external file this asset was last updated from, or empty.</summary>
        public static string SourceOf(string assetPath)
        {
            return SourceOf(AssetImporter.GetAtPath(assetPath));
        }

        public static string SourceOf(AssetImporter importer)
        {
            if (importer == null || string.IsNullOrEmpty(importer.userData))
            {
                return string.Empty;
            }

            try
            {
                string data = importer.userData;
                int marker = data.LastIndexOf(MetadataMarker, StringComparison.Ordinal);
                if (marker >= 0) data = data.Substring(marker + MetadataMarker.Length);
                Link link = JsonUtility.FromJson<Link>(data);
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

            sourceFile = Path.GetFullPath(sourceFile);
            if (SourceOf(importer) == sourceFile) return;

            importer.userData = WithSource(importer.userData ?? string.Empty, sourceFile);
            importer.SaveAndReimport();
        }

        internal static void RememberDuringImport(AssetImporter importer, string sourceFile)
        {
            importer.userData = WithSource(importer.userData ?? string.Empty, Path.GetFullPath(sourceFile));
        }

        private static string WithSource(string data, string source)
        {
            int marker = data.LastIndexOf(MetadataMarker, StringComparison.Ordinal);
            if (marker >= 0) data = data.Substring(0, marker);
            string json = JsonUtility.ToJson(new Link { fofuxoSource = source });
            if (string.IsNullOrWhiteSpace(data)) return json;

            // Preserve other JSON fields, including nested values, without depending
            // on a third-party JSON package. Only edit our top-level string property.
            string trimmed = data.Trim();
            if (trimmed.StartsWith("{", StringComparison.Ordinal) && trimmed.EndsWith("}", StringComparison.Ordinal))
            {
                int depth = 0;
                for (int i = 0; i < data.Length; i++)
                {
                    char character = data[i];
                    if (character == '{' || character == '[') depth++;
                    else if (character == '}' || character == ']') depth--;
                    else if (character == '"')
                    {
                        int end = StringEnd(data, i);
                        if (depth == 1 && data.Substring(i, end - i + 1) == "\"fofuxoSource\"")
                        {
                            int value = end + 1;
                            while (value < data.Length && char.IsWhiteSpace(data[value])) value++;
                            if (value < data.Length && data[value] == ':')
                            {
                                value++;
                                while (value < data.Length && char.IsWhiteSpace(data[value])) value++;
                                if (value < data.Length && data[value] == '"')
                                {
                                    string encoded = json.Substring(json.IndexOf(':') + 1).TrimEnd('}');
                                    return data.Substring(0, value) + encoded + data.Substring(StringEnd(data, value) + 1);
                                }
                            }
                        }
                        i = end;
                    }
                }
                int closing = data.LastIndexOf('}');
                string separator = trimmed.Substring(1, trimmed.Length - 2).Trim().Length == 0 ? "" : ",";
                return data.Substring(0, closing) + separator + json.Substring(1, json.Length - 2) + data.Substring(closing);
            }

            // Non-JSON userData remains available verbatim before our own suffix.
            return data + MetadataMarker + json;
        }

        private static int StringEnd(string data, int start)
        {
            for (int i = start + 1; i < data.Length; i++)
            {
                if (data[i] == '\\') i++;
                else if (data[i] == '"') return i;
            }
            return data.Length - 1;
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
                string destination = Path.GetFullPath(assetPath);
                if (string.Equals(Path.GetFullPath(source), destination,
                        Application.platform == RuntimePlatform.WindowsEditor
                            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                {
                    return "the source points to the project copy; choose the original external file";
                }
                File.Copy(source, destination, true);
                AssetDatabase.ImportAsset(assetPath,
                    ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                return "could not update: " + error.Message;
            }

            return string.Empty;
        }

        /// <summary>
        /// Selected models and other tracked assets, including folder contents.
        /// </summary>
        public static List<KeyValuePair<string, string>> Selected()
        {
            var found = new List<KeyValuePair<string, string>>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (UnityEngine.Object selected in
                     Selection.GetFiltered(typeof(UnityEngine.Object), SelectionMode.Assets))
            {
                string path = AssetDatabase.GetAssetPath(selected);

                var paths = new List<string>();
                if (AssetDatabase.IsValidFolder(path))
                {
                    foreach (string guid in AssetDatabase.FindAssets(string.Empty, new[] { path }))
                        paths.Add(AssetDatabase.GUIDToAssetPath(guid));
                }
                else paths.Add(path);

                foreach (string candidate in paths)
                {
                    if (!candidate.StartsWith("Assets/", StringComparison.Ordinal) || !seen.Add(candidate)) continue;
                    string source = SourceOf(candidate);
                    if (!string.IsNullOrEmpty(source) || ModelAsset.Is(candidate))
                        found.Add(new KeyValuePair<string, string>(candidate, source));
                }
            }

            return found;
        }
    }
}
