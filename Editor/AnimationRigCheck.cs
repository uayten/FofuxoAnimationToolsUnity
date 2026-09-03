using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace FofuxoAnimationTools.Editor
{
    /// <summary>
    /// Asks whether a clip can actually drive a given character.
    ///
    /// A clip holds no reference to a skeleton. It holds a list of paths --
    /// "DEF-Root/DEF-pelvis/DEF-spine" -- and hopes to find a transform at each one.
    /// If the rig it was exported from is not the rig it is played on, nothing
    /// throws and nothing is logged: the curves simply address bones that are not
    /// there, and the character stands still. Which looks exactly like a clip that
    /// was imported wrong, or a controller wired wrong, or root motion set up wrong.
    ///
    /// That silence is worth breaking before four hundred clips are written, not
    /// after, so this compares the paths in the clip against the transforms on the
    /// character and says which of the two it is.
    /// </summary>
    public static class AnimationRigCheck
    {
        public enum Verdict
        {
            /// <summary>No character to compare against.</summary>
            Unknown,

            /// <summary>Every path in the clip exists on the character.</summary>
            Fits,

            /// <summary>
            /// Every bone exists, but not at the path the clip names. The skeleton is
            /// the right one, hanging somewhere else.
            /// </summary>
            Rerooted,

            /// <summary>Bones the character does not have. A different rig.</summary>
            Missing
        }

        /// <summary>
        /// The transform paths of a character, in the form Unity binds curves to:
        /// relative to the object holding the Animator, which is the model root, and
        /// with that root itself left out.
        /// </summary>
        public sealed class Skeleton
        {
            public string Name = string.Empty;
            public readonly HashSet<string> Paths = new HashSet<string>();
            public readonly HashSet<string> Bones = new HashSet<string>();

            public bool IsEmpty => Paths.Count == 0;
        }

        /// <summary>
        /// How far two rigs disagree about their own rest pose, bone by bone.
        /// </summary>
        public sealed class BindResult
        {
            public int Differing;
            public int Compared;
            public float Worst;
            public string WorstBone = string.Empty;

            /// <summary>
            /// Ten degrees is far past anything an export tolerance explains, and well
            /// under the 180 an axis convention produces. Nothing lands in between by
            /// accident.
            /// </summary>
            public bool IsProblem => Differing > 0 && Worst > 10f;
        }

        public sealed class Result
        {
            public Verdict Verdict = Verdict.Unknown;
            public int Missing;
            public int Total;
            public string Note = string.Empty;

            public bool IsProblem => Verdict == Verdict.Rerooted || Verdict == Verdict.Missing;
        }

        private static readonly Result Unknown = new Result();

        /// <summary>
        /// The character a folder is about, if it holds one: a model with a skinned
        /// mesh, which an animation-only export does not have.
        ///
        /// Searched in that folder alone, never in the project at large. A project has
        /// more than one character, and picking whichever one a wider search happened
        /// to reach first is worse than not guessing -- the check would be run against
        /// a rig the clips were never meant for, and every take would be reported as
        /// broken when nothing is.
        ///
        /// The models being imported are excluded. An animation FBX often carries the
        /// mesh along with the take, and checking a take against the file it came out
        /// of always passes, which makes the check say nothing at all.
        /// </summary>
        public static GameObject FindCharacter(string folder, ICollection<string> exclude)
        {
            if (string.IsNullOrEmpty(folder) || !AssetDatabase.IsValidFolder(folder))
            {
                return null;
            }

            foreach (string path in ModelAsset.Under(folder))
            {
                if (exclude != null && exclude.Contains(path))
                {
                    continue;
                }

                var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (model != null && model.GetComponentInChildren<SkinnedMeshRenderer>(true) != null)
                {
                    return model;
                }
            }

            return null;
        }

        public static Skeleton Read(GameObject character)
        {
            var skeleton = new Skeleton();
            if (character == null)
            {
                return skeleton;
            }

            skeleton.Name = character.name;
            Walk(character.transform, string.Empty, skeleton);
            return skeleton;
        }

        private static void Walk(Transform parent, string prefix, Skeleton skeleton)
        {
            foreach (Transform child in parent)
            {
                string path = prefix.Length == 0 ? child.name : prefix + "/" + child.name;

                skeleton.Paths.Add(path);
                skeleton.Bones.Add(child.name);

                Walk(child, path, skeleton);
            }
        }

        /// <summary>
        /// Compares the rest pose of the model a set of takes came out of against the
        /// character they are meant to drive.
        ///
        /// The path check above asks whether the bones are there. This asks whether they
        /// start in the same place, and it is the failure that survives the first one:
        /// export the same skeleton as FBX and as glTF and the two files can disagree
        /// about which way the root faces by a full 180 degrees, every bone name
        /// matching perfectly. A clip carries local rotations, not poses — played on a
        /// rig whose rest pose is somewhere else, it animates the difference, and the
        /// character folds into shapes it cannot make.
        ///
        /// Measured here: a character extracted from the glb export, against takes from
        /// the FBX export of the same asset — 89 of 168 bones apart, worst 180 degrees.
        /// Both files were correct. Mixing them was not.
        ///
        /// Bones the character does not have are skipped; that is the other check's job.
        ///
        /// Read from the mesh's bindposes rather than from the transforms, which is the
        /// only reliable place. Unity imports an FBX with its hierarchy already posed on
        /// frame 0 of the animation inside it -- measured here, zero bones apart from
        /// that frame -- so comparing transforms would report every animated bone as a
        /// rig mismatch, on every animation FBX ever imported. A bindpose is the pose the
        /// skin was bound in and no animation moves it.
        /// </summary>
        public static BindResult CompareRest(GameObject sourceModel, GameObject character)
        {
            var result = new BindResult();

            Dictionary<string, Quaternion> source = BindRotations(sourceModel);
            Dictionary<string, Quaternion> theirs = BindRotations(character);

            if (source.Count == 0 || theirs.Count == 0)
            {
                return result;
            }

            foreach (KeyValuePair<string, Quaternion> bone in source)
            {
                if (!theirs.TryGetValue(bone.Key, out Quaternion mine))
                {
                    continue;
                }

                result.Compared++;
                float angle = Quaternion.Angle(bone.Value, mine);

                if (angle > 1f)
                {
                    result.Differing++;
                }

                if (angle > result.Worst)
                {
                    result.Worst = angle;
                    result.WorstBone = bone.Key;
                }
            }

            return result;
        }

        /// <summary>
        /// Bind rotation per bone name, off every skinned mesh in the model. By name and
        /// not by path, because the two files can hang the same skeleton under differently
        /// named roots without that being the problem.
        /// </summary>
        private static Dictionary<string, Quaternion> BindRotations(GameObject model)
        {
            var map = new Dictionary<string, Quaternion>();

            if (model == null)
            {
                return map;
            }

            foreach (SkinnedMeshRenderer renderer in
                     model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                Mesh mesh = renderer.sharedMesh;
                if (mesh == null)
                {
                    continue;
                }

                Matrix4x4[] binds = mesh.bindposes;
                Transform[] bones = renderer.bones;
                int count = Mathf.Min(binds.Length, bones.Length);

                for (int i = 0; i < count; i++)
                {
                    if (bones[i] != null)
                    {
                        map[bones[i].name] = binds[i].rotation;
                    }
                }
            }

            return map;
        }

        public static Result Check(AnimationClip clip, Skeleton skeleton)
        {
            if (clip == null || skeleton == null || skeleton.IsEmpty)
            {
                return Unknown;
            }

            var paths = new HashSet<string>();

            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings(clip))
            {
                // An empty path is the Animator's own curves -- RootT and friends --
                // which address the object, not a bone, and always resolve.
                if (!string.IsNullOrEmpty(binding.path))
                {
                    paths.Add(binding.path);
                }
            }

            var result = new Result { Total = paths.Count };

            if (paths.Count == 0)
            {
                result.Verdict = Verdict.Fits;
                return result;
            }

            bool everyBoneExists = true;

            foreach (string path in paths)
            {
                if (skeleton.Paths.Contains(path))
                {
                    continue;
                }

                result.Missing++;
                everyBoneExists &= skeleton.Bones.Contains(LastSegment(path));
            }

            if (result.Missing == 0)
            {
                result.Verdict = Verdict.Fits;
                return result;
            }

            // Nothing matched, yet every bone is on the character somewhere: the rig is
            // right and the hierarchy above it is not. Usually an extra root in the
            // export, or one missing.
            if (result.Missing == result.Total && everyBoneExists)
            {
                result.Verdict = Verdict.Rerooted;
                result.Note =
                    $"every bone exists on {skeleton.Name}, but not at the path this clip " +
                    "names — the export is rooted differently, and the clip will not move it";
                return result;
            }

            result.Verdict = Verdict.Missing;
            result.Note =
                $"{result.Missing} of {result.Total} bone paths do not exist on {skeleton.Name} — " +
                "this is a different rig, and those curves will animate nothing";

            return result;
        }

        private static string LastSegment(string path)
        {
            int slash = path.LastIndexOf('/');
            return slash < 0 ? path : path.Substring(slash + 1);
        }
    }
}
