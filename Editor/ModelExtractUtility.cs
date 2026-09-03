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
    /// Takes what a model file contains -- meshes, the avatar, the hierarchy -- and
    /// writes it out as ordinary Unity assets that no longer need the model file.
    ///
    /// The mesh and the avatar are copies: Instantiate produces a real, independent
    /// object, and once it is an asset the FBX has nothing more to say about it. The
    /// hierarchy is the harder half. A skinned character is not a mesh; it is two
    /// hundred transforms and a renderer holding an array of references into them, in
    /// the order the bindposes expect. That survives extraction only as a prefab,
    /// unpacked from the model so it owns its own copy of the hierarchy rather than
    /// inheriting one.
    ///
    /// Whether that is a good idea is a separate question from whether it works, and
    /// the honest answer differs per asset. See <see cref="Estimate"/>: with the
    /// project on text serialisation a mesh written as YAML runs several times the
    /// size of the FBX it came from, and extracting it also throws away the path by
    /// which a new export would have reached the project. The window says so; this
    /// class just does the work.
    /// </summary>
    public static class ModelExtractUtility
    {
        /// <summary>
        /// Bytes of YAML per vertex, measured on a 38,895-vertex skinned character
        /// that came out at 7.7 MB. Rough, and enough to tell 300 KB from 8 MB before
        /// the file is written rather than after.
        /// </summary>
        private const long BytesPerVertex = 198;

        public sealed class Options
        {
            public bool Meshes = true;
            public bool OneFilePerMesh = true;
            public bool Avatar = true;
            public bool Prefab = true;
            public bool MatchMaterials = true;

            /// <summary>
            /// Write out the embedded materials that matched nothing in the project.
            ///
            /// Without this, a model gets so far and no further. A character with six
            /// material slots and four real materials keeps two pointing at defaults
            /// that live inside the FBX, and those two are enough to tether the whole
            /// prefab to the file it was supposed to replace.
            /// </summary>
            public bool ExtractUnmatchedMaterials = true;

            /// <summary>
            /// Put the skeleton back in its bind pose.
            ///
            /// The transforms in the prefab come from the nodes in the FBX, and an
            /// FBX carrying animation usually has those left wherever the exporter
            /// stopped evaluating -- mid-stride, mid-swing. The mesh is unaffected,
            /// since its vertices are stored in bind pose regardless, so the result
            /// is a prefab whose rest pose is one arbitrary frame of one take.
            ///
            /// The bind pose is not lost: it is exactly what the mesh's bindposes
            /// describe, and putting the bones back is arithmetic.
            /// </summary>
            public bool BindPose = true;

            /// <summary>
            /// Also write out the animation takes the model carries, as .anim assets
            /// beside the rest of it.
            ///
            /// An export that brings the character along usually brings a take with it —
            /// which is the whole reason the file is named after an animation. Extracting
            /// the character and then going to another window for the clip inside the
            /// same file is two passes over one file.
            /// </summary>
            public bool Animations;
        }

        /// <summary>What a model holds, and what extracting it would cost.</summary>
        public sealed class Plan
        {
            public string ModelPath;
            public GameObject Model;
            public Avatar Avatar;
            public readonly List<Mesh> Meshes = new List<Mesh>();

            /// <summary>Materials embedded in the model, and the project match found.</summary>
            public readonly List<ModelMaterialMatcher.Slot> Materials =
                new List<ModelMaterialMatcher.Slot>();

            public int Vertices;
            public long ModelBytes;

            public long EstimatedBytes => Vertices * BytesPerVertex;

            /// <summary>The file's name, which is not necessarily the character's.</summary>
            public string FileName => Path.GetFileNameWithoutExtension(ModelPath);

            /// <summary>
            /// What to call the things that come out of this model.
            ///
            /// The file name is the wrong answer whenever the character travels inside a
            /// file named after something else — which is every animation export, where
            /// the file is called AS_Attack_Air_to_Floor_01_End_Seq and the character
            /// inside it is called Fergus. Extracting that produced a prefab and an
            /// avatar named after one take of animation.
            ///
            /// The skin knows better: the mesh a character is skinned to carries the
            /// character's name, put there by whoever built it. The biggest one wins when
            /// there are several, and the file name is only the fallback.
            /// </summary>
            public string Name
            {
                get
                {
                    if (Model == null)
                    {
                        return FileName;
                    }

                    string best = null;
                    int most = 0;

                    foreach (SkinnedMeshRenderer renderer in
                             Model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    {
                        Mesh mesh = renderer.sharedMesh;
                        if (mesh == null || mesh.vertexCount <= most)
                        {
                            continue;
                        }

                        most = mesh.vertexCount;
                        best = string.IsNullOrEmpty(mesh.name) ? renderer.name : mesh.name;
                    }

                    return string.IsNullOrEmpty(best) ? FileName : best;
                }
            }
        }

        public sealed class Result
        {
            public readonly List<string> Written = new List<string>();
            public string PrefabPath;

            /// <summary>How many of the written assets were already there.</summary>
            public int Replaced;

            /// <summary>
            /// Assets the prefab still needs from the model file. Empty means the FBX
            /// can go; anything in it means deleting the FBX would break the prefab.
            /// </summary>
            public readonly List<string> StillNeeded = new List<string>();

            /// <summary>Where the avatar landed, or null when there was none to write.</summary>
            public string AvatarPath;

            /// <summary>
            /// True when the avatar was built here rather than taken from the model.
            /// Worth saying out loud: the model's Rig tab still reads No Avatar, and the
            /// one that now exists is this package's doing.
            /// </summary>
            public bool AvatarBuilt;

            public bool SelfContained => PrefabPath != null && StillNeeded.Count == 0;
        }

        public static Plan Inspect(string modelPath, Dictionary<string, List<Material>> index)
        {
            var plan = new Plan
            {
                ModelPath = modelPath,
                Model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath)
            };

            if (File.Exists(modelPath))
            {
                plan.ModelBytes = new FileInfo(modelPath).Length;
            }

            foreach (Object member in AssetDatabase.LoadAllAssetsAtPath(modelPath))
            {
                if (member is Avatar avatar)
                {
                    plan.Avatar = avatar;
                }
                else if (member is Mesh mesh)
                {
                    plan.Meshes.Add(mesh);
                    plan.Vertices += mesh.vertexCount;
                }
            }

            plan.Meshes.Sort((a, b) => string.CompareOrdinal(a.name, b.name));

            foreach (Material material in EmbeddedMaterials(modelPath))
            {
                var slot = new ModelMaterialMatcher.Slot { Name = material.name, Current = material };
                slot.Suggestion = Best(material.name, index);
                slot.Note = slot.Suggestion == null
                    ? "no project material matches this name"
                    : $"matches {slot.Suggestion.name}";

                plan.Materials.Add(slot);
            }

            return plan;
        }

        /// <summary>
        /// Materials that are still part of the model. One already remapped to a
        /// project asset is not embedded and needs no matching -- it is already the
        /// answer this would have looked for.
        /// </summary>
        private static IEnumerable<Material> EmbeddedMaterials(string modelPath)
        {
            foreach (Object member in AssetDatabase.LoadAllAssetRepresentationsAtPath(modelPath))
            {
                if (member is Material material &&
                    AssetDatabase.GetAssetPath(material) == modelPath)
                {
                    yield return material;
                }
            }
        }

        private static Material Best(string name, Dictionary<string, List<Material>> index)
        {
            if (index == null ||
                !index.TryGetValue(ModelMaterialMatcher.Normalize(name), out List<Material> found) ||
                found.Count != 1)
            {
                return null;
            }

            return found[0];
        }

        public static Result Extract(Plan plan, string folder, Options options)
        {
            var result = new Result();
            var meshes = new Dictionary<Mesh, Mesh>();

            try
            {
                if (options.Meshes)
                {
                    ExtractMeshes(plan, folder, options.OneFilePerMesh, meshes, result);
                }

                Avatar avatar = options.Avatar ? ExtractAvatar(plan, folder, result) : null;

                if (options.ExtractUnmatchedMaterials)
                {
                    ExtractMaterials(plan, folder, result);
                }

                AssetDatabase.SaveAssets();

                if (options.Prefab)
                {
                    BuildPrefab(plan, folder, options, meshes, avatar, result);
                }

                if (options.Animations)
                {
                    ExtractAnimations(plan, folder, result);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                AssetDatabase.SaveAssets();
            }

            return result;
        }

        /// <summary>
        /// Writes the takes inside the model out as .anim assets, and compresses them the
        /// same way the extract-and-sync window does.
        ///
        /// A clip of the same name already in the folder is rewritten in place rather
        /// than duplicated, so a second run updates what the project references instead
        /// of leaving a numbered copy beside it.
        /// </summary>
        private static void ExtractAnimations(Plan plan, string folder, Result result)
        {
            var tolerances = ClipCompressionUtility.Tolerances.Stored;
            bool compress = EditorPrefs.GetBool("Fofuxo.Sync.Compress", true);

            foreach (AnimationClip take in AnimationClipSyncUtility.ClipsInModel(plan.ModelPath))
            {
                string path = $"{folder}/{Sanitise(take.name)}.anim";
                var existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);

                AnimationClip written;

                if (existing != null)
                {
                    AnimationClipSyncUtility.UpdateInPlace(
                        take, existing, AnimationClipSyncUtility.Preserve.None,
                        FofuxoToolsSettings.RootBone);

                    written = existing;
                    result.Replaced++;
                }
                else
                {
                    written = AnimationClipSyncUtility.Extract(take, folder);
                }

                if (written == null)
                {
                    continue;
                }

                result.Written.Add(AssetDatabase.GetAssetPath(written));

                if (compress)
                {
                    ClipCompressionUtility.Compress(written, tolerances);
                }
            }
        }

        private static void ExtractMeshes(
            Plan plan,
            string folder,
            bool oneFilePerMesh,
            Dictionary<Mesh, Mesh> meshes,
            Result result)
        {
            string shared = null;

            for (int i = 0; i < plan.Meshes.Count; i++)
            {
                Mesh source = plan.Meshes[i];

                EditorUtility.DisplayProgressBar(
                    "Extracting meshes", source.name, (float)i / plan.Meshes.Count);

                if (oneFilePerMesh || shared == null)
                {
                    string path = $"{folder}/{Sanitise(source.name)}.asset";
                    meshes[source] = Write(source, path, result);

                    if (!oneFilePerMesh)
                    {
                        shared = path;
                    }
                }
                else
                {
                    // Everything after the first goes in beside it, which is how a
                    // model with a dozen pieces stays one file instead of a dozen.
                    meshes[source] = WriteBeside(source, shared, result);
                }
            }
        }

        /// <summary>
        /// Writes an object to a path, replacing what is already there instead of
        /// creating a second file beside it.
        ///
        /// Replacing means copying into the existing asset rather than deleting and
        /// recreating it. The asset keeps its GUID, so the prefab built by the last
        /// extraction goes on pointing at the same mesh and the second extraction is
        /// an update rather than a fork. It is the same reason the clip sync rewrites
        /// clips in place, for the same gain.
        /// </summary>
        private static T Write<T>(T source, string path, Result result) where T : Object
        {
            Object occupant = AssetDatabase.LoadMainAssetAtPath(path);

            if (occupant is T existing && existing != source)
            {
                Overwrite(source, existing);

                result.Written.Add(path);
                result.Replaced++;

                return existing;
            }

            // Something of another type lives there. Overwriting it would be a
            // different operation than the one that was asked for.
            string target = occupant == null ? path : AssetDatabase.GenerateUniqueAssetPath(path);

            var copy = Object.Instantiate(source);
            copy.name = source.name;
            copy.hideFlags = HideFlags.None;

            AssetDatabase.CreateAsset(copy, target);
            result.Written.Add(target);

            return copy;
        }

        /// <summary>The same, for an object living inside another asset's file.</summary>
        private static T WriteBeside<T>(T source, string assetPath, Result result) where T : Object
        {
            foreach (Object member in AssetDatabase.LoadAllAssetsAtPath(assetPath))
            {
                if (member is T existing &&
                    existing.name == source.name &&
                    !AssetDatabase.IsMainAsset(existing))
                {
                    Overwrite(source, existing);
                    result.Replaced++;

                    return existing;
                }
            }

            var copy = Object.Instantiate(source);
            copy.name = source.name;
            copy.hideFlags = HideFlags.None;

            AssetDatabase.AddObjectToAsset(copy, assetPath);
            return copy;
        }

        private static void Overwrite(Object source, Object destination)
        {
            string name = destination.name;

            EditorUtility.CopySerialized(source, destination);

            // CopySerialized brings the name and hide flags along, and the source is a
            // sub-asset of a model. Neither belongs on the asset being written.
            destination.name = name;
            destination.hideFlags = HideFlags.None;

            EditorUtility.SetDirty(destination);
        }

        /// <summary>
        /// Copies out the embedded materials nothing in the project matched, and
        /// points the plan at the copies so the prefab uses them instead of the ones
        /// inside the model. This is what Unity's own Extract Materials does, applied
        /// only to the leftovers.
        /// </summary>
        private static void ExtractMaterials(Plan plan, string folder, Result result)
        {
            foreach (ModelMaterialMatcher.Slot slot in plan.Materials)
            {
                if (slot.Suggestion != null || slot.Current == null)
                {
                    continue;
                }

                slot.Suggestion = Write(
                    slot.Current, $"{folder}/{Sanitise(slot.Current.name)}.mat", result);

                slot.Note = "extracted, nothing in the project matched it";
            }
        }

        /// <summary>
        /// Leaves a model carrying no materials of its own.
        ///
        /// Every material still inside it is either matched to one the project
        /// already has or written out as a .mat beside them, and either way the
        /// importer is remapped onto the file rather than the copy inside itself.
        /// After this the model is geometry and a rig; the surfaces are assets, in
        /// one place, editable without reimporting anything.
        ///
        /// Returns how many slots were externalised.
        /// </summary>
        public static int ExternaliseMaterials(string modelPath, string folder)
        {
            // Any importer will do. Remapping a material slot is AssetImporter's, not
            // the model importer's, so a glb externalises its materials the same way.
            AssetImporter importer = AssetImporter.GetAtPath(modelPath);

            if (importer == null || !ModelAsset.Is(modelPath) || !AssetDatabase.IsValidFolder(folder))
            {
                return 0;
            }

            Dictionary<string, List<Material>> index =
                ModelMaterialMatcher.Index(FofuxoToolsSettings.MaterialSearchFolder);

            // Materialised before touching the importer: the list comes from the
            // imported result, and remapping is what changes it.
            var embedded = new List<Material>(EmbeddedMaterials(modelPath));
            var result = new Result();
            int changed = 0;

            foreach (Material material in embedded)
            {
                Material target = Best(material.name, index) ??
                                  Write(material, $"{folder}/{Sanitise(material.name)}.mat", result);

                if (target == null)
                {
                    continue;
                }

                importer.AddRemap(
                    new AssetImporter.SourceAssetIdentifier(typeof(Material), material.name), target);

                changed++;
            }

            if (changed > 0)
            {
                AssetDatabase.SaveAssets();
                importer.SaveAndReimport();
            }

            return changed;
        }

        private static Avatar ExtractAvatar(Plan plan, string folder, Result result)
        {
            Avatar avatar = plan.Avatar;

            if (avatar == null)
            {
                avatar = BuildAvatar(plan);
                result.AvatarBuilt = avatar != null;
            }

            if (avatar == null)
            {
                return null;
            }

            // Named after the character, not after the avatar object the importer made,
            // which inherits the file name -- and the file is often one animation take.
            Avatar written = Write(avatar, $"{folder}/{Sanitise(plan.Name)}Avatar.asset", result);
            result.AvatarPath = AssetDatabase.GetAssetPath(written);

            return written;
        }

        /// <summary>
        /// Builds a generic Avatar for a model that shipped without one.
        ///
        /// Unity's model importer creates the Avatar as part of importing; a scripted
        /// importer is under no obligation to, and UnityGLTF only does it for humanoid
        /// rigs. So a glb character arrives with a skeleton and no Avatar, and without
        /// one there is nothing for an animation file to copy from and no root motion.
        ///
        /// The rig is right there in the hierarchy, which is all a generic Avatar is
        /// made of. The root motion bone is the one this package already asks about in
        /// Preferences, used only when the model actually has a bone by that name --
        /// naming one that does not exist produces an invalid Avatar.
        /// </summary>
        private static Avatar BuildAvatar(Plan plan)
        {
            if (plan.Model == null ||
                plan.Model.GetComponentInChildren<SkinnedMeshRenderer>(true) == null)
            {
                return null;
            }

            string rootBone = FofuxoToolsSettings.RootBone;

            if (!string.IsNullOrEmpty(rootBone) &&
                plan.Model.transform.Find(rootBone) == null &&
                !HasDescendant(plan.Model.transform, rootBone))
            {
                rootBone = string.Empty;
            }

            Avatar built = AvatarBuilder.BuildGenericAvatar(plan.Model, rootBone);

            if (built == null || !built.isValid)
            {
                return null;
            }

            built.name = $"{plan.Name}Avatar";
            return built;
        }

        /// <summary>
        /// Gives the prefab an Animator carrying the avatar.
        ///
        /// Without one the avatar is an asset nothing points at, and the prefab cannot
        /// play a clip -- which makes "extract the character" stop one step short of a
        /// character. An Animator already on the model is reused rather than doubled.
        /// </summary>
        private static void Attach(GameObject prefabRoot, Avatar avatar)
        {
            if (prefabRoot == null || avatar == null)
            {
                return;
            }

            // Not ?? -- a missing component comes back as Unity's fake null, which the
            // null-coalescing operator takes for a real object and hands straight on.
            Animator animator = prefabRoot.GetComponent<Animator>();

            if (animator == null)
            {
                animator = prefabRoot.AddComponent<Animator>();
            }

            animator.avatar = avatar;
            animator.applyRootMotion = true;
        }

        private static bool HasDescendant(Transform parent, string name)
        {
            foreach (Transform child in parent.GetComponentsInChildren<Transform>(true))
            {
                if (child.name == name)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The hierarchy, as a prefab that owns it.
        ///
        /// Instantiating the model gives a prefab instance whose every transform is
        /// still inherited from the FBX; unpacking it completely turns those into real
        /// objects belonging to the new prefab. Only then is rewiring the renderers
        /// worth anything, because only then is there something to rewire that will
        /// still be there once the model file is gone.
        ///
        /// It happens in a preview scene so that extracting a character does not
        /// quietly dirty whatever the user had open.
        /// </summary>
        private static void BuildPrefab(
            Plan plan,
            string folder,
            Options options,
            Dictionary<Mesh, Mesh> meshes,
            Avatar avatar,
            Result result)
        {
            if (plan.Model == null)
            {
                return;
            }

            Scene preview = EditorSceneManager.NewPreviewScene();
            GameObject instance = null;

            try
            {
                instance = (GameObject)PrefabUtility.InstantiatePrefab(plan.Model, preview);
                if (instance == null)
                {
                    return;
                }

                PrefabUtility.UnpackPrefabInstance(
                    instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);

                // Either switch produces materials the prefab should point at: one
                // finds them in the project, the other writes them out of the model.
                Rewire(instance, meshes, avatar, plan,
                    options.MatchMaterials || options.ExtractUnmatchedMaterials);

                if (options.BindPose)
                {
                    RestoreBindPose(instance);
                }

                // SaveAsPrefabAsset overwrites whatever is at the path and the file
                // keeps its GUID, so a second extraction updates the prefab the scene
                // is already using rather than leaving it behind next to a new one.
                string path = $"{folder}/{Sanitise(plan.Name)}.prefab";

                if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null)
                {
                    result.Replaced++;
                }

                PrefabUtility.SaveAsPrefabAsset(instance, path);

                result.PrefabPath = path;
                result.Written.Add(path);
            }
            finally
            {
                if (instance != null)
                {
                    Object.DestroyImmediate(instance);
                }

                EditorSceneManager.ClosePreviewScene(preview);
            }

            Verify(plan.ModelPath, result);
        }

        private static void Rewire(
            GameObject root,
            Dictionary<Mesh, Mesh> meshes,
            Avatar avatar,
            Plan plan,
            bool matchMaterials)
        {
            foreach (SkinnedMeshRenderer renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (renderer.sharedMesh != null &&
                    meshes.TryGetValue(renderer.sharedMesh, out Mesh replacement))
                {
                    renderer.sharedMesh = replacement;
                }

                if (matchMaterials)
                {
                    renderer.sharedMaterials = Matched(renderer.sharedMaterials, plan);
                }
            }

            foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh != null &&
                    meshes.TryGetValue(filter.sharedMesh, out Mesh replacement))
                {
                    filter.sharedMesh = replacement;
                }
            }

            if (matchMaterials)
            {
                foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>(true))
                {
                    renderer.sharedMaterials = Matched(renderer.sharedMaterials, plan);
                }
            }

            if (avatar != null)
            {
                Animator[] animators = root.GetComponentsInChildren<Animator>(true);

                foreach (Animator animator in animators)
                {
                    animator.avatar = avatar;
                }

                // A model imported with Avatar Definition: No Avatar arrives with no
                // Animator at all, so there was nothing for the avatar to be handed to
                // and the prefab came out unable to play a clip. Give it one.
                if (animators.Length == 0)
                {
                    Attach(root, avatar);
                }
            }
        }

        /// <summary>
        /// Puts every bone back where it stood when the mesh was bound to it.
        ///
        /// A bindpose is the matrix taking the renderer's space into a bone's local
        /// space at bind time, which is the same statement backwards: the bone's
        /// world matrix at bind time is the renderer's, times the inverse of its
        /// bindpose. So the pose was never lost -- it is in the mesh, and the bones
        /// simply have to be told.
        ///
        /// Parents are placed before children. Setting a world position moves
        /// everything below it, so a bone placed before its parent would be moved
        /// again afterwards and end up somewhere else entirely.
        /// </summary>
        private static void RestoreBindPose(GameObject root)
        {
            foreach (SkinnedMeshRenderer renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                Mesh mesh = renderer.sharedMesh;
                Transform[] bones = renderer.bones;

                if (mesh == null || bones == null || bones.Length == 0)
                {
                    continue;
                }

                Matrix4x4[] bindposes = mesh.bindposes;
                if (bindposes == null || bindposes.Length == 0)
                {
                    continue;
                }

                var order = new List<int>();
                for (int i = 0; i < bones.Length && i < bindposes.Length; i++)
                {
                    if (bones[i] != null)
                    {
                        order.Add(i);
                    }
                }

                order.Sort((a, b) => Depth(bones[a]).CompareTo(Depth(bones[b])));

                Matrix4x4 rendererToWorld = renderer.transform.localToWorldMatrix;

                foreach (int i in order)
                {
                    Matrix4x4 world = rendererToWorld * bindposes[i].inverse;
                    Transform bone = bones[i];

                    bone.SetPositionAndRotation(world.GetColumn(3), world.rotation);

                    Vector3 parent = bone.parent != null ? bone.parent.lossyScale : Vector3.one;
                    Vector3 wanted = world.lossyScale;

                    if (Mathf.Abs(parent.x) > 1e-6f && Mathf.Abs(parent.y) > 1e-6f && Mathf.Abs(parent.z) > 1e-6f)
                    {
                        bone.localScale = new Vector3(
                            wanted.x / parent.x, wanted.y / parent.y, wanted.z / parent.z);
                    }
                }
            }
        }

        private static int Depth(Transform transform)
        {
            int depth = 0;

            for (Transform parent = transform.parent; parent != null; parent = parent.parent)
            {
                depth++;
            }

            return depth;
        }

        private static Material[] Matched(Material[] materials, Plan plan)
        {
            var result = new Material[materials.Length];

            for (int i = 0; i < materials.Length; i++)
            {
                result[i] = materials[i];

                if (materials[i] == null)
                {
                    continue;
                }

                foreach (ModelMaterialMatcher.Slot slot in plan.Materials)
                {
                    if (slot.Current == materials[i] && slot.Suggestion != null)
                    {
                        result[i] = slot.Suggestion;
                        break;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Whether the prefab still needs the model file. This is the only answer that
        /// decides whether the FBX can actually be deleted, and it is worth asking the
        /// asset database rather than assuming the rewiring caught everything.
        /// </summary>
        private static void Verify(string modelPath, Result result)
        {
            if (result.PrefabPath == null)
            {
                return;
            }

            AssetDatabase.ImportAsset(result.PrefabPath, ImportAssetOptions.ForceUpdate);

            foreach (string dependency in AssetDatabase.GetDependencies(result.PrefabPath, true))
            {
                if (dependency == modelPath)
                {
                    result.StillNeeded.Add(modelPath);
                    break;
                }
            }
        }

        private static string Sanitise(string name)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(invalid, '_');
            }

            return name;
        }
    }
}
