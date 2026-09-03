using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.ShortcutManagement;
using UnityEngine;

namespace FofuxoAnimationTools.Editor
{
    /// <summary>
    /// Keeps Play mode edits alive after the Play mode ends.
    ///
    /// Unity snapshots the scene on entering Play mode and restores that snapshot on
    /// exit, so every value tuned while the game runs is thrown away — which is where
    /// tuning actually happens. This captures the selected objects on the way out and
    /// writes them back once the Editor is idle again, as a single undo step.
    ///
    /// Off by default, and the switch lives in EditorPrefs: it is a per-machine taste,
    /// not a project setting, and it never reaches the repository.
    /// </summary>
    [InitializeOnLoad]
    public static class PlayModePersist
    {
        private const string EnabledPrefKey = "Fofuxo.PlayModePersist.Enabled";
        private const string TransformPrefKey = "Fofuxo.PlayModePersist.IncludeTransform";

        private static readonly List<Capture> Captured = new List<Capture>();

        static PlayModePersist()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        /// <summary>Whether Play mode edits on the selection survive the exit.</summary>
        public static bool Enabled
        {
            get => EditorPrefs.GetBool(EnabledPrefKey, false);
            set => EditorPrefs.SetBool(EnabledPrefKey, value);
        }

        /// <summary>
        /// Whether Transform comes along. Usually it should not: the character has
        /// walked halfway across the arena by the time you stop, and restoring that
        /// teleports the scene object to wherever the run happened to end.
        /// </summary>
        public static bool IncludeTransform
        {
            get => EditorPrefs.GetBool(TransformPrefKey, false);
            set => EditorPrefs.SetBool(TransformPrefKey, value);
        }

        [SettingsProvider]
        private static SettingsProvider CreateSettingsProvider()
        {
            return new SettingsProvider("Preferences/Fofuxo's Animation Tools", SettingsScope.User)
            {
                label = "Fofuxo's Animation Tools",
                keywords = new[] { "play", "mode", "persist", "playmode", "fofuxo", "animation" },
                guiHandler = _ =>
                {
                    EditorGUIUtility.labelWidth = 260f;

                    EditorGUILayout.Space(6);
                    EditorGUILayout.LabelField("Play Mode Persist", EditorStyles.boldLabel);

                    Enabled = EditorGUILayout.ToggleLeft(
                        new GUIContent(
                            "Keep Play mode edits on the selection",
                            "When Play mode ends, write the selected objects' component " +
                            "values back into the scene instead of letting Unity revert them."),
                        Enabled);

                    using (new EditorGUI.DisabledScope(!Enabled))
                    {
                        EditorGUI.indentLevel++;
                        IncludeTransform = EditorGUILayout.ToggleLeft(
                            new GUIContent(
                                "Include Transform",
                                "Off by default — restoring Transform moves the scene object " +
                                "to wherever it ended up during the run."),
                            IncludeTransform);
                        EditorGUI.indentLevel--;
                    }

                    EditorGUILayout.Space(4);
                    EditorGUILayout.HelpBox(
                        "Only what is selected in the Hierarchy when you stop the game is kept, " +
                        "and it arrives as one undo step. References to other scene objects may " +
                        "not survive: the scene is rebuilt on exit and their instance IDs change.",
                        MessageType.Info);

                    EditorGUILayout.Space(2);
                    EditorGUILayout.LabelField(
                        "Stored per machine in EditorPrefs, not in the project.",
                        EditorStyles.miniLabel);
                }
            };
        }

        [Shortcut("Fofuxo/Toggle Play Mode Persist", KeyCode.P, ShortcutModifiers.Alt | ShortcutModifiers.Shift)]
        private static void ToggleShortcut()
        {
            Enabled = !Enabled;
            Debug.Log($"Play Mode Persist {(Enabled ? "on" : "off")}.");
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (!Enabled)
            {
                Captured.Clear();
                return;
            }

            if (state == PlayModeStateChange.ExitingPlayMode)
            {
                CaptureSelection();
            }
            else if (state == PlayModeStateChange.EnteredEditMode)
            {
                RestoreCaptured();
            }
        }

        /// <summary>
        /// Serializes the selection while the running objects still exist. Identity is
        /// stored as a GlobalObjectId because instance IDs do not survive the exit.
        /// </summary>
        private static void CaptureSelection()
        {
            Captured.Clear();

            foreach (GameObject selected in Selection.gameObjects)
            {
                if (selected == null || !selected.scene.IsValid())
                {
                    continue;
                }

                foreach (Component component in selected.GetComponents<Component>())
                {
                    if (component == null || !ShouldCapture(component))
                    {
                        continue;
                    }

                    Captured.Add(new Capture
                    {
                        Id = GlobalObjectId.GetGlobalObjectIdSlow(component),
                        Json = EditorJsonUtility.ToJson(component)
                    });
                }
            }
        }

        private static void RestoreCaptured()
        {
            if (Captured.Count == 0)
            {
                return;
            }

            int undoGroup = Undo.GetCurrentGroup();
            int restored = 0;

            foreach (Capture capture in Captured)
            {
                Object target = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(capture.Id);
                if (target == null)
                {
                    continue;
                }

                Undo.RecordObject(target, "Play Mode Persist");
                EditorJsonUtility.FromJsonOverwrite(capture.Json, target);
                EditorUtility.SetDirty(target);

                if (target is Component component && component.gameObject.scene.IsValid())
                {
                    EditorSceneManager.MarkSceneDirty(component.gameObject.scene);
                }

                restored++;
            }

            Captured.Clear();

            Undo.CollapseUndoOperations(undoGroup);
            Undo.SetCurrentGroupName("Play Mode Persist");

            if (restored > 0)
            {
                Debug.Log($"Play Mode Persist kept {restored} component(s). Ctrl+Z drops them.");
            }
        }

        /// <summary>
        /// Components whose Play mode state is meaningless in Edit mode are skipped.
        /// Restoring a Rigidbody's velocity or a stopped ParticleSystem's internal
        /// state only produces a dirty scene with nothing useful in it.
        /// </summary>
        private static bool ShouldCapture(Component component)
        {
            if (component is Transform)
            {
                return IncludeTransform;
            }

            return !(component is Rigidbody)
                   && !(component is Rigidbody2D)
                   && !(component is ParticleSystem);
        }

        private struct Capture
        {
            public GlobalObjectId Id;
            public string Json;
        }
    }
}
