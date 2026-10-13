using ThePitt.Core;
using ThePitt.Interaction;
using UnityEngine;

namespace ThePitt.Production
{
    /// <summary>A switchable lamp that provides light to planters. Will draw power once utilities exist.</summary>
    public class GrowLight : MonoBehaviour, IInteractable, ISaveState
    {
        [SerializeField] private bool isOn = true;
        [Tooltip("Light level (0-1) planters receive while on.")]
        [SerializeField, Range(0f, 1f)] private float level = 0.85f;
        [SerializeField] private Light lamp;
        [SerializeField] private Renderer bulb;
        [SerializeField] private Color onEmission = new Color(1f, 0.55f, 0.85f) * 2f;

        private static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");
        private MaterialPropertyBlock block;

        public bool IsOn => isOn;
        public float Level => isOn ? level : 0f;

        private void Start() => Apply();

        public string GetPrompt(PlayerInteractor interactor) => isOn ? "Turn grow light off" : "Turn grow light on";
        public bool CanInteract(PlayerInteractor interactor) => true;

        public void Interact(PlayerInteractor interactor)
        {
            isOn = !isOn;
            Apply();
        }

        public string CaptureState() => isOn ? "1" : "0";

        public void RestoreState(string json)
        {
            isOn = json == "1";
            Apply();
        }

        private void Apply()
        {
            if (lamp != null) lamp.enabled = isOn;
            if (bulb != null)
            {
                block ??= new MaterialPropertyBlock();
                bulb.GetPropertyBlock(block);
                block.SetColor(EmissionColor, isOn ? onEmission : Color.black);
                bulb.SetPropertyBlock(block);
            }
        }
    }
}
