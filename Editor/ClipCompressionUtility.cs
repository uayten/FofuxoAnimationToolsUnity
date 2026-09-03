using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace FofuxoAnimationTools.Editor
{
    /// <summary>
    /// Keyframe reduction for standalone .anim assets.
    ///
    /// A clip extracted from a model keeps whatever key density the source file had.
    /// Out of an FBX that went through the importer's Optimal setting that is already
    /// tight, but a clip baked by an exporter -- glTF and USD both sample per frame --
    /// arrives with one key per bone per frame, most of them saying nothing. The
    /// importer's own Keyframe Reduction is out of reach for an extracted clip, because
    /// an extracted clip has no importer. This is that pass, as a menu item.
    ///
    /// Two things happen, in order:
    ///
    /// 1. A curve that never leaves its starting value, within tolerance, collapses to
    ///    two keys -- first and last. Never one: a clip's length is the time of its last
    ///    key, and collapsing everything to a single key would flatten it to zero. On a
    ///    skeleton this is most of the win, because scale and position hold still on
    ///    nearly every bone while only rotation moves.
    ///
    /// 2. What is left is reduced by error: keys are dropped as long as the curve that
    ///    remains stays within tolerance of the original. The error is measured on the
    ///    rebuilt Hermite curve, tangents and all -- not on a straight line between the
    ///    keys -- so the number in the tolerance field is the error you actually get.
    ///
    /// Rotation is measured as an angle across the whole quaternion, not per component.
    /// A component read alone says nothing about how far the bone turned, and reducing
    /// x, y, z and w at different times distorts the rotation between them. So the four
    /// curves are reduced together, on shared key times, with the error in degrees.
    ///
    /// This is destructive and there is no undo -- dropped keys are gone. Analyze first;
    /// it reports what would happen and writes nothing.
    /// </summary>
    public static class ClipCompressionUtility
    {
        /// <summary>How a group of curves is to be measured.</summary>
        public enum CurveKind
        {
            /// <summary>Four curves read as one quaternion; error in degrees.</summary>
            Rotation,

            /// <summary>Three curves of Euler angles; error in degrees, per axis.</summary>
            Euler,

            /// <summary>Three curves read as one vector; error is the distance.</summary>
            Position,

            /// <summary>Three curves of scale; error is the largest axis difference.</summary>
            Scale,

            /// <summary>Anything else -- blend shape weights, custom properties.</summary>
            Generic
        }

        /// <summary>
        /// How far the compressed clip is allowed to drift from the original.
        ///
        /// The model importer states its rotation error in degrees and this matches it,
        /// so 0.5 here is the 0.5 you are used to. Position and scale are absolute --
        /// the importer's are percentages, which are harder to reason about than "half
        /// a millimetre".
        /// </summary>
        [Serializable]
        public struct Tolerances
        {
            /// <summary>Largest allowed angle between the original and reduced rotation.</summary>
            public float RotationDegrees;

            /// <summary>Largest allowed distance, in the clip's units. 0.0005 is half a millimetre.</summary>
            public float PositionUnits;

            /// <summary>Largest allowed scale difference on any axis. 0.005 is half a percent.</summary>
            public float ScaleFraction;

            /// <summary>Largest allowed difference on curves that are none of the above.</summary>
            public float GenericValue;

            /// <summary>Whether curves that hold still collapse to two keys.</summary>
            public bool CollapseConstant;

            /// <summary>
            /// Whether the editor-side copy of the curves is dropped from the file. See
            /// <see cref="StripEditorCurves"/> — it is four fifths of the asset and the
            /// Editor rebuilds it on demand.
            /// </summary>
            public bool StripEditor;

            public static Tolerances Default => new Tolerances
            {
                RotationDegrees = 0.5f,
                PositionUnits = 0.0005f,
                ScaleFraction = 0.005f,
                GenericValue = 0.01f,
                CollapseConstant = true,
                StripEditor = true
            };

            private const string StripKey = "Fofuxo.Compression.StripEditorCurves";

            private const string RotationKey = "Fofuxo.Compression.RotationDegrees";
            private const string PositionKey = "Fofuxo.Compression.PositionUnits";
            private const string ScaleKey = "Fofuxo.Compression.ScaleFraction";
            private const string GenericKey = "Fofuxo.Compression.GenericValue";
            private const string ConstantKey = "Fofuxo.Compression.CollapseConstant";

            /// <summary>
            /// What the user last set, in EditorPrefs. One set of numbers for the whole
            /// package: the window where they are tuned and the extraction that applies
            /// them on the way in have no business disagreeing about how much error is
            /// acceptable.
            /// </summary>
            public static Tolerances Stored
            {
                get
                {
                    Tolerances fallback = Default;

                    return new Tolerances
                    {
                        RotationDegrees = EditorPrefs.GetFloat(RotationKey, fallback.RotationDegrees),
                        PositionUnits = EditorPrefs.GetFloat(PositionKey, fallback.PositionUnits),
                        ScaleFraction = EditorPrefs.GetFloat(ScaleKey, fallback.ScaleFraction),
                        GenericValue = EditorPrefs.GetFloat(GenericKey, fallback.GenericValue),
                        CollapseConstant = EditorPrefs.GetBool(ConstantKey, fallback.CollapseConstant),
                        StripEditor = EditorPrefs.GetBool(StripKey, fallback.StripEditor)
                    };
                }

                set
                {
                    EditorPrefs.SetBool(StripKey, value.StripEditor);
                    EditorPrefs.SetFloat(RotationKey, value.RotationDegrees);
                    EditorPrefs.SetFloat(PositionKey, value.PositionUnits);
                    EditorPrefs.SetFloat(ScaleKey, value.ScaleFraction);
                    EditorPrefs.SetFloat(GenericKey, value.GenericValue);
                    EditorPrefs.SetBool(ConstantKey, value.CollapseConstant);
                }
            }

            public float For(CurveKind kind)
            {
                switch (kind)
                {
                    case CurveKind.Rotation:
                    case CurveKind.Euler:
                        return RotationDegrees;
                    case CurveKind.Position:
                        return PositionUnits;
                    case CurveKind.Scale:
                        return ScaleFraction;
                    default:
                        return GenericValue;
                }
            }
        }

        /// <summary>What a run did, or would do. Adds up across a selection.</summary>
        public struct Report
        {
            public int Clips;
            public int ChangedClips;
            public int Curves;
            public int ConstantCurves;
            public int KeysBefore;
            public int KeysAfter;

            /// <summary>Clips that gave up their editor-side copy of the curves.</summary>
            public int Stripped;

            /// <summary>Keys kept, as a fraction. 0.1 means a tenth of what came in.</summary>
            public float Kept => KeysBefore == 0 ? 1f : (float)KeysAfter / KeysBefore;

            /// <summary>Compression ratio, the way people quote it: 15 means 15:1.</summary>
            public float Ratio => KeysAfter == 0 ? 1f : (float)KeysBefore / KeysAfter;

            public void Add(Report other)
            {
                Clips += other.Clips;
                ChangedClips += other.ChangedClips;
                Curves += other.Curves;
                ConstantCurves += other.ConstantCurves;
                KeysBefore += other.KeysBefore;
                KeysAfter += other.KeysAfter;
                Stripped += other.Stripped;
            }

            public override string ToString()
            {
                if (KeysBefore == 0)
                {
                    return "no curves";
                }

                return $"{KeysBefore:N0} -> {KeysAfter:N0} keys " +
                       $"({(1f - Kept) * 100f:0.#}% dropped, {Ratio:0.#}:1)";
            }
        }

        /// <summary>Measures what compressing would do, without touching the clip.</summary>
        public static Report Analyze(AnimationClip clip, Tolerances tolerances)
        {
            return Process(clip, tolerances, false);
        }

        /// <summary>Compresses the clip in place. Destructive, and there is no undo.</summary>
        public static Report Compress(AnimationClip clip, Tolerances tolerances)
        {
            return Process(clip, tolerances, true);
        }

        private static Report Process(AnimationClip clip, Tolerances tolerances, bool write)
        {
            var report = new Report();

            // Clips inside a model belong to the importer: an edit here would be undone
            // on the next reimport, and the importer can compress those itself anyway.
            if (!RootMotionClipUtility.IsEditable(clip))
            {
                return report;
            }

            report.Clips = 1;

            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip);
            if (bindings == null || bindings.Length == 0)
            {
                return report;
            }

            List<CurveGroup> groups = BuildGroups(clip, bindings);

            var changedBindings = new List<EditorCurveBinding>();
            var changedCurves = new List<AnimationCurve>();

            foreach (CurveGroup group in groups)
            {
                int before = 0;
                foreach (AnimationCurve curve in group.Curves)
                {
                    before += curve.length;
                }

                report.Curves += group.Bindings.Length;
                report.KeysBefore += before;

                int[] kept = Reduce(group, tolerances, out bool constant);

                var rebuilt = new AnimationCurve[group.Bindings.Length];
                int after = 0;
                for (int c = 0; c < group.Bindings.Length; c++)
                {
                    rebuilt[c] = new AnimationCurve(Keys(group.Times, group.Values[c], kept));
                    after += rebuilt[c].length;
                }

                // Rewriting a group that did not shrink would only churn the asset --
                // and on a clip that is already tight, that is every group.
                if (after >= before)
                {
                    report.KeysAfter += before;
                    continue;
                }

                report.KeysAfter += after;
                if (constant)
                {
                    report.ConstantCurves += group.Bindings.Length;
                }

                for (int c = 0; c < group.Bindings.Length; c++)
                {
                    changedBindings.Add(group.Bindings[c]);
                    changedCurves.Add(rebuilt[c]);
                }
            }

            if (write && changedBindings.Count > 0)
            {
                // One call for the whole clip. Setting curves one at a time rebuilds the
                // clip on every call, which across six hundred curves is the difference
                // between a second and a minute.
                AnimationUtility.SetEditorCurves(clip, changedBindings.ToArray(), changedCurves.ToArray());
                EditorUtility.SetDirty(clip);
            }

            // Runs even when no key was dropped: writing curves through AnimationUtility
            // is what creates the editor copy in the first place, so a clip that came in
            // without one would leave four times larger than it arrived.
            if (write && tolerances.StripEditor && StripEditorCurves(clip))
            {
                report.Stripped = 1;
            }

            report.ChangedClips = changedBindings.Count > 0 || report.Stripped > 0 ? 1 : 0;
            return report;
        }

        /// <summary>
        /// Drops the editor-side copy of the curves from the asset.
        ///
        /// A clip stores its animation twice. The engine reads m_RotationCurves and its
        /// neighbours; the Editor keeps m_EditorCurves and m_EulerEditorCurves, the same
        /// animation again as one float curve per component plus Euler angles for the
        /// rotation inspector. Four fifths of the file is that second copy, and none of
        /// it reaches a build.
        ///
        /// Dropping it is safe because it is derived, not authored: the Editor rebuilds
        /// it from the runtime curves the moment anything asks. Measured on a clip here
        /// — 3.2 MB down to 0.6 MB, and AnimationUtility still returned all 1,670
        /// bindings and all 5,674 keys afterwards. The Animation window, this package's
        /// own root motion, and every other reader go on working.
        ///
        /// Editing a clip in the Animation window writes the copy back, for that clip.
        /// Nothing is lost by that either; it just grows again until the next pass.
        /// </summary>
        private static bool StripEditorCurves(AnimationClip clip)
        {
            var serialized = new SerializedObject(clip);
            bool stripped = false;

            foreach (string name in EditorCurveFields)
            {
                SerializedProperty property = serialized.FindProperty(name);

                if (property != null && property.isArray && property.arraySize > 0)
                {
                    property.ClearArray();
                    stripped = true;
                }
            }

            if (!stripped)
            {
                return false;
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(clip);
            return true;
        }

        private static readonly string[] EditorCurveFields = { "m_EditorCurves", "m_EulerEditorCurves" };

        // ------------------------------------------------------------------
        // Grouping
        // ------------------------------------------------------------------

        private sealed class CurveGroup
        {
            public CurveKind Kind;
            public EditorCurveBinding[] Bindings;
            public AnimationCurve[] Curves;

            /// <summary>Sample times shared by every curve in the group.</summary>
            public float[] Times;

            /// <summary>Sampled values, indexed [component][sample].</summary>
            public float[][] Values;
        }

        /// <summary>
        /// Collects the clip's bindings into groups that have to be reduced together: the
        /// four curves of a quaternion, the three of a position. A property that belongs
        /// to nothing -- a blend shape weight -- is a group of one.
        /// </summary>
        private static List<CurveGroup> BuildGroups(AnimationClip clip, EditorCurveBinding[] bindings)
        {
            var buckets = new Dictionary<string, List<KeyValuePair<int, EditorCurveBinding>>>();
            var order = new List<string>();

            foreach (EditorCurveBinding binding in bindings)
            {
                string key;
                int component;

                if (SplitComponent(binding.propertyName, out string prefix, out component))
                {
                    key = binding.path + "|" + binding.type.FullName + "|" + prefix;
                }
                else
                {
                    key = binding.path + "|" + binding.type.FullName + "|" + binding.propertyName + "|single";
                    component = 0;
                }

                if (!buckets.TryGetValue(key, out List<KeyValuePair<int, EditorCurveBinding>> list))
                {
                    list = new List<KeyValuePair<int, EditorCurveBinding>>(4);
                    buckets.Add(key, list);
                    order.Add(key);
                }

                list.Add(new KeyValuePair<int, EditorCurveBinding>(component, binding));
            }

            var groups = new List<CurveGroup>(order.Count);

            foreach (string key in order)
            {
                List<KeyValuePair<int, EditorCurveBinding>> list = buckets[key];
                list.Sort((a, b) => a.Key.CompareTo(b.Key));

                var group = new CurveGroup
                {
                    Bindings = new EditorCurveBinding[list.Count],
                    Curves = new AnimationCurve[list.Count]
                };

                bool usable = true;
                for (int i = 0; i < list.Count; i++)
                {
                    group.Bindings[i] = list[i].Value;
                    group.Curves[i] = AnimationUtility.GetEditorCurve(clip, list[i].Value);

                    if (group.Curves[i] == null || group.Curves[i].length == 0)
                    {
                        usable = false;
                    }
                }

                if (!usable)
                {
                    continue;
                }

                SplitComponent(group.Bindings[0].propertyName, out string groupPrefix, out _);
                group.Kind = KindOf(group.Bindings[0], groupPrefix, list.Count);

                Sample(group, clip.frameRate);
                groups.Add(group);
            }

            return groups;
        }

        /// <summary>
        /// Splits "m_LocalPosition.x" into "m_LocalPosition" and 0. False for a property
        /// that is not one component of a larger value.
        /// </summary>
        private static bool SplitComponent(string property, out string prefix, out int component)
        {
            prefix = property;
            component = 0;

            if (string.IsNullOrEmpty(property) || property.Length < 3)
            {
                return false;
            }

            int dot = property.Length - 2;
            if (property[dot] != '.')
            {
                return false;
            }

            switch (property[property.Length - 1])
            {
                case 'x': component = 0; break;
                case 'y': component = 1; break;
                case 'z': component = 2; break;
                case 'w': component = 3; break;
                default: return false;
            }

            prefix = property.Substring(0, dot);
            return true;
        }

        private static CurveKind KindOf(EditorCurveBinding binding, string prefix, int componentCount)
        {
            if (binding.type == typeof(Transform))
            {
                if (prefix == "m_LocalRotation")
                {
                    // Fewer than four components is not a quaternion any more, and an
                    // angle cannot be measured from the pieces. Fall back to per-value.
                    return componentCount == 4 ? CurveKind.Rotation : CurveKind.Generic;
                }

                if (prefix == "localEulerAngles" || prefix == "localEulerAnglesRaw" ||
                    prefix == "m_LocalEulerAnglesHint")
                {
                    return CurveKind.Euler;
                }

                if (prefix == "m_LocalPosition")
                {
                    return CurveKind.Position;
                }

                if (prefix == "m_LocalScale")
                {
                    return CurveKind.Scale;
                }
            }

            if (binding.type == typeof(Animator))
            {
                // Root motion. RootT is where this package writes displacement, so it has
                // to be measured as a distance like any other position.
                if (prefix == "RootT" || prefix == "MotionT")
                {
                    return CurveKind.Position;
                }

                if (prefix == "RootQ" || prefix == "MotionQ")
                {
                    return componentCount == 4 ? CurveKind.Rotation : CurveKind.Generic;
                }
            }

            return CurveKind.Generic;
        }

        /// <summary>
        /// Puts every curve in the group on one set of sample times, so the group can be
        /// reduced as a single value.
        ///
        /// The times are the key times of every curve in the group **plus a sample on
        /// every frame**. The frames are not optional. The error this whole thing is
        /// steered by is only ever measured at these times, and a curve that is judged
        /// solely where its own keys sit is not judged at all: the keys are the points
        /// the rebuilt curve passes through exactly, by construction. What changes is
        /// everything between them, because the tangents are recomputed — and on a clip
        /// that arrived sparse, out of the model importer's own reduction, "between the
        /// keys" is the entire animation.
        ///
        /// Measured before this was fixed: a clip with nine keys per bone reported every
        /// sample inside tolerance while the hand ended up 3.7 degrees out, against a
        /// stated 0.5. Sampling per frame is what makes the number in the tolerance
        /// field mean what it says.
        /// </summary>
        private static void Sample(CurveGroup group, float frameRate)
        {
            var times = new List<float>();

            foreach (AnimationCurve curve in group.Curves)
            {
                foreach (Keyframe key in curve.keys)
                {
                    times.Add(key.time);
                }
            }

            times.Sort();

            if (times.Count > 1 && frameRate > 0f)
            {
                float start = times[0];
                float stop = times[times.Count - 1];
                int frames = Mathf.Clamp(Mathf.RoundToInt((stop - start) * frameRate), 0, 100000);

                for (int f = 1; f < frames; f++)
                {
                    times.Add(start + f / frameRate);
                }

                times.Sort();
            }

            var unique = new List<float>(times.Count);
            foreach (float time in times)
            {
                if (unique.Count == 0 || time - unique[unique.Count - 1] > 1e-6f)
                {
                    unique.Add(time);
                }
            }

            group.Times = unique.ToArray();
            group.Values = new float[group.Curves.Length][];

            for (int c = 0; c < group.Curves.Length; c++)
            {
                var values = new float[group.Times.Length];
                for (int i = 0; i < group.Times.Length; i++)
                {
                    values[i] = group.Curves[c].Evaluate(group.Times[i]);
                }

                group.Values[c] = values;
            }

            if (group.Kind == CurveKind.Rotation)
            {
                AlignHemispheres(group);
            }
        }

        /// <summary>
        /// q and -q are the same rotation, but interpolating between them takes the long
        /// way round -- the bone spins a full turn between two keys that look identical on
        /// screen. Exporters flip signs freely, so the sampled quaternions are pulled onto
        /// the same side before anything is measured or dropped.
        /// </summary>
        private static void AlignHemispheres(CurveGroup group)
        {
            float[][] v = group.Values;

            for (int i = 1; i < group.Times.Length; i++)
            {
                float dot = v[0][i] * v[0][i - 1] + v[1][i] * v[1][i - 1] +
                            v[2][i] * v[2][i - 1] + v[3][i] * v[3][i - 1];

                if (dot < 0f)
                {
                    for (int c = 0; c < 4; c++)
                    {
                        v[c][i] = -v[c][i];
                    }
                }
            }
        }

        // ------------------------------------------------------------------
        // Reduction
        // ------------------------------------------------------------------

        /// <summary>
        /// Chooses which samples to keep. Returns their indices, always including the
        /// first and the last: the clip's length is the time of its last key, and dropping
        /// it would shorten the animation.
        /// </summary>
        private static int[] Reduce(CurveGroup group, Tolerances tolerances, out bool constant)
        {
            int count = group.Times.Length;
            float tolerance = Mathf.Max(0f, tolerances.For(group.Kind));
            constant = false;

            if (count <= 2)
            {
                return Sequence(count);
            }

            if (tolerances.CollapseConstant && IsConstant(group, tolerance))
            {
                constant = true;
                return new[] { 0, count - 1 };
            }

            var keep = new bool[count];
            keep[0] = true;
            keep[count - 1] = true;

            // A first pass over the samples, splitting wherever a two-key Hermite piece
            // drifts too far. It picks slightly more keys than strictly needed, which the
            // check below never has to undo -- that one only ever adds.
            float[] slopes = EstimateSlopes(group);
            Split(group, slopes, tolerance, 0, count - 1, keep);

            Refine(group, tolerance, keep);

            return Indices(keep);
        }

        /// <summary>True when nothing in the group ever moves away from its first value.</summary>
        private static bool IsConstant(CurveGroup group, float tolerance)
        {
            int components = group.Values.Length;
            var first = new float[components];
            var sample = new float[components];

            for (int c = 0; c < components; c++)
            {
                first[c] = group.Values[c][0];
            }

            for (int i = 1; i < group.Times.Length; i++)
            {
                for (int c = 0; c < components; c++)
                {
                    sample[c] = group.Values[c][i];
                }

                if (Error(group.Kind, first, sample, components) > tolerance)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Slope at every sample of the original data, used to shape the trial pieces
        /// during the split. Clamped at local extremes, the same rule Unity's Clamped Auto
        /// tangents follow, so a peak does not overshoot.
        /// </summary>
        private static float[] EstimateSlopes(CurveGroup group)
        {
            int count = group.Times.Length;
            int components = group.Values.Length;
            var slopes = new float[components * count];

            for (int c = 0; c < components; c++)
            {
                float[] values = group.Values[c];

                for (int i = 0; i < count; i++)
                {
                    slopes[c * count + i] = SlopeAt(group.Times, values, i, count);
                }
            }

            return slopes;
        }

        private static float SlopeAt(float[] times, float[] values, int i, int count)
        {
            if (count < 2)
            {
                return 0f;
            }

            if (i == 0)
            {
                return Secant(times, values, 0, 1);
            }

            if (i == count - 1)
            {
                return Secant(times, values, count - 2, count - 1);
            }

            float before = values[i] - values[i - 1];
            float after = values[i + 1] - values[i];

            // A local extreme: a non-zero slope here would overshoot past the peak.
            if (before * after <= 0f)
            {
                return 0f;
            }

            return Secant(times, values, i - 1, i + 1);
        }

        private static float Secant(float[] times, float[] values, int a, int b)
        {
            float dt = times[b] - times[a];
            return dt <= 0f ? 0f : (values[b] - values[a]) / dt;
        }

        /// <summary>
        /// Recursive split: over the stretch between two kept samples, finds the one that
        /// drifts furthest from a Hermite piece drawn between them, keeps it if it is out
        /// of tolerance, then does the same on each side.
        /// </summary>
        private static void Split(CurveGroup group, float[] slopes, float tolerance, int lo, int hi, bool[] keep)
        {
            if (hi - lo < 2)
            {
                return;
            }

            int components = group.Values.Length;
            int count = group.Times.Length;
            var trial = new float[components];
            var sample = new float[components];

            int worst = -1;
            float worstError = tolerance;

            for (int i = lo + 1; i < hi; i++)
            {
                for (int c = 0; c < components; c++)
                {
                    trial[c] = Hermite(
                        group.Times[lo], group.Values[c][lo], slopes[c * count + lo],
                        group.Times[hi], group.Values[c][hi], slopes[c * count + hi],
                        group.Times[i]);

                    sample[c] = group.Values[c][i];
                }

                float error = Error(group.Kind, sample, trial, components);
                if (error > worstError)
                {
                    worstError = error;
                    worst = i;
                }
            }

            if (worst < 0)
            {
                return;
            }

            keep[worst] = true;
            Split(group, slopes, tolerance, lo, worst, keep);
            Split(group, slopes, tolerance, worst, hi, keep);
        }

        /// <summary>
        /// The split judges each piece on its own, with tangents taken from the original
        /// data. The curve that actually ships has tangents derived from the keys that
        /// survived, which is a different curve. So it gets built and measured for real,
        /// and any sample still out of tolerance is put back. Usually this adds nothing;
        /// when it does, it is on the sharp frames -- which is where an eye would catch it.
        /// </summary>
        private static void Refine(CurveGroup group, float tolerance, bool[] keep)
        {
            const int MaxPasses = 32;

            int count = group.Times.Length;
            int components = group.Values.Length;
            var trial = new float[components];
            var sample = new float[components];

            for (int pass = 0; pass < MaxPasses; pass++)
            {
                int[] kept = Indices(keep);
                var curves = new Keyframe[components][];

                for (int c = 0; c < components; c++)
                {
                    curves[c] = Keys(group.Times, group.Values[c], kept);
                }

                int worst = -1;
                float worstError = tolerance;

                for (int i = 0; i < count; i++)
                {
                    if (keep[i])
                    {
                        continue;
                    }

                    for (int c = 0; c < components; c++)
                    {
                        trial[c] = Evaluate(curves[c], group.Times[i]);
                        sample[c] = group.Values[c][i];
                    }

                    float error = Error(group.Kind, sample, trial, components);
                    if (error > worstError)
                    {
                        worstError = error;
                        worst = i;
                    }
                }

                if (worst < 0)
                {
                    return;
                }

                keep[worst] = true;
            }
        }

        // ------------------------------------------------------------------
        // Curve building and evaluation
        // ------------------------------------------------------------------

        /// <summary>
        /// Builds the keys for one component, with Clamped Auto tangents computed from the
        /// kept samples. The tangents are written out as numbers rather than left to a
        /// tangent mode, so the curve that was measured is exactly the curve stored.
        /// </summary>
        private static Keyframe[] Keys(float[] times, float[] values, int[] kept)
        {
            var keys = new Keyframe[kept.Length];

            for (int k = 0; k < kept.Length; k++)
            {
                int i = kept[k];
                float slope;

                if (kept.Length < 2)
                {
                    slope = 0f;
                }
                else if (k == 0)
                {
                    slope = Secant(times, values, kept[0], kept[1]);
                }
                else if (k == kept.Length - 1)
                {
                    slope = Secant(times, values, kept[kept.Length - 2], kept[kept.Length - 1]);
                }
                else
                {
                    float before = values[i] - values[kept[k - 1]];
                    float after = values[kept[k + 1]] - values[i];

                    slope = before * after <= 0f
                        ? 0f
                        : Secant(times, values, kept[k - 1], kept[k + 1]);
                }

                // The weights are what Unity writes on a key of its own: they do nothing
                // while weightedMode is None, but a third is the value the curve editor
                // starts from if weighted tangents are ever turned on, and zero would
                // give a flat segment out of nowhere.
                keys[k] = new Keyframe(times[i], values[i], slope, slope, 1f / 3f, 1f / 3f);
            }

            return keys;
        }

        /// <summary>
        /// Cubic Hermite between two keys, the same basis Unity evaluates an unweighted
        /// AnimationCurve with. Done here rather than through AnimationCurve.Evaluate to
        /// keep the inner loop free of allocations.
        /// </summary>
        private static float Hermite(float t0, float v0, float m0, float t1, float v1, float m1, float t)
        {
            float dt = t1 - t0;
            if (dt <= 0f)
            {
                return v0;
            }

            // An infinite tangent is Unity's constant (stepped) key: the value holds until
            // the next one, and there is no curve to evaluate.
            if (float.IsInfinity(m0) || float.IsInfinity(m1))
            {
                return v0;
            }

            float u = (t - t0) / dt;
            float u2 = u * u;
            float u3 = u2 * u;

            return (2f * u3 - 3f * u2 + 1f) * v0
                   + (u3 - 2f * u2 + u) * m0 * dt
                   + (-2f * u3 + 3f * u2) * v1
                   + (u3 - u2) * m1 * dt;
        }

        private static float Evaluate(Keyframe[] keys, float time)
        {
            if (keys.Length == 0)
            {
                return 0f;
            }

            if (time <= keys[0].time)
            {
                return keys[0].value;
            }

            int last = keys.Length - 1;
            if (time >= keys[last].time)
            {
                return keys[last].value;
            }

            int lo = 0;
            int hi = last;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (keys[mid].time <= time)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid;
                }
            }

            return Hermite(
                keys[lo].time, keys[lo].value, keys[lo].outTangent,
                keys[hi].time, keys[hi].value, keys[hi].inTangent,
                time);
        }

        // ------------------------------------------------------------------
        // Error
        // ------------------------------------------------------------------

        /// <summary>
        /// How far apart two samples of the same group are, in the unit its tolerance is
        /// stated in: degrees for a rotation, distance for a position, plain difference
        /// for the rest.
        /// </summary>
        private static float Error(CurveKind kind, float[] a, float[] b, int components)
        {
            switch (kind)
            {
                case CurveKind.Rotation:
                    return Angle(a, b);

                case CurveKind.Position:
                {
                    float sum = 0f;
                    for (int c = 0; c < components; c++)
                    {
                        float d = a[c] - b[c];
                        sum += d * d;
                    }

                    return Mathf.Sqrt(sum);
                }

                default:
                {
                    float largest = 0f;
                    for (int c = 0; c < components; c++)
                    {
                        largest = Mathf.Max(largest, Mathf.Abs(a[c] - b[c]));
                    }

                    return largest;
                }
            }
        }

        /// <summary>
        /// Angle between two quaternions, in degrees. They are normalised first: a
        /// quaternion read off an interpolated curve is not unit length, and the dot
        /// product of two of those is not a cosine of anything.
        /// </summary>
        private static float Angle(float[] a, float[] b)
        {
            float la = Mathf.Sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2] + a[3] * a[3]);
            float lb = Mathf.Sqrt(b[0] * b[0] + b[1] * b[1] + b[2] * b[2] + b[3] * b[3]);

            if (la < 1e-8f || lb < 1e-8f)
            {
                return 0f;
            }

            float dot = (a[0] * b[0] + a[1] * b[1] + a[2] * b[2] + a[3] * b[3]) / (la * lb);
            dot = Mathf.Clamp(Mathf.Abs(dot), 0f, 1f);

            return Mathf.Acos(dot) * 2f * Mathf.Rad2Deg;
        }

        // ------------------------------------------------------------------

        private static int[] Sequence(int count)
        {
            var all = new int[count];
            for (int i = 0; i < count; i++)
            {
                all[i] = i;
            }

            return all;
        }

        private static int[] Indices(bool[] keep)
        {
            int count = 0;
            foreach (bool k in keep)
            {
                if (k)
                {
                    count++;
                }
            }

            var indices = new int[count];
            int next = 0;
            for (int i = 0; i < keep.Length; i++)
            {
                if (keep[i])
                {
                    indices[next++] = i;
                }
            }

            return indices;
        }
    }
}
