# Changelog

All notable changes to this package are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
