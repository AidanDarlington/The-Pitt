using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ThePitt.UI
{
    /// <summary>Whether a text box has the keyboard, so letter hotkeys (menu, flashlight) leave typing alone.</summary>
    public static class TextEntry
    {
        public static bool Active
        {
            get
            {
                var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
                return selected != null && selected.TryGetComponent<InputField>(out var field) && field.isFocused;
            }
        }
    }
}
