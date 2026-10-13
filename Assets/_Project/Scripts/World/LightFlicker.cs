using UnityEngine;

namespace ThePitt.World
{
    /// <summary>A failing light: mostly steady, with buzzing dips and the odd blackout.</summary>
    [RequireComponent(typeof(Light))]
    public class LightFlicker : MonoBehaviour
    {
        [SerializeField, Range(0f, 1f)] private float buzz = 0.15f;
        [Tooltip("Chance per second of cutting out briefly.")]
        [SerializeField, Range(0f, 5f)] private float dropoutRate = 0.4f;
        [SerializeField] private Vector2 dropoutDuration = new(0.05f, 0.4f);
        [Tooltip("Optional glowing fixture to dim along with the light.")]
        [SerializeField] private Renderer fixture;

        private static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");
        private Light lamp;
        private float baseIntensity;
        private float seed;
        private float offUntil;
        private Color baseEmission;
        private MaterialPropertyBlock block;

        private void Awake()
        {
            lamp = GetComponent<Light>();
            baseIntensity = lamp.intensity;
            seed = Random.value * 100f;
            CacheFixture();
        }

        /// <summary>The glowing fixture to dim along with the light (for lights built while the game runs).</summary>
        /// <summary>Distance fade from <see cref="LightCuller"/>; the flicker plays on top of it.</summary>
        public float Fade { get; set; } = 1f;

        public void SetFixture(Renderer renderer)
        {
            fixture = renderer;
            CacheFixture();
        }

        private void CacheFixture()
        {
            block = null;
            if (fixture != null && fixture.sharedMaterial.HasProperty(EmissionColor))
            {
                baseEmission = fixture.sharedMaterial.GetColor(EmissionColor);
                block = new MaterialPropertyBlock();
            }
        }

        private void Update()
        {
            float level;
            if (Time.time < offUntil)
            {
                level = 0.05f;
            }
            else
            {
                if (Random.value < dropoutRate * Time.deltaTime)
                    offUntil = Time.time + Random.Range(dropoutDuration.x, dropoutDuration.y);
                level = 1f - buzz * Mathf.PerlinNoise(seed, Time.time * 12f);
            }

            lamp.intensity = baseIntensity * level * Fade;
            if (block != null)
            {
                block.SetColor(EmissionColor, baseEmission * level);
                fixture.SetPropertyBlock(block);
            }
        }
    }
}
