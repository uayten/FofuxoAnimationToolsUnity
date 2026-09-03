# Changelog

All notable changes to this package are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **Compress on write**, on by default in the extract and sync window. Every clip
  written goes through the keyframe reduction on the way out, which is what Unity's
  model importer would have done and what an extracted clip no longer has an importer
  for. It matters most for anything baked: measured on the same 1.83 s animation
  exported both ways, the take out of the glb went from 185,370 keys to 5,674 and the
  asset from 65.4 MB to 0.6 MB — 101:1, and 0.8 % larger than the same animation
  taken through the FBX importer. It runs on FBX takes too and barely touches them,
  because doing it to everything is predictable and "compresses, unless the source
  file was of this kind" is not. Tolerances are shared with the compression window.
- **The editor copy of the curves is dropped** as part of compressing. A clip stores
  its animation twice — once in the curves the engine reads, once in `m_EditorCurves`
  and `m_EulerEditorCurves` for the Animation window — and that second copy is four
  fifths of the file while reaching no build. It is derived rather than authored, so
  the Editor rebuilds it whenever anything asks: after stripping, `AnimationUtility`
  still returned all 1,670 bindings and all 5,674 keys, and the clip still played at
  its full length. This also fixes compression *growing* a clip that arrived without
  the copy: writing curves through `AnimationUtility` is what creates it, so a clip
  extracted straight from an FBX used to leave four times larger than it came in.
- **glb and gltf are models now**, everywhere in the package. Unity's `t:Model` search
  and its `is ModelImporter` test only recognise the formats its own importer handles,
  so a `.glb` — which arrives through a ScriptedImporter a package installed — was
  invisible to every tool here while sitting in the same folder as the FBX beside it,
  holding the same skeleton, meshes, materials and named takes. The question is now
  asked of the result instead of the importer: an asset whose main object is a
  GameObject, that came from a file and is not a prefab, is a model. `Export
  Animations to Folder`, `Match Materials by Name`, `Extract Materials Out of the
  Model`, `Extract Mesh and Avatar` and `Assign Avatar to Models` all take one, as
  does the destination-folder search behind the rig check. Nothing names UnityGLTF —
  another glTF importer, or a USD one, falls in the same hole and comes out the same
  way.
- **A generic Avatar is built when the model has none.** Unity's importer creates the
  Avatar while importing; a scripted importer is under no obligation to, and UnityGLTF
  only does it for humanoid rigs. So a glb character arrived with a skeleton and no
  Avatar — nothing for an animation file to copy from, and no root motion. `Extract
  Mesh and Avatar` now builds one from the hierarchy, using the root motion bone from
  Preferences when the model actually has a bone by that name.
- **Keyframe compression** for standalone clips (`Animation Clips › Compress…`, or
  the window under `Window → Fofuxo's Animation Tools`). Curves that hold still
  collapse to two keys, and the rest are reduced by error until the curve that
  remains drifts as far from the original as the tolerance allows. It is the
  Keyframe Reduction the model importer does, for the clips that no longer have an
  importer — which matters most for anything baked, since glTF and USD exporters
  write one key per bone per frame. Rotation is measured as an angle across the
  whole quaternion rather than per component, on shared key times, and against the
  rebuilt curve including its tangents, so the tolerance is the error that ships.
  Quaternions are pulled onto one hemisphere first, and the first and last key of
  every curve are kept so the clip does not lose its length. Analyze reports what
  would happen and writes nothing; Compress cannot be undone.
- **Sync Animation Clips** window (`Window → Fofuxo's Animation Tools`). Pairs the
  takes in a freshly imported set of FBX files against the `.anim` assets the project
  already uses, and rewrites the contents of each existing clip in place with
  `EditorUtility.CopySerialized`. The asset keeps its GUID, its file ID and its name,
  so nothing that referenced it — including tools that name clips by string rather
  than by GUID — notices anything happened, and there is no reorganisation afterwards.
  Matching handles case, trailing numbers and prefix/suffix conventions; the table is
  shown and can be argued with before anything is written. Loop Time, the loop pose
  flags and the root motion conversion are carried across the update; start and stop
  time deliberately are not. Each row reports how many assets reference the clip it
  is about to rewrite, so "this one is load-bearing" and "this one is used by nothing"
  are told apart before the fact rather than after.
