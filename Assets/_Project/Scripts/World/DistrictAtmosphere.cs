using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace ThePitt.World
{
    /// <summary>
    /// Blends the look toward the colours of the district the player is in, so darkness reads as a
    /// colour (crimson, sodium, mildew green, cold blue, ultraviolet) rather than a void.
    /// Sets live ambient and fog, and a colour-filter grade on <see cref="gradeVolume"/>. The city is built at
    /// runtime with no baked lighting, so the ambient (strongest from above, onto street floors) stands in for
    /// the light the old bake bounced around; tuned against captures of the baked city.
    /// </summary>
    public class DistrictAtmosphere : MonoBehaviour
    {
        [SerializeField] private PittGreybox greybox;
        [Tooltip("Defaults to the main camera.")]
        [SerializeField] private Transform viewer;
        [Tooltip("How far district colours bleed into each other at borders.")]
        [SerializeField, Min(1f)] private float blendDistance = 25f;
        [SerializeField, Min(0.01f)] private float transitionSpeed = 1.5f;
        [Tooltip("Overall multiplier on district ambient light.")]
        [SerializeField, Range(0f, 10f)] private float ambientBoost = 2.5f;
        [Tooltip("Ambient brightness on upward faces, relative to walls.")]
        [SerializeField, Range(0f, 5f)] private float skyBias = 1.5f;
        [Tooltip("Ambient brightness on downward faces (undersides of ledges, catwalks).")]
        [SerializeField, Range(0f, 1f)] private float groundBias = 0.3f;
        [Tooltip("Exponential-squared fog. Thick enough to tint the space overhead, thin enough to see down a walkway.")]
        [SerializeField, Range(0f, 0.05f)] private float fogDensity = 0.0055f;

        [Header("Watcher Outposts")]
        [SerializeField] private Color outpostAmbient = new(0.07f, 0.08f, 0.1f);
        [SerializeField] private Color outpostFog = new(0.06f, 0.07f, 0.09f);

        [Header("Grade")]
        [Tooltip("Global volume whose Color Adjustments filter is tinted by the current district.")]
        [SerializeField] private Volume gradeVolume;
        [Tooltip("How strongly the district colour filters the whole image. Keep low: the grade, the murk light and the\n" +
                 "split toning all multiply by the district colour, and stacked multiplies drive every hue toward red/green/blue.")]
        [SerializeField, Range(0f, 1f)] private float gradeStrength = 0.08f;
        [Tooltip("District colour pushed into the shadows (split toning). The murk takes the colour; lights keep theirs.")]
        [SerializeField, Range(0f, 1f)] private float shadowTint = 0.3f;
        [SerializeField, Range(0f, 1f)] private float midtoneTint = 0.15f;
        [Tooltip("Highlights lean warm, like sodium and fluorescent light.")]
        [SerializeField] private Color highlightTint = new Color(1f, 0.94f, 0.8f);
        [Tooltip("Exposure of the grade volume. Slightly negative: wall albedo is bright (it carries colour and detail), so\n" +
                 "this sits the frame down to the Aneurism reference (mean luminance ~0.075).")]
        [SerializeField, Range(-3f, 5f)] private float gradeExposure = -0.3f;

        private ColorAdjustments grade;
        private ShadowsMidtonesHighlights split;

        /// <summary>The current district's colour, normalised so its brightest channel is 1, blended at borders.</summary>
        public Color CurrentHue { get; private set; } = Color.white;

        private Color ambient, fog;
        private bool initialised;
        private int currentDistrict = -1;

        /// <summary>Fired with the new district's name when the player crosses into it.</summary>
        public event Action<string> DistrictChanged;

        public string CurrentDistrictName =>
            greybox != null && currentDistrict >= 0 && currentDistrict < greybox.districts.Count
                ? greybox.districts[currentDistrict].name
                : "";

        private void Start()
        {
            // The city lives in the World scene; no reference can reach across.
            if (greybox == null) greybox = PittGreybox.Current;
            // The Pitt has no sun or sky: only the city's own lights, and black beyond the edge.
            RenderSettings.sun = null;
            RenderSettings.skybox = null;
            RenderSettings.reflectionIntensity = 0f;
            if (Camera.main != null)
            {
                Camera.main.clearFlags = CameraClearFlags.SolidColor;
                Camera.main.backgroundColor = Color.black;
            }
        }

        private void LateUpdate()
        {
            if (greybox == null || greybox.districts.Count == 0) return;
            if (viewer == null && Camera.main != null) viewer = Camera.main.transform;
            if (viewer == null) return;

            var p = new Vector2(viewer.position.x, viewer.position.z);
            Blend(p, out var targetAmbient, out var targetFog);

            int district = greybox.DistrictIndexAt(p);
            // At a Watcher outpost the murk clears to a cold, clean white, like nowhere else down here.
            float outpost = OutpostBlend(p);
            if (outpost > 0f)
            {
                targetAmbient = Color.Lerp(targetAmbient, outpostAmbient, outpost);
                targetFog = Color.Lerp(targetFog, outpostFog, outpost);
            }

            // Inside a property (built out of the way, off the map): take on its own district.
            int home = PropertyDistrict(viewer.position);
            if (home >= 0)
            {
                district = home;
                var palette = greybox.palettes[greybox.districts[home].palette % greybox.palettes.Length];
                targetAmbient = palette.ambient;
                targetFog = palette.fog;
            }
            if (district != currentDistrict)
            {
                currentDistrict = district;
                DistrictChanged?.Invoke(greybox.districts[district].name);
            }

            if (!initialised)
            {
                ambient = targetAmbient;
                fog = targetFog;
                initialised = true;
            }
            float t = 1f - Mathf.Exp(-transitionSpeed * Time.deltaTime);
            ambient = Color.Lerp(ambient, targetAmbient, t);
            fog = Color.Lerp(fog, targetFog, t);
            float max = Mathf.Max(ambient.r, Mathf.Max(ambient.g, ambient.b));
            CurrentHue = max > 0f ? new Color(ambient.r / max, ambient.g / max, ambient.b / max) : Color.white;

            // Trilight gives faces some direction (lit from above, darker underneath) so shapes read
            // in what is otherwise flat coloured murk.
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = ambient * (ambientBoost * skyBias);
            RenderSettings.ambientEquatorColor = ambient * ambientBoost;
            RenderSettings.ambientGroundColor = ambient * (ambientBoost * groundBias);
            RenderSettings.fog = fogDensity > 0f;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = fogDensity;
            RenderSettings.fogColor = fog;

            ApplyGrade();
        }

        private void ApplyGrade()
        {
            if (gradeVolume == null) return;
            if (grade == null && !gradeVolume.profile.TryGet(out grade)) grade = gradeVolume.profile.Add<ColorAdjustments>();

            grade.colorFilter.overrideState = true;
            grade.colorFilter.value = Color.Lerp(Color.white, CurrentHue, gradeStrength);
            grade.postExposure.overrideState = true;
            grade.postExposure.value = gradeExposure;

            if (split == null && !gradeVolume.profile.TryGet(out split)) split = gradeVolume.profile.Add<ShadowsMidtonesHighlights>();
            Vector4 Tone(Color c) => new Vector4(c.r, c.g, c.b, 0f);
            split.shadows.overrideState = true;
            split.shadows.value = Tone(Color.Lerp(Color.white, CurrentHue, shadowTint));
            split.midtones.overrideState = true;
            split.midtones.value = Tone(Color.Lerp(Color.white, CurrentHue, midtoneTint));
            split.highlights.overrideState = true;
            split.highlights.value = Tone(highlightTint);
        }

        /// <summary>1 inside a Watcher outpost, fading to 0 a few metres out.</summary>
        private float OutpostBlend(Vector2 p)
        {
            float best = 0f;
            foreach (var o in greybox.watcherOutposts)
            {
                // The platform and its bridge.
                var r = Rect.MinMaxRect(Mathf.Min(o.rect.xMin, o.bridge.xMin), Mathf.Min(o.rect.yMin, o.bridge.yMin),
                    Mathf.Max(o.rect.xMax, o.bridge.xMax), Mathf.Max(o.rect.yMax, o.bridge.yMax));
                float dx = Mathf.Max(r.xMin - p.x, 0f, p.x - r.xMax);
                float dz = Mathf.Max(r.yMin - p.y, 0f, p.y - r.yMax);
                best = Mathf.Max(best, 1f - Mathf.Clamp01(Mathf.Sqrt(dx * dx + dz * dz) / 8f));
            }
            return best;
        }

        private int PropertyDistrict(Vector3 position)
        {
            foreach (var property in Properties.Property.All)
            {
                if (!property.Contains(position, -0.5f)) continue;
                for (int i = 0; i < greybox.districts.Count; i++)
                    if (greybox.districts[i].name == property.District) return i;
            }
            return -1;
        }

        /// <summary>Soft-min weighting of district colours by distance to each district centre.</summary>
        private void Blend(Vector2 p, out Color blendedAmbient, out Color blendedFog)
        {
            var districts = greybox.districts;
            float nearest = float.MaxValue;
            foreach (var d in districts) nearest = Mathf.Min(nearest, Vector2.Distance(p, d.center));

            blendedAmbient = Color.black;
            blendedFog = Color.black;
            float total = 0f;
            foreach (var d in districts)
            {
                float w = Mathf.Exp(-(Vector2.Distance(p, d.center) - nearest) / blendDistance);
                var palette = greybox.palettes[d.palette % greybox.palettes.Length];
                blendedAmbient += palette.ambient * w;
                blendedFog += palette.fog * w;
                total += w;
            }
            blendedAmbient /= total;
            blendedFog /= total;
        }
    }
}
