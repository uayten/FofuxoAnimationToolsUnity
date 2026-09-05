using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace FofuxoAnimationTools.Editor
{
    /// <summary>
    /// The front end for <see cref="ModelExtractUtility"/>.
    ///
    /// Exports a material-free character FBX containing only the renderable hierarchy
    /// and its rig. The FBX becomes the lightweight model used by scene previews, while
    /// gameplay prefabs remain separate and never bring their components into a preview.
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

            EditorGUILayout.HelpBox(
                "Creates a binary CharacterPreview.fbx with its mesh, skeleton and Avatar. " +
                "It creates no materials of its own; named slots are remapped to matching " +
                "project materials. Animation takes, colliders, scripts and other gameplay " +
                "components are not included.",
                MessageType.Info);

            options.MatchMaterials = EditorGUILayout.Toggle(
                new GUIContent(
                    "Match project materials",
                    "Search the configured material folder by normalized name and write " +
                    "external material remaps on the generated FBX importer."),
                options.MatchMaterials);

            options.BindPose = EditorGUILayout.Toggle(
                new GUIContent(
                    "Put skeleton in bind pose",
                    "Animation exports can leave their transforms on an arbitrary frame. " +
                    "This restores the pose stored by the skinned mesh before exporting."),
                options.BindPose);

            options.Animations = EditorGUILayout.Toggle(
                new GUIContent(
                    "Extract animations too",
                    "Writes the takes inside the model out as .anim assets in the same " +
                    "folder, compressed, updating any clip of that name already there " +
                    "instead of leaving a copy beside it. An export that carries the " +
                    "character usually carries a take as well, and this saves going to " +
                    "the other window for the other half of the same file."),
                options.Animations);

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
                    if (ModelExtractUtility.IsGeneratedPreview(models[i]))
                    {
                        continue;
                    }

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

            EditorGUILayout.LabelField(
                $"Output: {plan.Name}Preview.fbx (gameplay components excluded)",
                EditorStyles.miniLabel);

            if (options.MatchMaterials && plan.Materials.Count > 0)
            {
                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField("Project material remaps", EditorStyles.miniBoldLabel);

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

            EditorGUILayout.EndVertical();
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
            int replaced = 0;
            int matchedMaterials = 0;

            foreach (ModelExtractUtility.Plan plan in plans)
            {
                string folder = chosen ?? System.IO.Path
                    .GetDirectoryName(plan.ModelPath)
                    .Replace('\\', '/');

                ModelExtractUtility.Result result =
                    ModelExtractUtility.Extract(plan, folder, options);

                written.AddRange(result.Written);
                replaced += result.Replaced;
                matchedMaterials += result.MatchedMaterials;

                if (result.CharacterPath != null && !result.SelfContained)
                {
                    tethered.Add(plan.Name);
                }

                if (result.AvatarPath == null)
                {
                    withoutAvatar.Add(plan.Name);
                }
            }

            AssetDatabase.Refresh();

            // What went right goes to the console. A modal with a warning triangle on
            // it reads as "something broke" before a word of it has been read, and
            // spending that alarm on "it worked" is the surest way to have the one
            // that matters clicked through.
            Debug.Log(
                $"Wrote {written.Count} asset(s), {replaced} of them over what was already " +
                $"there, keeping its GUID. Applied {matchedMaterials} project material " +
                "remap(s). The preview FBX files create no materials of their own and " +
                "contain no gameplay components." +
                (tethered.Count == 0
                    ? " They no longer reference their source models."
                    : string.Empty));

            var trouble = new List<string>();

            if (withoutAvatar.Count > 0)
            {
                trouble.Add(
                    $"The exported FBX did not produce a valid Avatar for " +
                    $"{string.Join(", ", withoutAvatar)}. Check the generated model's Rig tab.");
            }

            if (tethered.Count > 0)
            {
                trouble.Add(
                    $"{tethered.Count} generated FBX file(s) still reference their source " +
                    $"model: {string.Join(", ", tethered)}.");
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
