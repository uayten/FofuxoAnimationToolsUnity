using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FofuxoAnimationTools.Editor
{
    /// <summary>
    /// A field for choosing a folder under Assets.
    ///
    /// The obvious way is an ObjectField typed to DefaultAsset, and it is the wrong
    /// way: its picker lists every folder in the project flat, alphabetically, with
    /// no hierarchy and no way to tell one "Armor" from the other three. It also
    /// offers the folders inside every package, which are never the answer.
    ///
    /// This shows the path, takes a folder dropped on it from the Project window,
    /// and opens the system browser for the rest. The path is remembered so the next
    /// extraction starts where the last one ended.
    /// </summary>
    public static class FolderField
    {
        public static string Draw(GUIContent label, string path, string prefKey, string emptyHint)
        {
            Rect row = EditorGUILayout.GetControlRect();

            Rect field = new Rect(row.x, row.y, row.width - 58f, row.height);
            Rect browse = new Rect(row.xMax - 54f, row.y, 26f, row.height);
            Rect clear = new Rect(row.xMax - 26f, row.y, 26f, row.height);

            string shown = string.IsNullOrEmpty(path) ? emptyHint : path;

            EditorGUI.LabelField(field, label, new GUIContent(shown, path));

            string next = Dropped(field, path);

            if (GUI.Button(browse, new GUIContent("…", "Browse for a folder inside Assets.")))
            {
                next = Browse(next);
            }

            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(next)))
            {
                if (GUI.Button(clear, new GUIContent("×", "Clear.")))
                {
                    next = string.Empty;
                }
            }

            if (next != path && !string.IsNullOrEmpty(prefKey))
            {
                EditorPrefs.SetString(prefKey, next);
            }

            return next;
        }

        public static string Remembered(string prefKey)
        {
            string path = EditorPrefs.GetString(prefKey, string.Empty);
            return AssetDatabase.IsValidFolder(path) ? path : string.Empty;
        }

        /// <summary>
        /// Dragging a folder out of the Project window is the fastest way to say
        /// which one, and the one thing the ObjectField did get right.
        /// </summary>
        private static string Dropped(Rect area, string path)
        {
            Event current = Event.current;

            if (current.type != EventType.DragUpdated && current.type != EventType.DragPerform)
            {
                return path;
            }

            if (!area.Contains(current.mousePosition) || DragAndDrop.objectReferences.Length == 0)
            {
                return path;
            }

            string candidate = Folder(DragAndDrop.objectReferences[0]);

            DragAndDrop.visualMode = candidate == null
                ? DragAndDropVisualMode.Rejected
                : DragAndDropVisualMode.Copy;

            if (current.type == EventType.DragPerform && candidate != null)
            {
                DragAndDrop.AcceptDrag();
                current.Use();
                return candidate;
            }

            return path;
        }

        /// <summary>
        /// The folder a dragged object stands for: itself if it is one, otherwise the
        /// one it lives in. Dropping a clip to mean "here, next to this" is the same
        /// intent as dropping the folder.
        /// </summary>
        private static string Folder(Object dragged)
        {
            string path = AssetDatabase.GetAssetPath(dragged);

            if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets"))
            {
                return null;
            }

            return AssetDatabase.IsValidFolder(path)
                ? path
                : Path.GetDirectoryName(path).Replace('\\', '/');
        }

        private static string Browse(string current)
        {
            string start = AssetDatabase.IsValidFolder(current)
                ? Path.GetFullPath(current)
                : Application.dataPath;

            string chosen = EditorUtility.OpenFolderPanel("Choose a folder inside Assets", start, string.Empty);

            if (string.IsNullOrEmpty(chosen))
            {
                return current;
            }

            string relative = ToProjectPath(chosen);

            if (relative == null)
            {
                EditorUtility.DisplayDialog(
                    "Outside the project",
                    "That folder is not under Assets. Unity can only write assets in there.",
                    "OK");

                return current;
            }

            if (!AssetDatabase.IsValidFolder(relative))
            {
                AssetDatabase.Refresh();
            }

            return relative;
        }

        public static string ToProjectPath(string absolute)
        {
            string root = Application.dataPath.Replace('\\', '/');
            string path = absolute.Replace('\\', '/');

            if (path == root)
            {
                return "Assets";
            }

            return path.StartsWith(root + "/") ? "Assets" + path.Substring(root.Length) : null;
        }
    }
}
