using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace FofuxoAnimationTools
{
    /// <summary>
    /// Plays a single AnimationClip on this object, looping forever, in the
    /// Scene view and without entering Play mode.
    ///
    /// This is the Unity answer to dropping an Animation Sequence into an Unreal
    /// level: a throwaway copy of the character in the world, running one clip,
    /// so a whole folder of animations can be reviewed side by side.
    ///
    /// It uses a PlayableGraph in manual mode rather than an AnimatorController,
    /// so no controller asset has to exist for a clip to be previewed. The graph
    /// is driven by hand and evaluated on each tick.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Animator))]
    [AddComponentMenu("Fofuxo/Animation Clip Scene Preview")]
    public sealed class AnimationClipScenePreview : MonoBehaviour
    {
        [Tooltip("The clip to play on loop.")]
        public AnimationClip clip;

        [Tooltip("Playback speed. Negative values play the clip backwards.")]
        public float speed = 1f;

        [Tooltip("Keep playing while the editor is not in Play mode.")]
        public bool playInEditMode = true;

        private Animator animator;
        private PlayableGraph graph;
        private AnimationClipPlayable clipPlayable;
        private double elapsed;

#if UNITY_EDITOR
        private double lastEditorTime;
#endif

        private void OnEnable()
        {
            animator = GetComponent<Animator>();
            Build();

#if UNITY_EDITOR
            lastEditorTime = EditorApplication.timeSinceStartup;
            EditorApplication.update += EditorTick;
#endif
        }

        private void OnDisable()
        {
#if UNITY_EDITOR
            EditorApplication.update -= EditorTick;
#endif
            Destroy();
        }

        private void OnValidate()
        {
            Rebuild();
        }

        /// <summary>
        /// Rebuilds the graph around the current clip.
        ///
        /// Call this after setting <see cref="clip"/> from code. OnEnable already
        /// ran by then -- AddComponent triggers it immediately under
        /// ExecuteAlways -- and it found no clip to build around, so nothing
        /// would play until a domain reload happened to reinitialise everything.
        /// </summary>
        public void Rebuild()
        {
            if (!isActiveAndEnabled)
            {
                return;
            }

            // The clip can also be swapped from the Inspector; the graph holds a
            // reference to the old one and has to be rebuilt around the new.
            Destroy();
            Build();
        }

        private void Update()
        {
            if (Application.isPlaying)
            {
                Advance(Time.deltaTime);
            }
        }

#if UNITY_EDITOR
        private void EditorTick()
        {
            if (Application.isPlaying || !playInEditMode)
            {
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            float delta = (float)(now - lastEditorTime);
            lastEditorTime = now;

            Advance(delta);

            // Edit mode does not run a render loop on its own. Without asking for
            // a repaint the pose only updates when something else happens to
            // redraw the view, which reads as a frozen character.
            SceneView.RepaintAll();
        }
#endif

        private void Advance(float delta)
        {
            if (!graph.IsValid() || clip == null || clip.length <= 0f)
            {
                return;
            }

            elapsed += delta * speed;

            // The forever loop. Modulo keeps the time inside the clip in both
            // directions, so a negative speed wraps to the end instead of
            // sticking at zero.
            elapsed %= clip.length;
            if (elapsed < 0d)
            {
                elapsed += clip.length;
            }

            clipPlayable.SetTime(elapsed);
            graph.Evaluate();
        }

        private void Build()
        {
            if (animator == null)
            {
                animator = GetComponent<Animator>();
            }

            if (animator == null || clip == null)
            {
                return;
            }

            graph = PlayableGraph.Create($"{name} - {clip.name}");
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);

            clipPlayable = AnimationClipPlayable.Create(graph, clip);

            // Without this the clip stops contributing once its own duration is
            // over, and the pose freezes on the last frame.
            clipPlayable.SetDuration(double.MaxValue);

            AnimationPlayableOutput output =
                AnimationPlayableOutput.Create(graph, "Preview", animator);

            output.SetSourcePlayable(clipPlayable);

            elapsed = 0d;
            graph.Evaluate();
        }

        private void Destroy()
        {
            if (graph.IsValid())
            {
                graph.Destroy();
            }
        }
    }
}
