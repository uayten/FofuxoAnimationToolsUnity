using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
namespace FofuxoAnimationTools.Editor
{
    /// <summary>
    /// Adds a Root Motion toggle to the AnimationClip Inspector, next to the rest of
    /// the clip settings.
    ///
    /// Unity's own AnimationClip Inspector draws the timeline, the curves, the
    /// events and the interactive preview, and none of that is worth losing over one
    /// checkbox. So this creates the built-in editor and forwards everything to it,
    /// drawing only the extra block on top.
    /// </summary>
    [CustomEditor(typeof(AnimationClip))]
    [CanEditMultipleObjects]
    public sealed class AnimationClipRootMotionInspector : UnityEditor.Editor
    {
        private const string RootBonePrefKey = "Fofuxo.RootMotion.RootBone";

        private UnityEditor.Editor builtInEditor;
        private string rootBone;

        private void OnEnable()
        {
            rootBone = EditorPrefs.GetString(RootBonePrefKey, RootMotionClipUtility.DefaultRootBone);
            CreateBuiltInEditor();
        }

        private void OnDisable()
        {
            if (builtInEditor != null)
            {
                DestroyImmediate(builtInEditor);
                builtInEditor = null;
            }
        }

        public override void OnInspectorGUI()
        {
            DrawScenePreviewBlock();
            DrawRootMotionBlock();

            EditorGUILayout.Space();

            if (builtInEditor != null)
            {
                builtInEditor.OnInspectorGUI();
            }
            else
            {
                base.OnInspectorGUI();
            }
        }

        /// <summary>
        /// Drops a copy of the character into the scene playing this clip on loop --
        /// the closest thing to dragging an Animation Sequence into an Unreal level.
        /// A clip carries no reference to its skeleton, so the model has to be told
        /// once; it is remembered from then on.
        /// </summary>
        private void DrawScenePreviewBlock()
        {
            List<AnimationClip> clips = TargetClips();
            if (clips.Count == 0)
            {
                return;
            }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Scene Preview", EditorStyles.boldLabel);

                GameObject model = ScenePreviewSpawner.PreviewModel();

                using (var check = new EditorGUI.ChangeCheckScope())
                {
                    var chosen = (GameObject)EditorGUILayout.ObjectField(
                        new GUIContent("Preview Model", "The rigged model these clips animate."),
                        model,
                        typeof(GameObject),
                        false);

                    if (check.changed)
                    {
                        ScenePreviewSpawner.SetPreviewModel(chosen);
                        model = chosen;
                    }
                }

                using (new EditorGUI.DisabledScope(model == null))
                {
                    string label = clips.Count > 1
                        ? $"Spawn {clips.Count} Previews in Scene"
                        : "Spawn in Scene";

                    if (GUILayout.Button(label))
                    {
                        Spawn(clips, model);
                    }
                }

                if (model == null)
                {
                    EditorGUILayout.HelpBox(
                        "Pick the rigged model these clips belong to. A clip on its own " +
                        "does not know which skeleton it animates.",
                        MessageType.None);
                }
            }
        }

        private static void Spawn(List<AnimationClip> clips, GameObject model)
        {
            var spawned = new List<UnityEngine.Object>(clips.Count);
            float offset = 0f;

            foreach (AnimationClip clip in clips)
            {
                GameObject instance = ScenePreviewSpawner.Spawn(model, clip, offset);
                if (instance == null)
                {
                    continue;
                }

                spawned.Add(instance);
                offset += 2f;
            }

            if (spawned.Count > 0)
            {
                Selection.objects = spawned.ToArray();
            }
        }

