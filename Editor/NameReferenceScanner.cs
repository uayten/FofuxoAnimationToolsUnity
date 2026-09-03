using System.Collections.Generic;
using System.IO;
using UnityEditor;

namespace FofuxoAnimationTools.Editor
{
    /// <summary>
    /// Finds the references that are not references.
    ///
    /// <see cref="AssetUsageIndex"/> answers the question the asset database can
    /// answer: who holds a GUID pointing at this. It is blind to the other kind. A
    /// tool that stores `clipName: AS_Run_Combat_Loop_Seq` and looks the clip up by
    /// name at runtime is using that clip every bit as much, and the asset database
    /// has no idea. Delete it and the index says nobody minded.
    ///
    /// This is a plain text search for the asset's name across the files that could
    /// hold such a thing. It cannot be remapped -- rewriting a string reference means
    /// understanding what the string means to whoever wrote it -- so the only useful
    /// thing to do with the answer is show it before the delete goes through.
    ///
    /// Clips and other bulk data are deliberately not searched. A `.anim` is
    /// megabytes of curve data that never names another asset, and there are four
    /// hundred of them.
    /// </summary>
    public static class NameReferenceScanner
    {
        private static readonly HashSet<string> Searchable = new HashSet<string>
        {
            ".asset", ".prefab", ".unity", ".controller", ".overridecontroller",
            ".playable", ".mat", ".cs"
        };

        /// <summary>
        /// Which of the given names appear, as whole words, in which files. Names
        /// shorter than this are skipped -- "Idle" occurs in half a project and every
        /// hit would be noise.
        /// </summary>
        private const int ShortestName = 6;

        public static Dictionary<string, List<string>> Find(
            IEnumerable<string> assetPaths, ICollection<string> ignore)
        {
            var byName = new Dictionary<string, string>();

            foreach (string path in assetPaths)
            {
                string name = Path.GetFileNameWithoutExtension(path);

                if (name.Length >= ShortestName && !byName.ContainsKey(name))
                {
                    byName[name] = path;
                }
            }

            var hits = new Dictionary<string, List<string>>();
            if (byName.Count == 0)
            {
                return hits;
            }

            List<string> files = Candidates(ignore);

            try
            {
                for (int i = 0; i < files.Count; i++)
                {
                    if (i % 20 == 0 && EditorUtility.DisplayCancelableProgressBar(
                            "Looking for references by name",
                            Path.GetFileName(files[i]),
                            (float)i / files.Count))
                    {
                        break;
                    }

                    Scan(files[i], byName, hits);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            return hits;
        }

        private static List<string> Candidates(ICollection<string> ignore)
        {
            var files = new List<string>();

            foreach (string path in AssetDatabase.GetAllAssetPaths())
            {
                if (!path.StartsWith("Assets/") || (ignore != null && ignore.Contains(path)))
                {
                    continue;
                }

                if (Searchable.Contains(Path.GetExtension(path).ToLowerInvariant()))
                {
                    files.Add(path);
                }
            }

            return files;
        }

        private static void Scan(
            string file,
            Dictionary<string, string> byName,
            Dictionary<string, List<string>> hits)
        {
            string text;

            try
            {
                text = File.ReadAllText(file);
            }
            catch (IOException)
            {
                return;
            }

            // Walking the identifiers rather than searching per name keeps this one
            // pass over the text instead of one pass per name, and it is what makes
            // the match a whole word: AS_Run never matches AS_Run_Fast.
            int i = 0;

            while (i < text.Length)
            {
                if (!IsWordCharacter(text[i]))
                {
                    i++;
                    continue;
                }

                int start = i;
                while (i < text.Length && IsWordCharacter(text[i]))
                {
                    i++;
                }

                if (i - start < ShortestName)
                {
                    continue;
                }

                string word = text.Substring(start, i - start);

                if (!byName.TryGetValue(word, out string asset))
                {
                    continue;
                }

                // An object saying what it is called is not a reference to anything.
                // A character prefab holds a GameObject named Tigre_Grant and its
                // avatar holds a bone by that name; neither would notice the FBX going
                // away, and reporting them buries the one hit that would.
                if (IsOwnName(text, start))
                {
                    continue;
                }

                if (!hits.TryGetValue(asset, out List<string> holders))
                {
                    holders = new List<string>();
                    hits[asset] = holders;
                }

                if (!holders.Contains(file))
                {
                    holders.Add(file);
                }
            }
        }

        private static bool IsWordCharacter(char character)
        {
            return char.IsLetterOrDigit(character) || character == '_';
        }

        /// <summary>
        /// True when the word is something naming itself rather than pointing at
        /// anything. Three shapes, all of which a character called Tigre_Grant
        /// produces in quantity:
        ///
        ///   m_Name: Tigre_Grant          an object saying what it is called
        ///   3000315205: Tigre_Grant      a hash-to-name table, which is how an
        ///                                Avatar stores the names of its bones
        ///   value: Tigre_Grant           under propertyPath: m_Name -- a prefab
        ///                                override of a name
        ///
        /// None of them would notice the asset going away, and reporting them buries
        /// the one hit that would.
        /// </summary>
        private static bool IsOwnName(string text, int wordStart)
        {
            int line = LineStart(text, wordStart);

            foreach (string field in OwnName)
            {
                if (Begins(text, line, field))
                {
                    return true;
                }
            }

            if (IsNumberedEntry(text, line, wordStart))
            {
                return true;
            }

            return Begins(text, line, "value:") && OverridesAName(text, line);
        }

        private static readonly string[] OwnName =
        {
            "m_Name:", "name:", "m_TagString:", "m_SkeletonName:"
        };

        /// <summary>Where the line containing this index begins, indentation included.</summary>
        private static int RawLineStart(string text, int index)
        {
            return index <= 0 ? 0 : text.LastIndexOf('\n', index - 1) + 1;
        }

        private static int Trim(string text, int start, int limit)
        {
            while (start < limit &&
                   (text[start] == ' ' || text[start] == '\t' || text[start] == '-'))
            {
                start++;
            }

            return start;
        }

        /// <summary>Where the content of the line containing this index begins.</summary>
        private static int LineStart(string text, int index)
        {
            return Trim(text, RawLineStart(text, index), index);
        }

        private static bool Begins(string text, int at, string word)
        {
            return at + word.Length <= text.Length &&
                   string.CompareOrdinal(text, at, word, 0, word.Length) == 0;
        }

        /// <summary>A key that is only digits, as in a hash table keyed by name.</summary>
        private static bool IsNumberedEntry(string text, int line, int wordStart)
        {
            int i = line;

            while (i < wordStart && char.IsDigit(text[i]))
            {
                i++;
            }

            return i > line && i < wordStart && text[i] == ':';
        }

        /// <summary>
        /// Whether the value on this line belongs to a prefab override of a name. The
        /// property being overridden is written a line or two above it.
        /// </summary>
        private static bool OverridesAName(string text, int line)
        {
            // Walking back from the trimmed start lands on the trimmed start again --
            // the indentation is still on the same line -- so the raw start is what
            // has to be carried.
            int raw = RawLineStart(text, line);

            for (int back = 0; back < 3 && raw > 0; back++)
            {
                int previous = RawLineStart(text, raw - 1);
                int content = Trim(text, previous, raw);

                if (Begins(text, content, "propertyPath:"))
                {
                    int value = content + "propertyPath:".Length;

                    while (value < text.Length && text[value] == ' ')
                    {
                        value++;
                    }

                    return Begins(text, value, "m_Name");
                }

                raw = previous;
            }

            return false;
        }
    }
}
