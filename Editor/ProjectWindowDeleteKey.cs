using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FofuxoAnimationTools.Editor
{
    /// <summary>
    /// Catches the delete keystroke in the Project window before anything acts on it.
    ///
    /// The guard's problem has always been that OnWillDeleteAsset is asked after the
    /// delete has begun, and the only way to stop one is to report it as failed --
    /// which Unity answers with an error dialog saying something is keeping a hook on
    /// the asset. That is not true and it is the first thing the user sees. Getting in
    /// front of the keystroke removes the problem rather than working around it:
    /// nothing has started, so there is nothing to refuse.
    ///
    /// Where to stand is the whole question. The key never reaches the Project
    /// window's own GUI: the Shortcut Manager owns it, turns the Edit/Delete shortcut
    /// into a SoftDelete command and sends that straight to the focused window. Both
    /// happen inside EditorApplication.Internal_CallGlobalEventHandler, which calls
    /// globalEventHandler first and the shortcut system second -- so a handler on that
    /// field sees the key one step before the shortcut does, and consuming the event
    /// there means the command is never sent.
    ///
    /// Which key it is comes from the Shortcut Manager rather than from a guess. A
    /// user who rebound delete to X gets X caught; the keys Unity ships with are the
    /// fallback for when no binding can be read.
    ///
    /// The event is only taken when the Project window has focus, no text field is
    /// being edited, and the selection holds something the guard actually watches. A
    /// folder of textures keeps Unity's own confirmation, which is better than
    /// borrowing this one for a case it was not written for.
    ///
    /// The other way a person deletes something -- the Delete entry on the Assets and
    /// right-click menus -- goes through Unity's own delete and reaches the guard
    /// through OnWillDeleteAsset, where stopping it costs Unity's own
    /// could-not-be-deleted dialog first. A managed MenuItem can no longer take
    /// that entry over: Unity 6000.6 rejects it as already existing.
    /// </summary>
    [InitializeOnLoad]
    internal static class ProjectWindowDeleteKey
    {
        /// <summary>
        /// The shortcuts that mean "delete this". Edit/Delete is the one the Project
        /// window listens to; the menu entries are here because a binding put on one
        /// of them means the same thing to whoever put it there.
        /// </summary>
        private static readonly string[] DeleteShortcuts =
        {
            "Edit/Delete", "Main Menu/Edit/Delete", "Main Menu/Assets/Delete"
        };

        private static readonly EditorApplication.CallbackFunction Handler = OnEvent;

        static ProjectWindowDeleteKey()
        {
            FieldInfo hook = typeof(EditorApplication).GetField(
                "globalEventHandler",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);

            if (hook == null)
            {
                Debug.LogWarning(
                    "Fofuxo's Animation Tools: this version of Unity has no " +
                    "EditorApplication.globalEventHandler, so the delete key cannot be " +
                    "caught before the Shortcut Manager. Deleting still asks about " +
                    "references, but Unity puts its own could-not-be-deleted dialog first.");
                return;
            }

            var subscribed = (EditorApplication.CallbackFunction)hook.GetValue(null);

            // First in the list, ahead of the shortcut system's own handler, which
            // subscribes to this same field: whoever runs first decides whether the
            // key still exists by the time the other one looks. Removing before
            // combining keeps a reload from handling every key twice.
            hook.SetValue(null, (EditorApplication.CallbackFunction)Delegate.Combine(
                Handler, Delegate.Remove(subscribed, Handler)));
        }

        private static void OnEvent()
        {
            Event current = Event.current;

            if (current == null || current.type != EventType.KeyDown)
            {
                return;
            }

            if (!FofuxoToolsSettings.GuardDeletes || !FofuxoToolsSettings.InterceptDelete)
            {
                return;
            }

            // Renaming an asset, or typing in the search field. The letter is a letter.
            if (EditorGUIUtility.editingTextField || !InProjectWindow())
            {
                return;
            }

            if (!IsDelete(current))
            {
                return;
            }

            List<string> paths = Selected();

            if (paths.Count == 0 || !AssetDeleteGuard.Watches(paths))
            {
                return;
            }

            // Taken here, the Shortcut Manager never sees it and no command is sent.
            current.Use();

            // Not from inside an event handler.
            EditorApplication.delayCall += () => AssetDeleteGuard.RequestDelete(paths);
        }

        private static bool InProjectWindow()
        {
            EditorWindow focused = EditorWindow.focusedWindow;

            return focused != null && focused.GetType().Name == "ProjectBrowser";
        }

        /// <summary>
        /// Whether this keystroke is the one bound to deleting. The Shortcut Manager
        /// is asked, so a rebound key is the one that gets caught. A binding made of
        /// more than one keystroke is left alone, being something this cannot answer
        /// halfway through.
        /// </summary>
        private static bool IsDelete(Event current)
        {
            bool bound = false;

            foreach (string id in DeleteShortcuts)
            {
                ShortcutBinding binding;

                try
                {
                    binding = ShortcutManager.instance.GetShortcutBinding(id);
                }
                catch (ArgumentException)
                {
                    // No shortcut by that name in this version of Unity.
                    continue;
                }

                KeyCombination? only = Single(binding);

                if (only == null)
                {
                    continue;
                }

                bound = true;

                if (Matches(current, only.Value))
                {
                    return true;
                }
            }

            if (bound)
            {
                return false;
            }

            // Nothing readable to compare against, so fall back to the keys Unity
            // ships with: Delete on Windows and Linux, Command+Backspace on a Mac.
            // Backspace on its own is not a delete anywhere.
            if (current.keyCode == KeyCode.Delete)
            {
                return true;
            }

            return current.keyCode == KeyCode.Backspace && (current.command || current.control);
        }

        private static KeyCombination? Single(ShortcutBinding binding)
        {
            KeyCombination? only = null;

            foreach (KeyCombination combination in binding.keyCombinationSequence)
            {
                if (only != null)
                {
                    return null;
                }

                only = combination;
            }

            return only;
        }

        private static bool Matches(Event current, KeyCombination combination)
        {
            if (current.keyCode != combination.keyCode)
            {
                return false;
            }

            ShortcutModifiers modifiers = combination.modifiers;

            bool alt = (modifiers & ShortcutModifiers.Alt) != 0;
            bool shift = (modifiers & ShortcutModifiers.Shift) != 0;
            bool action = (modifiers & ShortcutModifiers.Action) != 0;

            return current.alt == alt &&
                   current.shift == shift &&
                   EditorGUI.actionKey == action;
        }

        private static List<string> Selected()
        {
            var paths = new List<string>();

            foreach (Object selected in Selection.GetFiltered(typeof(Object), SelectionMode.Assets))
            {
                string path = AssetDatabase.GetAssetPath(selected);

                if (!string.IsNullOrEmpty(path) && path.StartsWith("Assets"))
                {
                    paths.Add(path);
                }
            }

            return paths;
        }
    }
}
