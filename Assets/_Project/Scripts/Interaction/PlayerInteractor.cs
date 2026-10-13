using System;
using ThePitt.Items;
using ThePitt.Player;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ThePitt.Interaction
{
    /// <summary>Raycasts from the camera for an <see cref="IInteractable"/> and triggers it on Interact.</summary>
    public class PlayerInteractor : MonoBehaviour
    {
        [SerializeField] private InputActionAsset actions;
        [SerializeField] private Transform viewPoint;
        [SerializeField] private FirstPersonController controller;
        [SerializeField] private Inventory inventory;
        [SerializeField, Min(0.1f)] private float reach = 2.5f;
        [SerializeField] private LayerMask mask = ~0;

        private InputAction interact;
        private BuildModeController buildMode;
        private readonly RaycastHit[] hits = new RaycastHit[16];

        public Inventory Inventory => inventory;
        public InputAction InteractAction => interact;
        public IInteractable Focus { get; private set; }

        /// <summary>Fired when the focused object or its prompt changes: prompt text, can interact. Null text = nothing focused.</summary>
        public event Action<string, bool> PromptChanged;

        private string lastPrompt;
        private bool lastCanInteract;

        private void Awake()
        {
            interact = actions.FindActionMap("Player", true).FindAction("Interact", true);
            if (viewPoint == null && Camera.main != null) viewPoint = Camera.main.transform;
            if (controller == null) controller = GetComponent<FirstPersonController>();
            if (inventory == null) inventory = GetComponent<Inventory>();
            buildMode = GetComponent<BuildModeController>();
        }

        private void Update()
        {
            bool active = controller == null || (controller.InputEnabled && Cursor.lockState == CursorLockMode.Locked);
            // Build mode takes over E and the crosshair.
            if (buildMode != null && buildMode.IsActive) active = false;
            Focus = active ? FindFocus() : null;

            string prompt = Focus?.GetPrompt(this);
            bool canInteract = Focus != null && Focus.CanInteract(this);
            if (prompt != lastPrompt || canInteract != lastCanInteract)
            {
                lastPrompt = prompt;
                lastCanInteract = canInteract;
                PromptChanged?.Invoke(prompt, canInteract);
            }

            if (canInteract && interact.WasPressedThisFrame()) Focus.Interact(this);
        }

        private IInteractable FindFocus()
        {
            if (viewPoint == null) return null;
            var ray = new Ray(viewPoint.position, viewPoint.forward);
            int count = Physics.RaycastNonAlloc(ray, hits, reach, mask, QueryTriggerInteraction.Collide);

            // Nearest hit that isn't the player's own collider; anything solid in front blocks what's behind it.
            RaycastHit? nearest = null;
            for (int i = 0; i < count; i++)
            {
                if (hits[i].collider.transform.IsChildOf(transform)) continue;
                if (nearest == null || hits[i].distance < nearest.Value.distance) nearest = hits[i];
            }
            return nearest?.collider.GetComponentInParent<IInteractable>();
        }
    }
}
