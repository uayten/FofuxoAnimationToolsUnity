using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
namespace FofuxoAnimationTools.Editor
{
    /// <summary>
    /// Bakes the planar travel out of a standalone .anim asset so the animation
    /// plays in place, and restores it on demand.
    ///
    /// This is the Generic-rig counterpart of the Root Motion toggle: Unity's own
    /// "Bake Into Pose" inspector only exists for Humanoid clips, while this
    /// pipeline authors standalone .anim files on a Generic skeleton. Baking here
    /// means the root bone's local X/Z is frozen at its first value -- the mesh
    /// stops travelling across the floor while bob (Y) and facing (rotation) stay
    /// in the pose.
    ///
    /// The operation is reversible: the original bone and RootT curves are cloned
    /// into hidden backup bindings inside the same clip, so there is no sidecar
    /// file to lose, move or clean up. Unbaking writes the backups back and drops
    /// them.
    /// </summary>
    public static class BakePoseClipUtility
    {
        private const float MovementTolerance = 0.0001f;

        /// <summary>Carrier path for the hidden originals. Never a real bone.</summary>
        private const string BackupPath = "__FofuxoBakeBackup__";

        /// <summary>The bone properties frozen by the bake, in planar order.</summary>
        private static readonly string[] PlanarPosition =
        {
            "m_LocalPosition.x", "m_LocalPosition.z"
        };

        /// <summary>The bone properties carried in the backup, for an exact restore.</summary>
        private static readonly string[] BackedUpBonePosition =
        {
            "m_LocalPosition.x", "m_LocalPosition.y", "m_LocalPosition.z"
        };

        /// <summary>The root-motion properties carried in the backup.</summary>
        private static readonly string[] RootTranslation = { "RootT.x", "RootT.y", "RootT.z" };

        /// <summary>Backup slots, one per backed-up track.</summary>
        private static readonly string[] BackupSlots =
        {
            "posX", "posY", "posZ", "rootTx", "rootTy", "rootTz"
        };

        /// <summary>
        /// False for clips that live inside a model asset. Those belong to the
        /// importer, and any edit would be lost on the next reimport.
        /// </summary>
        public static bool IsEditable(AnimationClip clip)
        {
            return clip != null && AssetDatabase.IsMainAsset(clip);
        }

