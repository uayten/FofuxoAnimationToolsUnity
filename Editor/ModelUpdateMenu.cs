using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace FofuxoAnimationTools.Editor
{
    /// <summary>
    /// The Project window entry for <see cref="ModelSourceLink"/>: point a model at the
    /// file it comes from once, and from then on one click pulls the newest export in
    /// over it.
    ///
    /// What makes this worth a menu item rather than dragging the file in again: dragging
    /// in a new file makes a new asset with a new GUID, and everything built on the old
    /// one keeps pointing at the old one. Writing over the same path keeps the GUID, so
    /// the prefabs, the extracted clips and the material remappings all follow.
    /// </summary>
    public static class ModelUpdateMenu
    {
        private const string UpdatePath = "Assets/Fofuxo's Animation Tools/Update From Source File";
        private const string LinkPath = "Assets/Fofuxo's Animation Tools/Set Source File...";
        private const string LastFolderKey = "Fofuxo.Source.LastFolder";

        [MenuItem(UpdatePath, false, 33)]
        private static void Update()
        {
            List<KeyValuePair<string, string>> models = ModelSourceLink.Selected();
            var missing = new List<string>();
            var failed = new List<string>();
            int updated = 0;

            try
            {
                for (int i = 0; i < models.Count; i++)
                {
                    EditorUtility.DisplayProgressBar(
                        "Updating from source", models[i].Key, (float)i / models.Count);

                    if (string.IsNullOrEmpty(models[i].Value))
                    {
                        missing.Add(Path.GetFileName(models[i].Key));
                        continue;
                    }

                    string trouble = ModelSourceLink.Update(models[i].Key);

                    if (trouble.Length == 0)
                    {
                        updated++;
                    }
                    else
                    {
                        failed.Add(Path.GetFileName(models[i].Key) + " — " + trouble);
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            // A model that has never been linked is the ordinary first case, not an
            // error: offer to link it instead of reporting nothing happened.
            if (updated == 0 && failed.Count == 0 && missing.Count == models.Count && models.Count > 0)
            {
                if (EditorUtility.DisplayDialog(
                        "Update from source",
                        models.Count == 1
                            ? $"{missing[0]} has no source file remembered yet.\n\nPick the file " +
                              "it comes from and it will be remembered from now on."
                            : $"None of these {models.Count} models has a source file " +
                              "remembered yet.\n\nLink them one at a time with Set Source File.",
                        models.Count == 1 ? "Pick the file" : "Ok",
                        "Cancel") && models.Count == 1)
                {
                    Link();
                }

                return;
            }

            Debug.Log(
                $"Updated {updated} model(s) from their source files." +
                (missing.Count > 0
                    ? $" {missing.Count} had no source remembered: {string.Join(", ", missing)}."
                    : string.Empty) +
                (failed.Count > 0
                    ? $" {failed.Count} failed: {string.Join("; ", failed)}."
                    : string.Empty));
        }

        [MenuItem(LinkPath, false, 34)]
        private static void Link()
        {
            List<KeyValuePair<string, string>> models = ModelSourceLink.Selected();

            if (models.Count == 0)
            {
                return;
            }

            string asset = models[0].Key;
            string extension = Path.GetExtension(asset).TrimStart('.');

            string start = models[0].Value.Length > 0
                ? Path.GetDirectoryName(models[0].Value)
                : EditorPrefs.GetString(LastFolderKey, string.Empty);

            string chosen = EditorUtility.OpenFilePanel(
                $"Source file for {Path.GetFileName(asset)}", start, extension);

            if (string.IsNullOrEmpty(chosen))
            {
                return;
            }

            EditorPrefs.SetString(LastFolderKey, Path.GetDirectoryName(chosen));

            // The whole selection is linked, not just the one that opened the dialog:
            // a folder of exports lives in one output folder, and pointing at that
            // folder once should cover every file that has a match in it.
            int linked = 0;

            foreach (KeyValuePair<string, string> model in models)
            {
                string beside = Path.Combine(
                    Path.GetDirectoryName(chosen) ?? string.Empty,
                    Path.GetFileName(model.Key));

                if (File.Exists(beside))
                {
                    ModelSourceLink.Remember(model.Key, beside);
                    linked++;
                }
            }

            Debug.Log(
                $"Linked {linked} of {models.Count} model(s) to files in " +
                $"{Path.GetDirectoryName(chosen)}." +
                (linked < models.Count
                    ? " The rest have no file of the same name there."
                    : string.Empty));
        }

        [MenuItem(UpdatePath, true)]
        [MenuItem(LinkPath, true)]
        private static bool HasModelSelected()
        {
            string path = AssetDatabase.GetAssetPath(Selection.activeObject);
            return !string.IsNullOrEmpty(path) &&
                   (AssetDatabase.IsValidFolder(path) || ModelAsset.Is(path));
        }
    }
}
