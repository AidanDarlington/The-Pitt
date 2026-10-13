using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace ThePitt.UI
{
    /// <summary>
    /// Shared canvas scaling. Every canvas is laid out against 1920x1080 and scaled with Expand, so the
    /// whole reference area always fits: on ultrawide the canvas just gets wider (HUD spreads to the
    /// edges, panels stay centred and sized by height) instead of cropping top and bottom.
    /// The player's UI scale multiplier sits on top.
    /// </summary>
    public static class UIScale
    {
        public const string Key = "UIScale";
        public const float Min = 0.75f, Max = 1.1f; // above ~1.1 the tallest panels (960px) no longer fit 1080
        public static readonly Vector2 Reference = new(1920f, 1080f);

        private static readonly List<CanvasScaler> scalers = new();

        public static float Value { get; private set; } = 1f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Init()
        {
            Value = Mathf.Clamp(PlayerPrefs.GetFloat(Key, 1f), Min, Max);
            scalers.Clear();
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        // Canvases placed in scenes rather than built in code.
        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            foreach (var root in scene.GetRootGameObjects())
            foreach (var scaler in root.GetComponentsInChildren<CanvasScaler>(true))
                if (scaler.uiScaleMode == CanvasScaler.ScaleMode.ScaleWithScreenSize) Apply(scaler);
        }

        /// <summary>Sets a canvas up for screen-size scaling and keeps it following the UI scale setting.</summary>
        public static void Apply(CanvasScaler scaler)
        {
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
            // A smaller reference resolution means a bigger scale factor.
            scaler.referenceResolution = Reference / Value;
            if (!scalers.Contains(scaler)) scalers.Add(scaler);
        }

        public static void Set(float value)
        {
            // 5% steps, so dragging the slider doesn't relayout every canvas on every pixel.
            value = Mathf.Clamp(Mathf.Round(value * 20f) / 20f, Min, Max);
            if (Mathf.Approximately(value, Value)) return;
            Value = value;
            PlayerPrefs.SetFloat(Key, Value);
            scalers.RemoveAll(s => s == null);
            foreach (var scaler in scalers) scaler.referenceResolution = Reference / Value;
        }
    }
}
