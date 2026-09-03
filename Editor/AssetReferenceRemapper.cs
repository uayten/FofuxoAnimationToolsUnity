using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace FofuxoAnimationTools.Editor
{
    /// <summary>
    /// Points every reference to one asset at another one instead.
    ///
    /// The obvious implementation is to rewrite the GUID in the YAML of every file
    /// that mentions it. That is fast and it is what most GUID-swapping scripts do,
    /// but it only works while the project is set to text serialisation, it has to
    /// guess the file ID of the sub-object being referenced, and a mistake corrupts
    /// an asset with no way back. This walks the object graph instead: load the
    /// referencing asset, iterate its serialised properties, and assign the new
    /// object to the ones that pointed at the old one. Slower, and correct by
    /// construction -- Unity writes the file, so whatever it writes is valid.
    ///
    /// Three kinds of holder need three different treatments. A prefab has to go
    /// through the prefab contents API or variants and nested prefabs come out
    /// wrong. A scene has to be opened. And a model's material assignments are not
    /// in the model at all -- they live on the importer, as the external object map,
    /// and are rewritten by remapping rather than by assignment.
    ///
    /// There is no undo. Source control is the undo.
    /// </summary>
    public static class AssetReferenceRemapper
    {
        /// <summary>
        /// References that identify an object rather than point at one. Swapping the
        /// script of a component, or the prefab an instance came from, would be a
        /// different operation entirely, and never the one that was asked for.
        /// </summary>
        private static readonly HashSet<string> Protected = new HashSet<string>
        {
            "m_Script", "m_CorrespondingSourceObject", "m_PrefabInstance", "m_PrefabAsset"
        };

        public sealed class Report
        {
            public int References;
            public int ChangedAssets;
            public readonly List<string> Changed = new List<string>();
            public readonly List<string> Failed = new List<string>();

            public override string ToString()
            {
                return $"{References} reference(s) in {ChangedAssets} asset(s)";
            }
        }

        /// <summary>
        /// Rewrites every reference listed in <paramref name="replacements"/> inside
        /// the given assets. A null value clears the reference instead of pointing it
        /// somewhere new, which is how "delete and leave nothing dangling" is done.
        ///
        /// With <paramref name="dryRun"/> the assets are read and counted but never
        /// written, so the same call that reports also performs.
        /// </summary>
        public static Report Replace(
            IReadOnlyDictionary<Object, Object> replacements,
            IEnumerable<string> paths,
            bool dryRun)
        {
            var report = new Report();
            if (replacements == null || replacements.Count == 0)
            {
                return report;
            }

            var scenes = new List<string>();
            var assets = new List<string>();

            foreach (string path in paths)
            {
                if (Path.GetExtension(path).ToLowerInvariant() == ".unity")
                {
                    scenes.Add(path);
                }
                else
                {
                    assets.Add(path);
                }
            }

            try
            {
                int total = assets.Count + scenes.Count;

                for (int i = 0; i < assets.Count; i++)
                {
                    Progress(dryRun, assets[i], i, total);
                    Record(report, assets[i], ReplaceInAsset(assets[i], replacements, dryRun));
                }

                if (scenes.Count > 0)
                {
                    ReplaceInScenes(scenes, replacements, dryRun, report, assets.Count, total);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (!dryRun && report.ChangedAssets > 0)
            {
                AssetDatabase.SaveAssets();
            }

            return report;
        }

        /// <summary>
        /// How many references the given assets hold, without touching anything.
        /// </summary>
        public static int Count(IReadOnlyDictionary<Object, Object> replacements, IEnumerable<string> paths)
        {
            return Replace(replacements, paths, true).References;
        }

        /// <summary>
        /// Pairs up everything inside one asset with its counterpart inside another,
        /// by type and name, and adds the pairs to <paramref name="map"/>.
        ///
        /// Swapping one FBX for another is almost never a matter of swapping one
        /// object. Nothing in the project points at the FBX itself: the prefab points
        /// at a Mesh inside it, the Animator at an Avatar inside it, the renderer at
        /// a Material inside it. Replacing the model means finding each of those in
        /// the new file, and the only thing the two files agree on is the name.
        ///
        /// A null <paramref name="to"/> maps everything to null, which is how a
        /// reference is cleared rather than redirected.
        ///
        /// Returns how many members found no counterpart -- objects whose references
        /// will end up empty even though a replacement was given, because the new
        /// file calls them something else. Worth saying out loud before it happens.
        /// </summary>
        public static int MapContents(string fromPath, Object to, IDictionary<Object, Object> map)
        {
            Object[] candidates = to == null
                ? new Object[0]
                : Contents(AssetDatabase.GetAssetPath(to));

            Object fromMain = AssetDatabase.LoadMainAssetAtPath(fromPath);
            Object[] members = Contents(fromPath);
            int unmatched = 0;

            foreach (Object member in members)
            {
                // Whether this is the only object of its type in the file it came
                // from. Pairing by type alone is only safe when there is nothing else
                // it could have meant, on either side.
                bool alone = Alone(members, member);

                Object counterpart = member == fromMain ? to : Counterpart(member, candidates, alone);

                if (counterpart == null && to != null)
                {
                    unmatched++;
                }

                map[member] = counterpart;
            }

            if (fromMain != null && !map.ContainsKey(fromMain))
            {
                map[fromMain] = to;
            }

            return unmatched;
        }

        private static bool Alone(Object[] members, Object member)
        {
            int count = 0;

            foreach (Object other in members)
            {
                if (other != null && other.GetType() == member.GetType() && ++count > 1)
                {
                    return false;
                }
            }

            return true;
        }

        private static Object[] Contents(string path)
        {
            if (string.IsNullOrEmpty(path) || Path.GetExtension(path).ToLowerInvariant() == ".unity")
            {
                return new Object[0];
            }

            var members = new List<Object>();

            foreach (Object member in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                // Unity keeps a hidden clip per take alongside the real ones, for the
                // importer's own preview. Nothing outside the model can reference it.
                if (member != null && !member.name.StartsWith("__preview__"))
                {
                    members.Add(member);
                }
            }

            return members.ToArray();
        }

        private static Object Counterpart(Object member, Object[] candidates, bool alone)
        {
            Object sameName = null;
            Object onlyOfType = null;
            int ofType = 0;

            foreach (Object candidate in candidates)
            {
                if (candidate == null)
                {
                    continue;
                }

                if (candidate.GetType() == member.GetType())
                {
                    ofType++;
                    onlyOfType = candidate;
                }

                if (candidate.name != member.name)
                {
                    continue;
                }

                if (candidate.GetType() == member.GetType())
                {
                    return candidate;
                }

                // A Mesh and a GameObject can share a name inside an FBX, so an exact
                // type match is worth preferring; this is the runner-up.
                if (sameName == null && member.GetType().IsInstanceOfType(candidate))
                {
                    sameName = candidate;
                }
            }

            if (sameName != null)
            {
                return sameName;
            }

            // Nothing matched by name, but there is exactly one object of this type on
            // each side. Two exports of the same character disagreeing on what to call
            // the mesh is ordinary; there being two meshes to confuse is not, and when
            // there are, the name goes back to deciding.
            return ofType == 1 && alone ? onlyOfType : null;
        }

        private static void Progress(bool dryRun, string path, int index, int total)
        {
            EditorUtility.DisplayProgressBar(
                dryRun ? "Counting references" : "Rewriting references",
                $"{Path.GetFileName(path)}  ({index + 1}/{total})",
                total == 0 ? 1f : (float)index / total);
        }

        private static void Record(Report report, string path, int hits)
        {
            if (hits <= 0)
            {
                return;
            }

            report.References += hits;
            report.ChangedAssets++;
            report.Changed.Add(path);
        }

        private static int ReplaceInAsset(
            string path, IReadOnlyDictionary<Object, Object> replacements, bool dryRun)
        {
            AssetImporter importer = AssetImporter.GetAtPath(path);
            int hits = ReplaceInExternalObjects(importer, replacements, dryRun);

            // Everything a model asset contains is generated on import. Assigning to
            // it would hold until the next reimport and no longer, so the external
            // object map above is the only real handle on it.
            if (importer is ModelImporter)
            {
                return hits;
            }

            if (Path.GetExtension(path).ToLowerInvariant() == ".prefab")
            {
                return hits + ReplaceInPrefab(path, replacements, dryRun);
            }

            foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                hits += ReplaceInObject(asset, replacements, dryRun);
            }

            return hits;
        }

        /// <summary>
        /// The material a model uses is a property of the importer, not of the model.
        /// Rewriting it means replacing the entry that maps the name baked into the
        /// FBX to a project asset, and then reimporting so the meshes pick it up.
        /// </summary>
        private static int ReplaceInExternalObjects(
            AssetImporter importer, IReadOnlyDictionary<Object, Object> replacements, bool dryRun)
        {
            if (importer == null)
            {
                return 0;
            }

            Dictionary<AssetImporter.SourceAssetIdentifier, Object> map = importer.GetExternalObjectMap();
            if (map == null || map.Count == 0)
            {
                return 0;
            }

            var hitList = new List<KeyValuePair<AssetImporter.SourceAssetIdentifier, Object>>();

            foreach (KeyValuePair<AssetImporter.SourceAssetIdentifier, Object> entry in map)
            {
                if (entry.Value != null && replacements.TryGetValue(entry.Value, out Object replacement))
                {
                    hitList.Add(new KeyValuePair<AssetImporter.SourceAssetIdentifier, Object>(
                        entry.Key, replacement));
                }
            }

            if (hitList.Count == 0 || dryRun)
            {
                return hitList.Count;
            }

            foreach (KeyValuePair<AssetImporter.SourceAssetIdentifier, Object> entry in hitList)
            {
                if (entry.Value == null)
                {
                    importer.RemoveRemap(entry.Key);
                }
                else
                {
                    importer.AddRemap(entry.Key, entry.Value);
                }
            }

            importer.SaveAndReimport();
            return hitList.Count;
        }

        private static int ReplaceInPrefab(
            string path, IReadOnlyDictionary<Object, Object> replacements, bool dryRun)
        {
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            if (root == null)
            {
                return 0;
            }

            try
            {
                int hits = ReplaceInHierarchy(root, replacements, dryRun);

                if (hits > 0 && !dryRun)
                {
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                }

                return hits;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>
        /// Scenes are handled as a group, and last, because each one has to be opened
        /// to be edited. The editor's own set of open scenes is captured first and put
        /// back afterwards, so a delete that happens to touch six scenes does not
        /// leave the user somewhere else entirely when it finishes.
        /// </summary>
        private static void ReplaceInScenes(
            List<string> paths,
            IReadOnlyDictionary<Object, Object> replacements,
            bool dryRun,
            Report report,
            int done,
            int total)
        {
            if (!dryRun && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                report.Failed.AddRange(paths);
                return;
            }

            SceneSetup[] setup = EditorSceneManager.GetSceneManagerSetup();

            try
            {
                for (int i = 0; i < paths.Count; i++)
                {
                    string path = paths[i];
                    Progress(dryRun, path, done + i, total);

                    Scene scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                    if (!scene.IsValid())
                    {
                        report.Failed.Add(path);
                        continue;
                    }

                    int hits = 0;
                    foreach (GameObject root in scene.GetRootGameObjects())
                    {
                        hits += ReplaceInHierarchy(root, replacements, dryRun);
                    }

                    if (hits > 0 && !dryRun)
                    {
                        EditorSceneManager.MarkSceneDirty(scene);
                        EditorSceneManager.SaveScene(scene);
                    }

                    Record(report, path, hits);
                }
            }
            finally
            {
                if (setup != null && setup.Length > 0)
                {
                    EditorSceneManager.RestoreSceneManagerSetup(setup);
                }
            }
        }

        private static int ReplaceInHierarchy(
            GameObject root, IReadOnlyDictionary<Object, Object> replacements, bool dryRun)
        {
            // Instances first: swapping the asset one stands on rebuilds the objects
            // underneath it, and there is no point walking them beforehand.
            int hits = ReplaceInstances(root, replacements, dryRun);

            foreach (Component component in root.GetComponentsInChildren<Component>(true))
            {
                // Null when a component's script is missing. Nothing to read there.
                if (component != null)
                {
                    hits += ReplaceInObject(component, replacements, dryRun);
                }
            }

            return hits;
        }

        /// <summary>
        /// Repoints the prefab instances that stand on an asset being replaced.
        ///
        /// This is the tie no reference swap can undo. A character prefab does not
        /// merely reference the model it was built from -- it *is* an instance of it,
        /// and that link lives in the prefab's own structure rather than in a field
        /// any walk can reach. Left alone, deleting the model leaves the prefab
        /// standing on nothing, with every hitbox and script added on top of it.
        ///
        /// Unity has the operation: ReplacePrefabAssetOfPrefabInstance, matching the
        /// old objects to the new ones by name and carrying the overrides across. A
        /// rig that gained or lost a bone keeps everything attached to the bones that
        /// still exist.
        ///
        /// With nothing to replace it with, the instance is unpacked instead. The
        /// prefab then owns the hierarchy outright, which is the difference between
        /// losing the link and losing the character.
        /// </summary>
        private static int ReplaceInstances(
            GameObject root, IReadOnlyDictionary<Object, Object> replacements, bool dryRun)
        {
            var instances = new List<GameObject>();

            foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
            {
                if (PrefabUtility.IsAnyPrefabInstanceRoot(transform.gameObject))
                {
                    instances.Add(transform.gameObject);
                }
            }

            int hits = 0;

            foreach (GameObject instance in instances)
            {
                if (instance == null)
                {
                    continue;
                }

                string path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(instance);
                Object source = string.IsNullOrEmpty(path)
                    ? null
                    : AssetDatabase.LoadMainAssetAtPath(path);

                if (source == null || !replacements.TryGetValue(source, out Object replacement))
                {
                    continue;
                }

                hits++;

                if (dryRun)
                {
                    continue;
                }

                if (replacement is GameObject asset)
                {
                    PrefabUtility.ReplacePrefabAssetOfPrefabInstance(
                        instance, asset, Replacing, InteractionMode.AutomatedAction);
                }
                else
                {
                    PrefabUtility.UnpackPrefabInstance(
                        instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                }
            }

            return hits;
        }

        private static PrefabReplacingSettings Replacing => new PrefabReplacingSettings
        {
            // By name, not by hierarchy: the whole reason the model is being replaced
            // is usually that the hierarchy changed.
            objectMatchMode = ObjectMatchMode.ByName,
            prefabOverridesOptions = PrefabOverridesOptions.KeepAllPossibleOverrides,
            changeRootNameToAssetName = false,
            logInfo = false
        };

        private static int ReplaceInObject(
            Object target, IReadOnlyDictionary<Object, Object> replacements, bool dryRun)
        {
            if (target == null)
            {
                return 0;
            }

            var serialized = new SerializedObject(target);
            SerializedProperty property = serialized.GetIterator();
            bool enterChildren = true;
            int hits = 0;

            while (property.Next(enterChildren))
            {
                // Descending into a string walks it one character at a time.
                enterChildren = property.propertyType != SerializedPropertyType.String;

                if (property.propertyType != SerializedPropertyType.ObjectReference)
                {
                    continue;
                }

                if (Protected.Contains(property.name))
                {
                    continue;
                }

                Object current = property.objectReferenceValue;
                if (current == null || !replacements.TryGetValue(current, out Object replacement))
                {
                    continue;
                }

                hits++;

                if (!dryRun)
                {
                    property.objectReferenceValue = replacement;
                }
            }

            if (hits > 0 && !dryRun)
            {
                serialized.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(target);
            }

            return hits;
        }
    }
}
