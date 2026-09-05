using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace FofuxoAnimationTools.Editor
{
    /// <summary>
    /// Drops a copy of the character into the scene playing one clip on loop --
    /// the Unity equivalent of dragging an Animation Sequence into an Unreal
    /// level.
    ///
    /// Unity has no built-in behaviour for this: dragging a .anim into the scene
    /// does nothing, because a clip has no idea which skeleton it belongs to.
    /// That is what the preview model setting is for.
    /// </summary>
    public static class ScenePreviewSpawner
    {
        public const string PreviewModelPrefKey = "Fofuxo.AnimationTools.PreviewModel";

        private const string MenuPath = "Assets/Animation Clips/Preview in Scene";

        [MenuItem(MenuPath, false, 40)]
        private static void SpawnSelected()
        {
            List<AnimationClip> clips = SelectedClips();
            if (clips.Count == 0)
            {
                return;
            }

            if (PreviewModel(clips[0]) == null)
            {
                EditorUtility.DisplayDialog(
                    "Fofuxo Animation Tools",
                    $"No preview model for '{clips[0].name}'.\n\n" +
                    "No folder from the clip up to Assets holds a rigged model. Set " +
                    "Preview Model in the Scene Preview block of the Inspector; the " +
                    "choice is remembered for the whole folder.",
                    "Ok");
                return;
            }

            var spawned = new List<Object>(clips.Count);
            float offset = 0f;

            foreach (AnimationClip clip in clips)
            {
                GameObject instance = Spawn(PreviewModel(clip), clip, offset);
                if (instance == null)
                {
                    continue;
                }

                spawned.Add(instance);

                // Several clips at once line up instead of stacking on top of
                // each other, which is the whole point when reviewing a folder.
                offset += 2f;
            }

            if (spawned.Count > 0)
            {
                Selection.objects = spawned.ToArray();
            }
        }

        [MenuItem(MenuPath, true)]
        private static bool HasClips()
        {
            return SelectedClips().Count > 0;
        }

        public static GameObject Spawn(GameObject model, AnimationClip clip, float sideOffset)
        {
            if (model == null || clip == null)
            {
                return null;
            }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(model);
            if (instance == null)
            {
                return null;
            }

            instance.name = $"Preview - {clip.name}";

            // Unpacked on purpose: this is a throwaway object, and leaving it
            // linked to the model prefab invites accidental edits to the source.
            PrefabUtility.UnpackPrefabInstance(
                instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);

            Transform pivot = SceneView.lastActiveSceneView != null
                ? SceneView.lastActiveSceneView.camera.transform
                : null;

            Vector3 origin = pivot != null
                ? pivot.position + pivot.forward * 4f
                : Vector3.zero;

            origin.y = 0f;
            instance.transform.position = origin + Vector3.right * sideOffset;

            Animator animator = instance.GetComponent<Animator>();
            if (animator == null)
            {
                animator = instance.GetComponentInChildren<Animator>();
            }

            if (animator == null)
            {
                animator = instance.AddComponent<Animator>();
            }

            // A controller would override the graph this component drives.
            animator.runtimeAnimatorController = null;

            AnimationClipScenePreview preview =
                animator.gameObject.AddComponent<AnimationClipScenePreview>();

            preview.clip = clip;

            // AddComponent already fired OnEnable, back when there was no clip to
            // build a graph around. Without this the character stands in T-pose
            // until something forces a reinitialise -- entering Play, typically.
            preview.Rebuild();

            Undo.RegisterCreatedObjectUndo(instance, "Preview Animation Clip");
            return instance;
        }

        /// <summary>
        /// The model a clip is meant to animate.
        ///
        /// One remembered model for the whole project only works while the project has
        /// one character. On the second, selecting a clip offers the wrong rig, and
        /// previewing it folds the body into shapes it cannot make — which reads as a
        /// broken export rather than as the wrong model having been asked.
        ///
        /// So the clip's own folder answers first, and answers by itself: walking up
        /// from where the clip lives, the nearest folder holding a rigged model is the
        /// character those clips belong to. `Characters/Grant/AnimFiles/x.anim` finds
        /// `Characters/Grant/Grant.fbx` one level up and stops, without ever reaching
        /// `Characters/`, where it would have to choose between everybody.
        ///
        /// Each level is searched without descending, for the same reason: a recursive
        /// look from one character's folder reaches the neighbours' as well.
        /// </summary>
        public static GameObject PreviewModel(AnimationClip clip = null)
        {
            string folder = FolderOf(clip);

            if (!string.IsNullOrEmpty(folder))
            {
                GameObject chosen = Load(EditorPrefs.GetString(FolderKey(folder), string.Empty));
                if (chosen != null)
                {
                    return chosen;
                }

                GameObject nearby = NearestCharacter(folder);
                if (nearby != null)
                {
                    return nearby;
                }
            }

            return Load(EditorPrefs.GetString(PreviewModelPrefKey, string.Empty));
        }

        /// <summary>
        /// Remembers a model chosen by hand, against the clip's folder rather than the
        /// clip itself: four hundred clips of one character live in one folder, and
        /// answering the question once should not have to be done four hundred times.
        /// </summary>
        public static void SetPreviewModel(GameObject model, AnimationClip clip = null)
        {
            string guid = model == null
                ? string.Empty
                : AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(model));

            string folder = FolderOf(clip);

            if (!string.IsNullOrEmpty(folder))
            {
                if (string.IsNullOrEmpty(guid))
                {
                    EditorPrefs.DeleteKey(FolderKey(folder));
                }
                else
                {
                    EditorPrefs.SetString(FolderKey(folder), guid);
                }
            }

            // The project-wide value stays as the last resort, for the callers that have
            // no clip in hand to ask about.
            if (string.IsNullOrEmpty(guid))
            {
                EditorPrefs.DeleteKey(PreviewModelPrefKey);
            }
            else
            {
                EditorPrefs.SetString(PreviewModelPrefKey, guid);
            }
        }

        private static string FolderKey(string folder)
        {
            return PreviewModelPrefKey + "." + folder;
        }

        private static string FolderOf(AnimationClip clip)
        {
            string path = clip == null ? null : AssetDatabase.GetAssetPath(clip);

            return string.IsNullOrEmpty(path)
                ? string.Empty
                : System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
        }

        private static GameObject Load(string guid)
        {
            if (string.IsNullOrEmpty(guid))
            {
                return null;
            }

            string path = AssetDatabase.GUIDToAssetPath(guid);
            return string.IsNullOrEmpty(path)
                ? null
                : AssetDatabase.LoadAssetAtPath<GameObject>(path);
        }

        /// <summary>
        /// Walks up from a folder looking for the character, stopping short of Assets.
        ///
        /// A candidate is anything with a skinned mesh. A material-free FBX generated by
        /// Extract Mesh and Avatar wins explicitly, so a nearby gameplay prefab cannot
        /// bring colliders, scripts and other authored components into the preview.
        /// Files carrying animation are passed over: an animation export often ships the
        /// mesh alongside the take, so the folder can be full of things that look like
        /// the character and are really one clip each.
        ///
        /// Within a level the biggest rig wins, which is what tells a character apart
        /// from the sword lying next to it. Only if a level offers nothing but animation
        /// files does one of those get used, so a folder that has not had its character
        /// extracted yet still previews.
        /// </summary>
        private static GameObject NearestCharacter(string folder)
        {
            while (!string.IsNullOrEmpty(folder) && folder != "Assets" && folder.StartsWith("Assets"))
            {
                GameObject best = BestGeneratedPreviewUnder(folder) ??
                                  Best(folder, false) ??
                                  Best(folder, true);

                if (best != null)
                {
                    return best;
                }

                folder = System.IO.Path.GetDirectoryName(folder)?.Replace('\\', '/');
            }

            return null;
        }

        /// <summary>
        /// Generated previews may live in a Reimport folder beside the AnimFiles folder.
        /// Searching only the current level would miss that sibling and fall back to a
        /// gameplay prefab at the character root. The label makes the recursive search
        /// safe: arbitrary models and neighbouring prefabs are not candidates here.
        /// </summary>
        private static GameObject BestGeneratedPreviewUnder(string folder)
        {
            GameObject best = null;
            int bones = 0;

            foreach (string guid in AssetDatabase.FindAssets(
                         $"l:{ModelExtractUtility.GeneratedPreviewLabel} t:GameObject",
                         new[] { folder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);

                if (model == null ||
                    !ModelExtractUtility.IsGeneratedPreview(path) ||
                    model.GetComponentInChildren<SkinnedMeshRenderer>(true) == null)
                {
                    continue;
                }

                int count = model.GetComponentsInChildren<Transform>(true).Length;
                if (count > bones)
                {
                    bones = count;
                    best = model;
                }
            }

            return best;
        }

        private static GameObject Best(string folder, bool allowAnimationFiles)
        {
            GameObject best = null;
            int bones = 0;
            bool bestIsGeneratedPreview = false;

            foreach (string guid in AssetDatabase.FindAssets("t:GameObject", new[] { folder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);

                if (System.IO.Path.GetDirectoryName(path).Replace('\\', '/') != folder)
                {
                    continue;
                }

                var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);

                if (model == null || model.GetComponentInChildren<SkinnedMeshRenderer>(true) == null)
                {
                    continue;
                }

                if (!allowAnimationFiles && CarriesAnimation(path))
                {
                    continue;
                }

                int count = model.GetComponentsInChildren<Transform>(true).Length;
                bool isGeneratedPreview = ModelExtractUtility.IsGeneratedPreview(path);

                if (best == null ||
                    (isGeneratedPreview && !bestIsGeneratedPreview) ||
                    (isGeneratedPreview == bestIsGeneratedPreview && count > bones))
                {
                    bones = count;
                    best = model;
                    bestIsGeneratedPreview = isGeneratedPreview;
                }
            }

            return best;
        }

        private static bool CarriesAnimation(string path)
        {
            foreach (Object member in AssetDatabase.LoadAllAssetRepresentationsAtPath(path))
            {
                if (member is AnimationClip clip && !clip.name.StartsWith("__preview__"))
                {
                    return true;
                }
            }

            return false;
        }

        private static List<AnimationClip> SelectedClips()
        {
            var clips = new List<AnimationClip>();

            foreach (Object selected in Selection.GetFiltered(
                         typeof(AnimationClip), SelectionMode.Assets))
            {
                if (selected is AnimationClip clip)
                {
                    clips.Add(clip);
                }
            }

            return clips;
        }
    }
}
