using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace FofuxoAnimationTools.Editor
{
    /// <summary>
    /// Points a set of models at one avatar.
    ///
    /// This is Unity's answer to Unreal's Skeleton asset, and it is easy to miss
    /// because it is spelled as an importer setting rather than as a relationship.
    /// A model set to Copy From Other Avatar is saying "my animations belong to that
    /// rig", and Unity will then check the hierarchy against it on every import and
    /// complain when they diverge -- which is the same silence this package's rig
    /// check exists to break, caught one step earlier, at import.
    ///
    /// Doing it one FBX at a time through the Rig tab is why it usually does not get
    /// done at all.
    /// </summary>
    public sealed class AvatarAssignWindow : EditorWindow
    {
        private readonly List<string> models = new List<string>();

        private Object source;
        private Avatar avatar;
        private Vector2 scroll;
        private bool scanned;

        [MenuItem("Window/Fofuxo's Animation Tools/Assign Avatar to Models")]
        public static void Open()
        {
            AvatarAssignWindow window = GetWindow<AvatarAssignWindow>();
            window.titleContent = new GUIContent("Assign Avatar");
            window.minSize = new Vector2(560f, 360f);
            window.Show();
        }

        private void OnGUI()
        {
            EditorGUIUtility.labelWidth = 150f;
            EditorGUILayout.Space(6);

            EditorGUI.BeginChangeCheck();
            source = EditorGUILayout.ObjectField(
                new GUIContent("Models", "An FBX, or a folder of them."),
                source, typeof(Object), false);

            if (EditorGUI.EndChangeCheck())
            {
                scanned = false;
            }

            avatar = (Avatar)EditorGUILayout.ObjectField(
                new GUIContent(
                    "Avatar",
                    "The rig these models animate. Extract it out of the character with " +
                    "Extract Mesh and Avatar and point everything at that, so the " +
                    "animations stop depending on the character file."),
                avatar, typeof(Avatar), false);

            if (avatar != null && !avatar.isValid)
            {
                EditorGUILayout.HelpBox("That avatar is not valid.", MessageType.Error);
            }

            EditorGUILayout.Space(4);

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
                EditorGUILayout.HelpBox("Pick a model or a folder of models and press Scan.", MessageType.Info);
            }

            foreach (string model in models)
            {
                DrawModel(model);
            }

            EditorGUILayout.EndScrollView();
            DrawFooter();
        }

        private void Scan()
        {
            models.Clear();
            models.AddRange(AnimationClipSyncUtility.ModelsUnder(AssetDatabase.GetAssetPath(source)));
            scanned = true;
        }

        private static void DrawModel(string path)
        {
            ModelImporter importer = ModelAsset.Importer(path);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(Path.GetFileName(path), GUILayout.Width(240f));

            // Avatar Definition is a ModelImporter setting and has no counterpart on a
            // scripted importer: a glb either builds its own avatar or has none, and
            // there is no field to point at someone else's. Saying so beats leaving the
            // row out, which reads as "handled".
            if (importer == null)
            {
                EditorGUILayout.LabelField(
                    "no avatar setting — not Unity's model importer", EditorStyles.miniLabel);
                EditorGUILayout.EndHorizontal();
                return;
            }

            string current = importer.avatarSetup == ModelImporterAvatarSetup.CopyFromOther
                ? importer.sourceAvatar != null
                    ? $"copies {importer.sourceAvatar.name}"
                    : "copies — nothing set"
                : importer.avatarSetup.ToString();

            EditorGUILayout.LabelField(current, EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }

        private void DrawFooter()
        {
            EditorGUILayout.Space(2);

            bool ready = scanned && models.Count > 0 && avatar != null && avatar.isValid;

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                scanned ? $"{models.Count} model(s)." : string.Empty, EditorStyles.miniLabel);

            GUILayout.FlexibleSpace();

            using (new EditorGUI.DisabledScope(!ready))
            {
                if (GUILayout.Button("Assign and reimport", GUILayout.Width(160f), GUILayout.Height(24f)))
                {
                    Assign();
                }
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(4);
        }

        private void Assign()
        {
            int changed = 0;
            int unsupported = 0;

            try
            {
                for (int i = 0; i < models.Count; i++)
                {
                    EditorUtility.DisplayProgressBar("Assigning avatar", models[i], (float)i / models.Count);

                    ModelImporter importer = ModelAsset.Importer(models[i]);

                    if (importer == null)
                    {
                        unsupported++;
                        continue;
                    }

                    if (importer.sourceAvatar == avatar)
                    {
                        continue;
                    }

                    importer.avatarSetup = ModelImporterAvatarSetup.CopyFromOther;
                    importer.sourceAvatar = avatar;
                    importer.SaveAndReimport();

                    changed++;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            // A model the setting cannot reach is worth a line. Reporting only the ones
            // that worked is how a selection ends up half done without anyone noticing.
            Debug.Log(
                $"Pointed {changed} model(s) at '{avatar.name}'." +
                (unsupported > 0
                    ? $" {unsupported} left alone: Avatar Definition is a setting on Unity's " +
                      "model importer, and those files come in through a scripted importer."
                    : string.Empty));
        }
    }
}
