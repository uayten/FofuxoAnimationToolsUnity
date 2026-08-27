# Fofuxo's Animation Tools

Editor tools for **standalone `.anim` assets** — the clips you get after importing
a model, extracting its animations and deleting the source file.

That pipeline keeps a repository small, but it walks away from every setting
Unity puts on the model importer. Root node, Loop Time, Root Transform: all of it
lives on the importer, and an extracted clip has no importer. This package puts
the ones that matter back within reach.

## Install

Unity Package Manager → **Add package from git URL**:

```
https://github.com/uayten/fofuxo-animation-tools.git
```

The repository is private, so the machine installing it needs GitHub credentials
configured — same as `com.danilomacb.embedded-animation-tools`. For SSH, use:

```
ssh://git@github.com/uayten/fofuxo-animation-tools.git
```

Pin a version by appending `#v0.1.0`.

Requires Unity 6000.0 or newer.

## What it does

### Root Motion on extracted clips

Select a `.anim` and the Inspector gains a **Root Motion** block:

| Field | Meaning |
|---|---|
| **Root Bone** | The bone carrying the movement. Defaults to `DEF-Root`; remembered between sessions. |
| **Use Root Motion** | On: the movement drives the GameObject. Off: the clip animates in place. |

Turning it on writes the bone's position curve into Unity's `RootT` curves, which
is what the engine reads as root motion. The bone curve is left untouched on
purpose — Unity *subtracts* `RootT` from the pose when applying it, expecting the
hierarchy to still carry the movement. Freezing the bone as well makes the object
travel while the mesh compensates backwards, and nothing appears to move.

That also makes the operation non-destructive: nothing is removed, so turning it
off is simply dropping the `RootT` curves. Toggling it any number of times never
loses a frame.

Clips that live inside a model asset are refused, with a note pointing at the
importer's own Root node setting. Editing those would be undone on the next
reimport.

### Loop Time in bulk

Unity only exposes Loop Time one clip at a time, behind an Apply and a reimport.
For a folder of four hundred clips that is not a workflow.

Right-click in the Project window → **Animation Clips › Loop Time › Enable** or
**Disable**. Works on any number of selected clips, writes straight to the assets,
no reimport.

The same menu carries **Use Root Motion** and **Clear Root Motion** for batch
work.

### Drop a clip into the scene

Drag a `.anim` from the Project window straight into the Scene view. A copy of the
character appears where you dropped it, playing that clip on loop, **without
entering Play mode** — the Unity answer to dropping an Animation Sequence into an
Unreal level.

Drag several at once and they line up side by side, two metres apart, which is
what makes reviewing a folder of animations practical.

A clip holds no reference to the skeleton it animates, so the model has to be
named once: select any clip and set **Preview Model** in the Scene Preview block.
It is remembered from then on.

The spawned object carries an `AnimationClipScenePreview` component with three
fields: the clip (swap it and the graph rebuilds), playback **speed** (negative
plays backwards) and **play in edit mode**. It runs on a `PlayableGraph` in manual
mode, so no AnimatorController asset is needed for any clip to be previewed.

## Notes

Root motion here is the **Generic** rig path: Unity derives the movement from the
root bone named in the Avatar. Humanoid rigs have their own mechanism through the
Avatar and do not need this.

If a script on your character implements `OnAnimatorMove`, Unity hands root motion
over to it — the engine stops applying it and offers `animator.deltaPosition`
instead. A clip can be perfectly set up and still not move anything until that
script applies the delta.

## License

See [LICENSE.md](LICENSE.md).
