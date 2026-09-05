using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace FofuxoAnimationTools.Editor
{
    /// <summary>
    /// The handful of choices that outlive a single operation, kept in EditorPrefs
    /// so they follow the machine rather than the project.
    ///
    /// The name-stripping lists are the interesting ones. A material arriving from
    /// Unreal is called MI_GrantClothes and the one already in the Unity project is
    /// called M_GrantClothes: the same material, named by two different conventions,
    /// which is why matching on the raw name finds nothing and Unity's own Search
    /// and Remap comes back empty. Stripping the prefix from both sides makes them
    /// the same word.
    /// </summary>
    public static class FofuxoToolsSettings
    {
        public const string RootBoneKey = "Fofuxo.RootMotion.RootBone";

        private const string GuardKey = "Fofuxo.Delete.Guard";
        private const string GuardEverythingKey = "Fofuxo.Delete.GuardEverything";
        private const string ConfirmSafeKey = "Fofuxo.Delete.ConfirmSafe";
        private const string ScanNamesKey = "Fofuxo.Delete.ScanNames";
        private const string InterceptKey = "Fofuxo.Delete.InterceptKey";
        private const string MaterialPrefixKey = "Fofuxo.Materials.StripPrefixes";
        private const string MaterialSuffixKey = "Fofuxo.Materials.StripSuffixes";
        private const string MaterialAutoKey = "Fofuxo.Materials.RemapOnImport";
        private const string MaterialFolderKey = "Fofuxo.Materials.SearchFolder";

        public const string DefaultMaterialPrefixes = "M_, MI_, Mat_, Material_";
        public const string DefaultMaterialSuffixes = "_Mat, _Material, _Inst, _Instance";

        /// <summary>
        /// Asset types the delete guard watches when it is not watching everything.
        /// These are the ones whose loss breaks something silently: a missing clip
        /// leaves an Animator state playing nothing, a missing material leaves a
        /// mesh pink, and neither says a word in the console.
        /// </summary>
        public static readonly HashSet<string> GuardedExtensions = new HashSet<string>
        {
            ".fbx", ".obj", ".blend", ".dae", ".3ds", ".max", ".ma", ".mb",
            ".glb", ".gltf",
            ".anim", ".controller", ".overridecontroller", ".mat", ".prefab"
        };

        public static bool GuardDeletes
        {
            get => EditorPrefs.GetBool(GuardKey, true);
            set => EditorPrefs.SetBool(GuardKey, value);
        }

        public static bool GuardEveryAssetType
        {
            get => EditorPrefs.GetBool(GuardEverythingKey, false);
            set => EditorPrefs.SetBool(GuardEverythingKey, value);
        }

        /// <summary>
        /// Whether a delete that breaks nothing still stops to say so.
        ///
        /// Silence is not the same answer as "nothing uses this". A delete that goes
        /// through without a word is indistinguishable from a guard that never ran,
        /// and the whole value of the guard is knowing which of the two happened.
        /// </summary>
        public static bool ConfirmSafeDeletes
        {
            get => EditorPrefs.GetBool(ConfirmSafeKey, true);
            set => EditorPrefs.SetBool(ConfirmSafeKey, value);
        }

        /// <summary>
        /// Whether to also look for the asset's name spelled out in other assets.
        ///
        /// The dependency index only knows about GUIDs. A tool that stores a clip's
        /// name and looks it up at runtime is using that clip just as much, and
        /// nothing in the asset database has any idea.
        /// </summary>
        public static bool ScanNamesOnDelete
        {
            get => EditorPrefs.GetBool(ScanNamesKey, true);
            set => EditorPrefs.SetBool(ScanNamesKey, value);
        }

        /// <summary>
        /// Whether to catch the Delete key in the Project window before Unity acts
        /// on it.
        ///
        /// It is the difference between asking and refusing. Caught early there is no
        /// delete to stop, so Unity never reports one as failed and never shows the
        /// dialog claiming something is keeping a hook on the asset. Menu entries,
        /// scripts and other tools always go through Unity's own delete -- a managed
        /// MenuItem can no longer take the Assets/Delete entry over on Unity 6000.6,
        /// so those reach the guard through OnWillDeleteAsset, where refusing one
        /// costs Unity's own dialog first.
        /// </summary>
        public static bool InterceptDelete
        {
            get => EditorPrefs.GetBool(InterceptKey, true);
            set => EditorPrefs.SetBool(InterceptKey, value);
        }

        public static string MaterialPrefixes
        {
            get => EditorPrefs.GetString(MaterialPrefixKey, DefaultMaterialPrefixes);
            set => EditorPrefs.SetString(MaterialPrefixKey, value);
        }

        public static string MaterialSuffixes
        {
            get => EditorPrefs.GetString(MaterialSuffixKey, DefaultMaterialSuffixes);
            set => EditorPrefs.SetString(MaterialSuffixKey, value);
        }

        public static bool RemapMaterialsOnImport
        {
            get => EditorPrefs.GetBool(MaterialAutoKey, false);
            set => EditorPrefs.SetBool(MaterialAutoKey, value);
        }

        public static string MaterialSearchFolder
        {
            get => EditorPrefs.GetString(MaterialFolderKey, "Assets");
            set => EditorPrefs.SetString(MaterialFolderKey, value);
        }

        public static string RootBone
        {
            get => EditorPrefs.GetString(RootBoneKey, RootMotionClipUtility.DefaultRootBone);
            set => EditorPrefs.SetString(RootBoneKey, value);
        }

        public static string[] Split(string commaSeparated)
        {
            if (string.IsNullOrWhiteSpace(commaSeparated))
            {
                return new string[0];
            }

            string[] parts = commaSeparated.Split(',');
            var cleaned = new List<string>(parts.Length);

            foreach (string part in parts)
            {
                string trimmed = part.Trim();
                if (trimmed.Length > 0)
                {
                    cleaned.Add(trimmed);
                }
            }

            return cleaned.ToArray();
        }

        [SettingsProvider]
        private static SettingsProvider Create()
        {
            return new SettingsProvider("Preferences/Fofuxo's Animation Tools", SettingsScope.User)
            {
                guiHandler = _ => Draw(),
                keywords = new HashSet<string>
                {
                    "animation", "root motion", "delete", "references", "material", "remap"
                }
            };
        }

        private static void Draw()
        {
            EditorGUIUtility.labelWidth = 220f;
            EditorGUILayout.Space(6);

            EditorGUILayout.LabelField("Root motion", EditorStyles.boldLabel);
            RootBone = EditorGUILayout.TextField(
                new GUIContent("Root bone", "The bone carrying the movement on your rigs."),
                RootBone);

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Deleting", EditorStyles.boldLabel);

            GuardDeletes = EditorGUILayout.Toggle(
                new GUIContent(
                    "Warn about references",
                    "Stop a delete that would break something and show what would break."),
                GuardDeletes);

            using (new EditorGUI.DisabledScope(!GuardDeletes))
            {
                GuardEveryAssetType = EditorGUILayout.Toggle(
                    new GUIContent(
                        "Watch every asset type",
                        "Off: models, clips, controllers, materials and prefabs. " +
                        "On: anything at all, including scripts and textures."),
                    GuardEveryAssetType);

                ConfirmSafeDeletes = EditorGUILayout.Toggle(
                    new GUIContent(
                        "Say so when nothing uses it",
                        "A delete that breaks nothing still stops to tell you so. " +
                        "Off, it goes straight through and only writes a line to the " +
                        "console, which is quieter but leaves you unable to tell a " +
                        "clean delete from a guard that never ran."),
                    ConfirmSafeDeletes);

                ScanNamesOnDelete = EditorGUILayout.Toggle(
                    new GUIContent(
                        "Also look for the name",
                        "The dependency index only sees GUIDs. This also searches the " +
                        "project's text assets for the name spelled out, which is how " +
                        "tools that store a clip name rather than a reference get found."),
                    ScanNamesOnDelete);

                InterceptDelete = EditorGUILayout.Toggle(
                    new GUIContent(
                        "Catch the delete early",
                        "Check before Unity starts deleting rather than after: the Delete " +
                        "key in the Project window. Unity has no way to stop a delete " +
                        "quietly -- refusing one makes it claim something is keeping a " +
                        "hook on the asset -- so getting in front of the keystroke is " +
                        "the only way that dialog never appears for it. Menu entries " +
                        "always go through Unity's own delete and reach the guard " +
                        "afterwards. Off, the key goes straight to Unity's own delete " +
                        "too."),
                    InterceptDelete);
            }

            EditorGUILayout.LabelField(
                AssetUsageIndex.IsBuilt
                    ? $"Reference index: {AssetUsageIndex.IndexedAssets} assets"
                    : "Reference index: built on first use",
                EditorStyles.miniLabel);

            if (GUILayout.Button("Rebuild reference index", GUILayout.Width(200f)))
            {
                AssetUsageIndex.Build();
            }

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Material matching", EditorStyles.boldLabel);

            MaterialSearchFolder = EditorGUILayout.TextField(
                new GUIContent("Search folder", "Where to look for materials to match against."),
                MaterialSearchFolder);

            MaterialPrefixes = EditorGUILayout.TextField(
                new GUIContent(
                    "Strip prefixes",
                    "Removed from both names before comparing. Comma separated."),
                MaterialPrefixes);

            MaterialSuffixes = EditorGUILayout.TextField(
                new GUIContent("Strip suffixes", "Same, at the end of the name."),
                MaterialSuffixes);

            RemapMaterialsOnImport = EditorGUILayout.Toggle(
                new GUIContent(
                    "Remap on import",
                    "Match materials automatically the first time a model is imported. " +
                    "Off by default: it reimports the model a second time to apply, and " +
                    "the window gives you the matches to look at before they are made."),
                RemapMaterialsOnImport);

            EditorGUILayout.Space(6);
            EditorGUILayout.HelpBox(
                "Example: an FBX material named MI_GrantClothes matches the project " +
                "material M_GrantClothes once both prefixes are stripped.",
                MessageType.None);
        }
    }
}
