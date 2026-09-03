using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace FofuxoAnimationTools.Editor
{
    public static class ModelUpdateMenu
    {
        private const string UpdatePath = "Assets/Fofuxo's Animation Tools/Update From Source File";
        private const string LinkPath = "Assets/Fofuxo's Animation Tools/Set Source File...";
        private const string LastFolderKey = "Fofuxo.Source.LastFolder";

        [MenuItem(UpdatePath, false, 33)]
        private static void Update()
        {
            var paths = new List<string>();
            foreach (var model in ModelSourceLink.Selected()) paths.Add(model.Key);
            UpdateAssets(paths);
        }

        public static void UpdateAssets(IReadOnlyList<string> paths)
        {
            int updated = 0;
            var failed = new List<string>();
            foreach (string assetPath in paths)
            {
                string source = ModelSourceLink.SourceOf(assetPath);
                if (string.IsNullOrEmpty(source))
                {
                    // Older assets have no recoverable drag history. Pick and update
                    // in the same action, without a redundant warning dialog.
                    if (!ChooseSource(assetPath)) continue;
                }
                else if (!File.Exists(source))
                {
                    if (!EditorUtility.DisplayDialog("Source file not found",
                            $"The source file or its folder could not be found:\n\n{source}",
                            "Locate File", "Cancel") || !ChooseSource(assetPath)) continue;
                }

                string trouble = ModelSourceLink.Update(assetPath);
                if (trouble.Length == 0) updated++;
                else failed.Add($"{Path.GetFileName(assetPath)}: {trouble}");
            }

            if (failed.Count > 0)
                Debug.LogWarning($"Updated {updated} asset(s) from source. " + string.Join("; ", failed));
            else if (updated > 0)
                Debug.Log($"Updated {updated} asset(s) from their source files.");
        }

        public static bool ChooseSource(string assetPath)
        {
            string source = ModelSourceLink.SourceOf(assetPath);
            string start = string.IsNullOrEmpty(source)
                ? EditorPrefs.GetString(LastFolderKey, string.Empty) : Path.GetDirectoryName(source);
            if (!Directory.Exists(start)) start = string.Empty;

            string chosen = EditorUtility.OpenFilePanel($"Source file for {Path.GetFileName(assetPath)}",
                start, Path.GetExtension(assetPath).TrimStart('.'));
            if (string.IsNullOrEmpty(chosen)) return false;

            if (!string.Equals(Path.GetExtension(chosen), Path.GetExtension(assetPath),
                    StringComparison.OrdinalIgnoreCase) || !File.Exists(chosen) ||
                AssetSourceCapture.IsInsideProject(chosen))
            {
                Debug.LogWarning("Choose an existing external source file with the same extension as the asset.");
                return false;
            }

            ModelSourceLink.Remember(assetPath, chosen);
            EditorPrefs.SetString(LastFolderKey, Path.GetDirectoryName(chosen));
            return true;
        }

        [MenuItem(LinkPath, false, 34)]
        private static void Link()
        {
            var models = ModelSourceLink.Selected();
            if (models.Count == 0 || !ChooseSource(models[0].Key)) return;

            // The explicitly selected source may have a different filename.
            string folder = Path.GetDirectoryName(ModelSourceLink.SourceOf(models[0].Key));
            for (int i = 1; i < models.Count; i++)
            {
                string source = Path.Combine(folder, Path.GetFileName(models[i].Key));
                if (File.Exists(source)) ModelSourceLink.Remember(models[i].Key, source);
            }
        }

        [MenuItem(UpdatePath, true)]
        [MenuItem(LinkPath, true)]
        private static bool HasModelSelected()
        {
            string path = AssetDatabase.GetAssetPath(Selection.activeObject);
            return !string.IsNullOrEmpty(path) && path.StartsWith("Assets/", StringComparison.Ordinal) &&
                   (AssetDatabase.IsValidFolder(path) || ModelAsset.Is(path) ||
                    !string.IsNullOrEmpty(ModelSourceLink.SourceOf(path)));
        }
    }
}