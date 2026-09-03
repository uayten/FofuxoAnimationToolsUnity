# Fofuxo's Animation Tools Unity

Editor tools for a pipeline that builds its animations somewhere else — Unreal,
Blender, anywhere — and brings them into Unity as **standalone `.anim` assets**.

With this package installed you can:

- **Reimport four hundred animations** over the clips the project is already
  using, without a single reference breaking.
- **Remember external import sources** when files or folders are dragged into
  Project, inspect their paths, and update the project copies from the originals.
- **Be told what breaks before it breaks**, when you delete an FBX or a clip
  something else is holding on to — and point those references somewhere else
  instead.
- **Give a new FBX the materials the project already has**, matched by name
  across two naming conventions.
- **Turn bone displacement into root motion** on a clip that has no importer.
- **Flip Loop Time on any number of clips at once**, with no reimport.
- **Drop a clip into the Scene view** and watch it loop on the character,
  without entering Play mode.

## Contents

- [Install](#install)
- [Source files and updating imports](#source-files-and-updating-imports)
- [Reimporting a set of animations](#reimporting-a-set-of-animations)
  - [FBX, glb, and anything else](#fbx-glb-and-anything-else)
  - [The rig check](#the-rig-check)
  - [Why the clips are rewritten and not replaced](#why-the-clips-are-rewritten-and-not-replaced)
  - [Matching the names](#matching-the-names)
  - [What survives the update](#what-survives-the-update)
- [Deleting something that is in use](#deleting-something-that-is-in-use)
  - [The reference index](#the-reference-index)
  - [References that are not references](#references-that-are-not-references)
  - [Finding the damage already done](#finding-the-damage-already-done)
- [Materials by name](#materials-by-name)
- [Extracting a model](#extracting-a-model)
  - [Whether you should](#whether-you-should)
- [Root Motion on extracted clips](#root-motion-on-extracted-clips)
- [Loop Time in bulk](#loop-time-in-bulk)
- [Compressing clips](#compressing-clips)
  - [What the tolerances mean](#what-the-tolerances-mean)
  - [The editor copy of the curves](#the-editor-copy-of-the-curves)
- [Drop a clip into the scene](#drop-a-clip-into-the-scene)
- [Preferences](#preferences)
- [Notes](#notes)
- [License](#license)

## Install

Unity Package Manager → **Add package from git URL**:

```
https://github.com/uayten/FofuxoAnimationToolsUnity.git
```

The repository is private, so the machine installing it needs GitHub credentials
configured — same as `com.danilomacb.embedded-animation-tools`. For SSH, use:

```
ssh://git@github.com/uayten/FofuxoAnimationToolsUnity.git
```

Pin a version by appending `#v0.1.0`.

Requires Unity 6000.0 or newer.

## Source files and updating imports

Drag external files or folders into Unity's **Project** window. The package records
the original path for each imported file, including files in nested folders. This
works with model files, textures, audio and other file types that have an importer.
The path is saved in the asset's `.meta` file and survives renaming or moving the
asset inside the project.

Select an asset to see **Source File** in the Inspector. With UnityGLTF, this block
appears in the **Info** tab; other importers show it below the Inspector header.
The path is selectable and has **Set Source...**, **Show in Explorer**, and
**Update From Source** controls. The header is also the fallback if a future
UnityGLTF version changes its internal Info-tab implementation.

**Update From Source** copies the current external file over the project asset and
rebuilds its imported contents, preserving the asset GUID and import settings.
The same command is available under **Assets › Fofuxo's Animation Tools › Update
From Source File**. A model's imported animations then reflect the current source:
new takes appear and existing takes are rebuilt. References to takes that were
removed or renamed in the source depend on the importer's identifier handling.

For assets imported before this feature, the original location cannot be recovered
from Unity's project copy. The first update opens the source picker directly and
performs the update immediately after a file is chosen. A warning dialog appears
when a recorded source file or its folder cannot be found, with an option to locate
it again. Files copied into Assets outside Unity have no drag event; use **Set
Source...** to record their location once.

Unity's built-in **Reimport** rebuilds the copy already in the project. To pull a new
export from its external location, use **Update From Source**. Standalone `.anim`
files extracted from a model are separate assets: update those through
[Extract & Sync Clips](#reimporting-a-set-of-animations) after updating the model.
For formats with external companion files, such as `.gltf` plus `.bin` and
textures, update those tracked files too, or select their containing folder.

## Reimporting a set of animations

**Right-click the model › Fofuxo's Animation Tools › Extract Animations to Folder…**
or **Window › Fofuxo's Animation Tools › Extract & Sync Clips**

Either way it ends in the same table. The menu asks for the destination folder
first, because the folder is what decides the question: a take whose name is
already an asset in there is an *update* to that asset, references and all, and a
take whose name is not is a new file nothing yet knows about.

The window takes the FBX files that just came out of the exporter and the folder of
`.anim` assets the project has been using. It pairs them up by name and shows the
result before writing anything:

| | |
|---|---|
| **update** | a take and an existing clip matched — the clip gets the new animation |
| **new** | a take with no clip in the project yet — extracted into a new `.anim` |
| **ambiguous** | two takes, or two clips, with the same name — left alone |
| **not covered** | an existing clip the new import has nothing for |

A row that guessed wrong is corrected by dragging the right clip onto its field.
The **tick all / none** buttons work on whatever the filter is showing, which is
what makes a trial run on three clips practical before committing to four hundred.

Each row also says how many assets reference the clip it is about to rewrite, so
the load-bearing ones and the ones nothing has ever used are told apart before the
fact rather than after.

### FBX, glb, and anything else

Every tool in this package takes a **`.glb` or `.gltf`** wherever it takes an FBX.

That is worth saying because Unity does not. `t:Model` and `is ModelImporter` only
recognise the formats Unity's own importer handles; a glb comes in through a
ScriptedImporter that a package installed — UnityGLTF, here — and to the asset
database it is a GameObject like any other. It can sit in the same folder as the
FBX, holding the same skeleton, the same meshes and the same named takes, and be
invisible to every menu item. So the question is asked of the result rather than of
the importer: an asset whose main object is a GameObject, that came from a file and
is not a prefab, is a model. No importer is named anywhere, so a different glTF
package, or a USD one, works the same.

Two things to know before switching a pipeline over:

- **glb takes arrive baked**, and are compressed on the way in. Exporters sample per
  frame — the same 1.83 s animation came out of the FBX importer at 4,833 keys and out
  of the glb at 185,370, a factor of 38. **Compress on write** is on by default and
  closes that gap on extraction, so the sizes below are what actually lands:

  | | source file | keys | `.anim` written |
  |---|---|---|---|
  | FBX | 5.1 MB | 4,833 → 4,791 | **0.64 MB** |
  | glb | 23.6 MB | 185,370 → 5,674 | **0.65 MB** |

  Nothing was wrong with the glb; the FBX went through the importer's Keyframe
  Reduction and the glb had no importer to go through. [Compressing
  clips](#compressing-clips) is that pass, and this is it running automatically.
- **UnityGLTF numbers its takes.** A single animation comes out as `MyClip_0`, so
  turn on **Strip trailing number** for the names to match the `.anim` assets the
  project already has.

One thing genuinely does not survive the switch: **Avatar Definition**. Copy From
Other Avatar is a setting on Unity's model importer and has no counterpart on a
scripted one, so `Assign Avatar to Models` skips glb files and says how many it
skipped rather than reporting a job it did not do. In exchange, `Extract Mesh and
Avatar` **builds** a generic Avatar for a model that shipped without one, which
UnityGLTF only does for humanoid rigs.

### The rig check

Set **Character** to the model these clips are supposed to animate, and every take
is compared against it, bone path by bone path.

It fills itself in from the **destination folder** — a model there with a skinned
mesh, which is the character and not an animation-only export. That folder is the
only place searched; a project has more than one character, and a wider search
would pick whichever it reached first and then call every take broken for not
fitting a rig they were never meant for. The models being imported are skipped for
the same reason in reverse: an animation FBX often carries the mesh along, and
checking a take against the file it came out of always passes.

When the folder holds no character the preview model is used as a guess — and only
as a guess. If it turns out to be a different rig from the import, it is dropped
rather than reported: the window says nothing was checked, which is the truth,
instead of marking every row red because the last character you previewed was
someone else. Set the field by hand and it stays set, warnings and all.

This matters more than it sounds. A clip holds no reference to a skeleton; it holds
a list of paths like `DEF-Root/DEF-pelvis/DEF-spine` and hopes to find a transform
at each one. Play a clip exported from a different rig and nothing throws, nothing
is logged, and the character stands still — which looks exactly like a clip
imported wrong, a controller wired wrong, or root motion set up wrong. Four steps
from the actual cause.

Rows that do not fit are marked in the table, and overwriting a clip something
references with animation from a foreign rig asks first. The check also tells the
two failures apart:

- **a different rig** — bones the character does not have.
- **rooted differently** — every bone exists, at a different path. The skeleton is
  the right one, hanging somewhere else in the export. Just as fatal, entirely
  fixable in the exporter.
- **a different export of the same rig** — every bone exists, at the right path, and
  they do not start facing the same way. This is the one that used to get through.
  Export a character as FBX and as glTF and the two files can disagree about the rest
  pose by a full 180 degrees on every bone, with every name matching. A clip carries
  local rotations, not poses, so on the wrong one it animates the difference. Take the
  character and the animations from the same export; the check now measures the bind
  poses and refuses to stay quiet about it.

### Why the clips are rewritten and not replaced

The obvious way to reimport a set of animations is to extract them again and point
everything at the new files. That is four hundred new GUIDs, and every Animator
state, Timeline track and inspector field that named a clip is now naming a clip
nobody kept. **The reorganisation afterwards is the real cost of the reimport, and
it is entirely self-inflicted.**

This does the opposite. The existing `.anim` asset stays where it is and keeps its
GUID and its file ID; only its contents are replaced. Nothing that referenced the
clip can tell the difference, because as far as the asset database is concerned
nothing happened. There is no reorganisation, because there is nothing to
reorganise.

That includes the references no remapping tool could have fixed. Tools that name
clips by string rather than by GUID — `EmbeddedAnimationTools` among them — keep
working for the same reason: the name did not change either.

### Matching the names

Exports rarely come back spelled the same. A take gains a numeric suffix, a naming
convention changes between two runs, and the clips are otherwise identical. Three
rules, in the window:

- **Ignore case** — `Attack_Loop` matches `attack_loop`.
- **Strip trailing number** — `AS_Run_Loop_223` matches `AS_Run_Loop`. Only a
  number after an underscore at the very end; the `01` in `Attack_01` is left
  alone.
- **Strip prefixes / suffixes** — comma separated, removed from both names before
  comparing.

### What survives the update

The incoming clip brings the importer's settings with it, which would undo the
work of the last pass. Two toggles put it back:

- **Keep Loop Time and pose** — Loop Time, cycle offset, mirror and the loop pose
  flags as the existing clip had them. Start and stop time are deliberately *not*
  restored: they say how long the animation is, and the point of the update is
  that it might now be a different length.
- **Keep root motion** — re-runs the root motion conversion on any clip that
  already had it.

The clips the import does not cover are what is left over from the old export.
**Select the ones not covered** puts them in the Project window selection, where
deleting them runs into the next feature.

## Deleting something that is in use

Unity deletes an asset the moment it is asked. Anything that was pointing at it is
left holding a null — an Animator state that plays nothing, a renderer that goes
pink — and nothing is logged. The damage is found later, by noticing.

Delete an FBX, a clip, a controller, a material or a prefab that something else
references and a window comes up instead, listing what refers to it. Every row is
clickable and pings the asset. Then:

- **Replace and delete** — point the references at another asset first. For a
  model this pairs up what is *inside* the two files, by type and name: the mesh
  the prefab uses, the avatar the Animator uses, the materials the renderers use.
  Nothing in the project ever points at the FBX itself.
- **Clear and delete** — empty the references rather than leave them broken.
- **Force delete** — Unity's own behaviour, for when you know something the index
  does not.
- **Fill by name** — for the case where the replacement is already in the project
  under the same name in a different folder.

There is no undo. Source control is the undo.

### Where the check happens

Both ways of deleting from the Project window -- the Delete key, and the Delete entry
on the Assets and right-click menus -- are caught before Unity begins. Unity offers no
way to stop a delete quietly: refusing one already under way makes it report the
assets as failed and put up *"Some assets could not be deleted. Make sure nothing is
keeping a hook on them"*, which is untrue and arrives ahead of the window that
explains what is really holding the file. Nothing is started here, so nothing has to
be refused.

Taking the menu entry means replacing Unity's, and a managed menu item cannot take its
place in the ordering: **Delete sits at the end of the first block** of the Assets and
right-click menus rather than under Open. A selection with nothing the guard watches
is handed straight to Unity's own delete, confirmation and all.

A delete from a script or another tool still arrives after the fact, where refusing it
is the only option and the dialog is unavoidable. The window then opens saying that
nothing was deleted and nothing has a hook on the files.

### The reference index

Unity only answers the forward question: `AssetDatabase.GetDependencies` tells you
what an asset *needs*. There is no API for the reverse. So the index asks the
forward question about every asset in the project once and turns the answers
inside out. It takes a few seconds the first time and is then kept up to date as
assets change.

What it cannot see is a reference that is not a reference: a clip looked up by
name at runtime, an `AssetBundle` path, a `Resources.Load` string. Those survive a
replace untouched, and break on a delete without warning.

### References that are not references

The index answers the question the asset database can answer: who holds a GUID
pointing at this. It is blind to the other kind. A tool that stores
`clipName: AS_Run_Combat_Loop_Seq` and looks the clip up at runtime is using that
clip every bit as much, and nothing in Unity has any idea. A script doing
`Resources.Load("AS_Roll_F_0_Seq")` is the same story.

So the guard also searches the project's text assets for the name written out, and
lists those separately. They cannot be redirected — rewriting a string means
knowing what it means to whatever reads it — which makes them an argument for
replacing under the same name rather than pointing references somewhere new.

### Finding the damage already done

**Window › Fofuxo's Animation Tools › Find Broken References**

Every delete that went through before anything was watching left a hole, and Unity
does not report them. A missing clip on an Animator state, a missing material on a
renderer: each is a GUID in a file that resolves to nothing, and each shows up as a
blank field in an inspector nobody has opened lately.

The report groups by the missing GUID rather than by the file holding the hole,
because one deleted file is usually the reason for a dozen. If that file is still
in source control, restoring it puts every one of them back at once — the GUID
lives in the `.meta` and comes back with it.

## Materials by name

**Right-click an FBX › Fofuxo's Animation Tools › Match Materials by Name**
or **Window › Fofuxo's Animation Tools › Match Model Materials**

Unity has a version of this — the Materials tab has a **Search and Remap** button
that looks for a material of the same name. It matches exactly, which is precisely
where a model arriving from another engine falls down. Unreal names a material
instance `MI_GrantClothes`; the same surface, built in Unity by hand, is called
`M_GrantClothes`. Two conventions for one material, no exact match between them,
so the button comes back with nothing and every slot gets a fresh grey material.

This strips the convention off both sides before comparing — both read
`GrantClothes` — and where several materials match, it prefers the one spelled
exactly like the slot, then the one living nearest the model.

What it writes is the importer's external object map, the same thing Search and
Remap writes. The result is an ordinary remap, visible in the Materials tab and
undone from there.

The matches are all found first and shown together, because each one is a guess
made from a name and applying a guess to twenty models is worth a look first. A
wrong guess is corrected by dragging the right material onto its row.

**Remap on import** in the preferences does it automatically for models arriving
with no material remaps at all. Off by default: it costs a second import, and the
guesses are worth watching until the naming rules are tuned.

## Extracting a model

**Right-click the FBX › Fofuxo's Animation Tools › Extract Mesh and Avatar…**

Writes what the model contains out as ordinary Unity assets: each mesh as its own
`.asset` (or all of them in one file), the avatar as its own, and a prefab that
owns the bone hierarchy rather than inheriting it from the model.

The prefab is the part that matters. A skinned character is not a mesh — it is two
hundred transforms and a renderer holding an array of references into them, in the
order the bindposes expect. Extracting the mesh alone gives you a mesh with no
skeleton to be skinned to. The prefab is unpacked from the model, so it holds its
own copy of the hierarchy, with the renderers pointed at the extracted meshes, the
Animator at the extracted avatar, and the materials at the project's own — matched
by name, with the ones that matched nothing written out too. A single unmatched
slot, usually a default material on a slot that never got one, is enough to keep
the whole prefab tied to the file it was supposed to replace.

Afterwards it asks the asset database whether the prefab still depends on the model
and tells you either way. That answer is the only one that decides whether the FBX
can be deleted, and it is worth having rather than assuming.

### Whether you should

Working and being a good idea are different questions, and the answer differs by
what is being extracted.

**The avatar: yes.** It is small, it is the closest thing Unity has to Unreal's
Skeleton asset, and once it stands on its own every animation FBX can point at it
with **Copy From Other Avatar** instead of at the character file. That decouples
the animations from the character without costing anything, and
**Window › Fofuxo's Animation Tools › Assign Avatar to Models** points a whole
folder of them at it in one go. Unity then checks each import against that rig and
complains when they diverge — the same silence the rig check exists to break,
caught one step earlier.

**The mesh: usually not.** Two reasons, and neither is visible until afterwards:

- **Size.** With the project on text serialisation the mesh is written as YAML.
  Measured on a 38,895-vertex character: the FBX is 2.1 MB, the extracted mesh is
  7.5 MB. It lands in the repository at that size, and again at that size every
  time it is re-exported. Binary serialisation shrinks it, at the cost of every
  asset being unmergeable.
- **The update path.** While the character lives in an FBX, re-exporting it from
  the DCC updates the mesh, the skeleton and the bounds, and that flows through to
  every prefab built on it. An extracted mesh is a frozen copy: a new export means
  redoing the extraction and the rewiring by hand.

Animations are a different case, and the opposite one — they are data with no
update path worth keeping, which is why extracting them is right and why the
animation FBX files are disposable once it is done.

The window shows the size comparison before the button, so the trade is made with
the number in view.

## Root Motion on extracted clips

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

## Loop Time in bulk

Unity only exposes Loop Time one clip at a time, behind an Apply and a reimport.
For a folder of four hundred clips that is not a workflow.

Right-click in the Project window → **Animation Clips › Loop Time › Enable** or
**Disable**. Works on any number of selected clips, writes straight to the assets,
no reimport.

The same menu carries **Use Root Motion** and **Clear Root Motion** for batch
work.

## Compressing clips

A clip keeps whatever key density the file it came from had. Out of an FBX that
went through the importer's **Optimal** setting that is already tight — but the
importer is exactly what an extracted clip walks away from, and every other route
into Unity is worse. glTF and USD exporters *bake*: one key per bone per frame,
most of them saying nothing. Four hundred of those is a repository nobody wants.

**Animation Clips › Compress…**, or `Window → Fofuxo's Animation Tools → Compress
Animation Clips`. Two things happen, in order:

1. A curve that never leaves its starting value collapses to two keys. On a
   skeleton this is most of the saving: scale and position hold still on nearly
   every bone while only rotation moves.
2. What is left is reduced by error — keys are dropped for as long as the curve
   that remains stays within tolerance of the original.

The window measures before it writes. **Analyze** runs the whole thing and touches
nothing, reporting keys before and after, the ratio, and which clips gave up the
most. **Compress** then rewrites the assets, and that cannot be undone — the keys
that go are gone. Analyze until the number looks right, then compress.

### What the tolerances mean

| Field | Unit | Default |
|---|---|---|
| **Rotation** | degrees the bone may end up turned from where it was | 0.5 |
| **Position** | distance it may end up from where it was, in the clip's units | 0.0005 |
| **Scale** | difference on any one axis | 0.005 |
| **Other curves** | plain difference — blend shape weights, custom properties | 0.01 |

The rotation default is the model importer's own, in the same unit, so 0.5 here is
the 0.5 you already accept from every FBX. Position and scale are absolute rather
than the importer's percentages, because "half a millimetre" is a thing you can
picture and "0.5 %" of an unstated quantity is not.

Rotation is measured as **an angle across the whole quaternion**, not per
component. A component read on its own says nothing about how far a bone turned,
and reducing x, y, z and w at different times distorts the rotation in between —
so the four curves are reduced together, on shared key times. The error is also
measured on the rebuilt curve with its tangents, not on a straight line between
the surviving keys, which is why the number in the field is the error you actually
get rather than an optimistic one.

Two things are protected on the way through. The first and last key of every curve
always survive, because a clip's length is the time of its last key and dropping
it would shorten the animation. And quaternions are pulled onto the same
hemisphere before anything is measured: `q` and `-q` are the same rotation, but
interpolating between them takes the long way round, and a bone that spins a full
turn between two identical-looking keys is the classic way a reduced clip breaks.

Clips inside a model asset are skipped. They belong to the importer, which has
this feature already and would undo the edit on the next reimport anyway.

### The editor copy of the curves

A clip stores its animation **twice**. The engine reads `m_RotationCurves` and its
neighbours; the Editor keeps `m_EditorCurves` and `m_EulerEditorCurves` — the same
animation again, one float curve per component plus Euler angles for the rotation
inspector. On a real clip here that second copy is **four fifths of the file**, and
none of it reaches a build.

**Drop the editor copy of the curves** removes it, and is on by default. It is safe
because the copy is derived rather than authored: the Editor rebuilds it from the
runtime curves the moment anything asks for it. Measured — 3.2 MB down to 0.6 MB,
and afterwards `AnimationUtility` still returned all 1,670 bindings and all 5,674
keys, the Animation window still drew them, and root motion still read them.

It also stops compression from *growing* a clip. Writing curves through
`AnimationUtility` is what creates the editor copy, so a clip extracted straight
from an FBX — which arrives without one — used to come out of the compressor four
times larger than it went in, having lost almost no keys. That is fixed.

Editing a clip in the Animation window writes the copy back for that clip. Nothing
is lost by it; the file just grows again until the next pass.

## Drop a clip into the scene

Drag a `.anim` from the Project window straight into the Scene view. A copy of the
character appears where you dropped it, playing that clip on loop, **without
entering Play mode** — the Unity answer to dropping an Animation Sequence into an
Unreal level.

Drag several at once and they line up side by side, two metres apart, which is
what makes reviewing a folder of animations practical.

A clip holds no reference to the skeleton it animates, so the model has to come
from somewhere. It comes from **the folder the clip is in**: walking up from the
clip, the nearest folder holding a rigged model is taken to be its character.
`Characters/Grant/AnimFiles/x.anim` finds `Grant.fbx` one level up and stops there,
without ever reaching `Characters/`, where it would have to pick between everybody.

That is why a project with two characters no longer previews the second one's clips
on the first one's rig — which does not fail, it just folds the body into shapes it
cannot make, and reads as a broken export rather than as the wrong model.

A prefab counts, and wins over a model file: it is usually the extracted character,
which is the thing the clips are meant to drive. Files carrying animation are passed
over, because an animation export often ships the mesh alongside the take and a
folder can be full of things that look like the character and are really one clip
each.

To override it, set **Preview Model** in the Scene Preview block of any clip. The
choice is remembered for the whole folder, not for that clip — four hundred clips of
one character live in one folder, and the question should only have to be answered
once.

The spawned object carries an `AnimationClipScenePreview` component with three
fields: the clip (swap it and the graph rebuilds), playback **speed** (negative
plays backwards) and **play in edit mode**. It runs on a `PlayableGraph` in manual
mode, so no AnimatorController asset is needed for any clip to be previewed.

## Preferences

**Preferences › Fofuxo's Animation Tools** — the root bone name, whether the
delete guard is on and how wide it watches, the material naming rules and the
folder they are searched in. All of it lives in `EditorPrefs`: a per-machine
preference, never part of the project.

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
