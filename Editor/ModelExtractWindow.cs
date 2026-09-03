using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace FofuxoAnimationTools.Editor
{
    /// <summary>
    /// The front end for <see cref="ModelExtractUtility"/>.
    ///
    /// The window exists to argue with the idea before carrying it out. Extracting a
    /// model works, and for an avatar it is a straightforwardly good trade; for a
    /// mesh it is usually not, and the reason is a number the user cannot see
    /// anywhere else. So the estimate sits next to the button, and the comparison
    /// against the FBX is made before the extraction rather than discovered in the
    /// next commit.
    /// </summary>
    public sealed class ModelExtractWindow : EditorWindow
    {
        private readonly List<ModelExtractUtility.Plan> plans = new List<ModelExtractUtility.Plan>();
        private readonly ModelExtractUtility.Options options = new ModelExtractUtility.Options();

        private const string DestinationKey = "Fofuxo.Extract.Destination";

        private Object source;
        private string destination = string.Empty;
        private Vector2 scroll;
        private bool scanned;

        private void OnEnable()
        {
            destination = FolderField.Remembered(DestinationKey);
            EditorApplication.delayCall += Rescan;
        }

        private void OnDisable()
        {
            EditorApplication.delayCall -= Rescan;
        }

        /// <summary>
        /// Reads the models again. There is no button for this: the scan is what the
        /// window is showing, so it happens when the window opens and whenever an
        /// input that changes the answer changes.
        /// </summary>
        private void Rescan()
        {
            if (this == null)
            {
                return;
            }

            if (source == null)
            {
                plans.Clear();
                scanned = false;
                Repaint();
                return;
            }

            Scan();
            Repaint();
        }

        [MenuItem("Window/Fofuxo's Animation Tools/Extract & Sync Models")]
        public static void Open()
        {
            ModelExtractWindow window = GetWindow<ModelExtractWindow>();
            window.titleContent = new GUIContent("Extract & Sync Models");
            window.minSize = new Vector2(640f, 460f);
            window.Show();
        }

        [MenuItem("Assets/Fofuxo's Animation Tools/Extract Mesh and Avatar...", false, 31)]
        private static void FromSelection()
        {
            ModelExtractWindow window = GetWindow<ModelExtractWindow>();
            window.titleContent = new GUIContent("Extract & Sync Models");
            window.minSize = new Vector2(640f, 460f);
            window.source = Selection.activeObject;
            window.destination = FolderField.Remembered(DestinationKey);
            window.Rescan();
            window.Show();
        }

        [MenuItem("Assets/Fofuxo's Animation Tools/Extract Mesh and Avatar...", true)]
        private static bool HasModelSelected()
        {
            string path = AssetDatabase.GetAssetPath(Selection.activeObject);
            return !string.IsNullOrEmpty(path) &&
                   (AssetDatabase.IsValidFolder(path) || ModelAsset.Is(path));
        }

        private void OnGUI()
        {
            EditorGUIUtility.labelWidth = 170f;
            EditorGUILayout.Space(6);

            DrawSetup();

            scroll = EditorGUILayout.BeginScrollView(scroll);

            if (!scanned)
            {
                EditorGUILayout.HelpBox("Pick a model or a folder of models.", MessageType.Info);
            }

            foreach (ModelExtractUtility.Plan plan in plans)
            {
                DrawPlan(plan);
            }

            EditorGUILayout.EndScrollView();

            DrawFooter();
        }

        private void DrawSetup()
        {
            EditorGUI.BeginChangeCheck();

            source = EditorGUILayout.ObjectField(
                new GUIContent("Models", "An FBX, or a folder of them."),
                source, typeof(Object), false);

            bool changed = EditorGUI.EndChangeCheck();

            string before = destination;

            destination = FolderField.Draw(
                new GUIContent(
                    "Destination folder",
                    "Where the extracted assets are written. Left empty, each model's " +
                    "assets go beside the model itself. Remembered between runs."),
                destination,
                DestinationKey,
                "next to each model");

            if (changed || destination != before)
            {
                Rescan();
            }

            EditorGUILayout.Space(4);

            options.Meshes = EditorGUILayout.Toggle(
                new GUIContent("Extract meshes", "Each mesh becomes a standalone .asset."),
                options.Meshes);

            using (new EditorGUI.DisabledScope(!options.Meshes))
            {
                options.OneFilePerMesh = EditorGUILayout.Toggle(
                    new GUIContent(
                        "One file per mesh",
                        "On: a file each, as they sit in the model. Off: all of them in " +
                        "one file, the first as the main asset."),
                    options.OneFilePerMesh);
            }

            options.Avatar = EditorGUILayout.Toggle(
                new GUIContent(
                    "Extract avatar",
                    "The rig, as its own asset. This is the one that is worth doing on " +
                    "its own: it is small, and every animation FBX can then point at it " +
                    "with Copy From Other Avatar instead of at the character file. A model " +
                    "that has no avatar of its own gets a generic one built from its " +
                    "hierarchy, and the prefab's Animator is pointed at it."),
                options.Avatar);

            options.Animations = EditorGUILayout.Toggle(
                new GUIContent(
                    "Extract animations too",
                    "Writes the takes inside the model out as .anim assets in the same " +
                    "folder, compressed, updating any clip of that name already there " +
                    "instead of leaving a copy beside it. An export that carries the " +
                    "character usually carries a take as well, and this saves going to " +
                    "the other window for the other half of the same file."),
                options.Animations);

            options.Prefab = EditorGUILayout.Toggle(
                new GUIContent(
                    "Build a self-contained prefab",
                    "The bone hierarchy and the renderers, unpacked from the model so " +
                    "they belong to the prefab. Without this the extracted mesh has no " +
                    "skeleton to be skinned to and the model file cannot be deleted."),
                options.Prefab);

            using (new EditorGUI.DisabledScope(!options.Prefab))
            {
                options.MatchMaterials = EditorGUILayout.Toggle(
                    new GUIContent(
                        "Match materials by name",
                        "Point the prefab's renderers at the project's own materials, " +
                        "matched with the naming convention stripped off both sides."),
                    options.MatchMaterials);

                options.BindPose = EditorGUILayout.Toggle(
                    new GUIContent(
                        "Put the skeleton in bind pose",
                        "The transforms in a model come from the nodes in the FBX, and " +
                        "an FBX carrying animation has those left wherever the exporter " +
                        "stopped -- mid-stride, mid-swing. The mesh is unaffected. This " +
                        "puts the bones back where the mesh was bound to them."),
                    options.BindPose);

                options.ExtractUnmatchedMaterials = EditorGUILayout.Toggle(
                    new GUIContent(
                        "Extract the leftovers",
                        "Write out the embedded materials that matched nothing. Without " +
                        "this, a single unmatched slot keeps the whole prefab tied to the " +
                        "model file — which is usually a default material on a slot that " +
                        "never got one."),
                    options.ExtractUnmatchedMaterials);
            }

            EditorGUILayout.Space(4);
        }

        private void Scan()
        {
            plans.Clear();
            scanned = true;

            Dictionary<string, List<Material>> index =
                ModelMaterialMatcher.Index(FofuxoToolsSettings.MaterialSearchFolder);

            List<string> models = AnimationClipSyncUtility.ModelsUnder(AssetDatabase.GetAssetPath(source));

            try
            {
                for (int i = 0; i < models.Count; i++)
                {
                    EditorUtility.DisplayProgressBar("Reading models", models[i], (float)i / models.Count);
                    plans.Add(ModelExtractUtility.Inspect(models[i], index));
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private void DrawPlan(ModelExtractUtility.Plan plan)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.LabelField(plan.Name, EditorStyles.boldLabel);

            EditorGUILayout.LabelField(
                $"{plan.Meshes.Count} mesh(es), {plan.Vertices:N0} vertices, " +
                $"avatar: {(plan.Avatar != null ? plan.Avatar.name : "none")}",
                EditorStyles.miniLabel);

            // Asking for an avatar the model does not have is a tick box that quietly
            // does nothing, and the reason is one tab away in the importer.
            if (options.Avatar && plan.Avatar == null)
            {
                EditorGUILayout.HelpBox(
                    "This model has no avatar, so none will be written. Its Rig tab is set " +
                    "to Avatar Definition: No Avatar — set that to Create From This Model " +
                    "and there will be one to extract.",
                    MessageType.Warning);
            }

            DrawSize(plan);

            if (plan.Materials.Count > 0)
            {
                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField("Embedded materials", EditorStyles.miniBoldLabel);

                foreach (ModelMaterialMatcher.Slot slot in plan.Materials)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(slot.Name, GUILayout.Width(200f));
                    slot.Suggestion = (Material)EditorGUILayout.ObjectField(
                        slot.Suggestion, typeof(Material), false, GUILayout.Width(200f));
                    EditorGUILayout.LabelField(slot.Note, EditorStyles.miniLabel);
                    EditorGUILayout.EndHorizontal();
                }
            }
            else
            {
                EditorGUILayout.LabelField(
                    "No embedded materials — the model already points at project materials.",
                    EditorStyles.miniLabel);
            }

            EditorGUILayout.EndVertical();
        }

        /// <summary>
        /// The number the decision turns on. A mesh written as YAML is several times
        /// the FBX it came from, and nowhere in Unity does that get said out loud
        /// until it is already in the commit.
        /// </summary>
        private void DrawSize(ModelExtractUtility.Plan plan)
        {
            if (!options.Meshes || plan.Vertices == 0)
            {
                return;
            }

            float estimate = plan.EstimatedBytes / 1048576f;
            float model = plan.ModelBytes / 1048576f;

            string line = $"Extracted meshes: about {estimate:F1} MB, against {model:F1} MB for the model file.";

            if (plan.EstimatedBytes > plan.ModelBytes * 1.5f)
            {
                EditorGUILayout.HelpBox(
                    line + "\n\nText serialisation is why. The mesh is the same data " +
                    "written as YAML, and it will land in the repository at that size " +
                    "and re-land at that size every time it is re-exported.",
                    MessageType.Warning);
            }
            else
            {
                EditorGUILayout.LabelField(line, EditorStyles.miniLabel);
            }
        }

        private void DrawFooter()
        {
            EditorGUILayout.Space(2);

            bool ready = scanned && plans.Count > 0;

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();

            using (new EditorGUI.DisabledScope(!ready))
            {
                if (GUILayout.Button("Extract", GUILayout.Width(140f), GUILayout.Height(24f)))
                {
                    Extract();
                }
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(4);
        }

        private void Extract()
        {
            // No folder chosen means beside the model, which is where you would have
            // put it anyway, and is the only answer that still works when the models
            // being extracted live in different places.
            string chosen = string.IsNullOrEmpty(destination) ? null : destination;

            if (chosen != null && !AssetDatabase.IsValidFolder(chosen))
            {
                EditorUtility.DisplayDialog(
                    "Extract model", $"'{chosen}' is not a folder in the project.", "OK");
                return;
            }

            var written = new List<string>();
            var tethered = new List<string>();
            var withoutAvatar = new List<string>();
            var builtAvatar = new List<string>();
            int replaced = 0;

            foreach (ModelExtractUtility.Plan plan in plans)
            {
                string folder = chosen ?? System.IO.Path
                    .GetDirectoryName(plan.ModelPath)
                    .Replace('\\', '/');

                ModelExtractUtility.Result result =
                    ModelExtractUtility.Extract(plan, folder, options);

                written.AddRange(result.Written);
                replaced += result.Replaced;

                if (result.PrefabPath != null && !result.SelfContained)
                {
                    tethered.Add(plan.Name);
                }

                // Asked of what was written, not of what the model brought. A model with
                // Avatar Definition: No Avatar has none to extract, and one gets built
                // from its hierarchy instead -- reporting that as "no avatar was written"
                // was telling the user to go fix something that is already handled.
                if (options.Avatar && result.AvatarPath == null)
                {
                    withoutAvatar.Add(plan.Name);
                }
                else if (result.AvatarBuilt)
                {
                    builtAvatar.Add(plan.Name);
                }
            }

            AssetDatabase.Refresh();

            // What went right goes to the console. A modal with a warning triangle on
            // it reads as "something broke" before a word of it has been read, and
            // spending that alarm on "it worked" is the surest way to have the one
            // that matters clicked through.
            Debug.Log(
                $"Wrote {written.Count} asset(s), {replaced} of them over what was already " +
                "there, keeping its GUID." +
                (options.Prefab && tethered.Count == 0
                    ? " The prefabs no longer reference the model files."
                    : string.Empty) +
                (builtAvatar.Count > 0
                    ? $" {builtAvatar.Count} model(s) had no avatar of their own, so a generic " +
                      "one was built from the hierarchy and put on the prefab's Animator: " +
                      string.Join(", ", builtAvatar) + "."
                    : string.Empty));

            var trouble = new List<string>();

            if (withoutAvatar.Count > 0)
            {
                trouble.Add(
                    $"No avatar was written for {string.Join(", ", withoutAvatar)}: the model " +
                    "does not have one. Its Rig tab is set to Avatar Definition: No Avatar, " +
                    "so there is nothing to extract until that is Create From This Model.");
            }

            if (options.Prefab && tethered.Count > 0)
            {
                trouble.Add(
                    $"{tethered.Count} prefab(s) still reference the model file and would " +
                    $"break if it were deleted: {string.Join(", ", tethered)}.");
            }

            if (!options.Prefab)
            {
                trouble.Add(
                    "No prefab was built, so the model file is still the only thing holding " +
                    "the skeleton these meshes are skinned to. It cannot be deleted.");
            }

            if (trouble.Count > 0)
            {
                string message = string.Join("\n\n", trouble);

                Debug.LogWarning(message);
                EditorUtility.DisplayDialog("Extract & Sync Models", message, "OK");
            }

            // Closing from inside OnGUI tears the layout down mid-draw. One frame
            // later there is no layout left to upset.
            EditorApplication.delayCall += Close;
        }
    }
}