        /// <summary>True when the clip carries a bake backup, i.e. it is baked.</summary>
        public static bool HasBakedPose(AnimationClip clip)
        {
            if (clip == null)
            {
                return false;
            }

            foreach (string slot in BackupSlots)
            {
                AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, BackupBinding(slot));
                if (curve != null && curve.length > 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// True when the root bone actually travels across the floor during the
        /// clip. A bone that holds still has nothing to bake.
        /// </summary>
        public static bool HasTravelingRootBone(AnimationClip clip, string rootBone)
        {
            if (clip == null || string.IsNullOrEmpty(rootBone))
            {
                return false;
            }

            foreach (string property in PlanarPosition)
            {
                AnimationCurve curve = AnimationUtility.GetEditorCurve(
                    clip, BoneBinding(rootBone, property));
                if (curve != null && Travels(curve))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Freezes the root bone's planar position at its first value and removes
        /// the RootT curves, so the clip plays in place. Originals are cloned into
        /// the backup bindings first, making <see cref="Unbake"/> exact.
        /// </summary>
        public static bool Bake(AnimationClip clip, string rootBone)
        {
            if (!IsEditable(clip) || string.IsNullOrEmpty(rootBone) || HasBakedPose(clip))
            {
                return false;
            }

            if (!HasTravelingRootBone(clip, rootBone))
            {
                return false;
            }

            bool changed = false;

            for (int i = 0; i < BackedUpBonePosition.Length; i++)
            {
                EditorCurveBinding bone = BoneBinding(rootBone, BackedUpBonePosition[i]);
                AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, bone);
                if (curve == null || curve.length == 0)
                {
                    continue;
                }

                AnimationUtility.SetEditorCurve(clip, BackupBinding(BackupSlots[i]), Clone(curve));
                changed = true;
            }

            for (int i = 0; i < RootTranslation.Length; i++)
            {
                EditorCurveBinding root = RootBinding(i);
                AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, root);
                if (curve == null || curve.length == 0)
                {
                    continue;
                }

                AnimationUtility.SetEditorCurve(clip, BackupBinding(BackupSlots[3 + i]), Clone(curve));

                // Passing null removes the binding.
                AnimationUtility.SetEditorCurve(clip, root, null);
                changed = true;
            }

            if (!changed)
            {
                return false;
            }

            foreach (string property in PlanarPosition)
            {
                EditorCurveBinding bone = BoneBinding(rootBone, property);
                AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, bone);
                if (curve == null || curve.length == 0)
                {
                    continue;
                }

                AnimationUtility.SetEditorCurve(clip, bone, Flattened(curve));
            }

            EditorUtility.SetDirty(clip);
            return true;
        }

        /// <summary>
        /// Writes the backup curves back onto the bone and RootT bindings and drops
        /// the backup, restoring the clip exactly as it was before <see cref="Bake"/>.
        /// </summary>
        public static bool Unbake(AnimationClip clip, string rootBone)
        {
            if (!IsEditable(clip) || !HasBakedPose(clip))
            {
                return false;
            }

            for (int i = 0; i < BackedUpBonePosition.Length; i++)
            {
                AnimationCurve backup = AnimationUtility.GetEditorCurve(clip, BackupBinding(BackupSlots[i]));
                if (backup == null || backup.length == 0)
                {
                    continue;
                }

                AnimationUtility.SetEditorCurve(
                    clip, BoneBinding(rootBone, BackedUpBonePosition[i]), Clone(backup));
                AnimationUtility.SetEditorCurve(clip, BackupBinding(BackupSlots[i]), null);
            }

            for (int i = 0; i < RootTranslation.Length; i++)
            {
                AnimationCurve backup = AnimationUtility.GetEditorCurve(clip, BackupBinding(BackupSlots[3 + i]));
                if (backup == null || backup.length == 0)
                {
                    continue;
                }

                AnimationUtility.SetEditorCurve(clip, RootBinding(i), Clone(backup));
                AnimationUtility.SetEditorCurve(clip, BackupBinding(BackupSlots[3 + i]), null);
            }

            EditorUtility.SetDirty(clip);
            return true;
        }

        private static bool Travels(AnimationCurve curve)
        {
            if (curve == null || curve.length < 2)
            {
                return false;
            }

            float first = curve.keys[0].value;
            for (int i = 1; i < curve.length; i++)
            {
                if (Mathf.Abs(curve.keys[i].value - first) > MovementTolerance)
                {
                    return true;
                }
            }

            return false;
        }

        private static AnimationCurve Clone(AnimationCurve curve)
        {
            var clone = new AnimationCurve(curve.keys);
            clone.preWrapMode = curve.preWrapMode;
            clone.postWrapMode = curve.postWrapMode;
            return clone;
        }

        /// <summary>
        /// A constant curve holding the track's first value across the original
        /// time span, so the first frame -- and the loop point -- never pop.
        /// </summary>
        private static AnimationCurve Flattened(AnimationCurve curve)
        {
            float value = curve.keys[0].value;
            float start = curve.keys[0].time;
            float end = curve.keys[curve.length - 1].time;
            if (end <= start)
            {
                end = start + 1f;
            }

            var flat = new AnimationCurve(
                new Keyframe(start, value, 0f, 0f),
                new Keyframe(end, value, 0f, 0f));
            flat.preWrapMode = WrapMode.ClampForever;
            flat.postWrapMode = WrapMode.ClampForever;
            return flat;
        }

        private static EditorCurveBinding BoneBinding(string rootBone, string property)
        {
            return EditorCurveBinding.FloatCurve(rootBone, typeof(Transform), property);
        }

        private static EditorCurveBinding RootBinding(int axis)
        {
            return EditorCurveBinding.FloatCurve(string.Empty, typeof(Animator), RootTranslation[axis]);
        }

        private static EditorCurveBinding BackupBinding(string slot)
        {
            return EditorCurveBinding.FloatCurve(BackupPath, typeof(Transform), slot);
        }
    }
}