        private void DrawRootMotionBlock()
        {
            List<AnimationClip> clips = TargetClips();
            if (clips.Count == 0)
            {
                return;
            }

            // Clips inside a model asset belong to the importer -- Unity's own Root
            // node setting handles those, and anything written here would be dropped
            // on the next reimport.
            if (!AllEditable(clips))
            {
                EditorGUILayout.HelpBox(
                    "This clip is part of a model asset. Use the model's Rig > Root node " +
                    "and Animation > Root Transform settings instead.",
                    MessageType.Info);
                return;
            }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Root Motion", EditorStyles.boldLabel);

                using (var check = new EditorGUI.ChangeCheckScope())
                {
                    string edited = EditorGUILayout.TextField(
                        new GUIContent("Root Bone", "The bone that carries the movement in the clip."),
                        rootBone);

                    if (check.changed)
                    {
                        rootBone = edited;
                        EditorPrefs.SetString(RootBonePrefKey, rootBone);
                    }
                }

                bool enabled = AllHaveRootMotion(clips);
                bool mixed = enabled != AnyHasRootMotion(clips);

                using (new EditorGUI.DisabledScope(!enabled && !CanEnable(clips)))
                {
                    EditorGUI.showMixedValue = mixed;

                    using (var check = new EditorGUI.ChangeCheckScope())
                    {
                        bool wanted = EditorGUILayout.Toggle(
                            new GUIContent(
                                "Use Root Motion",
                                "On: the movement moves the GameObject and the bone stays in place.\n" +
                                "Off: the movement goes back onto the bone."),
                            enabled);

                        if (check.changed)
                        {
                            Apply(clips, wanted);
                        }
                    }

                    EditorGUI.showMixedValue = false;
                }

                if (!enabled && !CanEnable(clips))
                {
                    EditorGUILayout.HelpBox(
                        $"'{rootBone}' does not move in this clip, so there is no motion to take from it.",
                        MessageType.None);
                }
            }
        }

        private void Apply(List<AnimationClip> clips, bool useRootMotion)
        {
            int changed = 0;

            foreach (AnimationClip clip in clips)
            {
                bool done = useRootMotion
                    ? RootMotionClipUtility.Convert(clip, rootBone)
                    : RootMotionClipUtility.Revert(clip, rootBone);

                if (done)
                {
                    changed++;
                }
            }

            if (changed > 0)
            {
                AssetDatabase.SaveAssets();
            }
        }

        private List<AnimationClip> TargetClips()
        {
            var clips = new List<AnimationClip>(targets.Length);

            foreach (UnityEngine.Object target in targets)
            {
                if (target is AnimationClip clip)
                {
                    clips.Add(clip);
                }
            }

            return clips;
        }

        private static bool AllEditable(List<AnimationClip> clips)
        {
            foreach (AnimationClip clip in clips)
            {
                if (!RootMotionClipUtility.IsEditable(clip))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool AllHaveRootMotion(List<AnimationClip> clips)
        {
            foreach (AnimationClip clip in clips)
            {
                if (!RootMotionClipUtility.HasRootMotion(clip))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool AnyHasRootMotion(List<AnimationClip> clips)
        {
            foreach (AnimationClip clip in clips)
            {
                if (RootMotionClipUtility.HasRootMotion(clip))
                {
                    return true;
                }
            }

            return false;
        }

        private bool CanEnable(List<AnimationClip> clips)
        {
            foreach (AnimationClip clip in clips)
            {
                if (RootMotionClipUtility.HasMovingRootBone(clip, rootBone))
                {
                    return true;
                }
            }

            return false;
        }

        private void CreateBuiltInEditor()
        {
            // AnimationClipEditor is internal, so it can only be reached by name. If
            // Unity ever renames it the block still draws and the fallback keeps the
            // Inspector usable, just without the timeline.
            Type type = Type.GetType("UnityEditor.AnimationClipEditor, UnityEditor");
            if (type == null)
            {
                return;
            }

            builtInEditor = CreateEditor(targets, type);
        }

        // Everything below hands the preview back to Unity's editor. Without these
        // the clip preview at the bottom of the Inspector disappears.

        public override bool HasPreviewGUI()
        {
            return builtInEditor != null ? builtInEditor.HasPreviewGUI() : base.HasPreviewGUI();
        }

        public override GUIContent GetPreviewTitle()
        {
            return builtInEditor != null ? builtInEditor.GetPreviewTitle() : base.GetPreviewTitle();
        }

        public override void OnPreviewSettings()
        {
            if (builtInEditor != null)
            {
                builtInEditor.OnPreviewSettings();
                return;
            }

            base.OnPreviewSettings();
        }

        public override void OnPreviewGUI(Rect area, GUIStyle background)
        {
            if (builtInEditor != null)
            {
                builtInEditor.OnPreviewGUI(area, background);
                return;
            }

            base.OnPreviewGUI(area, background);
        }

        public override void OnInteractivePreviewGUI(Rect area, GUIStyle background)
        {
            if (builtInEditor != null)
            {
                builtInEditor.OnInteractivePreviewGUI(area, background);
                return;
            }

            base.OnInteractivePreviewGUI(area, background);
        }
    }

}
