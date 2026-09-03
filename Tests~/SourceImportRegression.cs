// Execute this file with Ivan MCP's script-execute (class: SourceImportRegression, method: Main).
// All fixtures are isolated. The runner restores selection and removes its own temporary files.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using FofuxoAnimationTools.Editor;

public static class SourceImportRegression
{
    private static readonly List<string> Passed = new List<string>();

    public static string Main()
    {
        Passed.Clear();
        string token = Guid.NewGuid().ToString("N");
        string assets = "Assets/__FofuxoSourceTests_" + token;
        string external = Path.Combine(Path.GetTempPath(), "FofuxoSourceTests_" + token);
        string pendingPath = Path.GetFullPath("Library/FofuxoSourcePending.json");
        string pending = File.Exists(pendingPath) ? File.ReadAllText(pendingPath) : null;
        var selection = Selection.objects;
        string[] dragPaths = DragAndDrop.paths;
        Directory.CreateDirectory(external);
        AssetDatabase.CreateFolder("Assets", Path.GetFileName(assets));
        try
        {
            string source = Path.Combine(external, "External File.txt");
            string asset = assets + "/External File.txt";
            File.WriteAllText(source, "original");

            // Exercise the actual registered drop callback without consuming Unity's drop.
            DragAndDrop.paths = new[] { source };
            MethodInfo drop = typeof(AssetSourceCapture).GetMethod("OnProjectDrop",
                BindingFlags.Static | BindingFlags.NonPublic);
            object id = Activator.CreateInstance(drop.GetParameters()[0].ParameterType);
            Check((DragAndDropVisualMode)drop.Invoke(null, new[] { id, (object)assets, false }) ==
                DragAndDropVisualMode.None, "Drag hover stays with Unity");
            Check((DragAndDropVisualMode)drop.Invoke(null, new[] { id, (object)assets, true }) ==
                DragAndDropVisualMode.None, "Drop stays with Unity");

            File.Copy(source, asset);
            Import(asset);
            Check(ModelSourceLink.SourceOf(asset) == Path.GetFullPath(source), "External file origin captured on import");
            string guid = AssetDatabase.AssetPathToGUID(asset);
            Import(asset);
            Check(ModelSourceLink.SourceOf(asset) == Path.GetFullPath(source), "Origin survives reimport");

            string moved = assets + "/Renamed.txt";
            Check(AssetDatabase.MoveAsset(asset, moved) == "", "Tracked asset renamed");
            Check(ModelSourceLink.SourceOf(moved) == Path.GetFullPath(source), "Origin survives asset rename");
            File.WriteAllText(source, "new file content");
            ModelUpdateMenu.UpdateAssets(new[] { moved });
            Check(File.ReadAllText(moved) == "new file content" &&
                AssetDatabase.LoadAssetAtPath<TextAsset>(moved).text == "new file content", "Update copies and synchronously imports");
            Check(AssetDatabase.AssetPathToGUID(moved) == guid, "Update preserves GUID");

            var importer = AssetImporter.GetAtPath(moved);
            importer.userData = "{\"owner\":42,\"nested\":{\"fofuxoSource\":\"keep\"},\"text\":\"a}b\"}";
            importer.SaveAndReimport();
            ModelSourceLink.Remember(moved, source);
            string secondSource = Path.Combine(external, "Changed Name.txt");
            File.WriteAllText(secondSource, "renamed source");
            ModelSourceLink.Remember(moved, secondSource);
            importer = AssetImporter.GetAtPath(moved);
            Check(JsonUtility.FromJson<OtherData>(importer.userData).owner == 42 &&
                importer.userData.Contains("\"fofuxoSource\":\"keep\""),
                "Other JSON metadata survives source changes");
            Check(ModelSourceLink.SourceOf(moved) == secondSource, "Different source filename is supported");
            Check(ModelSourceLink.Update(moved) == "" && File.ReadAllText(moved) == "renamed source",
                "Renamed source updates project copy");

            importer.userData = "custom opaque metadata";
            importer.SaveAndReimport();
            ModelSourceLink.Remember(moved, source);
            Check(importer.userData.StartsWith("custom opaque metadata") &&
                ModelSourceLink.SourceOf(moved) == source, "Opaque metadata retained");

            // A drop cancelled by Unity must not link an existing different file.
            string cancelledSource = Path.Combine(external, "Cancelled.txt");
            string cancelledAsset = assets + "/Cancelled.txt";
            File.WriteAllText(cancelledSource, "new");
            File.WriteAllText(cancelledAsset, "old");
            Import(cancelledAsset);
            AssetSourceCapture.Capture(new[] { cancelledSource }, assets);
            Import(cancelledAsset);
            Check(ModelSourceLink.SourceOf(cancelledAsset) == "", "Cancelled overwrite does not capture a false origin");

            // Folder drops can contain repeated basenames in different subfolders.
            string folder = Path.Combine(external, "Folder");
            Directory.CreateDirectory(Path.Combine(folder, "Nested"));
            File.WriteAllText(Path.Combine(folder, "Same.txt"), "top");
            File.WriteAllText(Path.Combine(folder, "Nested", "Same.txt"), "nested");
            File.WriteAllText(Path.Combine(folder, "Same.txt.meta"), "external metadata");
            AssetSourceCapture.Capture(new[] { folder }, assets);
            Directory.CreateDirectory(assets + "/Folder/Nested");
            File.Copy(Path.Combine(folder, "Same.txt"), assets + "/Folder/Same.txt");
            File.Copy(Path.Combine(folder, "Nested", "Same.txt"), assets + "/Folder/Nested/Same.txt");
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            Check(ModelSourceLink.SourceOf(assets + "/Folder/Same.txt") == Path.Combine(folder, "Same.txt") &&
                ModelSourceLink.SourceOf(assets + "/Folder/Nested/Same.txt") == Path.Combine(folder, "Nested", "Same.txt"),
                "Nested folder origins stay distinct");

            string internalCopy = assets + "/Cancelled 1.txt";
            AssetSourceCapture.Capture(new[] { Path.GetFullPath(cancelledAsset) }, assets);
            File.Copy(cancelledAsset, internalCopy);
            Import(internalCopy);
            Check(ModelSourceLink.SourceOf(internalCopy) == "", "Internal project drags are ignored");

            File.Delete(source);
            string beforeMissing = File.ReadAllText(moved);
            Check(ModelSourceLink.Update(moved).Contains("gone") && File.ReadAllText(moved) == beforeMissing,
                "Missing source leaves destination untouched");
            ModelSourceLink.Remember(moved, moved);
            Check(ModelSourceLink.Update(moved).Contains("project copy"), "Self-copy is rejected");
            string wrong = Path.Combine(external, "Wrong.png");
            File.WriteAllText(wrong, "wrong format");
            ModelSourceLink.Remember(moved, wrong);
            Check(ModelSourceLink.Update(moved).Contains("different format"), "Format mismatch is rejected");

            Selection.objects = new UnityEngine.Object[]
            {
                AssetDatabase.LoadMainAssetAtPath(assets + "/Folder/Same.txt"),
                AssetDatabase.LoadMainAssetAtPath(assets + "/Folder")
            };
            var selected = ModelSourceLink.Selected();
            Check(selected.Count == 2, "Folder selection includes tracked non-models without duplicates");

            TestAnimations(external, assets);
            return Passed.Count + " checks passed:\n" + string.Join("\n", Passed);
        }
        finally
        {
            Selection.objects = selection;
            DragAndDrop.paths = dragPaths;
            if (pending == null) File.Delete(pendingPath);
            else File.WriteAllText(pendingPath, pending);
            AssetDatabase.DeleteAsset(assets);
            string expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "FofuxoSourceTests_" + token));
            if (Path.GetFullPath(external) == expected && Directory.Exists(external))
                Directory.Delete(external, true);
        }
    }

    private static void TestAnimations(string external, string assets)
    {
        if (!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetType("UnityGLTF.GLTFImporter") != null))
        {
            Passed.Add("Animation fixture skipped: UnityGLTF is not installed");
            return;
        }
        string source = Path.Combine(external, "Animation.gltf");
        string asset = assets + "/Animation.gltf";
        File.WriteAllText(source, Gltf(1, false));
        AssetSourceCapture.Capture(new[] { source }, assets);
        File.Copy(source, asset);
        Import(asset);
        var oldClips = AnimationClipSyncUtility.ClipsInModel(asset);
        Check(oldClips.Count == 1, "Initial glTF imports one animation");
        string guid = AssetDatabase.AssetPathToGUID(asset);
        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(oldClips[0], out string oldGuid, out long oldId);
        File.WriteAllText(source, Gltf(3, true));
        ModelUpdateMenu.UpdateAssets(new[] { asset });
        var clips = AnimationClipSyncUtility.ClipsInModel(asset);
        Check(clips.Count == 2 && clips.Any(c => c.name == "TakeB"), "Source update adds new animations");
        var existing = clips.Single(c => c.name == "TakeA");
        var binding = AnimationUtility.GetCurveBindings(existing).First(b => b.propertyName == "m_LocalPosition.x");
        float value = AnimationUtility.GetEditorCurve(existing, binding).Evaluate(1);
        Check(Mathf.Abs(Mathf.Abs(value) - 3) < 0.001f, $"Source update replaces old animation curves (endpoint: {value})");
        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(existing, out string newGuid, out long newId);
        Check(guid == AssetDatabase.AssetPathToGUID(asset) && oldGuid == newGuid && oldId == newId,
            "Existing model and clip references survive update");

        var editor = UnityEditor.Editor.CreateEditor(AssetImporter.GetAtPath(asset));
        try
        {
            Check(AssetSourceInspector.TryExtendInfoTab(editor), "Source information attaches to glTF Info tab");
            Check(AssetSourceInspector.TryExtendInfoTab(editor), "Info integration is idempotent");
        }
        finally { UnityEngine.Object.DestroyImmediate(editor); }
    }

    private static string Gltf(float end, bool extra)
    {
        byte[] bytes;
        using (var stream = new MemoryStream())
        {
            using (var writer = new BinaryWriter(stream))
                foreach (float value in new[] { 0f, 1f, 0f, 0f, 0f, end, 0f, 0f }) writer.Write(value);
            bytes = stream.ToArray();
        }
        string take = "{\"name\":\"TakeA\",\"samplers\":[{\"input\":0,\"output\":1,\"interpolation\":\"LINEAR\"}],\"channels\":[{\"sampler\":0,\"target\":{\"node\":0,\"path\":\"translation\"}}]}";
        return "{\"asset\":{\"version\":\"2.0\"},\"scene\":0,\"scenes\":[{\"nodes\":[0]}],\"nodes\":[{\"name\":\"Root\"}]," +
            "\"buffers\":[{\"byteLength\":32,\"uri\":\"data:application/octet-stream;base64," + Convert.ToBase64String(bytes) + "\"}]," +
            "\"bufferViews\":[{\"buffer\":0,\"byteOffset\":0,\"byteLength\":8},{\"buffer\":0,\"byteOffset\":8,\"byteLength\":24}]," +
            "\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":2,\"type\":\"SCALAR\",\"min\":[0],\"max\":[1]}," +
            "{\"bufferView\":1,\"componentType\":5126,\"count\":2,\"type\":\"VEC3\"}]," +
            "\"animations\":[" + take + (extra ? "," + take.Replace("TakeA", "TakeB") : "") + "]}";
    }

    [Serializable]
    private sealed class OtherData { public int owner; }

    private static void Import(string path)
    {
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
    }

    private static void Check(bool result, string message)
    {
        if (!result) throw new InvalidOperationException("FAILED: " + message);
        Passed.Add(message);
    }
}
