using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
namespace FofuxoAnimationTools.Editor
{
    /// <summary>
    /// Batch versions of the clip settings, on the Project window context menu.
    ///
    /// The Inspector toggle is for one clip, or a handful. These are for the case
    /// this project actually has: four hundred clips where every one needs the same
    /// switch flipped.
    /// </summary>
    public static class AnimationClipBatchMenu
    {
        private const string RootBonePrefKey = "Fofuxo.RootMotion.RootBone";

        private const string ConvertPath = "Assets/Animation Clips/Use Root Motion";
        private const string RevertPath = "Assets/Animation Clips/Clear Root Motion";
        private const string LoopOnPath = "Assets/Animation Clips/Loop Time/Enable";
        private const string LoopOffPath = "Assets/Animation Clips/Loop Time/Disable";

        [MenuItem(ConvertPath, false, 0)]
        private static void UseRootMotion()
        {
            string rootBone = RootBone();
            int changed = 0;
            int skipped = 0;

            Run("Converting to root motion", clip =>
            {
                if (RootMotionClipUtility.Convert(clip, rootBone))
                {
                    changed++;
                }
                else
                {
                    skipped++;
                }
            });

            Debug.Log($"Root motion enabled on {changed} clip(s). " +
                      $"{skipped} had no movement on '{rootBone}' and were left alone.");
        }

        [MenuItem(RevertPath, false, 1)]
        private static void ClearRootMotion()
        {
            string rootBone = RootBone();
            int changed = 0;

            Run("Clearing root motion", clip =>
            {
                if (RootMotionClipUtility.Revert(clip, rootBone))
                {
                    changed++;
                }
            });

            Debug.Log($"Root motion moved back onto '{rootBone}' on {changed} clip(s).");
        }

        [MenuItem(LoopOnPath, false, 20)]
        private static void EnableLoop() => SetLoop(true);

        [MenuItem(LoopOffPath, false, 21)]
        private static void DisableLoop() => SetLoop(false);

        private static void SetLoop(bool looping)
        {
            int changed = 0;

            Run(looping ? "Enabling loop" : "Disabling loop", clip =>
            {
                if (RootMotionClipUtility.SetLooping(clip, looping))
                {
                    changed++;
                }
            });

            Debug.Log($"Loop Time {(looping ? "enabled" : "disabled")} on {changed} clip(s).");
        }

        [MenuItem(ConvertPath, true)]
        [MenuItem(RevertPath, true)]
        [MenuItem(LoopOnPath, true)]
        [MenuItem(LoopOffPath, true)]
        private static bool HasEditableClips()
        {
            return SelectedClips().Count > 0;
        }

        private static void Run(string title, System.Action<AnimationClip> action)
        {
            List<AnimationClip> clips = SelectedClips();
            if (clips.Count == 0)
            {
                return;
            }

            try
            {
                for (int i = 0; i < clips.Count; i++)
                {
                    bool cancelled = EditorUtility.DisplayCancelableProgressBar(
                        title,
                        $"{clips[i].name}  ({i + 1}/{clips.Count})",
                        (float)i / clips.Count);

                    if (cancelled)
                    {
                        break;
                    }

                    action(clips[i]);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            AssetDatabase.SaveAssets();
        }

        /// <summary>
        /// Standalone .anim assets in the selection. Clips inside a model are left
        /// out: they belong to the importer and any edit would vanish on reimport.
        /// </summary>
        private static List<AnimationClip> SelectedClips()
        {
            var clips = new List<AnimationClip>();

            foreach (Object selected in Selection.GetFiltered(typeof(AnimationClip), SelectionMode.Assets))
            {
                if (selected is AnimationClip clip && RootMotionClipUtility.IsEditable(clip))
                {
                    clips.Add(clip);
                }
            }

            return clips;
        }

        private static string RootBone()
        {
            return EditorPrefs.GetString(RootBonePrefKey, RootMotionClipUtility.DefaultRootBone);
        }
    }

}
