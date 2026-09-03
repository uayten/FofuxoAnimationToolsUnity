using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace FofuxoAnimationTools.Editor
{
    /// <summary>
    /// The report for <see cref="BrokenReferenceScanner"/>.
    ///
    /// Grouped by the missing asset rather than by the file holding the hole,
    /// because one delete is usually the reason for a dozen of them and the useful
    /// unit of work is "put this one thing back", not "fix these twelve files".
    /// </summary>
    public sealed class BrokenReferenceWindow : EditorWindow
    {
        private readonly List<BrokenReferenceScanner.Hole> holes =
            new List<BrokenReferenceScanner.Hole>();

        private readonly HashSet<string> expanded = new HashSet<string>();

        private Vector2 scroll;
        private bool scanned;

        [MenuItem("Window/Fofuxo's Animation Tools/Find Broken References")]
        public static void Open()
        {
            BrokenReferenceWindow window = GetWindow<BrokenReferenceWindow>();
            window.titleContent = new GUIContent("Broken References");
            window.minSize = new Vector2(620f, 400f);
            window.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(6);

            if (!BrokenReferenceScanner.ProjectIsText)
            {
                EditorGUILayout.HelpBox(
                    "This project is set to force binary serialisation, and the scan " +
                    "reads the text of the files. Nothing will be found.",
                    MessageType.Error);
            }

            EditorGUILayout.LabelField(
                "Every reference in the project that points at an asset which is no longer " +
                "there. Unity does not report these: they show up as an empty field in an " +
                "inspector nobody has opened.",
                EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.Space(4);

            if (GUILayout.Button("Scan the project", GUILayout.Height(24f)))
            {
                holes.Clear();
                holes.AddRange(BrokenReferenceScanner.Scan());
                expanded.Clear();
                scanned = true;
            }

            EditorGUILayout.Space(6);

            if (scanned)
            {
                DrawSummary();
            }

            scroll = EditorGUILayout.BeginScrollView(scroll);

            foreach (BrokenReferenceScanner.Hole hole in holes)
            {
                DrawHole(hole);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawSummary()
        {
            if (holes.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "Nothing broken. Every reference in the project points at something " +
                    "that exists.",
                    MessageType.Info);
                return;
            }

            int total = 0;
            foreach (BrokenReferenceScanner.Hole hole in holes)
            {
                total += hole.Total;
            }

            EditorGUILayout.HelpBox(
                $"{holes.Count} missing asset(s), leaving {total} broken reference(s).\n\n" +
                "The GUID is all that is left of what was deleted. If the file is still " +
                "in source control, restoring it puts every one of these back at once — " +
                "the GUID lives in the .meta and comes back with it.",
                MessageType.Warning);
        }

        private void DrawHole(BrokenReferenceScanner.Hole hole)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();

            bool open = expanded.Contains(hole.Guid);
            bool wanted = EditorGUILayout.Foldout(
                open, $"{hole.Guid}   —   {hole.Total} reference(s)", true);

            if (wanted != open)
            {
                if (wanted)
                {
                    expanded.Add(hole.Guid);
                }
                else
                {
                    expanded.Remove(hole.Guid);
                }
            }

            if (GUILayout.Button("copy guid", EditorStyles.miniButton, GUILayout.Width(80f)))
            {
                EditorGUIUtility.systemCopyBuffer = hole.Guid;
            }

            EditorGUILayout.EndHorizontal();

            if (wanted)
            {
                EditorGUI.indentLevel++;

                foreach (KeyValuePair<string, int> holder in hole.Holders)
                {
                    DrawHolder(holder.Key, holder.Value);
                }

                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndVertical();
        }

        private static void DrawHolder(string path, int count)
        {
            // A .meta names its own asset, which is the thing worth pinging.
            string asset = path.EndsWith(".meta") ? path.Substring(0, path.Length - 5) : path;
            Object target = AssetDatabase.LoadMainAssetAtPath(asset);

            var content = new GUIContent(
                count == 1 ? path : $"{path}   ({count}×)",
                AssetPreview.GetMiniThumbnail(target));

            Rect rect = EditorGUILayout.GetControlRect();

            if (GUI.Button(EditorGUI.IndentedRect(rect), content, EditorStyles.label))
            {
                EditorGUIUtility.PingObject(target);
            }
        }
    }
}
