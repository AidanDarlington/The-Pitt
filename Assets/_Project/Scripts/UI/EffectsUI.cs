using ThePitt.Player;
using UnityEngine;
using UnityEngine.UI;

namespace ThePitt.UI
{
    /// <summary>Lists active drug effects and their remaining in-game time above the vitals bars.</summary>
    public class EffectsUI : MonoBehaviour
    {
        [SerializeField] private PlayerEffects effects;
        [SerializeField] private Text label;
        [SerializeField] private Color color = new Color(0.8f, 0.6f, 1f);

        private void OnEnable()
        {
            if (effects != null) effects.Changed += Redraw;
            Redraw();
        }

        private void OnDisable()
        {
            if (effects != null) effects.Changed -= Redraw;
        }

        private void Redraw()
        {
            if (label == null || effects == null) return;
            var sb = new System.Text.StringBuilder();
            string hex = ColorUtility.ToHtmlStringRGB(color);
            foreach (var e in effects.Active)
                sb.AppendLine($"<color=#{hex}>{e.source.DisplayName.ToUpperInvariant()}</color>  {e.minutesLeft / 60}:{e.minutesLeft % 60:00}");
            label.text = sb.ToString();
        }
    }
}
