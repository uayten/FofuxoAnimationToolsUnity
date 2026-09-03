using System;
using System.Collections;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace FofuxoAnimationTools.Editor
{
    /// <summary>Adds source information without replacing existing importer inspectors.</summary>
    [InitializeOnLoad]
    public static class AssetSourceInspector
    {
        private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        static AssetSourceInspector()
        {
            UnityEditor.Editor.finishedDefaultHeaderGUI += OnHeader;
        }

        private static void OnHeader(UnityEditor.Editor editor)
        {
            if (!(editor.target is AssetImporter importer) || !importer.assetPath.StartsWith("Assets/", StringComparison.Ordinal))
                return;
            if (TryExtendInfoTab(editor)) return;
            Draw(editor);
        }

        // UnityGLTF exposes no public Info-tab callback. Keep this optional bridge
        // isolated: if its internals change, the standard Inspector header still works.
        public static bool TryExtendInfoTab(UnityEditor.Editor editor)
        {
            if (editor.GetType().FullName != "UnityGLTF.GLTFImporterInspector") return false;
            Type tabbedType = editor.GetType().BaseType;
            var tabs = tabbedType?.GetField("_tabs", InstanceFields)?.GetValue(editor) as IEnumerable;
            if (tabs == null) return false;

            foreach (object tab in tabs)
            {
                Type type = tab.GetType();
                if (type.GetField("Label", InstanceFields)?.GetValue(tab) as string != "Info") continue;
                FieldInfo callback = type.GetField("_tabGui", InstanceFields);
                if (!(callback?.GetValue(tab) is Action original)) return false;
                if (original.Target is SourceInfoTab) return true;
                try
                {
                    var extension = new SourceInfoTab(editor, original);
                    callback.SetValue(tab, (Action)extension.Draw);
                    return true;
                }
                catch (MemberAccessException)
                {
                    return false;
                }
            }
            return false;
        }

        private sealed class SourceInfoTab
        {
            private readonly UnityEditor.Editor editor;
            private readonly Action original;

            public SourceInfoTab(UnityEditor.Editor editor, Action original)
            {
                this.editor = editor;
                this.original = original;
            }

            public void Draw()
            {
                // Draw before the importer, whose Info GUI owns its disabled groups.
                AssetSourceInspector.Draw(editor);
                original();
            }
        }

        private static void Draw(UnityEditor.Editor editor)
        {
            if (!(editor.target is AssetImporter importer)) return;
            string source = ModelSourceLink.SourceOf(importer);
            bool multiple = editor.targets.Length > 1;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Source File", EditorStyles.boldLabel);
                if (multiple)
                {
                    EditorGUILayout.LabelField("Select one asset to inspect its source.", EditorStyles.wordWrappedLabel);
                    return;
                }

                if (string.IsNullOrEmpty(source))
                    EditorGUILayout.LabelField("No source recorded. Set it once for assets imported before tracking was enabled.",
                        EditorStyles.wordWrappedLabel);
                else
                {
                    Rect rect = EditorGUILayout.GetControlRect(false,
                        EditorGUIUtility.singleLineHeight * 3);
                    EditorGUI.SelectableLabel(rect, source, EditorStyles.wordWrappedLabel);
                    EditorGUILayout.LabelField(File.Exists(source) ? "Available" : "File or folder not found",
                        EditorStyles.miniLabel);
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    string path = importer.assetPath;
                    if (GUILayout.Button("Set Source..."))
                        EditorApplication.delayCall += () => ModelUpdateMenu.ChooseSource(path);
                    using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(source) || !File.Exists(source)))
                    {
                        if (GUILayout.Button("Show in Explorer")) EditorUtility.RevealInFinder(source);
                    }
                    if (GUILayout.Button("Update From Source"))
                        EditorApplication.delayCall += () => ModelUpdateMenu.UpdateAssets(new[] { path });
                }
            }
        }
    }
}