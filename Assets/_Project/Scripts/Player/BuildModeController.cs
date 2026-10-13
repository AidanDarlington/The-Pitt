using ThePitt.Interaction;
using ThePitt.Items;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace ThePitt.Player
{
    /// <summary>
    /// Build mode (B): look at something you've placed, left click to move it, E to pick it back up.
    /// B or Esc leaves. Normal interaction is paused while it's on.
    /// </summary>
    public class BuildModeController : MonoBehaviour
    {
        [SerializeField] private InputActionAsset actions;
        [SerializeField] private PlacementController placement;
        [SerializeField] private Inventory inventory;
        [SerializeField] private FirstPersonController controller;
        [SerializeField] private Transform viewPoint;
        [SerializeField] private Text hintLabel;
        [SerializeField, Min(1f)] private float reach = 6f;

        private InputAction toggle, move, pickUp;
        private PlacedStructure target;

        public bool IsActive { get; private set; }

        private void Awake()
        {
            var map = actions.FindActionMap("Player", true);
            toggle = map.FindAction("Build", true);
            move = map.FindAction("Attack", true);
            pickUp = map.FindAction("Interact", true);
            if (viewPoint == null && Camera.main != null) viewPoint = Camera.main.transform;
            if (inventory == null) inventory = GetComponent<Inventory>();
            if (placement == null) placement = GetComponent<PlacementController>();
            if (controller == null) controller = GetComponent<FirstPersonController>();
            SetActive(false);
        }

        private void SetActive(bool on)
        {
            IsActive = on;
            target = null;
            if (hintLabel != null) hintLabel.enabled = on;
        }

        private void Update()
        {
            bool inMenu = controller != null && !controller.InputEnabled;
            if (toggle.WasPressedThisFrame() && !inMenu)
            {
                if (placement.IsPlacing) placement.Cancel();
                SetActive(!IsActive);
            }
            if (!IsActive) return;
            if (inMenu)
            {
                SetActive(false);
                return;
            }

            // While a structure is being moved, the placement preview has the controls.
            if (placement.IsPlacing)
            {
                if (hintLabel != null) hintLabel.enabled = false;
                return;
            }
            if (hintLabel != null) hintLabel.enabled = true;

            bool escape = Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame;
            if (escape && placement.LastCancelFrame != Time.frameCount)
            {
                SetActive(false);
                return;
            }

            target = FindTarget();
            if (target == null)
            {
                SetHint("BUILD MODE   ·   look at something you've placed   ·   B to exit");
                return;
            }

            bool canPickUp = target.CanPickUp(out string reason);
            bool hasRoom = target.Item != null && inventory.SpaceFor(target.Item) > 0;
            string pick = !canPickUp ? $"<color=#C05040>can't pick up: {reason}</color>"
                : !hasRoom ? "<color=#C05040>inventory full</color>"
                : "E pick up";
            SetHint($"BUILD MODE   ·   <b>{target.DisplayName}</b>   ·   LMB move   ·   {pick}   ·   B to exit");

            if (move.WasPressedThisFrame() && target.Item != null && Cursor.lockState == CursorLockMode.Locked)
                placement.BeginMove(target.gameObject, target.Item);
            else if (pickUp.WasPressedThisFrame() && canPickUp && hasRoom)
            {
                inventory.Add(target.Item, 1);
                Destroy(target.gameObject);
                target = null;
            }
        }

        private void SetHint(string text)
        {
            if (hintLabel != null) hintLabel.text = text;
        }

        private PlacedStructure FindTarget()
        {
            var ray = new Ray(viewPoint.position, viewPoint.forward);
            PlacedStructure best = null;
            float bestDistance = float.MaxValue;
            foreach (var hit in Physics.RaycastAll(ray, reach, ~0, QueryTriggerInteraction.Collide))
            {
                if (hit.collider.transform.IsChildOf(transform) || hit.distance >= bestDistance) continue;
                bestDistance = hit.distance;
                best = hit.collider.GetComponentInParent<PlacedStructure>();
            }
            return best;
        }
    }
}
