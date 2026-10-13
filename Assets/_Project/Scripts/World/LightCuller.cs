using System.Collections.Generic;
using UnityEngine;

namespace ThePitt.World
{
    /// <summary>
    /// Keeps the city's lamps affordable as live lights: only the nearest few dozen around the camera are on,
    /// fading out with distance so none pops. Far lamps still read through their glowing fittings and the fog.
    /// Added to the generated city by WorldBootstrap; covers every light under it.
    /// </summary>
    public class LightCuller : MonoBehaviour
    {
        [Tooltip("Lamps beyond this are off.")]
        [SerializeField, Min(5f)] private float range = 60f;
        [Tooltip("Lamps fade out over this last stretch of the range.")]
        [SerializeField, Min(1f)] private float fadeLength = 15f;
        [Tooltip("At most this many lamps are on at once (the nearest).")]
        [SerializeField, Min(1)] private int maxLights = 96;
        [Tooltip("How often the nearest set is re-picked, in seconds. Fades update every frame.")]
        [SerializeField, Min(0.02f)] private float refreshInterval = 0.2f;

        private struct Lamp
        {
            public Light light;
            public LightFlicker flicker;
            public float intensity;
        }

        private readonly List<Lamp> lamps = new();
        private readonly List<int> order = new();
        private readonly HashSet<int> active = new();
        private Transform viewer;
        private float nextRefresh;

        private void Start()
        {
            foreach (var l in GetComponentsInChildren<Light>(true))
            {
                if (l.type == LightType.Directional) continue;
                lamps.Add(new Lamp { light = l, flicker = l.GetComponent<LightFlicker>(), intensity = l.intensity });
                l.enabled = false;
            }
        }

        private void LateUpdate()
        {
            if (viewer == null && Camera.main != null) viewer = Camera.main.transform;
            if (viewer == null) return;
            var eye = viewer.position;

            if (Time.unscaledTime >= nextRefresh)
            {
                nextRefresh = Time.unscaledTime + refreshInterval;
                Pick(eye);
            }

            float fadeStart = range - fadeLength;
            foreach (int i in active)
            {
                var lamp = lamps[i];
                if (lamp.light == null) continue;
                float d = Vector3.Distance(eye, lamp.light.transform.position);
                float fade = 1f - Mathf.Clamp01((d - fadeStart) / fadeLength);
                if (lamp.flicker != null) lamp.flicker.Fade = fade;
                else lamp.light.intensity = lamp.intensity * fade;
            }
        }

        /// <summary>Switches on the nearest lamps in range and off everything else.</summary>
        private void Pick(Vector3 eye)
        {
            order.Clear();
            float rangeSq = range * range;
            for (int i = 0; i < lamps.Count; i++)
                if (lamps[i].light != null && (lamps[i].light.transform.position - eye).sqrMagnitude < rangeSq) order.Add(i);
            if (order.Count > maxLights)
                order.Sort((a, b) => (lamps[a].light.transform.position - eye).sqrMagnitude
                    .CompareTo((lamps[b].light.transform.position - eye).sqrMagnitude));

            var keep = new HashSet<int>();
            for (int k = 0; k < order.Count && k < maxLights; k++) keep.Add(order[k]);
            foreach (int i in active)
                if (!keep.Contains(i) && lamps[i].light != null) lamps[i].light.enabled = false;
            foreach (int i in keep)
                if (!active.Contains(i)) lamps[i].light.enabled = true;
            active.Clear();
            active.UnionWith(keep);
        }
    }
}
