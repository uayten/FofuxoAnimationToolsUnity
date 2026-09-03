using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace FofuxoAnimationTools.Editor
{
    /// <summary>
    /// The front end for <see cref="ModelMaterialMatcher"/>.
    ///
    /// Remapping a material is a reimport, and a reimport of a character is not
    /// instant, so the matches are all found first and shown together. Every one of
    /// them is a guess made from a name, and a guess is worth looking at before it
    /// is applied to twenty models at once -- so each row is an object field, and a
    /// wrong guess is corrected by dragging the right material onto it.
    /// </summary>
    public sealed class ModelMaterialWindow : EditorWindow
    {
        private readonly List<ModelMaterialMatcher.Model> models = new List<ModelMaterialMatcher.Model>();
        private readonly HashSet<string> excluded = new HashSet<string>();

        private Object source;
        private Vector2 scroll;
        private bool scanned;

        [MenuItem("Window/Fofuxo's Animation Tools/Match Model Materials")]
        public static void Open()
        {
            ModelMaterialWindow window = GetWindow<ModelMaterialWindow>();
            window.titleContent = new GUIContent("Model Materials");
            window.minSize = new Vector2(620f, 400f);
            window.Show();
        }

        [MenuItem("Assets/Fofuxo's Animation Tools/Match Materials by Name", false, 32)]
        private static void FromSelection()
        {
            ModelMaterialWindow window = GetWindow<ModelMaterialWindow>();
            window.titleContent = new GUIContent("Model Materials");
            window.minSize = new Vector2(620f, 400f);
            window.source = Selection.activeObject;
            window.Scan();
            window.Show();
        }

        [MenuItem("Assets/Fofuxo's Animation Tools/Match Materials by Name", true)]
        private static bool HasModelSelected()
        {
            Object selected = Selection.activeObject;
            if (selected == null)
            {
                return false;
            }

            string path = AssetDatabase.GetAssetPath(selected);
            return AssetDatabase.IsValidFolder(path) || ModelAsset.Is(path);
        }

        private void OnGUI()
        {
            EditorGUIUtility.labelWidth = 150f;
            EditorGUILayout.Space(6);

            source = EditorGUILayout.ObjectField(
                new GUIContent("Models", "An FBX, or a folder of them."),
                source, typeof(Object), false);

            EditorGUILayout.LabelField(
                $"Matching against materials in {FofuxoToolsSettings.MaterialSearchFolder}, " +
                $"ignoring the prefixes {FofuxoToolsSettings.MaterialPrefixes}.",
                EditorStyles.miniLabel);

            using (new EditorGUI.DisabledScope(source == null))
            {
                if (GUILayout.Button("Scan", GUILayout.Height(22f)))
                {
                    Scan();
                }
            }

            EditorGUILayout.Space(4);

            scroll = EditorGUILayout.BeginScrollView(scroll);

            if (!scanned)
            {
                EditorGUILayout.HelpBox(
                    "Pick a model or a folder of models and press Scan.", MessageType.Info);
            }

            foreach (ModelMaterialMatcher.Model model in models)
            {
                DrawModel(model);
            }

            EditorGUILayout.EndScrollView();

            DrawFooter();
        }

        private void Scan()
        {
            models.Clear();
            excluded.Clear();
            scanned = true;

            Dictionary<string, List<Material>> index =
                ModelMaterialMatcher.Index(FofuxoToolsSettings.MaterialSearchFolder);

            List<string> paths = AnimationClipSyncUtility.ModelsUnder(AssetDatabase.GetAssetPath(source));

            try
            {
                for (int i = 0; i < paths.Count; i++)
                {
                    EditorUtility.DisplayProgressBar(
                        "Matching materials", paths[i], (float)i / paths.Count);

                    models.Add(ModelMaterialMatcher.Inspect(paths[i], index));
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private void DrawModel(ModelMaterialMatcher.Model model)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();

            bool include = !excluded.Contains(model.Path);
            bool wanted = EditorGUILayout.ToggleLeft(
                System.IO.Path.GetFileName(model.Path), include, EditorStyles.boldLabel);

            if (wanted != include)
            {
                if (wanted)
                {
                    excluded.Remove(model.Path);
                }
                else
                {
                    excluded.Add(model.Path);
                }
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField(
                $"{model.Changes} of {model.Slots.Count} would change",
                EditorStyles.miniLabel,
                GUILayout.Width(160f));

            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(model.Warning))
            {
                EditorGUILayout.HelpBox(model.Warning, MessageType.Warning);
            }

            using (new EditorGUI.DisabledScope(!wanted))
            {
                foreach (ModelMaterialMatcher.Slot slot in model.Slots)
                {
                    DrawSlot(slot);
                }
            }

            EditorGUILayout.EndVertical();
        }

        private static void DrawSlot(ModelMaterialMatcher.Slot slot)
        {
            EditorGUILayout.BeginHorizontal();

            EditorGUILayout.LabelField(
                new GUIContent(slot.Name, slot.Note),
                GUILayout.Width(200f));

            slot.Suggestion = (Material)EditorGUILayout.ObjectField(
                slot.Suggestion, typeof(Material), false, GUILayout.Width(220f));

            EditorGUILayout.LabelField(
                slot.Suggestion == null ? slot.Note : slot.WouldChange ? slot.Note : "already remapped",
                EditorStyles.miniLabel);

            EditorGUILayout.EndHorizontal();
        }

        private void DrawFooter()
        {
            int changes = 0;
            int touched = 0;

            foreach (ModelMaterialMatcher.Model model in models)
            {
                if (excluded.Contains(model.Path) || model.Changes == 0)
                {
                    continue;
                }

                changes += model.Changes;
                touched++;
            }

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                scanned ? $"{changes} slot(s) across {touched} model(s)." : string.Empty,
                EditorStyles.miniLabel);

            GUILayout.FlexibleSpace();

            using (new EditorGUI.DisabledScope(changes == 0))
            {
                if (GUILayout.Button("Apply and reimport", GUILayout.Width(150f), GUILayout.Height(24f)))
                {
                    Apply();
                }
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(4);
        }

        private void Apply()
        {
            int slots = 0;
            int reimported = 0;

            try
            {
                for (int i = 0; i < models.Count; i++)
                {
                    if (excluded.Contains(models[i].Path))
                    {
                        continue;
                    }

                    EditorUtility.DisplayProgressBar(
                        "Remapping materials", models[i].Path, (float)i / models.Count);

                    int changed = ModelMaterialMatcher.Apply(models[i]);
                    slots += changed;

                    if (changed > 0)
                    {
                        reimported++;
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            Debug.Log($"Remapped {slots} material slot(s) across {reimported} model(s).");
            Scan();
        }
    }
}
