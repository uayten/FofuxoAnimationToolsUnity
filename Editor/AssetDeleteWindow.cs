using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FofuxoAnimationTools.Editor
{
    /// <summary>
    /// What comes up instead of the delete: the assets that were about to go, who is
    /// holding on to them, and the three ways out.
    ///
    /// The order of the buttons is the order of preference. Replacing is what you
    /// almost always mean when a file is being swapped for a newer one. Clearing is
    /// for when the thing is genuinely going away and you would rather the holes be
    /// empty than broken. Forcing is for when you know something the index does not.
    /// </summary>
    public sealed class AssetDeleteWindow : EditorWindow
    {
        private sealed class Entry
        {
            public string Path;
            public Object Asset;
            public List<string> Users;

            /// <summary>
            /// Assets that spell the name out rather than hold a reference. These
            /// cannot be redirected -- rewriting a string means knowing what it means
            /// to whoever reads it -- so they are shown and left alone.
            /// </summary>
            public List<string> NameUsers = new List<string>();

            public Object Replacement;
            public bool Expanded;
        }

        private readonly List<Entry> entries = new List<Entry>();
        private readonly List<string> deletePaths = new List<string>();

        private const string AdoptKey = "Fofuxo.Delete.AdoptModelSettings";
        private const string CopyKey = "Fofuxo.Delete.CopyReplacement";

        private Vector2 scroll;
        private bool deleting;

        /// <summary>
        /// Whether Unity had already started the delete and was told it failed, which
        /// is the only way to stop one from OnWillDeleteAsset and costs an error
        /// dialog claiming something is keeping a hook on the assets. Nothing is, and
        /// the window says so rather than leaving that as the last word.
        /// </summary>
        private bool stopped;

        /// <summary>
        /// Opens on a set of paths the user tried to delete. Everything under a
        /// folder in that set counts as going too.
        ///
        /// <paramref name="stopped"/> says the delete had already begun and was
        /// refused, so Unity has shown its own error and the window has that to
        /// answer for.
        /// </summary>
        public static void Open(IEnumerable<string> paths, bool stopped = false)
        {
            var going = new List<string>();
            var roots = new List<string>();

            foreach (string path in paths)
            {
                roots.Add(path);
                going.AddRange(Expand(path));
            }

            Dictionary<string, List<string>> users = AssetUsageIndex.UsersOfAny(going);
            Dictionary<string, List<string>> named = NamedUsers(going);

            if (users.Count == 0 && named.Count == 0)
            {
                AssetDeleteGuard.DeleteWithoutGuard(roots);
                return;
            }

            Show(roots, users, named, true, "Delete Assets", stopped);
        }

        internal static Dictionary<string, List<string>> NamedUsers(List<string> going)
        {
            return FofuxoToolsSettings.ScanNamesOnDelete
                ? NameReferenceScanner.Find(going, new HashSet<string>(going))
                : new Dictionary<string, List<string>>();
        }

        private static void Show(
            List<string> roots,
            Dictionary<string, List<string>> users,
            Dictionary<string, List<string>> named,
            bool deleting,
            string title,
            bool stopped)
        {
            AssetDeleteWindow window = GetWindow<AssetDeleteWindow>(true);
            window.titleContent = new GUIContent(title);
            window.minSize = new Vector2(560f, 340f);
            window.Fill(roots, users, named, deleting, stopped);
            window.Focus();
        }

        internal static IEnumerable<string> Expand(string path)
        {
            if (!AssetDatabase.IsValidFolder(path))
            {
                yield return path;
                yield break;
            }

            string prefix = path.EndsWith("/") ? path : path + "/";

            foreach (string child in AssetDatabase.GetAllAssetPaths())
            {
                if (child.StartsWith(prefix) && !AssetDatabase.IsValidFolder(child))
                {
                    yield return child;
                }
            }
        }

        private void Fill(
            List<string> roots,
            Dictionary<string, List<string>> users,
            Dictionary<string, List<string>> named,
            bool isDelete,
            bool wasStopped)
        {
            entries.Clear();
            deletePaths.Clear();
            deletePaths.AddRange(roots);
            deleting = isDelete;
            stopped = wasStopped;
            scroll = Vector2.zero;

            var all = new SortedSet<string>(users.Keys);
            all.UnionWith(named.Keys);

            foreach (string path in all)
            {
                users.TryGetValue(path, out List<string> holders);
                named.TryGetValue(path, out List<string> callers);

                holders = holders ?? new List<string>();
                callers = callers ?? new List<string>();

                holders.Sort();
                callers.Sort();

                entries.Add(new Entry
                {
                    Path = path,
                    Asset = AssetDatabase.LoadMainAssetAtPath(path),
                    Users = holders,
                    NameUsers = callers,
                    Expanded = entries.Count < 4
                });
            }
        }

        private void OnGUI()
        {
            if (entries.Count == 0)
            {
                EditorGUILayout.HelpBox("Nothing to show.", MessageType.Info);
                return;
            }

            DrawHeader();

            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (Entry entry in entries)
            {
                DrawEntry(entry);
            }
            EditorGUILayout.EndScrollView();

            DrawButtons();
        }

        private void DrawHeader()
        {
            int references = 0;
            foreach (Entry entry in entries)
            {
                references += entry.Users.Count;
            }

            int named = 0;
            foreach (Entry entry in entries)
            {
                named += entry.NameUsers.Count;
            }

            EditorGUILayout.Space(6);

            if (deleting)
            {
                // What Unity's "some assets could not be deleted" was trying to say,
                // said by the only window that knows why.
                EditorGUILayout.HelpBox(
                    stopped
                        ? "Nothing has been deleted. Unity had already begun, and the only " +
                          "way to stop a delete under way is to report it as failed -- which " +
                          "is what it just called an asset it could not delete. Nothing is " +
                          "keeping a hook on these files; what is holding them is below."
                        : "Nothing has been deleted. What is holding these assets is below, " +
                          "and none of it changes until one of the buttons at the bottom is " +
                          "pressed.",
                    MessageType.Warning);
            }

            EditorGUILayout.LabelField(
                deleting
                    ? $"{entries.Count} asset(s) about to be deleted are used {references} time(s)."
                    : $"{entries.Count} asset(s) are used {references} time(s).",
                EditorStyles.boldLabel);

            if (deleting)
            {
                EditorGUILayout.LabelField(
                    "Deleting them as they are leaves those references empty.",
                    EditorStyles.miniLabel);
            }

            if (named > 0)
            {
                EditorGUILayout.HelpBox(
                    $"{named} of those are the name written out as text, not a reference. " +
                    "Nothing can redirect them: the asset database never knew about them, " +
                    "and rewriting a string means knowing what it means to whatever reads " +
                    "it. Replacing under the same name is the only thing that keeps them " +
                    "working.",
                    MessageType.Warning);
            }

            EditorGUILayout.Space(4);
        }

        private void DrawEntry(Entry entry)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();

            entry.Expanded = EditorGUILayout.Foldout(
                entry.Expanded,
                new GUIContent(
                    entry.NameUsers.Count > 0
                        ? $"{Path.GetFileName(entry.Path)}   ({entry.Users.Count} + {entry.NameUsers.Count} by name)"
                        : $"{Path.GetFileName(entry.Path)}   ({entry.Users.Count})",
                    AssetPreview.GetMiniThumbnail(entry.Asset)),
                true);

            if (deleting)
            {
                EditorGUIUtility.labelWidth = 90f;
                entry.Replacement = EditorGUILayout.ObjectField(
                    new GUIContent("replace with"),
                    entry.Replacement,
                    entry.Asset != null ? entry.Asset.GetType() : typeof(Object),
                    false,
                    GUILayout.Width(280f));
            }

            EditorGUILayout.EndHorizontal();

            if (entry.Expanded)
            {
                EditorGUI.indentLevel++;

                foreach (string user in entry.Users)
                {
                    DrawUser(user);
                }

                if (entry.NameUsers.Count > 0)
                {
                    EditorGUILayout.LabelField(
                        "spells the name out — cannot be redirected:",
                        EditorStyles.miniLabel);

                    foreach (string user in entry.NameUsers)
                    {
                        DrawUser(user);
                    }
                }

                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndVertical();
        }

        private static void DrawUser(string path)
        {
            Object asset = AssetDatabase.LoadMainAssetAtPath(path);

            var content = new GUIContent(path, AssetPreview.GetMiniThumbnail(asset));
            Rect rect = EditorGUILayout.GetControlRect();

            if (GUI.Button(EditorGUI.IndentedRect(rect), content, EditorStyles.label))
            {
                EditorGUIUtility.PingObject(asset);
            }
        }

        private void DrawButtons()
        {
            EditorGUILayout.Space(4);

            if (!deleting)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Close", GUILayout.Width(100f)))
                {
                    Close();
                }
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.Space(4);
                return;
            }

            int assigned = 0;
            foreach (Entry entry in entries)
            {
                if (entry.Replacement != null)
                {
                    assigned++;
                }
            }

            DrawAdopt();

            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button(
                    new GUIContent(
                        "Fill by name",
                        "Look for an asset of the same type and name elsewhere in the project."),
                    GUILayout.Width(110f)))
            {
                FillByName();
            }

            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Cancel", GUILayout.Width(80f)))
            {
                Close();
            }

            if (GUILayout.Button(
                    new GUIContent("Force delete", "Delete and leave the references broken."),
                    GUILayout.Width(100f)))
            {
                ForceDelete();
            }

            if (GUILayout.Button(
                    new GUIContent("Clear and delete", "Empty every reference first, then delete."),
                    GUILayout.Width(120f)))
            {
                Apply(false);
            }

            using (new EditorGUI.DisabledScope(assigned == 0))
            {
                if (GUILayout.Button(
                        new GUIContent(
                            "Replace and delete",
                            assigned == 0
                                ? "Assign a replacement above first."
                                : $"Redirect the references to the {assigned} replacement(s), then delete."),
                        GUILayout.Width(140f)))
                {
                    Apply(true);
                }
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(4);
        }

        /// <summary>
        /// Offered only when a model is being replaced by a model, which is the one
        /// case where the replacement arrives needing to be told how to read itself.
        /// </summary>
        private void DrawAdopt()
        {
            Entry model = null;

            foreach (Entry entry in entries)
            {
                if (entry.Replacement != null &&
                    ModelSettingsTransfer.Applies(entry.Path, AssetDatabase.GetAssetPath(entry.Replacement)))
                {
                    model = entry;
                    break;
                }
            }

            if (model == null)
            {
                return;
            }

            string replacement = AssetDatabase.GetAssetPath(model.Replacement);

            bool copy = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    "Copy the replacement in beside the old model",
                    "The project ends up using a file of its own rather than the export " +
                    "the animations came out of — which is going to be deleted, and would " +
                    "take the character with it."),
                EditorPrefs.GetBool(CopyKey, true));

            EditorPrefs.SetBool(CopyKey, copy);

            if (copy)
            {
                EditorGUILayout.LabelField(
                    "   " + ModelSettingsTransfer.CopyTarget(model.Path, replacement) +
                    ",  materials into " + ModelSettingsTransfer.MaterialFolder(model.Path),
                    EditorStyles.miniLabel);
            }

            bool adopt = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    "Give it the old model's import settings",
                    "Rig, avatar, compression, mesh options and the material remaps, " +
                    "copied across as a Preset before anything is redirected — so the " +
                    "avatar the prefab needs exists by the time the references are " +
                    "rewritten."),
                EditorPrefs.GetBool(AdoptKey, true));

            EditorPrefs.SetBool(AdoptKey, adopt);

            if (adopt)
            {
                EditorGUILayout.LabelField(
                    "   " + ModelSettingsTransfer.Describe(model.Path, replacement),
                    EditorStyles.miniLabel);
            }
        }

        /// <summary>
        /// The reimport case: the new file is already in the project under the same
        /// name in a different folder, and pairing them up by hand four hundred times
        /// is not a workflow.
        /// </summary>
        private void FillByName()
        {
            var going = new HashSet<string>();
            foreach (string root in deletePaths)
            {
                foreach (string path in Expand(root))
                {
                    going.Add(path);
                }
            }

            int found = 0;

            foreach (Entry entry in entries)
            {
                if (entry.Replacement != null || entry.Asset == null)
                {
                    continue;
                }

                string name = Path.GetFileNameWithoutExtension(entry.Path);
                Object match = null;
                bool ambiguous = false;

                foreach (string guid in AssetDatabase.FindAssets($"\"{name}\""))
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);

                    if (going.Contains(path) ||
                        Path.GetFileNameWithoutExtension(path) != name)
                    {
                        continue;
                    }

                    Object candidate = AssetDatabase.LoadMainAssetAtPath(path);
                    if (candidate == null || candidate.GetType() != entry.Asset.GetType())
                    {
                        continue;
                    }

                    ambiguous |= match != null;
                    match = candidate;
                }

                if (match != null && !ambiguous)
                {
                    entry.Replacement = match;
                    found++;
                }
            }

            Debug.Log($"Filled in {found} replacement(s) by name.");
        }

        private void ForceDelete()
        {
            if (!EditorUtility.DisplayDialog(
                    "Force delete",
                    $"{entries.Count} asset(s) will be deleted and the references to them " +
                    "left broken.\n\nThere is no undo.",
                    "Delete anyway",
                    "Cancel"))
            {
                return;
            }

            AssetDeleteGuard.DeleteWithoutGuard(deletePaths);
            Close();
        }

        /// <summary>
        /// Gets the replacement ready to be the thing the project uses.
        ///
        /// Three steps, in an order that matters. The copy comes first, because
        /// pointing the project at an export that is about to be deleted only defers
        /// the problem. The settings come next, because it is that reimport which
        /// builds the avatar the references are about to be pointed at. The materials
        /// come last, because they are the only part that has to be told where the
        /// others already live.
        /// </summary>
        private void Prepare()
        {
            bool copying = EditorPrefs.GetBool(CopyKey, true);
            bool adopting = EditorPrefs.GetBool(AdoptKey, true);

            if (!copying && !adopting)
            {
                return;
            }

            foreach (Entry entry in entries)
            {
                if (entry.Replacement == null)
                {
                    continue;
                }

                string path = AssetDatabase.GetAssetPath(entry.Replacement);

                if (!ModelSettingsTransfer.Applies(entry.Path, path))
                {
                    continue;
                }

                string materials = ModelSettingsTransfer.MaterialFolder(entry.Path);

                if (copying)
                {
                    string made = ModelSettingsTransfer.Copy(entry.Path, path);

                    if (made == null)
                    {
                        Debug.LogWarning($"Could not copy '{path}'; using it where it is.");
                    }
                    else
                    {
                        path = made;
                    }
                }

                if (adopting)
                {
                    ModelSettingsTransfer.Adopt(entry.Path, path);
                }

                ModelExtractUtility.ExternaliseMaterials(path, materials);

                // Each reimport built new objects; the old handle is stale.
                entry.Replacement = AssetDatabase.LoadMainAssetAtPath(path);
            }
        }

        private void Apply(bool replacing)
        {
            // Before anything is redirected: the replacement has to be reimported
            // with the old model's settings first, because that reimport is what
            // creates the avatar the references are about to be pointed at.
            if (replacing)
            {
                Prepare();
            }

            var map = new Dictionary<Object, Object>();
            var affected = new HashSet<string>();
            int orphans = 0;

            foreach (Entry entry in entries)
            {
                Object replacement = replacing ? entry.Replacement : null;

                orphans += AssetReferenceRemapper.MapContents(entry.Path, replacement, map);
                affected.UnionWith(entry.Users);
            }

            string question = replacing
                ? $"References in {affected.Count} asset(s) will be redirected, then " +
                  $"{deletePaths.Count} asset(s) deleted."
                : $"References in {affected.Count} asset(s) will be emptied, then " +
                  $"{deletePaths.Count} asset(s) deleted.";

            int named = 0;
            foreach (Entry entry in entries)
            {
                named += entry.NameUsers.Count;
            }

            if (named > 0)
            {
                question += $"\n\n{named} reference(s) are the name written out as text " +
                            "and will not be touched. Those break unless the replacement " +
                            "carries the same name.";
            }

            if (orphans > 0)
            {
                question += $"\n\n{orphans} object(s) inside the assets being replaced have no " +
                            "counterpart with the same name in the replacement, and the references " +
                            "to those will be emptied rather than redirected.";
            }

            if (!EditorUtility.DisplayDialog("Delete assets", question + "\n\nThere is no undo.", "Go ahead", "Cancel"))
            {
                return;
            }

            AssetReferenceRemapper.Report report = AssetReferenceRemapper.Replace(map, affected, false);
            AssetDeleteGuard.DeleteWithoutGuard(deletePaths);

            Debug.Log($"Rewrote {report} and deleted {deletePaths.Count} asset(s).");
            Close();
        }
    }
}