- **Export Animations to Folder** on the Project window context menu for a model.
  Asks for the destination folder first, because the folder is what decides the
  question: a take whose name already exists in there is an update to that asset,
  references and all, and a take whose name does not is a new file.
- **Rig check**. Every take is compared against the character it is meant to drive,
  bone path by bone path, and rows that do not fit are marked before they are
  written. A clip exported from the wrong rig does not throw and does not log — the
  curves address bones that are not there and the character stands still — so
  overwriting a clip something references with animation from a foreign rig is
  refused without an explicit confirmation.
- **Delete guard**. Deleting a model, clip, controller, material or prefab that
  something else references opens a window listing what refers to it, with the choice
  to redirect those references to another asset, empty them, or force the delete.
  Replacing a model pairs up the objects *inside* the two files by type and name,
  because nothing in a project ever points at the FBX itself. Toggled in
  `Preferences → Fofuxo's Animation Tools`. A delete that breaks nothing still says
  so, because silence is indistinguishable from a guard that never ran — turn
  **Say so when nothing uses it** off and it writes a line to the console instead.
- **Find References** on the Project window context menu. The same window without the
  deleting.
- **Match Materials by Name**, on the Project window context menu and as a window.
  Matches the material names baked into a model against the project's own materials
  with the naming convention stripped off both sides, so Unreal's `MI_GrantClothes`
  finds Unity's `M_GrantClothes` where Unity's own Search and Remap finds nothing.
  Writes the importer's external object map, so the result is an ordinary remap.
  Optionally runs automatically on first import — off by default.

- **Extract Mesh and Avatar**, on the Project window context menu for a model and as
  a window. Writes the meshes and the avatar out as standalone assets and builds a
  prefab that owns the bone hierarchy, unpacked from the model, with its renderers
  pointed at the extracted meshes and at the project's own materials. Embedded
  materials that matched nothing are written out too, because a single unmatched slot
  is enough to keep the whole prefab tied to the file it was meant to replace.
  Afterwards it asks the asset database whether the prefab still depends on the model
  and says so either way — that answer is the only one that decides whether the FBX
  can actually be deleted. The estimated size of the extracted meshes is shown against
  the size of the model file first, because under text serialisation it is usually
  several times larger. Leaving the destination folder empty writes each model's
  assets beside the model itself.
- **Find Broken References** (`Window`). Every reference in the project pointing at
  an asset that is no longer there, grouped by the missing GUID rather than by the
  file holding the hole, because one delete is usually the reason for a dozen. Reads
  the text of the files, so it reaches scenes without opening them.
- **References by name**. The dependency index only sees GUIDs, and a tool that
  stores a clip's name and looks it up at runtime is using that clip just as much.
  The delete guard now also searches the project's text assets for the name spelled
  out, and shows those separately: they cannot be redirected, because rewriting a
  string means knowing what it means to whatever reads it. Only replacing under the
  same name keeps them working.
- **Assign Avatar to Models** (`Window`, and the Project window context menu). Points
  a set of models at one avatar with Copy From Other Avatar, which is how Unity spells
  the relationship Unreal calls a Skeleton. Doing it one FBX at a time through the Rig
  tab is why it usually does not get done at all.
- **Replacing a model repoints the prefabs built on it.** A character prefab does
  not merely reference the model it came from -- it *is* an instance of it, and that
  link lives in the prefab's structure rather than in a field any reference walk can
  reach. Replace and delete now calls `ReplacePrefabAssetOfPrefabInstance`, matching
  the old objects to the new ones by name and carrying the overrides across, so the
  hitboxes and scripts added on top survive a rig that gained or lost a bone. With
  nothing to replace it with, the instance is unpacked instead: the prefab owns the
  hierarchy outright, which is the difference between losing the link and losing the
  character.
- **Delete key caught before Unity acts on it.** `OnWillDeleteAsset` is asked after
  the delete has begun and the only way to stop one is to report it as failed, which
  Unity answers with a dialog claiming something is keeping a hook on the asset.
  Catching the key removes the problem instead of working around it. Only when the
  selection holds a watched type; anything else keeps Unity's own confirmation.
- **Reference index**. A reverse dependency map of the project, built once from
  `AssetDatabase.GetDependencies` and kept current as assets change. Underlies the
  delete guard and Find References.
