using System.Collections.Generic;
using System.IO;
using UnityEditor;

namespace FofuxoAnimationTools.Editor
{
    /// <summary>
    /// Which assets point at which.
    ///
    /// Unity only answers the forward question: AssetDatabase.GetDependencies tells
    /// you what an asset needs. The question you ask right before deleting something
    /// is the reverse one -- who needs *this*? -- and there is no API for it. So the
    /// index asks the forward question about every asset in the project once and
    /// turns the answers inside out.
    ///
    /// Building costs a few seconds, so the result is kept and patched as assets
    /// change rather than rebuilt per query. Both directions are stored: the forward
    /// map is what makes the patch cheap, because removing an asset's old
    /// contribution needs to know what it used to depend on.
    /// </summary>
    public static class AssetUsageIndex
    {
        /// <summary>Extensions that cannot hold a reference to another asset.</summary>
        private static readonly HashSet<string> Leaves = new HashSet<string>
        {
            ".cs", ".asmdef", ".asmref", ".md", ".txt", ".json", ".xml"
        };

        private static readonly Dictionary<string, string[]> DependenciesOf =
            new Dictionary<string, string[]>();

        private static readonly Dictionary<string, HashSet<string>> UsersByAsset =
            new Dictionary<string, HashSet<string>>();

        private static readonly string[] None = new string[0];

        private static bool built;

        public static bool IsBuilt => built;

        public static int IndexedAssets => DependenciesOf.Count;

        public static void Invalidate()
        {
            built = false;
            DependenciesOf.Clear();
            UsersByAsset.Clear();
        }

        /// <summary>
        /// Builds the index if it is not there yet. False means the user cancelled
        /// the scan, in which case nothing usable was produced and the caller has to
        /// give up rather than report "no dependents".
        /// </summary>
        public static bool EnsureBuilt()
        {
            return built || Build();
        }

        public static bool Build()
        {
            Invalidate();

            List<string> paths = ScannablePaths();
            bool cancelled = false;

            try
            {
                for (int i = 0; i < paths.Count; i++)
                {
                    // Repainting the bar per asset costs more than the scan itself.
                    if (i % 50 == 0 && EditorUtility.DisplayCancelableProgressBar(
                            "Reading asset references",
                            $"{i}/{paths.Count}",
                            (float)i / paths.Count))
                    {
                        cancelled = true;
                        break;
                    }

                    Add(paths[i]);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (cancelled)
            {
                Invalidate();
                return false;
            }

            built = true;
            return true;
        }

        /// <summary>Assets that hold a reference to <paramref name="assetPath"/>.</summary>
        public static IReadOnlyList<string> UsersOf(string assetPath)
        {
            if (!UsersByAsset.TryGetValue(assetPath, out HashSet<string> users))
            {
                return None;
            }

            var list = new List<string>(users);
            list.Sort();
            return list;
        }

        /// <summary>
        /// Users of any of the given assets, minus the given assets themselves. A
        /// batch being deleted together often references itself -- a controller going
        /// out along with the clips it plays -- and that is not a reason to stop.
        /// </summary>
        public static Dictionary<string, List<string>> UsersOfAny(IEnumerable<string> assetPaths)
        {
            var targets = new HashSet<string>(assetPaths);
            var result = new Dictionary<string, List<string>>();

            foreach (string target in targets)
            {
                var external = new List<string>();

                foreach (string user in UsersOf(target))
                {
                    if (!targets.Contains(user))
                    {
                        external.Add(user);
                    }
                }

                if (external.Count > 0)
                {
                    result[target] = external;
                }
            }

            return result;
        }

        private static List<string> ScannablePaths()
        {
            var paths = new List<string>();

            foreach (string path in AssetDatabase.GetAllAssetPaths())
            {
                if (IsScannable(path))
                {
                    paths.Add(path);
                }
            }

            return paths;
        }

        private static bool IsScannable(string path)
        {
            if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/"))
            {
                return false;
            }

            if (AssetDatabase.IsValidFolder(path))
            {
                return false;
            }

            return !Leaves.Contains(Path.GetExtension(path).ToLowerInvariant());
        }

        private static void Add(string path)
        {
            string[] dependencies = AssetDatabase.GetDependencies(path, false);
            DependenciesOf[path] = dependencies;

            foreach (string dependency in dependencies)
            {
                if (dependency == path)
                {
                    continue;
                }

                if (!UsersByAsset.TryGetValue(dependency, out HashSet<string> users))
                {
                    users = new HashSet<string>();
                    UsersByAsset[dependency] = users;
                }

                users.Add(path);
            }
        }

        /// <summary>
        /// Forgets what this asset points at, leaving untouched the record of who
        /// points at it. Re-importing an asset can change the first and never the
        /// second, and dropping the second would quietly turn a used asset into one
        /// that reads as safe to delete.
        /// </summary>
        private static void RemoveOutgoing(string path)
        {
            if (!DependenciesOf.TryGetValue(path, out string[] dependencies))
            {
                return;
            }

            foreach (string dependency in dependencies)
            {
                if (UsersByAsset.TryGetValue(dependency, out HashSet<string> users))
                {
                    users.Remove(path);
                }
            }

            DependenciesOf.Remove(path);
        }

        private static void RemoveAsset(string path)
        {
            RemoveOutgoing(path);
            UsersByAsset.Remove(path);
        }

        /// <summary>
        /// Keeps the index current without rebuilding it. This runs on every asset
        /// change in the project, so it has to stay cheap: one forward dependency
        /// read per changed path.
        /// </summary>
        internal static void Patch(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (!built)
            {
                return;
            }

            foreach (string path in deleted)
            {
                RemoveAsset(path);
            }

            for (int i = 0; i < moved.Length && i < movedFrom.Length; i++)
            {
                Rename(movedFrom[i], moved[i]);
            }

            foreach (string path in imported)
            {
                Refresh(path);
            }
        }

        /// <summary>
        /// Moving an asset keeps its GUID, so every reference to it survives -- but
        /// the index is keyed by path, and the assets pointing at it are not
        /// re-imported and so are never reported as changed. Their cached dependency
        /// lists still name the old path, and left alone they would answer "nobody
        /// uses this" the next time the moved asset came up for deletion.
        /// </summary>
        private static void Rename(string from, string to)
        {
            UsersByAsset.TryGetValue(from, out HashSet<string> users);

            RemoveAsset(from);
            Refresh(to);

            if (users == null || users.Count == 0)
            {
                return;
            }

            foreach (string user in users)
            {
                if (!DependenciesOf.TryGetValue(user, out string[] dependencies))
                {
                    continue;
                }

                for (int i = 0; i < dependencies.Length; i++)
                {
                    if (dependencies[i] == from)
                    {
                        dependencies[i] = to;
                    }
                }
            }

            if (UsersByAsset.TryGetValue(to, out HashSet<string> existing))
            {
                existing.UnionWith(users);
            }
            else
            {
                UsersByAsset[to] = users;
            }
        }

        private static void Refresh(string path)
        {
            if (!IsScannable(path))
            {
                return;
            }

            RemoveOutgoing(path);
            Add(path);
        }
    }

    internal sealed class AssetUsageIndexWatcher : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(
            string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            AssetUsageIndex.Patch(imported, deleted, moved, movedFrom);
        }
    }
}
