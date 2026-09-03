using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;

namespace FofuxoAnimationTools.Editor
{
    /// <summary>
    /// Finds the damage already done: references to assets that are not there any
    /// more.
    ///
    /// Every delete that went through before anything was watching left holes, and
    /// Unity does not report them. A missing clip on an Animator state, a missing
    /// material on a renderer, a missing script on a component -- each is a GUID in
    /// a file that resolves to nothing, and each shows up as a blank field in an
    /// inspector nobody has opened lately.
    ///
    /// The search is over the text of the files rather than the loaded objects. That
    /// reaches scenes without opening them and prefabs without instantiating them,
    /// and it costs one pass over the project. It also means it only works while the
    /// project is serialised as text, which the scan checks before promising
    /// anything.
    /// </summary>
    public static class BrokenReferenceScanner
    {
        private static readonly Regex Reference =
            new Regex(@"guid:\s*([0-9a-f]{32})", RegexOptions.Compiled);

        /// <summary>
        /// GUIDs that resolve to nothing yet are perfectly fine. Unity's own built-in
        /// assets live outside the asset database and answer with an empty path.
        /// </summary>
        private static readonly HashSet<string> Builtin = new HashSet<string>
        {
            "00000000000000000000000000000000",
            "0000000000000000e000000000000000",
            "0000000000000000f000000000000000",
            "0000000000000000d000000000000000"
        };

        private static readonly HashSet<string> Searchable = new HashSet<string>
        {
            ".asset", ".prefab", ".unity", ".controller", ".overridecontroller",
            ".mat", ".playable", ".anim", ".shadergraph", ".inputactions", ".meta"
        };

        public sealed class Hole
        {
            public string Guid;

            /// <summary>Files holding a reference to it, and how many each holds.</summary>
            public readonly Dictionary<string, int> Holders = new Dictionary<string, int>();

            public int Total
            {
                get
                {
                    int count = 0;
                    foreach (int hits in Holders.Values)
                    {
                        count += hits;
                    }

                    return count;
                }
            }
        }

        public static bool ProjectIsText =>
            EditorSettings.serializationMode != SerializationMode.ForceBinary;

        /// <summary>
        /// Every dangling GUID in the project, grouped by the asset that is gone --
        /// which is the useful grouping, because one deleted file is usually the
        /// reason for a dozen holes.
        /// </summary>
        public static List<Hole> Scan()
        {
            var holes = new Dictionary<string, Hole>();
            var known = new Dictionary<string, bool>();

            List<string> files = Candidates();

            try
            {
                for (int i = 0; i < files.Count; i++)
                {
                    if (i % 20 == 0 && EditorUtility.DisplayCancelableProgressBar(
                            "Looking for broken references",
                            $"{i}/{files.Count}",
                            (float)i / files.Count))
                    {
                        break;
                    }

                    Scan(files[i], holes, known);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            var result = new List<Hole>(holes.Values);
            result.Sort((a, b) => b.Total.CompareTo(a.Total));

            return result;
        }

        private static List<string> Candidates()
        {
            var files = new List<string>();

            foreach (string path in AssetDatabase.GetAllAssetPaths())
            {
                if (path.StartsWith("Assets/") &&
                    Searchable.Contains(Path.GetExtension(path).ToLowerInvariant()))
                {
                    files.Add(path);
                }
            }

            return files;
        }

        private static void Scan(string file, Dictionary<string, Hole> holes, Dictionary<string, bool> known)
        {
            string text;

            try
            {
                text = File.ReadAllText(file);
            }
            catch (IOException)
            {
                return;
            }

            foreach (Match match in Reference.Matches(text))
            {
                string guid = match.Groups[1].Value;

                if (Builtin.Contains(guid))
                {
                    continue;
                }

                if (!known.TryGetValue(guid, out bool exists))
                {
                    exists = !string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(guid));
                    known[guid] = exists;
                }

                if (exists)
                {
                    continue;
                }

                if (!holes.TryGetValue(guid, out Hole hole))
                {
                    hole = new Hole { Guid = guid };
                    holes[guid] = hole;
                }

                hole.Holders.TryGetValue(file, out int count);
                hole.Holders[file] = count + 1;
            }
        }
    }
}
