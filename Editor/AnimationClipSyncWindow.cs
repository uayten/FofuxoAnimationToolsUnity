using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace FofuxoAnimationTools.Editor
{
    /// <summary>
    /// The front end for <see cref="AnimationClipSyncUtility"/>: point it at the
    /// models that just came out of the exporter and at the folder of clips the
    /// project has been using, and it pairs them up by name and writes the new
    /// animation into the old assets.
    ///
    /// The table is the product. Four hundred takes will not line up perfectly with
    /// four hundred clips -- a few will have been renamed, one will have been split
    /// in two, one will have been dropped -- and the ones that did not match are the
    /// only ones worth a human's attention. So the match is shown and argued with
    /// before anything is written, and a row that guessed wrong can be pointed at the
    /// right clip by hand.
    /// </summary>
    public sealed class AnimationClipSyncWindow : EditorWindow
    {
        private const float RowHeight = 20f;

        private const string SourceKey = "Fofuxo.Sync.Source";
        private const string TargetKey = "Fofuxo.Sync.Target";
        private const string PrefixKey = "Fofuxo.Sync.Prefixes";
        private const string SuffixKey = "Fofuxo.Sync.Suffixes";
        private const string TrailingKey = "Fofuxo.Sync.StripTrailingNumber";
        private const string CaseKey = "Fofuxo.Sync.IgnoreCase";
        private const string PlaybackKey = "Fofuxo.Sync.KeepPlayback";
        private const string RootMotionKey = "Fofuxo.Sync.KeepRootMotion";
        private const string CreateKey = "Fofuxo.Sync.CreateMissing";
        private const string CompressKey = "Fofuxo.Sync.Compress";
        private const string ElsewhereKey = "Fofuxo.Sync.ExtractElsewhere";

        private enum Outcome
        {
            Update,
            Create,
            Ambiguous,
            Orphan
        }

        private enum Filter
        {
            All,
            Update,
            New,
            Unmatched
        }

        private sealed class Row
        {
            public AnimationClip Source;
            public AnimationClip Target;
            public Outcome Outcome;
            public string Note;
            public bool Enabled = true;

            /// <summary>How many assets point at the clip about to be rewritten.</summary>
            public int References;

            public AnimationRigCheck.Result Rig;
        }

        private readonly List<Row> rows = new List<Row>();
        private readonly List<Row> visible = new List<Row>();

        /// <summary>
        /// Models handed over by the Project window menu, which take precedence over
        /// the source field. Right-clicking three FBX files and exporting them is a
        /// selection, and a selection is not a folder.
        /// </summary>
        private readonly List<string> pinned = new List<string>();

        /// <summary>The models the current takes came out of, for the rest pose check.</summary>
        private readonly List<string> sourceModels = new List<string>();

        private Object source;
        private string targetFolder = string.Empty;
        private Object character;

        /// <summary>Whether the character in the field was put there by hand.</summary>
        private bool characterChosen;

        /// <summary>
        /// Whether the character is the remembered preview model rather than something
        /// found in the destination folder. A guess, and one that is wrong the moment a
        /// second character exists in the project.
        /// </summary>
        private bool characterGuessed;

        private Vector2 scroll;
        private Filter filter;
        private bool scanned;
        private string summary = string.Empty;
        private string rigWarning = string.Empty;
        private int problemRows;

        [MenuItem("Window/Fofuxo's Animation Tools/Extract & Sync Clips")]
        public static void Open()
        {
            AnimationClipSyncWindow window = GetWindow<AnimationClipSyncWindow>();
            window.titleContent = new GUIContent("Extract & Sync Clips");
            window.minSize = new Vector2(660f, 420f);
            window.Show();
        }

        private void OnEnable()
        {
            source = AssetDatabase.LoadMainAssetAtPath(EditorPrefs.GetString(SourceKey, string.Empty));
            targetFolder = FolderField.Remembered(TargetKey);

            // The character is not read here. It depends on the destination folder,
            // which the scan is what settles.
            characterChosen = false;

            EditorApplication.delayCall += Rescan;
        }

        private void OnDisable()
        {
            EditorApplication.delayCall -= Rescan;
        }

        /// <summary>
        /// Reads the folders and the rules again. There is no button for this: the
        /// table is the scan, so it happens when the window opens and whenever an
        /// input that changes the answer changes.
        /// </summary>
        private void Rescan()
        {
            if (this == null)
            {
                return;
            }

            if ((source == null && pinned.Count == 0) || string.IsNullOrEmpty(targetFolder))
            {
                rows.Clear();
                scanned = false;
                Repaint();
                return;
            }

            Scan();
            Repaint();
        }

        /// <summary>
        /// Extracts the animation takes out of the selected models, and nothing else
        /// -- no meshes, no materials, no avatar.
        ///
        /// The destination is not asked for here. It used to be, back when the window
        /// had no folder field of its own, and asking twice for the same answer is
        /// worse than asking once in the place where it can also be changed.
        /// </summary>
        [MenuItem("Assets/Fofuxo's Animation Tools/Extract Animations to Folder...", false, 30)]
        private static void ExportAnimations()
        {
            List<string> models = SelectedModels();
            if (models.Count == 0)
            {
                return;
            }

            AnimationClipSyncWindow window = GetWindow<AnimationClipSyncWindow>();
            window.titleContent = new GUIContent("Extract & Sync Clips");
            window.minSize = new Vector2(720f, 420f);
            window.Adopt(models);
            window.Show();
        }

        [MenuItem("Assets/Fofuxo's Animation Tools/Extract Animations to Folder...", true)]
        private static bool HasModelsSelected()
        {
            return SelectedModels().Count > 0;
        }

        /// <summary>
        /// The folder the incoming models sit in, for extracting beside them.
        ///
        /// Empty when they are spread across more than one folder: "beside the model" has
        /// no single answer then, and picking one of them silently would scatter half the
        /// clips somewhere nobody chose.
        /// </summary>
        private string FolderOfSources()
        {
            string folder = string.Empty;

            foreach (string path in Sources())
            {
                string here = System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/') ?? string.Empty;

                if (folder.Length == 0)
                {
                    folder = here;
                }
                else if (folder != here)
                {
                    return string.Empty;
                }
            }

            return folder;
        }

        /// <summary>The model paths this window is working from, pinned or from the field.</summary>
        private List<string> Sources()
        {
            return pinned.Count > 0
                ? new List<string>(pinned)
                : AnimationClipSyncUtility.ModelsUnder(AssetDatabase.GetAssetPath(source));
        }

        private static List<string> SelectedModels()
        {
            var models = new List<string>();

            foreach (Object selected in Selection.GetFiltered(typeof(Object), SelectionMode.Assets))
            {
                string path = AssetDatabase.GetAssetPath(selected);

                if (ModelAsset.Is(path))
                {
                    models.Add(path);
                }
            }

            return models;
        }


        private void Adopt(List<string> models)
        {
            pinned.Clear();
            pinned.AddRange(models);
            source = null;

            targetFolder = FolderField.Remembered(TargetKey);

            // Rescan settles it either way: with the folder from last time it fills the
            // table, and without one it leaves the window asking for a folder rather
            // than showing an empty table that reads as "no matches".
            Rescan();
        }

        private AnimationClipSyncUtility.NameRules Rules => new AnimationClipSyncUtility.NameRules
        {
            IgnoreCase = EditorPrefs.GetBool(CaseKey, true),
            StripTrailingNumber = EditorPrefs.GetBool(TrailingKey, false),
            Prefixes = EditorPrefs.GetString(PrefixKey, string.Empty),
            Suffixes = EditorPrefs.GetString(SuffixKey, string.Empty)
        };

        private void OnGUI()
        {
            DrawSetup();
            DrawSummary();
            DrawTable();
            DrawFooter();
        }

        private void DrawSetup()
        {
            EditorGUIUtility.labelWidth = 150f;
            EditorGUILayout.Space(6);

            if (pinned.Count > 0)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(
                    new GUIContent(
                        "New models",
                        string.Join("\n", pinned)),
                    new GUIContent(pinned.Count == 1
                        ? System.IO.Path.GetFileName(pinned[0])
                        : $"{pinned.Count} models from the selection"));

                if (GUILayout.Button("clear", EditorStyles.miniButton, GUILayout.Width(50f)))
                {
                    pinned.Clear();
                    Rescan();
                }

                EditorGUILayout.EndHorizontal();
            }

            EditorGUI.BeginChangeCheck();

            if (pinned.Count == 0)
            {
                source = EditorGUILayout.ObjectField(
                    new GUIContent(
                        "New models",
                        "The freshly imported FBX, or a folder of them. Their animation " +
                        "takes are the new contents."),
                    source, typeof(Object), false);
            }

            bool elsewhere = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    "Extract into another folder",
                    "Off, the clips land beside the model they came out of, which is where " +
                    "they belong when the model was exported into the folder it belongs in. " +
                    "On, pick the folder — which is what you want when the .anim assets the " +
                    "project already references live somewhere else."),
                EditorPrefs.GetBool(ElsewhereKey, true));

            if (elsewhere != EditorPrefs.GetBool(ElsewhereKey, true))
            {
                EditorPrefs.SetBool(ElsewhereKey, elsewhere);
                Rescan();
            }

            if (elsewhere)
            {
                targetFolder = FolderField.Draw(
                    new GUIContent(
                        "Existing clips",
                        "The folder holding the .anim assets the project already references. " +
                        "These are the files that get rewritten in place. Remembered between " +
                        "runs."),
                    targetFolder,
                    TargetKey,
                    "pick a folder");
            }
            else
            {
                targetFolder = FolderOfSources();

                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.TextField(
                        new GUIContent(
                            "Existing clips",
                            "The folder the models sit in. Turn the box above on to choose " +
                            "another one."),
                        targetFolder.Length > 0 ? targetFolder : "— no model selected —");
                }
            }

            Object wasCharacter = character;

            character = EditorGUILayout.ObjectField(
                new GUIContent(
                    "Character",
                    "The model these clips are supposed to animate. Every bone path in " +
                    "every take is checked against it, because a clip exported from the " +
                    "wrong rig does not fail — it just moves nothing. Defaults to a " +
                    "character found in the destination folder, and falls back to the " +
                    "preview model. Set it here and it stays set."),
                character, typeof(GameObject), false);

            if (character != wasCharacter)
            {
                characterChosen = true;
                characterGuessed = false;
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Name matching", EditorStyles.boldLabel);

            Toggle(CaseKey, true, "Ignore case", "Attack_Loop matches attack_loop.");

            Toggle(TrailingKey, false, "Strip trailing number",
                "AS_Run_Loop_223 matches AS_Run_Loop. Only a number after an " +
                "underscore at the very end; the 01 in Attack_01 is left alone.");

            Text(PrefixKey, "Strip prefixes",
                "Removed from the start of both names before comparing. Comma separated.");

            Text(SuffixKey, "Strip suffixes", "The same, at the end.");

            // One check covering every input, because every one of them changes the
            // answer and there is no button left to ask with.
            if (EditorGUI.EndChangeCheck())
            {
                EditorPrefs.SetString(SourceKey, AssetDatabase.GetAssetPath(source));
                Rescan();
            }
        }

        private static void Toggle(string key, bool fallback, string label, string tooltip)
        {
            EditorPrefs.SetBool(key, EditorGUILayout.Toggle(
                new GUIContent(label, tooltip), EditorPrefs.GetBool(key, fallback)));
        }

        private static void Text(string key, string label, string tooltip)
        {
            // Delayed, so a rescan happens on Enter or on leaving the field rather
            // than once per keystroke across four hundred clips.
            EditorPrefs.SetString(key, EditorGUILayout.DelayedTextField(
                new GUIContent(label, tooltip), EditorPrefs.GetString(key, string.Empty)));
        }

        private void Scan()
        {
            rows.Clear();
            scanned = true;

            // With the folder box off the destination follows the models, and the models
            // can change under it -- so it is settled here rather than trusted from the
            // last time the window drew itself.
            if (!EditorPrefs.GetBool(ElsewhereKey, true))
            {
                targetFolder = FolderOfSources();
            }

            string targetPath = targetFolder;

            AnimationClipSyncUtility.NameRules rules = Rules;

            var models = new List<string>();
            if (pinned.Count > 0)
            {
                models.AddRange(pinned);
            }
            else
            {
                models.AddRange(AnimationClipSyncUtility.ModelsUnder(AssetDatabase.GetAssetPath(source)));
            }

            var sources = new Dictionary<string, List<AnimationClip>>();
            foreach (string model in models)
            {
                foreach (AnimationClip clip in AnimationClipSyncUtility.ClipsInModel(model))
                {
                    Bucket(sources, rules.Apply(clip.name)).Add(clip);
                }
            }

            var targets = new Dictionary<string, List<AnimationClip>>();
            foreach (AnimationClip clip in AnimationClipSyncUtility.ClipsInFolder(targetPath, true))
            {
                Bucket(targets, rules.Apply(clip.name)).Add(clip);
            }

            foreach (KeyValuePair<string, List<AnimationClip>> entry in sources)
            {
                targets.TryGetValue(entry.Key, out List<AnimationClip> matches);
                AddRows(entry.Value, matches);
            }

            foreach (KeyValuePair<string, List<AnimationClip>> entry in targets)
            {
                if (sources.ContainsKey(entry.Key))
                {
                    continue;
                }

                foreach (AnimationClip orphan in entry.Value)
                {
                    rows.Add(new Row
                    {
                        Target = orphan,
                        Outcome = Outcome.Orphan,
                        Enabled = false,
                        Note = "no take of this name in the new import"
                    });
                }
            }

            rows.Sort((a, b) => string.CompareOrdinal(Name(a), Name(b)));

            ResolveCharacter(models);
            sourceModels.Clear();
            sourceModels.AddRange(models);
            Annotate();
            Summarise();
        }

        /// <summary>
        /// Decides which character the rig check runs against, when the field was not
        /// filled in by hand.
        ///
        /// The destination folder comes first, and it is the only place searched. The
        /// preview model is the fallback, but only as a guess: it is one global value
        /// for the whole project, so on the second character it names the wrong rig,
        /// and the check would then call every take broken.
        /// </summary>
        private void ResolveCharacter(List<string> models)
        {
            if (characterChosen)
            {
                return;
            }

            GameObject found = AnimationRigCheck.FindCharacter(targetFolder, models);

            character = found != null ? found : ScenePreviewSpawner.PreviewModel();
            characterGuessed = found == null && character != null;
        }

        /// <summary>
        /// Fills in the two columns that are about consequence rather than matching:
        /// how much of the project is holding on to the clip about to be rewritten,
        /// and whether the incoming animation can drive the character at all.
        /// </summary>
        private void Annotate()
        {
            AnimationRigCheck.Skeleton skeleton =
                AnimationRigCheck.Read(character as GameObject);

            // A guessed character that does not fit is news about the guess, not about
            // the clips. Reporting every take as broken because the preview model
            // happens to be another character is worse than not checking: the warning
            // is loud, it is wrong, and after the second time it gets ignored.
            string ignored = string.Empty;

            if (characterGuessed && !skeleton.IsEmpty && !FitsAnyRow(skeleton))
            {
                ignored = skeleton.Name;
                character = null;
                characterGuessed = false;
                skeleton = new AnimationRigCheck.Skeleton();
            }

            bool counting = AssetUsageIndex.EnsureBuilt();
            problemRows = 0;

            try
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    if (i % 20 == 0 && EditorUtility.DisplayCancelableProgressBar(
                            "Checking clips", Name(rows[i]), (float)i / rows.Count))
                    {
                        break;
                    }

                    Row row = rows[i];

                    if (counting && row.Target != null)
                    {
                        row.References = AssetUsageIndex.UsersOf(
                            AssetDatabase.GetAssetPath(row.Target)).Count;
                    }

                    row.Rig = AnimationRigCheck.Check(row.Source, skeleton);

                    if (row.Rig != null && row.Rig.IsProblem)
                    {
                        problemRows++;
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            rigWarning = string.Empty;

            if (!string.IsNullOrEmpty(ignored))
            {
                rigWarning =
                    $"Nothing checked whether these clips fit the rig they are meant to " +
                    $"drive. There is no character in {targetFolder} to compare against — " +
                    "an animation-only export carries no skinned mesh — and the preview " +
                    $"model ({ignored}) is a different rig from this import, so it was not " +
                    "used. Set Character if you want the check.";
            }
            else if (skeleton.IsEmpty)
            {
                rigWarning = "No character set, so nothing checked whether these clips fit " +
                             "the rig they are meant to drive.";
            }
            else if (problemRows > 0)
            {
                Row first = rows.Find(r => r.Rig != null && r.Rig.IsProblem);

                rigWarning =
                    $"{problemRows} of {rows.Count} takes do not fit {skeleton.Name}: " +
                    $"{first?.Rig.Note}\n\nWriting them will not throw and will not log " +
                    "anything. The clips will simply animate nothing.";
            }
            else
            {
                rigWarning = RestPoseWarning(skeleton);
            }
        }

        /// <summary>
        /// The warning for takes whose bones all exist on the character but do not start
        /// where the character's do.
        ///
        /// This is the failure that gets through the path check, and it is the expensive
        /// one: every name matches, nothing is reported, and the clips are written. Then
        /// the character plays them folded into shapes it cannot make, which reads as a
        /// broken export — and the export is fine. What is broken is that the character
        /// and the takes came out of two different files.
        ///
        /// Export the same asset as FBX and as glTF and the two disagree about which way
        /// the root faces, by a full 180 degrees. Either file animates correctly on its
        /// own rig. Neither animates the other's.
        /// </summary>
        private string RestPoseWarning(AnimationRigCheck.Skeleton skeleton)
        {
            if (skeleton.IsEmpty || sourceModels.Count == 0)
            {
                return string.Empty;
            }

            foreach (string path in sourceModels)
            {
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                AnimationRigCheck.BindResult rest =
                    AnimationRigCheck.CompareRest(model, character as GameObject);

                if (!rest.IsProblem)
                {
                    continue;
                }

                return
                    $"Every bone matches, but {rest.Differing} of {rest.Compared} of them do " +
                    $"not start where {skeleton.Name}'s do — worst {rest.Worst:0} degrees on " +
                    $"{rest.WorstBone}.\n\n" +
                    $"{System.IO.Path.GetFileName(path)} and {skeleton.Name} came out of " +
                    "different exports of the same asset. A clip carries local rotations, " +
                    "not poses, so on this character it will animate the difference and the " +
                    "body will fold into shapes it cannot make. Nothing will be logged.\n\n" +
                    "Take the character and the animations from the same export.";
            }

            return string.Empty;
        }

        /// <summary>
        /// Whether any incoming take fits the skeleton. One take is enough to answer it
        /// -- an import comes off one rig -- and it runs before the per-row check so the
        /// answer is known while it still decides which character to check against.
        /// </summary>
        private bool FitsAnyRow(AnimationRigCheck.Skeleton skeleton)
        {
            foreach (Row row in rows)
            {
                if (row.Source == null)
                {
                    continue;
                }

                AnimationRigCheck.Result result = AnimationRigCheck.Check(row.Source, skeleton);
                return result == null || !result.IsProblem;
            }

            return true;
        }

        private void AddRows(List<AnimationClip> sourceClips, List<AnimationClip> targetClips)
        {
            foreach (AnimationClip clip in sourceClips)
            {
                var row = new Row { Source = clip };

                if (sourceClips.Count > 1)
                {
                    row.Outcome = Outcome.Ambiguous;
                    row.Enabled = false;
                    row.Note = $"{sourceClips.Count} takes in the new import share this name";
                }
                else if (targetClips == null)
                {
                    row.Outcome = Outcome.Create;
                    row.Note = "no clip of this name in the project yet";
                }
                else if (targetClips.Count > 1)
                {
                    row.Outcome = Outcome.Ambiguous;
                    row.Enabled = false;
                    row.Note = $"{targetClips.Count} existing clips share this name";
                }
                else
                {
                    row.Outcome = Outcome.Update;
                    row.Target = targetClips[0];
                }

                rows.Add(row);
            }
        }

        private static List<AnimationClip> Bucket(Dictionary<string, List<AnimationClip>> map, string key)
        {
            if (!map.TryGetValue(key, out List<AnimationClip> list))
            {
                list = new List<AnimationClip>();
                map[key] = list;
            }

            return list;
        }

        private static string Name(Row row)
        {
            return row.Source != null ? row.Source.name : row.Target.name;
        }

        private void Summarise()
        {
            int update = 0, create = 0, ambiguous = 0, orphan = 0;

            foreach (Row row in rows)
            {
                switch (row.Outcome)
                {
                    case Outcome.Update: update++; break;
                    case Outcome.Create: create++; break;
                    case Outcome.Ambiguous: ambiguous++; break;
                    case Outcome.Orphan: orphan++; break;
                }
            }

            int held = 0;
            int referenced = 0;

            foreach (Row row in rows)
            {
                if (row.Outcome != Outcome.Update || row.References <= 0)
                {
                    continue;
                }

                referenced++;
                held += row.References;
            }

            summary = $"{update} to update in place   ·   {create} with no counterpart yet   ·   " +
                      $"{ambiguous} ambiguous   ·   {orphan} existing clips the import does not cover\n" +
                      $"{referenced} of the {update} being updated are referenced by something " +
                      $"({held} reference(s) in all), and keep those references. " +
                      $"{update - referenced} are referenced by nothing.";
        }

        private void DrawSummary()
        {
            if (!scanned)
            {
                return;
            }

            EditorGUILayout.Space(6);

            if (!string.IsNullOrEmpty(rigWarning))
            {
                EditorGUILayout.HelpBox(
                    rigWarning,
                    problemRows > 0 ? MessageType.Error : MessageType.Info);
            }

            EditorGUILayout.LabelField(summary, EditorStyles.miniLabel, GUILayout.Height(28f));

            EditorGUILayout.BeginHorizontal();

            filter = (Filter)GUILayout.Toolbar((int)filter, new[] { "All", "Update", "New", "Unmatched" });

            // Four hundred rows arrive ticked, and the sensible first run is three of
            // them on a branch you can throw away. Without this, that means four
            // hundred clicks, and nobody does the trial run.
            if (GUILayout.Button("tick all", EditorStyles.miniButtonLeft, GUILayout.Width(60f)))
            {
                SetVisible(true);
            }

            if (GUILayout.Button("none", EditorStyles.miniButtonRight, GUILayout.Width(50f)))
            {
                SetVisible(false);
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(2);
        }

        private void SetVisible(bool enabled)
        {
            foreach (Row row in rows)
            {
                if (Matches(row) && row.Outcome != Outcome.Orphan)
                {
                    row.Enabled = enabled;
                }
            }
        }

        private void DrawTable()
        {
            visible.Clear();
            foreach (Row row in rows)
            {
                if (Matches(row))
                {
                    visible.Add(row);
                }
            }

            // GetRect takes a height, not a maximum: asking for ten thousand made the
            // table swallow the window and push the Apply button off the bottom.
            // Sixty is the floor, and ExpandHeight gives it whatever the fixed rows
            // above and below have not claimed.
            Rect viewport = GUILayoutUtility.GetRect(
                0f, 60f, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));

            GUI.Box(viewport, GUIContent.none, EditorStyles.helpBox);

            if (!scanned)
            {
                EditorGUI.LabelField(viewport, "Pick the two folders and press Scan.", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            if (visible.Count == 0)
            {
                EditorGUI.LabelField(viewport, "Nothing in this filter.", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            var content = new Rect(0f, 0f, viewport.width - 18f, visible.Count * RowHeight);
            scroll = GUI.BeginScrollView(viewport, scroll, content);

            // Four hundred rows of controls is more than IMGUI wants to draw every
            // repaint, and all but a screenful of them are off the top or bottom.
            int first = Mathf.Max(0, Mathf.FloorToInt(scroll.y / RowHeight) - 1);
            int last = Mathf.Min(visible.Count, first + Mathf.CeilToInt(viewport.height / RowHeight) + 2);

            for (int i = first; i < last; i++)
            {
                DrawRow(new Rect(2f, i * RowHeight, content.width - 4f, RowHeight), visible[i]);
            }

            GUI.EndScrollView();
        }

        private bool Matches(Row row)
        {
            switch (filter)
            {
                case Filter.Update: return row.Outcome == Outcome.Update;
                case Filter.New: return row.Outcome == Outcome.Create;
                case Filter.Unmatched: return row.Outcome == Outcome.Ambiguous || row.Outcome == Outcome.Orphan;
                default: return true;
            }
        }

        private static void DrawRow(Rect rect, Row row)
        {
            bool actionable = row.Outcome != Outcome.Orphan;
            bool broken = row.Rig != null && row.Rig.IsProblem;

            using (new EditorGUI.DisabledScope(!actionable))
            {
                row.Enabled = EditorGUI.Toggle(new Rect(rect.x, rect.y + 2f, 16f, 16f), row.Enabled);
            }

            EditorGUI.LabelField(
                new Rect(rect.x + 20f, rect.y + 1f, 74f, 18f),
                new GUIContent(Label(row.Outcome), row.Note),
                EditorStyles.miniLabel);

            EditorGUI.LabelField(
                new Rect(rect.x + 96f, rect.y + 1f, 62f, 18f),
                new GUIContent(
                    References(row),
                    row.References > 0
                        ? $"{row.References} asset(s) reference this clip, and go on referencing it"
                        : "nothing in the project references this clip"),
                EditorStyles.miniLabel);

            float fieldWidth = Mathf.Min(230f, rect.width * 0.35f);
            float nameWidth = rect.width - 162f - fieldWidth - 8f;

            Color previous = GUI.color;
            if (broken)
            {
                GUI.color = new Color(1f, 0.5f, 0.4f);
            }

            EditorGUI.LabelField(
                new Rect(rect.x + 162f, rect.y + 1f, nameWidth, 18f),
                new GUIContent(
                    broken ? "⚠ " + Name(row) : Name(row),
                    broken ? row.Rig.Note : row.Note));

            GUI.color = previous;

            row.Target = (AnimationClip)EditorGUI.ObjectField(
                new Rect(rect.xMax - fieldWidth, rect.y + 1f, fieldWidth, 18f),
                row.Target, typeof(AnimationClip), false);
        }

        private static string References(Row row)
        {
            if (row.Outcome == Outcome.Create)
            {
                return "—";
            }

            return row.References == 0 ? "no refs" : $"{row.References} ref(s)";
        }

        private static string Label(Outcome outcome)
        {
            switch (outcome)
            {
                case Outcome.Update: return "update";
                case Outcome.Create: return "new";
                case Outcome.Ambiguous: return "ambiguous";
                default: return "not covered";
            }
        }

        private void DrawFooter()
        {
            EditorGUILayout.Space(4);

            Toggle(PlaybackKey, true, "Keep Loop Time and pose",
                "The incoming clip brings the importer's settings. This puts back the " +
                "Loop Time, cycle offset, mirror and loop pose flags the existing clip " +
                "had, which is where the work of the last pass went.");

            Toggle(RootMotionKey, true, "Keep root motion",
                "Re-runs the root motion conversion on any clip that had it, using the " +
                "root bone from the preferences.");

            Toggle(CreateKey, true, "Create the ones with no counterpart",
                "Extracts takes that have no existing clip into new .anim assets in the " +
                "clips folder.");

            Toggle(CompressKey, true, "Compress on write",
                "Runs keyframe reduction on every clip written, which is what Unity's " +
                "model importer would have done and what an extracted clip no longer " +
                "has. Takes out of a glb or a USD arrive baked — one key per bone per " +
                "frame — and without this they land many times the size of the same " +
                "animation through an FBX. Tolerances live in Window › Fofuxo's " +
                "Animation Tools › Compress Animation Clips.");

            EditorGUILayout.BeginHorizontal();

            using (new EditorGUI.DisabledScope(!scanned))
            {
                if (GUILayout.Button(
                        new GUIContent(
                            "Select the ones not covered",
                            "Selects the existing clips the new import has nothing for, so " +
                            "you can look at them, or delete them and be told what breaks."),
                        GUILayout.Width(190f)))
                {
                    SelectOrphans();
                }
            }

            GUILayout.FlexibleSpace();

            using (new EditorGUI.DisabledScope(!scanned || rows.Count == 0))
            {
                if (GUILayout.Button("Apply", GUILayout.Width(120f), GUILayout.Height(24f)))
                {
                    Apply();
                }
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(4);
        }

        /// <summary>
        /// The one thing worth stopping for. Overwriting a clip that something
        /// references with animation from the wrong rig is the failure that leaves no
        /// trace: the reference survives, the file is valid, the character stands
        /// still, and the reason is four steps back.
        /// </summary>
        private bool Confirm()
        {
            int broken = 0;
            int referenced = 0;

            foreach (Row row in rows)
            {
                if (!row.Enabled || row.Source == null || row.Rig == null || !row.Rig.IsProblem)
                {
                    continue;
                }

                broken++;

                if (row.References > 0)
                {
                    referenced++;
                }
            }

            if (broken == 0)
            {
                return true;
            }

            string skeleton = character != null ? character.name : "the character";

            return EditorUtility.DisplayDialog(
                "The rig does not match",
                $"{broken} of the takes about to be written do not fit {skeleton}.\n\n" +
                (referenced > 0
                    ? $"{referenced} of them would overwrite a clip that something in the " +
                      "project is using, replacing working animation with animation that " +
                      "moves nothing.\n\n"
                    : string.Empty) +
                "Nothing will throw and nothing will be logged. The clips will simply " +
                "animate nothing.\n\nThere is no undo.",
                $"Write all {broken} anyway",
                "Cancel");
        }

        private void SelectOrphans()
        {
            var orphans = new List<Object>();

            foreach (Row row in rows)
            {
                if (row.Outcome == Outcome.Orphan)
                {
                    orphans.Add(row.Target);
                }
            }

            Selection.objects = orphans.ToArray();

            if (orphans.Count == 0)
            {
                Debug.Log("Every existing clip is covered by the new import.");
            }
        }

        /// <summary>
        /// Runs the keyframe reduction over everything that was just written.
        ///
        /// A take that arrives baked — which is every take out of a glb or a USD, since
        /// their exporters sample per frame — lands as one key per bone per frame, and
        /// on this project that is roughly forty times what the same animation costs
        /// coming through Unity's own model importer. Reduction is what the importer
        /// would have done, and an extracted clip has no importer, so it happens here.
        ///
        /// It runs on clips out of an FBX too, and takes almost nothing off them: they
        /// went through Keyframe Reduction already. Doing it to everything is worth the
        /// wasted pass, because a rule that reads "compresses, unless the file it came
        /// from was of this kind" is a rule nobody can predict the output of.
        /// </summary>
        private static ClipCompressionUtility.Report CompressWritten(List<AnimationClip> clips)
        {
            var total = new ClipCompressionUtility.Report();

            if (clips.Count == 0 || !EditorPrefs.GetBool(CompressKey, true))
            {
                return total;
            }

            ClipCompressionUtility.Tolerances tolerances = ClipCompressionUtility.Tolerances.Stored;

            try
            {
                for (int i = 0; i < clips.Count; i++)
                {
                    if (EditorUtility.DisplayCancelableProgressBar(
                            "Compressing clips",
                            $"{clips[i].name}  ({i + 1}/{clips.Count})",
                            (float)i / clips.Count))
                    {
                        break;
                    }

                    total.Add(ClipCompressionUtility.Compress(clips[i], tolerances));
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (total.ChangedClips > 0)
            {
                AssetDatabase.SaveAssets();
            }

            return total;
        }

        private void Apply()
        {
            if (!Confirm())
            {
                return;
            }

            AnimationClipSyncUtility.Preserve preserve = AnimationClipSyncUtility.Preserve.None;

            if (EditorPrefs.GetBool(PlaybackKey, true))
            {
                preserve |= AnimationClipSyncUtility.Preserve.Playback;
            }

            if (EditorPrefs.GetBool(RootMotionKey, true))
            {
                preserve |= AnimationClipSyncUtility.Preserve.RootMotion;
            }

            bool creating = EditorPrefs.GetBool(CreateKey, true);
            string folder = targetFolder;
            string rootBone = FofuxoToolsSettings.RootBone;

            int updated = 0;
            int created = 0;
            int skipped = 0;

            // Collected while writing, compressed afterwards. A clip created inside a
            // StartAssetEditing block is not a registered asset yet, and the compressor
            // refuses anything that is not one — inside the block it would quietly do
            // nothing to every clip it was just handed.
            var written = new List<AnimationClip>();

            try
            {
                AssetDatabase.StartAssetEditing();

                for (int i = 0; i < rows.Count; i++)
                {
                    Row row = rows[i];

                    if (!row.Enabled || row.Source == null)
                    {
                        continue;
                    }

                    if (i % 10 == 0 && EditorUtility.DisplayCancelableProgressBar(
                            "Syncing animation clips",
                            $"{row.Source.name}  ({i + 1}/{rows.Count})",
                            (float)i / rows.Count))
                    {
                        break;
                    }

                    if (row.Target != null)
                    {
                        if (AnimationClipSyncUtility.UpdateInPlace(row.Source, row.Target, preserve, rootBone))
                        {
                            updated++;
                            written.Add(row.Target);
                        }
                        else
                        {
                            skipped++;
                        }
                    }
                    else if (creating)
                    {
                        AnimationClip extracted = AnimationClipSyncUtility.Extract(row.Source, folder);

                        if (extracted != null)
                        {
                            created++;
                            written.Add(extracted);
                        }
                        else
                        {
                            skipped++;
                        }
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                EditorUtility.ClearProgressBar();
                AssetDatabase.SaveAssets();
            }

            ClipCompressionUtility.Report compression = CompressWritten(written);

            Debug.Log(
                $"Updated {updated} clip(s) in place, created {created}, skipped {skipped}. " +
                "Nothing that referenced the updated clips had to change." +
                (compression.Clips > 0 ? $" Compressed: {compression}." : string.Empty));

            // Closing from inside OnGUI tears the layout down mid-draw. One frame
            // later there is no layout left to upset.
            EditorApplication.delayCall += Close;
        }
    }
}
