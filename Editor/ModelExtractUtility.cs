using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Formats.Fbx.Exporter;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace FofuxoAnimationTools.Editor
{
    /// <summary>
    /// Takes the geometry and rig from a model and writes them into a clean FBX made
    /// specifically for animation preview and avatar sharing.
    ///
    /// The export owns its mesh and skeleton instead of inheriting a gameplay prefab.
    /// Only transforms and renderers reach the FBX; colliders, scripts, rigidbodies,
    /// controllers, cameras and lights stay behind. Material slots keep only the names
    /// needed for importer remaps; Unity creates no materials for the generated model
    /// and points the slots at matching .mat assets already in the project.
    /// </summary>
    public static class ModelExtractUtility
    {
        public const string GeneratedPreviewLabel = "FofuxoAnimationPreview";

        public sealed class Options
        {
            public bool MatchMaterials = true;

            /// <summary>
            /// Put the skeleton back in its bind pose.
            ///
            /// The transforms in the source model come from the nodes in its file, and an
            /// FBX carrying animation usually has those left wherever the exporter
            /// stopped evaluating -- mid-stride, mid-swing. The mesh is unaffected,
            /// since its vertices are stored in bind pose regardless, so the result
            /// is an export whose rest pose is one arbitrary frame of one take.
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
            public readonly List<ModelMaterialMatcher.Slot> Materials =
                new List<ModelMaterialMatcher.Slot>();

            public int Vertices;

            /// <summary>The file's name, which is not necessarily the character's.</summary>
            public string FileName => Path.GetFileNameWithoutExtension(ModelPath);

            /// <summary>
            /// What to call the things that come out of this model.
            ///
            /// The file name is the wrong answer whenever the character travels inside a
            /// file named after something else — which is every animation export, where
            /// the file is called AS_Attack_Air_to_Floor_01_End_Seq and the character
            /// inside it is called Fergus. Naming the preview after the take would hide
            /// which character the exported rig belongs to.
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
            public string CharacterPath;

            /// <summary>How many of the written assets were already there.</summary>
            public int Replaced;

            /// <summary>
            /// Assets the clean FBX still needs from the source model. This is expected
            /// to stay empty because geometry and skeleton are physically exported.
            /// </summary>
            public readonly List<string> StillNeeded = new List<string>();

            /// <summary>Where the avatar landed, or null when there was none to write.</summary>
            public string AvatarPath;

            /// <summary>
            /// True when the clean FBX importer built an avatar where the source had none.
            /// </summary>
            public bool AvatarBuilt;
            public int MatchedMaterials;

            public bool SelfContained => CharacterPath != null && StillNeeded.Count == 0;
        }

        public static Plan Inspect(
            string modelPath,
            Dictionary<string, List<Material>> materialIndex = null)
        {
            var plan = new Plan
            {
                ModelPath = modelPath,
                Model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath)
            };

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
            InspectMaterials(plan, materialIndex ??
                ModelMaterialMatcher.Index(FofuxoToolsSettings.MaterialSearchFolder));

            return plan;
        }

        /// <summary>
        /// Records the material names the FBX Exporter will write. The exporter reads
        /// renderer assignments rather than the source importer's original slot names,
        /// so these names are also the identifiers the generated importer must remap.
        /// </summary>
        private static void InspectMaterials(
            Plan plan,
            Dictionary<string, List<Material>> index)
        {
            var seen = new HashSet<string>();

            foreach (Renderer renderer in plan.Model.GetComponentsInChildren<Renderer>(true))
            {
                foreach (Material material in renderer.sharedMaterials)
                {
                    if (material == null || !seen.Add(material.name))
                    {
                        continue;
                    }

                    var slot = new ModelMaterialMatcher.Slot
                    {
                        Name = material.name,
                        Current = material
                    };

                    string materialPath = AssetDatabase.GetAssetPath(material);
                    if (materialPath.EndsWith(".mat", System.StringComparison.OrdinalIgnoreCase))
                    {
                        slot.Suggestion = material;
                        slot.Note = "already uses this project material";
                    }
                    else
                    {
                        ModelMaterialMatcher.Resolve(slot, index, plan.ModelPath);
                    }

                    plan.Materials.Add(slot);
                }
            }

            plan.Materials.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
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

            try
            {
                ExportCharacter(plan, folder, options, result);

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

        /// <summary>
        /// Writes an object to a path, replacing what is already there instead of
        /// creating a second file beside it.
        ///
        /// Replacing means copying into the existing asset rather than deleting and
        /// recreating it. The asset keeps its GUID, so a material remap built by the
        /// last extraction stays valid and the second extraction is an update rather
        /// than a fork.
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

        /// <summary>
        /// Exports a temporary, stripped instance so the generated FBX can never inherit
        /// gameplay components from a character prefab. The temporary scene keeps the
        /// operation away from the user's open scene and is discarded afterwards.
        /// </summary>
        private static void ExportCharacter(
            Plan plan,
            string folder,
            Options options,
            Result result)
        {
            if (plan.Model == null)
            {
                return;
            }

            Scene preview = EditorSceneManager.NewPreviewScene();
            GameObject instance = null;
            string exportedFbx = Path.Combine(
                Path.GetTempPath(), $"FofuxoCharacter_{System.Guid.NewGuid():N}.fbx");

            try
            {
                instance = (GameObject)PrefabUtility.InstantiatePrefab(plan.Model, preview);
                if (instance == null)
                {
                    return;
                }

                if (PrefabUtility.IsPartOfPrefabInstance(instance))
                {
                    PrefabUtility.UnpackPrefabInstance(
                        instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                }

                instance.name = plan.Name;

                if (options.BindPose)
                {
                    RestoreBindPose(instance);
                }

                StripUnityComponents(instance);

                var exportOptions = new ExportModelOptions
                {
                    AnimateSkinnedMesh = false,
                    EmbedTextures = false,
                    ExportFormat = ExportFormat.Binary,
                    ExportUnrendered = true,
                    KeepInstances = true,
                    LODExportType = LODExportType.All,
                    ModelAnimIncludeOption = Include.Model,
                    ObjectPosition = ObjectPosition.Reset,
                    PreserveImportSettings = false,
                    UseMayaCompatibleNames = false
                };

                if (ModelExporter.ExportObject(exportedFbx, instance, exportOptions) == null)
                {
                    throw new IOException($"FBX Exporter could not write '{plan.Name}'.");
                }

                string path = OutputPath(plan, folder);
                string absolutePath = AbsoluteProjectPath(path);
                bool replacing = File.Exists(absolutePath);

                File.Copy(exportedFbx, absolutePath, true);
                AssetDatabase.ImportAsset(
                    path,
                    ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

                Avatar avatar = ConfigureImporter(plan, path, options.MatchMaterials, result);
                MarkGeneratedPreview(path);

                result.CharacterPath = path;
                result.AvatarPath = avatar == null ? null : path;
                result.AvatarBuilt = plan.Avatar == null && avatar != null;
                result.Written.Add(path);

                if (replacing)
                {
                    result.Replaced++;
                }

                VerifySourceIndependence(plan.ModelPath, result);
            }
            finally
            {
                if (instance != null)
                {
                    Object.DestroyImmediate(instance);
                }

                EditorSceneManager.ClosePreviewScene(preview);
                DeleteTemporaryFile(exportedFbx);
            }
        }

        private static void StripUnityComponents(GameObject root)
        {
            foreach (Component component in root.GetComponentsInChildren<Component>(true))
            {
                if (component is Transform ||
                    component is MeshFilter ||
                    component is MeshRenderer ||
                    component is SkinnedMeshRenderer)
                {
                    continue;
                }

                Object.DestroyImmediate(component);
            }
        }

        private static Avatar ConfigureImporter(
            Plan plan,
            string path,
            bool matchMaterials,
            Result result)
        {
            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer == null)
            {
                return null;
            }

            ModelImporter sourceImporter = ModelAsset.Importer(plan.ModelPath);
            bool humanoid = plan.Avatar != null && plan.Avatar.isHuman;

            if (sourceImporter != null && sourceImporter.animationType == ModelImporterAnimationType.Human)
            {
                humanoid = true;
            }

            importer.importAnimation = false;
            // External remaps are only resolved when the importer evaluates the FBX's
            // material descriptions. None keeps the map serialised but leaves the
            // renderers on Unity's grey default material.
            importer.materialImportMode =
                ModelImporterMaterialImportMode.ImportViaMaterialDescription;
            importer.animationType = humanoid
                ? ModelImporterAnimationType.Human
                : ModelImporterAnimationType.Generic;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;

            if (matchMaterials)
            {
                foreach (ModelMaterialMatcher.Slot slot in plan.Materials)
                {
                    if (slot.Suggestion == null)
                    {
                        continue;
                    }

                    importer.AddRemap(
                        new AssetImporter.SourceAssetIdentifier(typeof(Material), slot.Name),
                        slot.Suggestion);
                    result.MatchedMaterials++;
                }
            }

            if (humanoid &&
                sourceImporter != null &&
                sourceImporter.animationType == ModelImporterAnimationType.Human)
            {
                importer.humanDescription = sourceImporter.humanDescription;
            }

            importer.SaveAndReimport();

            foreach (Object member in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (member is Avatar avatar)
                {
                    return avatar;
                }
            }

            return null;
        }

        private static string OutputPath(Plan plan, string folder)
        {
            string wanted = $"{folder}/{Sanitise(plan.Name)}Preview.fbx";
            Object occupant = AssetDatabase.LoadMainAssetAtPath(wanted);

            return occupant == null || IsGeneratedPreview(wanted)
                ? wanted
                : AssetDatabase.GenerateUniqueAssetPath(wanted);
        }

        private static string AbsoluteProjectPath(string assetPath)
        {
            string project = Directory.GetParent(Application.dataPath).FullName;
            return Path.GetFullPath(Path.Combine(project, assetPath));
        }

        private static void MarkGeneratedPreview(string path)
        {
            Object asset = AssetDatabase.LoadMainAssetAtPath(path);
            if (asset == null)
            {
                return;
            }

            var labels = new List<string>(AssetDatabase.GetLabels(asset));
            if (!labels.Contains(GeneratedPreviewLabel))
            {
                labels.Add(GeneratedPreviewLabel);
                AssetDatabase.SetLabels(asset, labels.ToArray());
            }
        }

        public static bool IsGeneratedPreview(string path)
        {
            Object asset = AssetDatabase.LoadMainAssetAtPath(path);
            return asset != null &&
                   System.Array.IndexOf(AssetDatabase.GetLabels(asset), GeneratedPreviewLabel) >= 0;
        }

        private static void DeleteTemporaryFile(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
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

        /// <summary>
        /// Confirms that Unity did not retain a dependency on the source after importing
        /// the generated FBX. The geometry and skeleton should now live in the file.
        /// </summary>
        private static void VerifySourceIndependence(string modelPath, Result result)
        {
            if (result.CharacterPath == null)
            {
                return;
            }

            foreach (string dependency in AssetDatabase.GetDependencies(result.CharacterPath, true))
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
