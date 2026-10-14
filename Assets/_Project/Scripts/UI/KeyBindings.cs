using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ThePitt.UI
{
    /// <summary>
    /// The player's own keys: rebinds of the Player actions, kept as binding overrides in PlayerPrefs and laid
    /// over the input asset whenever a menu that holds it starts up.
    /// </summary>
    public static class KeyBindings
    {
        public const string Key = "Options.Bindings";
        private const string Group = "Keyboard&Mouse";

        /// <summary>What can be rebound, in the order the options list shows them: (label, action, composite part or null).</summary>
        public static readonly (string label, string action, string part)[] Rebindable =
        {
            ("Move forward", "Move", "up"),
            ("Move back", "Move", "down"),
            ("Move left", "Move", "left"),
            ("Move right", "Move", "right"),
            ("Sprint", "Sprint", null),
            ("Crouch", "Crouch", null),
            ("Jump", "Jump", null),
            ("Interact", "Interact", null),
            ("Attack / use", "Attack", null),
            ("Inventory", "Inventory", null),
            ("Map", "Map", null),
            ("Flashlight", "Flashlight", null),
            ("Build mode", "Build", null),
        };

        private static InputActionRebindingExtensions.RebindingOperation operation;

        private static int endedFrame = -10;

        /// <summary>
        /// Waiting for a key, or just got one: Esc then cancels the rebind rather than backing out of the menu,
        /// including on the frame the Esc that cancelled it was pressed.
        /// </summary>
        public static bool Rebinding => operation != null || Time.frameCount - endedFrame <= 1;

        /// <summary>Lays the saved rebinds over the asset.</summary>
        public static void Load(InputActionAsset asset)
        {
            if (asset == null) return;
            asset.RemoveAllBindingOverrides();
            string json = PlayerPrefs.GetString(Key, "");
            if (!string.IsNullOrEmpty(json)) asset.LoadBindingOverridesFromJson(json);
        }

        private static void Save(InputActionAsset asset)
        {
            PlayerPrefs.SetString(Key, asset.SaveBindingOverridesAsJson());
            PlayerPrefs.Save();
        }

        public static void ResetAll(InputActionAsset asset)
        {
            Cancel();
            asset.RemoveAllBindingOverrides();
            PlayerPrefs.DeleteKey(Key);
            PlayerPrefs.Save();
        }

        /// <summary>The keyboard and mouse binding the list rebinds: the first one, or the composite part's first.</summary>
        public static int BindingIndex(InputAction action, string part)
        {
            var bindings = action.bindings;
            for (int i = 0; i < bindings.Count; i++)
            {
                var b = bindings[i];
                if (b.isComposite) continue;
                if (part != null && (!b.isPartOfComposite || !string.Equals(b.name, part, StringComparison.OrdinalIgnoreCase))) continue;
                if (part == null && b.isPartOfComposite) continue;
                if (b.groups != null && b.groups.Contains(Group)) return i;
            }
            return -1;
        }

        /// <summary>"Left Ctrl", "LMB"…</summary>
        public static string Display(InputAction action, int index)
        {
            if (index < 0) return "—";
            string name = action.GetBindingDisplayString(index, InputBinding.DisplayStringOptions.DontIncludeInteractions);
            // Some layouts name keys by the character they type: a blank for Space, a tab for Tab. Use the key's own name.
            if (string.IsNullOrWhiteSpace(name))
                name = InputControlPath.ToHumanReadableString(action.bindings[index].effectivePath, InputControlPath.HumanReadableStringOptions.OmitDevice);
            return name;
        }

        /// <summary>Waits for the next key or mouse button and binds it. Esc cancels. Calls back when done either way.</summary>
        public static void Rebind(InputActionAsset asset, InputAction action, int index, Action done)
        {
            Cancel();
            if (action == null || index < 0) return;
            bool wasEnabled = action.enabled;
            action.Disable();
            operation = action.PerformInteractiveRebinding(index)
                .WithControlsExcluding("<Mouse>/position")
                .WithControlsExcluding("<Mouse>/delta")
                .WithControlsExcluding("<Mouse>/scroll")
                .WithCancelingThrough("<Keyboard>/escape")
                .OnMatchWaitForAnother(0.1f)
                .OnComplete(op => Finish(asset, action, wasEnabled, done, true))
                .OnCancel(op => Finish(asset, action, wasEnabled, done, false))
                .Start();
        }

        private static void Finish(InputActionAsset asset, InputAction action, bool wasEnabled, Action done, bool save)
        {
            operation?.Dispose();
            operation = null;
            endedFrame = Time.frameCount;
            if (wasEnabled) action.Enable();
            if (save) Save(asset);
            done?.Invoke();
        }

        public static void Cancel() => operation?.Cancel();

        /// <summary>Other rebindable actions already on this key, for a warning.</summary>
        public static List<string> Clashes(InputActionAsset asset, InputAction action, int index)
        {
            var list = new List<string>();
            string path = action.bindings[index].effectivePath;
            foreach (var (label, name, part) in Rebindable)
            {
                var other = asset.FindAction(name);
                if (other == null) continue;
                int i = BindingIndex(other, part);
                if (i < 0 || (other == action && i == index)) continue;
                if (other.bindings[i].effectivePath == path) list.Add(label);
            }
            return list;
        }
    }
}
