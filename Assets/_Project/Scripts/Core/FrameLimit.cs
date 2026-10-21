using UnityEngine;

namespace ThePitt.Core
{
    /// <summary>
    /// Caps the frame rate. Uncapped, even a light scene renders hundreds of frames a second and runs the
    /// CPU and GPU flat out (fans and all) for nothing. Out of focus it drops right down, since nobody's watching.
    /// </summary>
    public static class FrameLimit
    {
        public const string Key = "FrameLimit";
        /// <summary>The choices offered in Options; 0 = unlimited.</summary>
        public static readonly int[] Options = { 30, 60, 100, 120, 144, 165, 240, 0 };
        public const int Default = 60;
        private const int Background = 15;

        public static int Value { get; private set; } = Default;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Init()
        {
            Value = PlayerPrefs.GetInt(Key, Default);
            if (System.Array.IndexOf(Options, Value) < 0) Value = Default;
            Application.focusChanged -= OnFocusChanged;
            Application.focusChanged += OnFocusChanged;
            Apply();
        }

        public static void Set(int value)
        {
            Value = value;
            PlayerPrefs.SetInt(Key, value);
            Apply();
        }

        /// <summary>The next choice after the current one, wrapping round.</summary>
        public static int Next() => Options[(System.Array.IndexOf(Options, Value) + 1) % Options.Length];

        public static string Label(int value) => value <= 0 ? "UNLIMITED" : $"{value} FPS";

        private static void OnFocusChanged(bool focused) => Apply();

        private static void Apply()
        {
            // VSync would override targetFrameRate.
            QualitySettings.vSyncCount = 0;
            int target = Value <= 0 ? -1 : Value;
            if (!Application.isFocused) target = target < 0 ? Background : Mathf.Min(target, Background);
            Application.targetFrameRate = target;
        }
    }
}
