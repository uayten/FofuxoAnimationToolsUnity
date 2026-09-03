using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;

namespace FofuxoAnimationTools.Editor
{
    /// <summary>Records external drop paths before Unity replaces them with asset paths.</summary>
    [InitializeOnLoad]
    public static class AssetSourceCapture
    {
        // Import workers run in separate processes. Library makes the drop history
        // available before import, including when the dropped files trigger a reload.
        private static string PendingPath => Path.GetFullPath("Library/FofuxoSourcePending.json");
        private static readonly StringComparison PathComparison =
            Application.platform == RuntimePlatform.WindowsEditor
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        [Serializable]
        private sealed class PendingFile
        {
            public string source;
            public string destination;
            public long expires;
        }

        [Serializable]
        private sealed class PendingBatch
        {
            public List<PendingFile> files = new List<PendingFile>();
        }

        static AssetSourceCapture()
        {
            if (AssetDatabase.IsAssetImportWorkerProcess()) return;
#if UNITY_6000_3_OR_NEWER
            DragAndDrop.AddDropHandlerV2(OnProjectDrop);
#else
            DragAndDrop.AddDropHandler(OnProjectDrop);
#endif
        }

#if UNITY_6000_3_OR_NEWER
        private static DragAndDropVisualMode OnProjectDrop(EntityId id, string destination, bool perform)
#else
        private static DragAndDropVisualMode OnProjectDrop(int id, string destination, bool perform)
#endif
        {
            if (perform) Capture(DragAndDrop.paths, destination);
            // Unity still owns copying, overwrite prompts and importing the drop.
            return DragAndDropVisualMode.None;
        }

        public static bool IsInsideProject(string path)
        {
            string root = Path.GetDirectoryName(Application.dataPath);
            string fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(fullPath, root, PathComparison) ||
                   fullPath.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);
        }

        public static void Capture(string[] sources, string dropUponPath)
        {
            if (sources == null || string.IsNullOrEmpty(dropUponPath)) return;
            string folder = AssetDatabase.IsValidFolder(dropUponPath)
                ? dropUponPath : Path.GetDirectoryName(dropUponPath)?.Replace('\\', '/');
            if (folder != "Assets" && (folder == null || !folder.StartsWith("Assets/", StringComparison.Ordinal))) return;

            PendingBatch batch = ReadPending();
            foreach (string path in sources)
            {
                if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path) || IsInsideProject(path)) continue;
                try
                {
                    string source = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    Collect(source, folder + "/" + Path.GetFileName(source), batch);
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                    Debug.LogWarning($"Could not remember the import source '{path}': {error.Message}");
                }
            }
            SavePending(batch);
        }

        private static void Collect(string source, string destination, PendingBatch batch, int depth = 0)
        {
            if (Directory.Exists(source))
            {
                // OneDrive directories are reparse points too. A depth limit avoids
                // endless junction loops while allowing cloud folders to be tracked.
                if (depth >= 32) return;
                foreach (string child in Directory.EnumerateFileSystemEntries(source))
                    Collect(child, destination + "/" + Path.GetFileName(child), batch, depth + 1);
            }
            else if (File.Exists(source) && !source.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            {
                batch.files.RemoveAll(file => string.Equals(file.destination, destination, PathComparison));
                batch.files.Add(new PendingFile
                {
                    source = source, destination = destination,
                    expires = DateTime.UtcNow.AddMinutes(10).Ticks
                });
            }
        }

        internal static string SourceForImport(string assetPath)
        {
            if (!assetPath.StartsWith("Assets/", StringComparison.Ordinal)) return string.Empty;
            PendingBatch batch = ReadPending();
            PendingFile match = batch.files.Find(file =>
                string.Equals(file.destination, assetPath, PathComparison));
            if (match == null || !File.Exists(assetPath)) return string.Empty;
            try
            {
                // A cancelled overwrite must not relink an older project copy.
                return SameContent(match.source, assetPath) ? match.source : string.Empty;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                Debug.LogWarning($"Could not remember the source of '{assetPath}': {error.Message}");
                return string.Empty;
            }
        }

        internal static void CompleteImports(string[] importedPaths)
        {
            if (AssetDatabase.IsAssetImportWorkerProcess()) return;
            PendingBatch batch = ReadPending();
            if (batch.files.Count == 0) return;
            foreach (string assetPath in importedPaths)
                batch.files.RemoveAll(file => string.Equals(file.destination, assetPath, PathComparison) &&
                    string.Equals(ModelSourceLink.SourceOf(assetPath), file.source, PathComparison));
            SavePending(batch);
        }

        private static bool SameContent(string source, string destination)
        {
            if (!File.Exists(source) || new FileInfo(source).Length != new FileInfo(destination).Length) return false;
            using (var hash = SHA256.Create())
            using (var sourceStream = File.OpenRead(source))
            using (var destinationStream = File.OpenRead(destination))
                return Convert.ToBase64String(hash.ComputeHash(sourceStream)) ==
                       Convert.ToBase64String(hash.ComputeHash(destinationStream));
        }

        private static PendingBatch ReadPending()
        {
            string json = string.Empty;
            if (File.Exists(PendingPath))
            {
                // Let the main Editor atomically replace the file while workers read
                // the previous complete snapshot on Windows.
                using (var stream = new FileStream(PendingPath, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream)) json = reader.ReadToEnd();
            }
            PendingBatch batch = string.IsNullOrEmpty(json) ? new PendingBatch() : JsonUtility.FromJson<PendingBatch>(json);
            batch.files.RemoveAll(file => file.expires < DateTime.UtcNow.Ticks);
            return batch;
        }

        private static void SavePending(PendingBatch batch)
        {
            string temporary = PendingPath + ".tmp";
            File.WriteAllText(temporary, JsonUtility.ToJson(batch));
            if (File.Exists(PendingPath)) File.Replace(temporary, PendingPath, null);
            else File.Move(temporary, PendingPath);
        }
    }

    internal sealed class AssetSourcePostprocessor : AssetPostprocessor
    {
        private void OnPreprocessAsset()
        {
            string source = AssetSourceCapture.SourceForImport(assetPath);
            if (!string.IsNullOrEmpty(source)) ModelSourceLink.RememberDuringImport(assetImporter, source);
        }

        private static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets,
            string[] movedAssets, string[] movedFromAssetPaths)
        {
            AssetSourceCapture.CompleteImports(importedAssets);
        }
    }
}
