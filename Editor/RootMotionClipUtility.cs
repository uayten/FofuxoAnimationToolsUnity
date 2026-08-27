using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
namespace FofuxoAnimationTools.Editor
{
    /// <summary>
    /// Turns the displacement stored on a skeleton bone into real root motion on a
    /// standalone .anim asset.
    ///
    /// Unity stores root motion in special curves: empty path, Animator type, and
    /// the RootT.x/y/z properties. That is what this writes. The bone itself is then
    /// frozen at its first value, so the animation plays in place while the object
    /// is the thing that moves.
    ///
    /// The importer can only do this for clips that live inside a model asset.
    /// Clips extracted into their own .anim file have no importer, which is why this
    /// exists.
    /// </summary>
    public static class RootMotionClipUtility
    {
        public const string DefaultRootBone = "DEF-Root";

        private const float MovementTolerance = 0.0001f;

        /// <summary>The three properties Unity reads as root displacement.</summary>
        private static readonly string[] RootTranslation = { "RootT.x", "RootT.y", "RootT.z" };

        /// <summary>The bone properties they come from, in matching order.</summary>
        private static readonly string[] LocalPosition =
        {
            "m_LocalPosition.x", "m_LocalPosition.y", "m_LocalPosition.z"
        };

        /// <summary>
        /// False for clips that live inside a model asset. Those belong to the
        /// importer, and any edit would be lost on the next reimport.
        /// </summary>
        public static bool IsEditable(AnimationClip clip)
        {
            return clip != null && AssetDatabase.IsMainAsset(clip);
        }

        /// <summary>True when the clip already carries root motion curves.</summary>
        public static bool HasRootMotion(AnimationClip clip)
        {
            if (clip == null)
            {
                return false;
            }

            for (int axis = 0; axis < 3; axis++)
            {
                AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, RootBinding(axis));
                if (curve != null && curve.length > 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// True when the root bone actually travels during the clip. A bone that
        /// holds still has nothing to convert, and converting it anyway would add a
        /// constant RootT curve for no reason.
        /// </summary>
        public static bool HasMovingRootBone(AnimationClip clip, string rootBone)
        {
            AnimationCurve[] curves = ReadBoneCurves(clip, rootBone);
            return Travels(curves);
        }

        /// <summary>
        /// Copies the bone's position curve onto RootT, so the movement drives the
        /// GameObject instead of staying inside the skeleton.
        ///
        /// The bone curve is deliberately left untouched. Unity subtracts RootT from
        /// the pose when it applies root motion -- it expects the hierarchy to still
        /// carry the movement, and treats RootT as how much to take out of it.
        /// Freezing the bone as well made the object travel while the mesh
        /// compensated backwards by the same amount, so nothing appeared to move.
        ///
        /// Leaving the bone alone also makes the operation non-destructive: nothing
        /// is lost, so reverting is just dropping the RootT curves.
        /// </summary>
        public static bool Convert(AnimationClip clip, string rootBone)
        {
            if (!IsEditable(clip))
            {
                return false;
            }

            AnimationCurve[] boneCurves = ReadBoneCurves(clip, rootBone);
            if (!Travels(boneCurves))
            {
                return false;
            }

            for (int axis = 0; axis < 3; axis++)
            {
                AnimationCurve curve = boneCurves[axis];
                if (curve == null)
                {
                    continue;
                }

                AnimationUtility.SetEditorCurve(clip, RootBinding(axis), curve);
            }

            EditorUtility.SetDirty(clip);
            return true;
        }

        /// <summary>
        /// Drops the RootT curves. The bone still holds the movement, so the clip
        /// goes back to animating in place with nothing lost.
        /// </summary>
        public static bool Revert(AnimationClip clip, string rootBone)
        {
            if (!IsEditable(clip))
            {
                return false;
            }

            bool changed = false;

            for (int axis = 0; axis < 3; axis++)
            {
                EditorCurveBinding root = RootBinding(axis);

                AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, root);
                if (curve == null || curve.length == 0)
                {
                    continue;
                }

                // Passing null removes the binding.
                AnimationUtility.SetEditorCurve(clip, root, null);
                changed = true;
            }

            if (changed)
            {
                EditorUtility.SetDirty(clip);
            }

            return changed;
        }

        public static bool IsLooping(AnimationClip clip)
        {
            return clip != null && AnimationUtility.GetAnimationClipSettings(clip).loopTime;
        }

        public static bool SetLooping(AnimationClip clip, bool looping)
        {
            if (!IsEditable(clip) || IsLooping(clip) == looping)
            {
                return false;
            }

            AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = looping;
            AnimationUtility.SetAnimationClipSettings(clip, settings);

            EditorUtility.SetDirty(clip);
            return true;
        }

        private static AnimationCurve[] ReadBoneCurves(AnimationClip clip, string rootBone)
        {
            var curves = new AnimationCurve[3];
            if (clip == null || string.IsNullOrEmpty(rootBone))
            {
                return curves;
            }

            for (int axis = 0; axis < 3; axis++)
            {
                AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, BoneBinding(rootBone, axis));
                if (curve != null && curve.length > 0)
                {
                    curves[axis] = curve;
                }
            }

            return curves;
        }

        private static bool Travels(IReadOnlyList<AnimationCurve> curves)
        {
            foreach (AnimationCurve curve in curves)
            {
                if (curve == null || curve.length < 2)
                {
                    continue;
                }

                float first = curve.keys[0].value;
                for (int i = 1; i < curve.length; i++)
                {
                    if (Mathf.Abs(curve.keys[i].value - first) > MovementTolerance)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static EditorCurveBinding RootBinding(int axis)
        {
            return EditorCurveBinding.FloatCurve(string.Empty, typeof(Animator), RootTranslation[axis]);
        }

        private static EditorCurveBinding BoneBinding(string rootBone, int axis)
        {
            return EditorCurveBinding.FloatCurve(rootBone, typeof(Transform), LocalPosition[axis]);
        }
    }

}
