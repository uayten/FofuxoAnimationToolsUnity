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
            GameObject model = PreviewModel();
            if (model == null)
            {
                EditorUtility.DisplayDialog(
                    "Fofuxo Animation Tools",
                    "No preview model is set.\n\n" +
                    "Select an animation clip and use the Root Motion block in the " +
                    "Inspector to pick the model these clips animate.",
                    "Ok");
                return;
            }

            List<AnimationClip> clips = SelectedClips();
            if (clips.Count == 0)
            {
                return;
            }

            var spawned = new List<Object>(clips.Count);
            float offset = 0f;

            foreach (AnimationClip clip in clips)
            {
                GameObject instance = Spawn(model, clip, offset);
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

        public static GameObject PreviewModel()
        {
            string guid = EditorPrefs.GetString(PreviewModelPrefKey, string.Empty);
            if (string.IsNullOrEmpty(guid))
            {
                return null;
            }

            string path = AssetDatabase.GUIDToAssetPath(guid);
            return string.IsNullOrEmpty(path)
                ? null
                : AssetDatabase.LoadAssetAtPath<GameObject>(path);
        }

        public static void SetPreviewModel(GameObject model)
        {
            if (model == null)
            {
                EditorPrefs.DeleteKey(PreviewModelPrefKey);
                return;
            }

            string path = AssetDatabase.GetAssetPath(model);
            EditorPrefs.SetString(PreviewModelPrefKey, AssetDatabase.AssetPathToGUID(path));
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
