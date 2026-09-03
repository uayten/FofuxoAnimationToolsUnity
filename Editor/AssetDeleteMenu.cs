using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FofuxoAnimationTools.Editor
{
    /// <summary>
    /// Takes over Assets/Delete, which is the entry both the Assets menu and the
    /// Project window's right-click menu use.
    ///
    /// Same reason as the Delete key. OnWillDeleteAsset is asked after the delete is
    /// under way, and the only way to stop one from there is to report it as failed --
    /// which Unity answers with "Some assets could not be deleted. Make sure nothing
    /// is keeping a hook on them", untrue and the first thing the user sees. Here
    /// nothing has started, so there is nothing to refuse and nothing to report.
    ///
    /// A MenuItem written on a path Unity already uses replaces Unity's rather than
    /// sitting beside it: the menu holds one Delete afterwards and it is this one.
    /// The cost is where it sits. Unity's own items share a single priority and order
    /// among themselves by the order they were registered, which a managed item
    /// cannot join, so Delete moves to the end of that first block instead of staying
    /// under Open.
    ///
    /// A selection with nothing the guard watches is handed to Unity's own delete,
    /// confirmation and all, rather than borrowing this one for a case it was not
    /// written for.
    /// </summary>
    internal static class AssetDeleteMenu
    {
        [MenuItem("Assets/Delete", false, 20)]
        private static void Delete()
        {
            // TEMPORARY
            Debug.Log("FOFUXO TRACE: the Assets/Delete menu item ran.");

            var assets = new List<Object>();
            List<string> paths = Selected(assets);

            if (paths.Count == 0)
            {
                return;
            }

            if (!FofuxoToolsSettings.GuardDeletes ||
                !FofuxoToolsSettings.InterceptDelete ||
                !AssetDeleteGuard.Watches(paths))
            {
                DeleteTheUnityWay(assets, paths);
                return;
            }

            AssetDeleteGuard.RequestDelete(paths);
        }

        [MenuItem("Assets/Delete", true, 20)]
        private static bool CanDelete()
        {
            return Selected(null).Count > 0;
        }

        /// <summary>
        /// The selected assets, as paths, and their objects alongside when asked for.
        ///
        /// Anything outside Assets -- a package, a built-in -- is left out entirely:
        /// Unity refuses to delete those, and the guard has nothing to say about a
        /// delete that is not going to happen.
        /// </summary>
        private static List<string> Selected(List<Object> assets)
        {
            var paths = new List<string>();

            foreach (Object selected in Selection.GetFiltered(typeof(Object), SelectionMode.Assets))
            {
                string path = AssetDatabase.GetAssetPath(selected);

                if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets"))
                {
                    continue;
                }

                paths.Add(path);
                assets?.Add(selected);
            }

            return paths;
        }

        /// <summary>
        /// Unity's own delete, reached through the same internal call its menu item
        /// makes, so the confirmation, the trash and the version control checks are
        /// the ones the user already knows.
        /// </summary>
        private static void DeleteTheUnityWay(List<Object> assets, List<string> paths)
        {
            if (UnityDelete(assets))
            {
                return;
            }

            // The internal call is not where it was. Ask and delete by hand rather
            // than leave the menu item doing nothing.
            bool go = EditorUtility.DisplayDialog(
                paths.Count == 1 ? "Delete selected asset?" : "Delete selected assets?",
                $"{Names(paths)}\n\nYou cannot undo this action.",
                "Delete",
                "Cancel");

            if (!go)
            {
                return;
            }

            var failed = new List<string>();
            AssetDatabase.MoveAssetsToTrash(paths.ToArray(), failed);

            foreach (string path in failed)
            {
                Debug.LogWarning($"Could not delete '{path}'.");
            }
        }

        /// <summary>
        /// Calls UnityEditor.ProjectWindowUtil.DeleteAssets, which is internal and
        /// takes a list of ids whose type has changed across versions -- int once,
        /// EntityId now -- so the list is built from whatever the parameter asks for.
        /// Returns false when the method is not where it used to be, which is the
        /// signal to fall back rather than to give up.
        /// </summary>
        private static bool UnityDelete(List<Object> assets)
        {
            try
            {
                Type utility = typeof(AssetDatabase).Assembly.GetType("UnityEditor.ProjectWindowUtil");
                MethodInfo delete = utility?.GetMethod(
                    "DeleteAssets",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

                if (delete == null)
                {
                    return false;
                }

                ParameterInfo[] parameters = delete.GetParameters();

                if (parameters.Length != 2 ||
                    !typeof(IList).IsAssignableFrom(parameters[0].ParameterType) ||
                    !parameters[0].ParameterType.IsGenericType ||
                    parameters[1].ParameterType != typeof(bool))
                {
                    return false;
                }

                Type id = parameters[0].ParameterType.GetGenericArguments()[0];
                var ids = (IList)Activator.CreateInstance(parameters[0].ParameterType);

                foreach (Object asset in assets)
                {
                    object value = Id(id, asset.GetInstanceID());

                    if (value == null)
                    {
                        return false;
                    }

                    ids.Add(value);
                }

                delete.Invoke(null, new object[] { ids, true });
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Unity's own delete could not be called: {exception.Message}");
                return false;
            }
        }

        private static object Id(Type type, int instanceId)
        {
            if (type == typeof(int))
            {
                return instanceId;
            }

            MethodInfo conversion = type.GetMethod(
                "op_Implicit",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(int) },
                null);

            if (conversion != null)
            {
                return conversion.Invoke(null, new object[] { instanceId });
            }

            ConstructorInfo constructor = type.GetConstructor(new[] { typeof(int) });

            return constructor?.Invoke(new object[] { instanceId });
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
