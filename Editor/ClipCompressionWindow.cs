using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace FofuxoAnimationTools.Editor
{
    /// <summary>
    /// The front end for <see cref="ClipCompressionUtility"/>.
    ///
    /// Compression is destructive and has no undo, so the window is built around
    /// measuring first: Analyze runs the whole thing and writes nothing, and only once
    /// the numbers look right does Compress touch the assets. The tolerances are the
    /// interesting part -- the useful ratio is found by trying a few, not by guessing
    /// one -- so they are right next to the button and the result stays on screen.
    /// </summary>
    public sealed class ClipCompressionWindow : EditorWindow
    {
        private struct Row
        {
            public string Name;
            public int KeysBefore;
            public int KeysAfter;
        }

        private readonly List<Row> rows = new List<Row>();

        private ClipCompressionUtility.Report total;
        private bool hasResult;
        private bool resultIsWrite;
        private long bytesBefore;
        private long bytesAfter;
        private Vector2 scroll;

        [MenuItem("Window/Fofuxo's Animation Tools/Compress Animation Clips")]
        public static void Open()
        {
            ClipCompressionWindow window = GetWindow<ClipCompressionWindow>();
            window.titleContent = new GUIContent("Compress Clips");
            window.minSize = new Vector2(420f, 380f);
            window.Show();
        }

        private ClipCompressionUtility.Tolerances Tolerances
        {
            get => ClipCompressionUtility.Tolerances.Stored;
            set => ClipCompressionUtility.Tolerances.Stored = value;
        }

        private void OnSelectionChange()
        {
            Repaint();
        }

        private void OnGUI()
        {
            List<AnimationClip> clips = AnimationClipBatchMenu.SelectedClips();

            EditorGUILayout.Space(6);
            DrawTolerances();

            EditorGUILayout.Space(8);
            DrawActions(clips);

            EditorGUILayout.Space(8);
            DrawResult();
        }

        private void DrawTolerances()
        {
            EditorGUIUtility.labelWidth = 190f;
            EditorGUILayout.LabelField("Allowed error", EditorStyles.boldLabel);

            ClipCompressionUtility.Tolerances tolerances = Tolerances;

            EditorGUI.BeginChangeCheck();

            tolerances.RotationDegrees = EditorGUILayout.FloatField(
                new GUIContent(
                    "Rotation (degrees)",
                    "How far a bone may end up turned from where the original clip had it. " +
                    "The model importer's Rotation Error is the same unit, and its default " +
                    "is 0.5."),
                tolerances.RotationDegrees);

            tolerances.PositionUnits = EditorGUILayout.FloatField(
                new GUIContent(
                    "Position (units)",
                    "How far a bone may end up from where it was, in the clip's own units. " +
                    "On a metre-scaled rig 0.0005 is half a millimetre."),
                tolerances.PositionUnits);

            tolerances.ScaleFraction = EditorGUILayout.FloatField(
                new GUIContent(
                    "Scale (fraction)",
                    "Allowed difference on any scale axis. 0.005 is half a percent."),
                tolerances.ScaleFraction);

            tolerances.GenericValue = EditorGUILayout.FloatField(
                new GUIContent(
                    "Other curves",
                    "Everything that is not a transform: blend shape weights, custom " +
                    "properties. Plain difference in whatever unit the property uses."),
                tolerances.GenericValue);

            tolerances.CollapseConstant = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    "Collapse curves that hold still",
                    "A curve that never leaves its first value keeps two keys instead of " +
                    "one per frame. On a skeleton this is most of the saving: scale and " +
                    "position sit still on nearly every bone."),
                tolerances.CollapseConstant);

            tolerances.StripEditor = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    "Drop the editor copy of the curves",
                    "A clip stores its animation twice: once for the engine and once for " +
                    "the Animation window, and the second copy is four fifths of the file " +
                    "while reaching no build. It is derived, so the Editor rebuilds it " +
                    "whenever anything asks — nothing is lost and nothing stops working."),
                tolerances.StripEditor);

            if (EditorGUI.EndChangeCheck())
            {
                Tolerances = tolerances;
                hasResult = false;
            }

            if (GUILayout.Button("Reset to defaults", GUILayout.Width(140f)))
            {
                Tolerances = ClipCompressionUtility.Tolerances.Default;
                hasResult = false;
            }
        }

        private void DrawActions(List<AnimationClip> clips)
        {
            EditorGUILayout.LabelField(
                clips.Count == 0
                    ? "Nothing selected."
                    : $"{clips.Count} clip(s) selected, {EditorUtility.FormatBytes(Bytes(clips))} on disk.",
                EditorStyles.miniLabel);

            using (new EditorGUI.DisabledScope(clips.Count == 0))
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Analyze", GUILayout.Height(24f)))
                {
                    Run(clips, false);
                }

                Color previous = GUI.backgroundColor;
                GUI.backgroundColor = new Color(1f, 0.75f, 0.6f);

                if (GUILayout.Button("Compress", GUILayout.Height(24f)) && Confirm(clips))
                {
                    Run(clips, true);
                }

                GUI.backgroundColor = previous;
            }

            EditorGUILayout.LabelField(
                "Analyze writes nothing. Compress cannot be undone.",
                EditorStyles.miniLabel);
        }

        private static bool Confirm(List<AnimationClip> clips)
        {
            return EditorUtility.DisplayDialog(
                "Compress animation clips",
                $"{clips.Count} clip(s) will be rewritten with fewer keys.\n\n" +
                "The keys that are dropped are gone -- there is no undo, and the assets " +
                "change on disk. Make sure they are committed, or that Analyze already " +
                "told you what you wanted to hear.",
                "Compress",
                "Cancel");
        }

        private void DrawResult()
        {
            if (!hasResult)
            {
                EditorGUILayout.HelpBox(
                    "Select .anim assets in the Project window, then Analyze to see how far " +
                    "they would compress at these tolerances.",
                    MessageType.None);
                return;
            }

            EditorGUILayout.LabelField(
                resultIsWrite ? "Compressed" : "Would compress",
                EditorStyles.boldLabel);

            EditorGUILayout.LabelField("Clips", $"{total.Clips:N0} ({total.ChangedClips:N0} changed)");
            EditorGUILayout.LabelField("Curves", $"{total.Curves:N0} ({total.ConstantCurves:N0} hold still)");
            EditorGUILayout.LabelField("Keys", total.ToString());

            if (resultIsWrite)
            {
                EditorGUILayout.LabelField(
                    "On disk",
                    $"{EditorUtility.FormatBytes(bytesBefore)} -> {EditorUtility.FormatBytes(bytesAfter)}");
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Biggest savings", EditorStyles.miniBoldLabel);

            using (var view = new EditorGUILayout.ScrollViewScope(scroll))
            {
                scroll = view.scrollPosition;

                int shown = Mathf.Min(rows.Count, 40);
                for (int i = 0; i < shown; i++)
                {
                    Row row = rows[i];
                    float ratio = row.KeysAfter == 0 ? 1f : (float)row.KeysBefore / row.KeysAfter;

                    EditorGUILayout.LabelField(
                        row.Name,
                        $"{row.KeysBefore:N0} -> {row.KeysAfter:N0}  ({ratio:0.#}:1)");
                }

                if (rows.Count > shown)
                {
                    EditorGUILayout.LabelField($"...and {rows.Count - shown:N0} more.", EditorStyles.miniLabel);
                }
            }
        }

        private void Run(List<AnimationClip> clips, bool write)
        {
            rows.Clear();
            total = default;
            bytesBefore = Bytes(clips);

            ClipCompressionUtility.Tolerances tolerances = Tolerances;

            try
            {
                for (int i = 0; i < clips.Count; i++)
                {
                    bool cancelled = EditorUtility.DisplayCancelableProgressBar(
                        write ? "Compressing clips" : "Analyzing clips",
                        $"{clips[i].name}  ({i + 1}/{clips.Count})",
                        (float)i / clips.Count);

                    if (cancelled)
                    {
                        break;
                    }

                    ClipCompressionUtility.Report report = write
                        ? ClipCompressionUtility.Compress(clips[i], tolerances)
                        : ClipCompressionUtility.Analyze(clips[i], tolerances);

                    total.Add(report);

                    if (report.KeysBefore > report.KeysAfter)
                    {
                        rows.Add(new Row
                        {
                            Name = clips[i].name,
                            KeysBefore = report.KeysBefore,
                            KeysAfter = report.KeysAfter
                        });
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (write)
            {
                AssetDatabase.SaveAssets();
                bytesAfter = Bytes(clips);
            }

            rows.Sort((a, b) => (b.KeysBefore - b.KeysAfter).CompareTo(a.KeysBefore - a.KeysAfter));

            hasResult = true;
            resultIsWrite = write;

            Debug.Log(
                $"{(write ? "Compressed" : "Analyzed")} {total.Clips:N0} clip(s): {total}." +
                (write
                    ? $" On disk {EditorUtility.FormatBytes(bytesBefore)} -> {EditorUtility.FormatBytes(bytesAfter)}."
                    : " Nothing was written."));
        }

        private static long Bytes(List<AnimationClip> clips)
        {
            long bytes = 0;

            foreach (AnimationClip clip in clips)
            {
                string path = AssetDatabase.GetAssetPath(clip);
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                try
                {
                    var info = new FileInfo(path);
                    if (info.Exists)
                    {
                        bytes += info.Length;
                    }
                }
                catch (IOException)
                {
                    // A file the Editor is mid-write on. Not worth failing a report over.
                }
            }

            return bytes;
        }
    }

}