- **Play Mode Persist**. Keeps the selected objects' component values after Play mode
  ends, instead of letting Unity restore its scene snapshot over them. Off by default,
  toggled in `Preferences → Fofuxo's Animation Tools` or with `Alt+Shift+P`, and stored
  in `EditorPrefs` — a per-machine preference, never part of the project. Transform is
  excluded unless asked for, and the restore lands as a single undo step.

- **Update From Source File**, on the Project window menu. Unreal remembers where an
  imported file came from and can go back and read it again; Unity cannot, because in
  Unity the imported file *is* the asset and its own Reimport re-reads the copy that
  has not changed. The path to the export outside the project is now kept on the
  importer's `userData`, so it travels in the `.meta` and therefore in the repository,
  and updating is a copy over the same asset path. The GUID does not move, so every
  prefab, clip and material remapping built on that model follows — the same outcome
  as delete-and-replace, without either. **Set Source File…** links a whole selection
  at once by matching file names in the folder picked.
- **Extract animations too**, a switch on the model extraction. An export that carries
  the character usually carries a take with it, and pulling the character out and then
  opening another window for the clip in the same file was two passes over one file.
  Clips land beside the rest of the extraction, compressed, and a clip of that name
  already there is rewritten in place rather than duplicated.
- **Extract into another folder**, a switch on the extract and sync window. Off, the
  clips land beside the model they came out of, which is what you want when the export
  already went into the folder it belongs in. On, the folder field comes back.

### Changed

- **A delete from the menu is caught before Unity starts it too.** The Delete key
  already was; the Delete entry on the Assets and right-click menus still went the
  other way, through `OnWillDeleteAsset`, where the only way to stop a delete already
  under way is to report it as failed -- which Unity answers with "Some assets could
  not be deleted. Make sure nothing is keeping a hook on them". That is untrue, and it
  is the first thing you see, ahead of the window that says what is actually holding
  the file. A `MenuItem` written on `Assets/Delete` replaces Unity's rather than
  sitting beside it, so the check runs before anything is deleted and the dialog never
  comes up. The cost is where Delete sits: built-in items share one priority and order
  among themselves by the order they were registered, which a managed item cannot
  join, so Delete moved to the end of the first block of the menu instead of staying
  under Open. A selection with nothing the guard watches is handed to Unity's own
  delete, confirmation and all. The preference is now **Catch the delete early** and
  covers both ways in.
- **The Project window menu is down to three items**: `Extract Animations to Folder…`,
  `Extract Mesh and Avatar…` and `Match Materials by Name`. `Find References`,
  `Assign Avatar to Models…` and `Extract Materials Out of the Model` were useful
  while the package was being figured out and are not what anyone reaches for now: the
  delete guard asks about references at the moment they matter and the extract table
  answers it per row, and extracting a model already externalises its materials on the
  way through. `Assign Avatar to Models` is still a window under `Window → Fofuxo's
  Animation Tools`; the other two are gone, along with the code behind Find References.
- `Export Animations to Folder…` is now **`Extract Animations to Folder…`**, matching
  the window it opens and the two items beside it. Extraction is the word this package
  uses for taking things out of a model file.

- **The preview model is per folder, and finds itself.** It used to be one value for
  the whole project, so on the second character selecting a clip offered the first
  one's rig and previewing it folded the body into shapes it cannot make — which
  reads as a broken export rather than as the wrong model having been asked. Now the
  clip's own folder answers: walking up from where the clip lives, the nearest folder
  holding a rigged model is the character. `Characters/Grant/AnimFiles/x.anim` finds
  `Grant.fbx` a level up and stops before reaching `Characters/`, where it would have
  to choose between everybody. Prefabs count and are preferred, files carrying
  animation are passed over — an animation export often ships the mesh alongside the
  take — and setting the field by hand is remembered for the whole folder rather than
  the one clip.

- **The rig check now compares bind poses, not just bone names.** Two exports of the
  same asset can carry identical bone names at identical paths and still disagree
  about which way those bones face — the FBX and glTF exports of one character here
  are a full 180 degrees apart on every bone. The path check passes them, nothing is
  logged, the clips are written, and the character then plays them folded into shapes
  it cannot make, which reads as a broken export when both exports are fine. What is
  broken is taking the character from one and the animations from the other. The
  window now says so before writing. Read from the mesh's bindposes rather than from
  the transforms: Unity imports an animation FBX with its hierarchy already posed on
  frame 0, so comparing transforms would flag every animated bone on every animation
  FBX ever imported.

