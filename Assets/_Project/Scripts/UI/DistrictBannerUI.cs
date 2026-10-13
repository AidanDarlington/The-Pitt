using ThePitt.World;
using UnityEngine;
using UnityEngine.UI;

namespace ThePitt.UI
{
    /// <summary>Fades the district name in under the clock when the player enters a new district.</summary>
    public class DistrictBannerUI : MonoBehaviour
    {
        [SerializeField] private DistrictAtmosphere atmosphere;
        [SerializeField] private Text label;
        [SerializeField, Min(0.1f)] private float holdTime = 2.5f;
        [SerializeField, Min(0.1f)] private float fadeTime = 1f;

        private float shownAt = -100f;

        private void OnEnable()
        {
            if (atmosphere != null) atmosphere.DistrictChanged += Show;
        }

        private void OnDisable()
        {
            if (atmosphere != null) atmosphere.DistrictChanged -= Show;
        }

        private void Show(string districtName)
        {
            label.text = districtName.ToUpperInvariant();
            shownAt = Time.time;
        }

        private void Update()
        {
            float t = Time.time - shownAt;
            float alpha = t < fadeTime ? t / fadeTime
                : t < fadeTime + holdTime ? 1f
                : 1f - (t - fadeTime - holdTime) / fadeTime;
            var c = label.color;
            c.a = Mathf.Clamp01(alpha);
            label.color = c;
        }
    }
}
