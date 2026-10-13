using ThePitt.World;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ThePitt.Player
{
    /// <summary>
    /// Head-mounted torch, toggled with the Flashlight action. Its beam picks up the colour of the
    /// district the player is in. Will need power/batteries later.
    /// </summary>
    public class Flashlight : MonoBehaviour
    {
        [SerializeField] private InputActionAsset actions;
        [SerializeField] private Light lamp;
        [SerializeField] private bool startOn = true;
        [SerializeField] private AudioSource clickSound;
        [Tooltip("Optional. Tints the beam toward the current district's colour.")]
        [SerializeField] private DistrictAtmosphere atmosphere;
        [SerializeField] private Color baseColor = new Color(1f, 0.95f, 0.85f);
        [Tooltip("How far the beam shifts toward the district colour.")]
        [SerializeField, Range(0f, 1f)] private float districtTint = 0.45f;

        private InputAction toggle;

        public bool IsOn => lamp != null && lamp.enabled;

        private void Awake()
        {
            toggle = actions.FindActionMap("Player", true).FindAction("Flashlight", true);
            if (lamp != null)
            {
                lamp.enabled = startOn;
                // Don't light the weapon in hand: at a few centimetres the beam would blow it out.
                lamp.renderingLayerMask &= ~(int)PlayerCombat.ViewmodelRenderingLayer;
            }
        }

        private void Update()
        {
            if (lamp == null) return;
            if (atmosphere != null) lamp.color = Color.Lerp(baseColor, baseColor * atmosphere.CurrentHue, districtTint);
            if (!toggle.WasPressedThisFrame()) return;
            lamp.enabled = !lamp.enabled;
            if (clickSound != null) clickSound.Play();
            else ThePitt.Audio.AudioManager.Play2D(ThePitt.Audio.AudioManager.Library?.click, 0.4f);
        }
    }
}
