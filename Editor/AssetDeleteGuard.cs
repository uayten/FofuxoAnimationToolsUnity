using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FofuxoAnimationTools.Editor
{
    /// <summary>
    /// Stops a delete that would break something, and hands the decision back to the
    /// user with the list of what would break.
    ///
    /// Unity deletes an asset the moment it is asked. Anything that was pointing at
    /// it is left holding a null: an Animator state that plays nothing, a renderer
    /// that goes pink. Nothing is logged, and the damage is only found later, by
    /// noticing. Unreal refuses the delete instead and shows what refers to the
    /// asset, with the option to point those references somewhere else first.
    ///
    /// The decision has to be made *inside* OnWillDeleteAsset and answered with
    /// DidNotDelete, because the only other answer is FailedDelete and Unity
    /// responds to that with an error dialog of its own -- "some assets could not be
    /// deleted, make sure nothing is keeping a hook on them" -- which is both untrue
    /// and the first thing the user sees. So the asking happens here, synchronously,
    /// and a delete the user confirms is passed straight through to Unity as if
    /// nothing had interrupted it.
    ///
    /// That is the fallback, not the way in. The Delete key is caught before Unity
    /// starts, by ProjectWindowDeleteKey, where refusing is not needed and that
    /// dialog never appears. What arrives here is a delete from a menu entry, a
    /// script or another tool, and stopping one of those is the only option Unity
    /// offers.
    ///
    /// The menu entry used to be caught the same distance upstream, by a MenuItem
    /// on Unity's own Assets/Delete path. Unity 6000.6 no longer lets a managed
    /// item replace that entry -- registering one only logs "a menu item with the
    /// same name already exists" and leaves Unity's in place -- so the takeover
    /// was removed rather than kept as a warning that intercepts nothing.
    ///
    /// Unity calls this once per path, so the answer is worked out for the whole
    /// selection on the first call and remembered for the rest.
    /// </summary>
    public class AssetDeleteGuard : AssetModificationProcessor
    {
        private static readonly HashSet<string> Approved = new HashSet<string>();
        private static readonly HashSet<string> Refused = new HashSet<string>();

        private static bool bypass;
        private static bool decided;

        /// <summary>
        /// Deletes without asking. This is the way back out of the reference window --
        /// the user has seen the damage and chosen it -- and the only path allowed to
        /// leave broken references behind.
        /// </summary>
        public static bool DeleteWithoutGuard(IEnumerable<string> paths)
        {
            var list = new List<string>(paths);
            if (list.Count == 0)
            {
                return true;
            }

            bypass = true;

            try
            {
                var failed = new List<string>();
                AssetDatabase.DeleteAssets(list.ToArray(), failed);

                foreach (string path in failed)
                {
                    Debug.LogWarning($"Could not delete '{path}'.");
                }

                return failed.Count == 0;
            }
            finally
            {
                bypass = false;
            }
        }

        /// <summary>
        /// Runs the check and, when the answer is yes, carries out the delete itself.
        ///
        /// This is the way in that has no Unity delete behind it. The Delete key is
        /// caught before Unity acts on it, so there is no operation to refuse and
        /// nothing for Unity to report as failed -- the error dialog it shows when
        /// a delete is stopped never comes up, because no delete was ever started.
        ///
        /// A delete from a menu entry, a script or another tool still arrives
        /// through OnWillDeleteAsset, where stopping it is the only option and
        /// Unity says so in its own words.
        /// </summary>
        public static void RequestDelete(List<string> paths)
        {
            // The key may not have been consumed, in which case Unity deleted these a
            // frame ago through the ordinary path and there is nothing left to ask
            // about.
            paths.RemoveAll(path => AssetDatabase.LoadMainAssetAtPath(path) == null);

            if (paths.Count == 0)
            {
                return;
            }

            var going = new List<string>();
            foreach (string path in paths)
            {
                going.AddRange(AssetDeleteWindow.Expand(path));
            }

            if (!AssetUsageIndex.EnsureBuilt())
            {
                Debug.LogWarning(
                    "Nothing was deleted: the reference scan was cancelled, so there is " +
                    "no telling what uses those assets.");
                return;
            }

            Dictionary<string, List<string>> users = AssetUsageIndex.UsersOfAny(going);
            Dictionary<string, List<string>> named = AssetDeleteWindow.NamedUsers(going);

            if (users.Count > 0 || named.Count > 0)
            {
                AssetDeleteWindow.Open(paths);
                return;
            }

            // Unity's own "you cannot undo this" never ran, so this is the only
            // confirmation there is and it is not optional.
            bool go = EditorUtility.DisplayDialog(
                paths.Count == 1 ? "Delete asset" : "Delete assets",
                $"Nothing in the project references {(paths.Count == 1 ? "it" : "them")}.\n\n" +
                $"{Names(paths)}\n\nThere is no undo.",
                paths.Count == 1 ? "Delete" : $"Delete {paths.Count}",
                "Cancel");

            if (go)
            {
                DeleteWithoutGuard(paths);
            }
        }

        /// <summary>
        /// Whether anything in this set is a type the guard watches. Used to decide
        /// whether the Delete key is worth intercepting at all -- a selection of
        /// textures should keep Unity's own dialog rather than borrow this one.
        /// </summary>
        public static bool Watches(IEnumerable<string> paths)
        {
            foreach (string path in paths)
            {
                if (Going(path).Count > 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static AssetDeleteResult OnWillDeleteAsset(string path, RemoveAssetOptions options)
        {
            if (bypass || !FofuxoToolsSettings.GuardDeletes || string.IsNullOrEmpty(path))
            {
                return AssetDeleteResult.DidNotDelete;
            }

            if (Going(path).Count == 0)
            {
                return AssetDeleteResult.DidNotDelete;
            }

            if (!decided)
            {
                Decide(path);
            }

            return Refused.Contains(path)
                ? AssetDeleteResult.FailedDelete
                : AssetDeleteResult.DidNotDelete;
        }

        /// <summary>
        /// Works out the answer for everything going at once.
        ///
        /// Unity hands over one path at a time with no hint that eleven more are
        /// coming, so the batch is taken from the selection -- which is what a delete
        /// from the Project window is -- and the verdict cached for the calls that
        /// follow.
        /// </summary>
        private static void Decide(string trigger)
        {
            decided = true;
            EditorApplication.delayCall += Forget;

            List<string> batch = Batch(trigger);

            var going = new List<string>();
            foreach (string path in batch)
            {
                going.AddRange(AssetDeleteWindow.Expand(path));
            }

            if (!AssetUsageIndex.EnsureBuilt())
            {
                // The scan was cancelled, so there is no answer to whether this is
                // safe. Refusing is the only honest response.
                Debug.LogWarning(
                    "Nothing was deleted: the reference scan was cancelled, so there is " +
                    "no telling what uses those assets.");

                Refused.UnionWith(batch);
                return;
            }

            Dictionary<string, List<string>> users = AssetUsageIndex.UsersOfAny(going);
            Dictionary<string, List<string>> named = AssetDeleteWindow.NamedUsers(going);

            if (users.Count == 0 && named.Count == 0)
            {
                Clean(batch);
                return;
            }

            Held(batch);
        }

        private static List<string> Batch(string trigger)
        {
            var batch = new List<string>();
            var seen = new HashSet<string>();

            foreach (Object selected in Selection.GetFiltered(typeof(Object), SelectionMode.Assets))
            {
                string path = AssetDatabase.GetAssetPath(selected);

                if (!string.IsNullOrEmpty(path) && Going(path).Count > 0 && seen.Add(path))
                {
                    batch.Add(path);
                }
            }

            if (seen.Add(trigger))
            {
                batch.Add(trigger);
            }

            return batch;
        }

        /// <summary>Nothing references any of them. Say so, or say nothing.</summary>
        private static void Clean(List<string> batch)
        {
            if (!FofuxoToolsSettings.ConfirmSafeDeletes)
            {
                Approved.UnionWith(batch);
                Debug.Log(
                    $"Deleting {batch.Count} asset(s). Nothing in the project referenced them.\n" +
                    Names(batch));
                return;
            }

            bool go = EditorUtility.DisplayDialog(
                batch.Count == 1 ? "Delete asset" : "Delete assets",
                $"Nothing in the project references {(batch.Count == 1 ? "it" : "them")}.\n\n" +
                $"{Names(batch)}\n\nThere is no undo.",
                batch.Count == 1 ? "Delete" : $"Delete {batch.Count}",
                "Cancel");

            if (go)
            {
                Approved.UnionWith(batch);
            }
            else
            {
                Refused.UnionWith(batch);
            }
        }

        /// <summary>
        /// Something depends on them. Straight to the window that shows what, with no
        /// dialog in front of it: everything such a dialog could say, the window says
        /// better, and every button it could offer, the window already has.
        /// </summary>
        private static void Held(List<string> batch)
        {
            // TEMPORARY: whoever started this delete is in the stack trace.
            Debug.Log("FOFUXO TRACE: refusing a delete that got past the early catch.");

            Refused.UnionWith(batch);

            var roots = new List<string>(batch);

            // Unity has already said the assets could not be deleted, so the window
            // opens knowing it has that to answer for.
            EditorApplication.delayCall += () => AssetDeleteWindow.Open(roots, true);
        }

        private static void Forget()
        {
            decided = false;
            Approved.Clear();
            Refused.Clear();
        }

        /// <summary>
        /// Every asset that would stop existing. A folder is a single path to Unity
        /// but takes everything under it, and each of those is a reference somebody
        /// else might be holding.
        /// </summary>
        private static List<string> Going(string path)
        {
            var going = new List<string>();

            if (!AssetDatabase.IsValidFolder(path))
            {
                if (IsGuarded(path))
                {
                    going.Add(path);
                }

                return going;
            }

            string prefix = path.EndsWith("/") ? path : path + "/";
            bool guarded = false;

            foreach (string child in AssetDatabase.GetAllAssetPaths())
            {
                if (!child.StartsWith(prefix) || AssetDatabase.IsValidFolder(child))
                {
                    continue;
                }

                going.Add(child);
                guarded |= IsGuarded(child);
            }

            // A folder of nothing but scripts and textures is not what this is for.
            return guarded ? going : new List<string>();
        }

        private static bool IsGuarded(string path)
        {
            if (FofuxoToolsSettings.GuardEveryAssetType)
            {
                return true;
            }

            return FofuxoToolsSettings.GuardedExtensions.Contains(
                Path.GetExtension(path).ToLowerInvariant());
        }

        private static string Names(List<string> paths)
        {
            const int Shown = 12;

            var names = new List<string>();

            for (int i = 0; i < paths.Count && i < Shown; i++)
            {
                names.Add(Path.GetFileName(paths[i]));
            }

            if (paths.Count > Shown)
            {
                names.Add($"and {paths.Count - Shown} more");
            }

            return string.Join("\n", names);
        }
    }
}
