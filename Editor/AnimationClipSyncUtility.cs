using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace FofuxoAnimationTools.Editor
{
    /// <summary>
    /// Brings a freshly imported set of animations onto the clips the project is
    /// already using, without any reference in the project noticing.
    ///
    /// The obvious way to reimport four hundred animations is to extract them again
    /// and point everything at the new files. That is four hundred new GUIDs, and
    /// every Animator state, Timeline track and inspector field that named a clip is
    /// now naming a clip nobody kept. The reorganisation afterwards is the real cost
    /// of the reimport, and it is entirely self-inflicted.
    ///
    /// This does the opposite. The existing .anim asset stays where it is, keeps its
    /// GUID and its file ID, and only its contents are replaced -- with
    /// EditorUtility.CopySerialized, the same copy Unity performs internally. Nothing
    /// that referenced the clip can tell the difference, because as far as the asset
    /// database is concerned nothing happened. There is nothing to reorganise, and
    /// that includes the tools that reference clips by name instead of by GUID, which
    /// a re-extraction would also have broken and which no reference remapper can
    /// reach.
    ///
    /// What does not survive the copy is everything that was set on the clip
    /// afterwards -- Loop Time, the root motion curves this package writes -- because
    /// the incoming clip has its own. Those are read before the copy and put back
    /// after it.
    /// </summary>
    public static class AnimationClipSyncUtility
    {
        /// <summary>Settings on the old clip that are worth surviving the update.</summary>
        [System.Flags]
        public enum Preserve
        {
            None = 0,

            /// <summary>Loop Time, cycle offset, mirror and the loop pose flags.</summary>
            Playback = 1,

            /// <summary>Re-run the root motion conversion if the clip had it.</summary>
            RootMotion = 2
        }

        /// <summary>
        /// How a name coming out of the new export is turned into the name to look
        /// for among the existing clips. Exports rarely come back spelled the same:
        /// a take gains a numeric suffix, a naming convention changes between two
        /// runs of the pipeline, and the clips are otherwise identical.
        /// </summary>
        public sealed class NameRules
        {
            public bool IgnoreCase = true;
            public bool StripTrailingNumber;
            public string Prefixes = string.Empty;
            public string Suffixes = string.Empty;

            public string Apply(string name)
            {
                if (string.IsNullOrEmpty(name))
                {
                    return string.Empty;
                }

                string result = name;

                foreach (string prefix in FofuxoToolsSettings.Split(Prefixes))
                {
                    if (result.StartsWith(prefix, Comparison))
                    {
                        result = result.Substring(prefix.Length);
                        break;
                    }
                }

                foreach (string suffix in FofuxoToolsSettings.Split(Suffixes))
                {
                    if (result.EndsWith(suffix, Comparison))
                    {
                        result = result.Substring(0, result.Length - suffix.Length);
                        break;
                    }
                }

                if (StripTrailingNumber)
                {
                    int cut = result.Length;
                    while (cut > 0 && char.IsDigit(result[cut - 1]))
                    {
                        cut--;
                    }

                    // Only a real suffix, "_223", not the 01 in Attack_01.
                    if (cut < result.Length && cut > 0 && result[cut - 1] == '_')
                    {
                        result = result.Substring(0, cut - 1);
                    }
                }

                return IgnoreCase ? result.ToLowerInvariant() : result;
            }

            private System.StringComparison Comparison => IgnoreCase
                ? System.StringComparison.OrdinalIgnoreCase
                : System.StringComparison.Ordinal;
        }

        /// <summary>
        /// The animation clips a model holds. Unity keeps a hidden duplicate of each
        /// take for the importer's own preview; those are not takes.
        /// </summary>
        public static List<AnimationClip> ClipsInModel(string modelPath)
        {
            var clips = new List<AnimationClip>();

            foreach (Object member in AssetDatabase.LoadAllAssetRepresentationsAtPath(modelPath))
            {
                if (member is AnimationClip clip && !clip.name.StartsWith("__preview__"))
                {
                    clips.Add(clip);
                }
            }

            clips.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            return clips;
        }

        /// <summary>Model assets at or under a path — glb and gltf included.</summary>
        public static List<string> ModelsUnder(string path)
        {
            return ModelAsset.Under(path);
        }

        /// <summary>Standalone .anim assets in a folder.</summary>
        public static List<AnimationClip> ClipsInFolder(string folder, bool recursive)
        {
            var clips = new List<AnimationClip>();

            if (string.IsNullOrEmpty(folder) || !AssetDatabase.IsValidFolder(folder))
            {
                return clips;
            }

            foreach (string guid in AssetDatabase.FindAssets("t:AnimationClip", new[] { folder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);

                if (!recursive && System.IO.Path.GetDirectoryName(path).Replace('\\', '/') != folder)
                {
                    continue;
                }

                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                if (clip != null && AssetDatabase.IsMainAsset(clip))
                {
                    clips.Add(clip);
                }
            }

            clips.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            return clips;
        }

        /// <summary>
        /// Replaces the contents of an existing clip asset with those of a freshly
        /// imported one, leaving the asset itself -- its GUID, its file ID, its path
        /// and its name -- exactly as it was.
        /// </summary>
        public static bool UpdateInPlace(
            AnimationClip source, AnimationClip target, Preserve preserve, string rootBone)
        {
            if (source == null || target == null || source == target)
            {
                return false;
            }

            if (!AssetDatabase.IsMainAsset(target))
            {
                return false;
            }

            string name = target.name;
            AnimationClipSettings previous = AnimationUtility.GetAnimationClipSettings(target);
            bool hadRootMotion = RootMotionClipUtility.HasRootMotion(target);

            EditorUtility.CopySerialized(source, target);

            // CopySerialized copies the name and the hide flags too, and the source is
            // a sub-asset of a model. Neither belongs on the asset being written.
            target.name = name;
            target.hideFlags = HideFlags.None;

            if ((preserve & Preserve.Playback) != 0)
            {
                Restore(target, previous);
            }

            if ((preserve & Preserve.RootMotion) != 0 && hadRootMotion)
            {
                RootMotionClipUtility.Convert(target, rootBone);
            }

            EditorUtility.SetDirty(target);
            return true;
        }

        /// <summary>
        /// Puts back the settings that were authored on the clip rather than imported
        /// with it. Start and stop time are deliberately left as the new clip has
        /// them: they describe how long this animation is, and the whole point of the
        /// update is that it might now be a different length.
        /// </summary>
        private static void Restore(AnimationClip clip, AnimationClipSettings previous)
        {
            AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(clip);

            settings.loopTime = previous.loopTime;
            settings.loopBlend = previous.loopBlend;
            settings.loopBlendOrientation = previous.loopBlendOrientation;
            settings.loopBlendPositionY = previous.loopBlendPositionY;
            settings.loopBlendPositionXZ = previous.loopBlendPositionXZ;
            settings.keepOriginalOrientation = previous.keepOriginalOrientation;
            settings.keepOriginalPositionY = previous.keepOriginalPositionY;
            settings.keepOriginalPositionXZ = previous.keepOriginalPositionXZ;
            settings.heightFromFeet = previous.heightFromFeet;
            settings.cycleOffset = previous.cycleOffset;
            settings.orientationOffsetY = previous.orientationOffsetY;
            settings.level = previous.level;
            settings.mirror = previous.mirror;

            AnimationUtility.SetAnimationClipSettings(clip, settings);
        }

        /// <summary>
        /// Extracts a clip out of a model into its own .anim asset, which is what
        /// Ctrl+D on the clip inside the model does, minus the clicking.
        /// </summary>
        public static AnimationClip Extract(AnimationClip source, string folder)
        {
            if (source == null || !AssetDatabase.IsValidFolder(folder))
            {
                return null;
            }

            var clip = new AnimationClip();
            EditorUtility.CopySerialized(source, clip);
            clip.name = source.name;
            clip.hideFlags = HideFlags.None;

            string path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{FileName(source.name)}.anim");
            AssetDatabase.CreateAsset(clip, path);

            return clip;
        }

        private static string FileName(string name)
        {
            var builder = new StringBuilder(name.Length);
            char[] invalid = System.IO.Path.GetInvalidFileNameChars();

            foreach (char character in name)
            {
                builder.Append(System.Array.IndexOf(invalid, character) >= 0 ? '_' : character);
            }

            return builder.ToString();
        }
    }
}