### Fixed

- **The delete guard did not watch glb or gltf.** Every other tool in the package
  treats them as models, and the list of extensions whose loss breaks something
  silently was left behind: deleting a glb that four materials pointed at went
  through without a word. `.glb` and `.gltf` are on the list now.
- **"Some assets could not be deleted" is no longer the last word.** A delete that
  arrives from a script or another tool still has to be refused, and Unity still says
  something is keeping a hook on the asset. The window that opens behind it now
  answers that where it can be read: nothing was deleted, nothing has a hook on the
  files, and what is holding them is the list below. It says the same when it was
  caught early, minus the apology.
- **Extraction named the character after the file.** A character travelling inside an
  animation export — which is every file named after a take — came out as
  `AS_Attack_Air_to_Floor_01_End_Seq.prefab` and
  `AS_Attack_Air_to_Floor_01_End_SeqAvatar.asset`. The name now comes from the skinned
  mesh, which is where whoever built the character wrote the character's name; the
  file name is only the fallback. Same model, same file: `Fergus.prefab` and
  `FergusAvatar.asset`.
- **The extracted prefab had no Animator**, so the avatar written beside it was an
  asset nothing pointed at and the prefab could not play a clip. A model imported with
  Avatar Definition: No Avatar arrives without an Animator, and the rewiring only ever
  filled in the ones that were already there. It now adds one.
- **"No avatar was written" was reported even when one had been.** The check asked
  whether the *model* had an avatar, not whether one had been written, so building a
  generic avatar for a model that shipped without one still ended in a warning telling
  the user to go and fix something already handled. The console now says an avatar was
  built and put on the prefab's Animator.
- **Keyframe compression could exceed its own tolerance**, badly, on clips that
  arrived sparse. The error was only ever measured at the times the curve already had
  keys — which are exactly the points a rebuilt curve passes through by construction —
  so nothing was measuring the part that changes: everything between them, where the
  recomputed tangents live. On a clip out of the model importer's own reduction, with
  nine keys per bone, every sample reported inside a stated 0.5 degrees while the hand
  ended up **3.7 degrees** out. Curves are now sampled on every frame as well as on
  their keys. Same clip, after: 0.396 degrees, no sample over tolerance. Clips out of
  a baked source were never affected — a key on every frame is already a dense sample —
  which is why the glb path measured 0.44 all along. **Clips compressed before this
  fix are worth compressing again from the source.**
- Keys written by the compressor carried a tangent weight of 0 where Unity writes
  1/3. Inert while `weightedMode` is None, and a flat segment out of nowhere the
  moment weighted tangents are switched on.

- The rig check ran against the preview model, which is one global value for the
  whole project — so importing a second character's animations into its own folder
  reported every take as broken against the first character's rig. The Character
  field now looks in the destination folder first, and the preview model is only a
  fallback: when it is a guess and it does not fit the import, it is dropped instead
  of used, and the window says nothing was checked rather than crying wrong. Models
  being imported are excluded from the search, since an animation FBX often carries
  the mesh and checking a take against the file it came out of always passes. The
  search never leaves the destination folder.
- The material search included materials embedded inside model files, so a model
  could match its own embedded material and remap itself to itself. Only standalone
  `.mat` assets are candidates now.

## [0.1.0] - 2026-08-26

First release.

### Added

- **Root Motion block** in the AnimationClip Inspector. Converts a bone's position
  curve into Unity's `RootT` curves, so movement stored on the skeleton drives the
  GameObject instead. Non-destructive — the bone curve is never modified, and
  turning it off only drops the `RootT` curves.
- **Scene Preview block** in the AnimationClip Inspector, with a remembered
  preview model and a spawn button.
- **Drag and drop** a `.anim` into the Scene view to spawn a character playing it
  on loop, without entering Play mode. Several clips at once line up side by side.
- **Project context menu** (`Animation Clips`) for batch work: Use Root Motion,
  Clear Root Motion, and Loop Time Enable/Disable across any number of selected
  clips, with no reimport.
- `AnimationClipScenePreview` component, driving a clip through a `PlayableGraph`
  in manual mode — no AnimatorController required.
