using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace FofuxoAnimationTools.Editor
{
    /// <summary>
    /// Makes an animation clip droppable straight into the Scene view, the way an
    /// Animation Sequence is dropped into an Unreal level.
    ///
    /// Unity does nothing with a dragged .anim on its own, because a clip carries
    /// no reference to the skeleton it animates. This fills that gap: the model
    /// is configured once, and from then on dropping a clip spawns a copy of the
    /// character playing it on loop, where the mouse released it.
    /// </summary>
    [InitializeOnLoad]
    public static class AnimationClipSceneDropHandler
    {
        static AnimationClipSceneDropHandler()
        {
            SceneView.duringSceneGui += OnSceneGui;
        }

        private static void OnSceneGui(SceneView view)
        {
            Event current = Event.current;

            if (current.type != EventType.DragUpdated && current.type != EventType.DragPerform)
            {
                return;
            }

            List<AnimationClip> clips = DraggedClips();
            if (clips.Count == 0)
            {
                // Not our payload -- leave the event alone so models, prefabs and
                // materials keep dropping the way they always did.
                return;
            }

            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;

            if (current.type != EventType.DragPerform)
            {
                current.Use();
                return;
            }

            DragAndDrop.AcceptDrag();
            current.Use();

            GameObject model = ScenePreviewSpawner.PreviewModel();
            if (model == null)
            {
                EditorUtility.DisplayDialog(
                    "Fofuxo Animation Tools",
                    "No preview model is set.\n\n" +
                    "Select an animation clip and use the Scene Preview block in the " +
                    "Inspector to pick the rigged model these clips animate. After that, " +
                    "dropping a clip into the scene works.",
                    "Ok");
                return;
            }

            Spawn(clips, model, DropPoint(current.mousePosition));
        }

        private static void Spawn(List<AnimationClip> clips, GameObject model, Vector3 where)
        {
            var spawned = new List<Object>(clips.Count);
            float offset = 0f;

            foreach (AnimationClip clip in clips)
            {
                GameObject instance = ScenePreviewSpawner.Spawn(model, clip, 0f);
                if (instance == null)
                {
                    continue;
                }

                // Dropping several at once lines them up instead of stacking
                // them in the same spot.
                instance.transform.position = where + Vector3.right * offset;
                offset += 2f;

                spawned.Add(instance);
            }

            if (spawned.Count > 0)
            {
                Selection.objects = spawned.ToArray();
            }
        }

        /// <summary>
        /// Where the mouse let go, in world space. Prefers whatever collider is
        /// under the cursor so the character lands on the floor; falls back to the
        /// ground plane when the drop is over empty space.
        /// </summary>
        private static Vector3 DropPoint(Vector2 mousePosition)
        {
            Ray ray = HandleUtility.GUIPointToWorldRay(mousePosition);

            if (Physics.Raycast(ray, out RaycastHit hit, 5000f))
            {
                return hit.point;
            }

            var ground = new Plane(Vector3.up, Vector3.zero);
            if (ground.Raycast(ray, out float distance))
            {
                return ray.GetPoint(distance);
            }

            return ray.GetPoint(10f);
        }

        private static List<AnimationClip> DraggedClips()
        {
            var clips = new List<AnimationClip>();

            foreach (Object dragged in DragAndDrop.objectReferences)
            {
                if (dragged is AnimationClip clip)
                {
                    clips.Add(clip);
                }
            }

            return clips;
        }
    }
}
