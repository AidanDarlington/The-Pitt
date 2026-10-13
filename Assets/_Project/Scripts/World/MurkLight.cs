using UnityEngine;

namespace ThePitt.World
{
    /// <summary>
    /// A dim fill so no corner of The Pitt reads as pure black: the city's glow hanging in the murk.
    /// A key from above, shadowed so enclosed streets fall darker than open ones, and a weaker shadowless
    /// fill from below and behind so every surface still reads; the lamps make the pools on top.
    /// Tinted toward the current district's colour by <see cref="DistrictAtmosphere"/>.
    /// </summary>
    public class MurkLight : MonoBehaviour
    {
        [SerializeField] private DistrictAtmosphere atmosphere;
        [Tooltip("Brightness of the light from above.")]
        [SerializeField, Range(0f, 2f)] private float keyIntensity = 1.1f;
        [Tooltip("Brightness of the light from below and behind, relative to the key.")]
        [SerializeField, Range(0f, 1f)] private float fillRatio = 0.65f;
        [Tooltip("How much of the district's colour the light takes on.")]
        [SerializeField, Range(0f, 1f)] private float districtTint = 0.75f;
        [Tooltip("Colour before district tinting.")]
        [SerializeField] private Color baseColor = new(0.85f, 0.8f, 0.75f);
        [SerializeField] private Vector3 keyAngles = new(62f, 35f, 0f);
        [Tooltip("Shadows on the light from above. With it near-vertical, walls and overhangs darken the alleys and\n" +
                 "underpasses beneath them: the enclosed-is-darker the baked probes used to give.")]
        [SerializeField] private LightShadows keyShadows = LightShadows.Soft;
        [SerializeField, Range(0f, 1f)] private float keyShadowStrength = 0.65f;
        [SerializeField] private Vector3 fillAngles = new(-35f, 215f, 0f);

        private Light key, fill;

        private void OnEnable()
        {
            key = Create("MurkKey", keyAngles);
            fill = Create("MurkFill", fillAngles);
            key.shadows = keyShadows;
            key.shadowStrength = keyShadowStrength;
        }

        private void OnDisable()
        {
            if (key != null) Destroy(key.gameObject);
            if (fill != null) Destroy(fill.gameObject);
        }

        private Light Create(string lightName, Vector3 angles)
        {
            var go = new GameObject(lightName);
            go.transform.SetParent(transform, false);
            go.transform.rotation = Quaternion.Euler(angles);
            var l = go.AddComponent<Light>();
            l.type = LightType.Directional;
            l.shadows = LightShadows.None;
            // Every rendering layer, including the weapon in the player's hand.
            l.renderingLayerMask = ~0;
            return l;
        }

        private void LateUpdate()
        {
            var hue = atmosphere != null ? atmosphere.CurrentHue : Color.white;
            var color = Color.Lerp(baseColor, baseColor * hue, districtTint);
            // Keep the same perceived brightness whatever the tint: a blue district would otherwise sink
            // into darkness and a green one glow, since the eye weights green far above blue.
            float target = baseColor.r * 0.2126f + baseColor.g * 0.7152f + baseColor.b * 0.0722f;
            float lum = color.r * 0.2126f + color.g * 0.7152f + color.b * 0.0722f;
            if (lum > 0.001f) color *= Mathf.Min(target / lum, 4f);
            key.color = fill.color = color;
            key.intensity = keyIntensity;
            fill.intensity = keyIntensity * fillRatio;
            key.transform.rotation = Quaternion.Euler(keyAngles);
            fill.transform.rotation = Quaternion.Euler(fillAngles);
        }
    }
}
