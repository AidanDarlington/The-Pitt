using System;
using System.Collections.Generic;
using ThePitt.Interaction;
using ThePitt.Items;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace ThePitt.Player
{
    /// <summary>
    /// Places a structure item (a station) in the world: a see-through preview follows where the
    /// player looks, green where it fits and red where it doesn't. It lines up with the room's walls (rooms are
    /// square to the world), whichever way the player faces: a new one starts facing the room's door, and
    /// R turns it a quarter turn and scroll turns it 15 degrees, both measured from the walls. Left click places, right click or Esc cancels. Placing uses up one of the item. Structures only go down inside a
    /// property the player owns, and only if the property has the water, electricity and gas to run them.
    /// </summary>
    public class PlacementController : MonoBehaviour
    {
        [SerializeField] private InputActionAsset actions;
        [SerializeField] private Inventory inventory;
        [SerializeField] private Transform viewPoint;
        [SerializeField] private Material validMaterial;
        [SerializeField] private Material invalidMaterial;
        [Tooltip("Optional hint shown while placing.")]
        [SerializeField] private Text hintLabel;
        [SerializeField, Min(1f)] private float reach = 5f;
        [Tooltip("Steepest ground (degrees) a structure can sit on.")]
        [SerializeField, Range(0f, 60f)] private float maxSlope = 20f;
        [Tooltip("Parent for placed structures; defaults to the scene root.")]
        [SerializeField] private Transform placedParent;

        /// <summary>Each R press: rooms are rectangles, so a quarter turn keeps it square to the walls.</summary>
        private const float QuarterTurn = 90f;
        /// <summary>Each scroll step, for setting it at an angle to the walls.</summary>
        private const float ScrollTurn = 15f;

        private InputAction place;
        private ItemDefinition item;
        private GameObject ghost;
        private readonly List<Renderer> ghostRenderers = new();
        private Bounds localBounds;
        private float yaw;
        private bool valid;
        /// <summary>Why the current spot won't do (outside a property, or short of a utility); null if it's fine.</summary>
        private string problem;
        // Allowed, but over a utility cap: shown as a caution.
        private string warning;
        private readonly Vector3[] footprint = new Vector3[5];
        private int startedFrame;
        // Set while moving an existing structure instead of placing a new one from the inventory.
        private GameObject moving;
        private Vector3 movingFromPosition;
        private Quaternion movingFromRotation;

        public bool IsPlacing => ghost != null;
        /// <summary>What's being placed or moved, while <see cref="IsPlacing"/>.</summary>
        public ItemDefinition PlacingItem => ghost != null ? item : null;
        public Transform PlacedParent => placedParent;
        public bool IsMoving => moving != null;
        /// <summary>Frame on which placement was last cancelled, so Esc isn't handled twice.</summary>
        public int LastCancelFrame { get; private set; } = -1;
        /// <summary>Frame on which something was last put down, so the same click doesn't also pick it up again.</summary>
        public int LastPlacedFrame { get; private set; } = -1;

        /// <summary>Fired with the placed object.</summary>
        public event Action<GameObject> Placed;

        private void Awake()
        {
            place = actions.FindActionMap("Player", true).FindAction("Attack", true);
            if (viewPoint == null && Camera.main != null) viewPoint = Camera.main.transform;
            if (inventory == null) inventory = GetComponent<Inventory>();
            SetHint(false);
        }

        public void Begin(ItemDefinition placeable)
        {
            Cancel();
            if (placeable == null || !placeable.IsPlaceable || inventory.Count(placeable) <= 0) return;
            item = placeable;
            startedFrame = Time.frameCount;
            // Square to the room, facing its door; away from the player if they aren't in a property.
            var room = Properties.Property.At(transform.position + Vector3.up * 0.1f);
            yaw = (room != null ? room.InsideYaw : transform.eulerAngles.y) + 180f;
            BuildGhost();
            SetHint(true);
        }

        /// <summary>Lifts a placed structure into the preview; placing puts it down again with its state intact.</summary>
        public void BeginMove(GameObject target, ItemDefinition placeable)
        {
            Cancel();
            if (target == null || placeable == null || !placeable.IsPlaceable) return;
            item = placeable;
            moving = target;
            movingFromPosition = target.transform.position;
            movingFromRotation = target.transform.rotation;
            yaw = target.transform.eulerAngles.y;
            startedFrame = Time.frameCount;
            target.SetActive(false);
            BuildGhost();
            SetHint(true);
        }

        public void Cancel()
        {
            if (ghost != null) LastCancelFrame = Time.frameCount;
            if (moving != null)
            {
                // Nothing moved: put it back where it was.
                moving.transform.SetPositionAndRotation(movingFromPosition, movingFromRotation);
                moving.SetActive(true);
                moving = null;
            }
            if (ghost != null) Destroy(ghost);
            ghost = null;
            item = null;
            ghostRenderers.Clear();
            SetHint(false);
        }

        private void SetHint(bool on)
        {
            if (hintLabel == null) return;
            hintLabel.enabled = on;
            if (!on) return;
            // Grows upward from the bottom line, so the draw and any problem stay on screen.
            hintLabel.verticalOverflow = VerticalWrapMode.Overflow;
            hintLabel.alignment = TextAnchor.LowerCenter;
            hintLabel.text = (problem != null ? $"<color=#E06048>{problem}</color>\n" : "") +
                             (problem == null && warning != null ? $"<color=#E8B040>{warning}</color>\n" : "") +
                             $"Draws {Properties.Property.DrawText(item.UtilityDraw)}\n" +
                             $"{(moving != null ? "Moving" : "Placing")} {item.DisplayName}   ·   LMB place   ·   R / scroll rotate   ·   RMB / Esc cancel";
        }

        /// <summary>A copy of the prefab with every behaviour, light and collider stripped, drawn see-through.</summary>
        private void BuildGhost()
        {
            ghost = Instantiate(item.PlaceablePrefab);
            ghost.name = $"{item.PlaceablePrefab.name} (preview)";
            foreach (var b in ghost.GetComponentsInChildren<MonoBehaviour>(true)) b.enabled = false;
            foreach (var b in ghost.GetComponentsInChildren<MonoBehaviour>(true)) Destroy(b);
            foreach (var l in ghost.GetComponentsInChildren<Light>(true)) l.enabled = false;
            foreach (var c in ghost.GetComponentsInChildren<Collider>(true)) Destroy(c);

            ghost.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            bool first = true;
            foreach (var r in ghost.GetComponentsInChildren<Renderer>(true))
            {
                ghostRenderers.Add(r);
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                if (first) localBounds = r.bounds;
                else localBounds.Encapsulate(r.bounds);
                first = false;
            }
        }

        private void Update()
        {
            if (ghost == null) return;
            if (moving == null && inventory.Count(item) <= 0)
            {
                Cancel();
                return;
            }

            var keyboard = Keyboard.current;
            var mouse = Mouse.current;
            if ((keyboard != null && keyboard.escapeKey.wasPressedThisFrame) || (mouse != null && mouse.rightButton.wasPressedThisFrame))
            {
                Cancel();
                return;
            }
            if (keyboard != null && keyboard.rKey.wasPressedThisFrame) yaw += QuarterTurn;
            if (mouse != null)
            {
                float scroll = mouse.scroll.ReadValue().y;
                if (Mathf.Abs(scroll) > 0.01f) yaw += Mathf.Sign(scroll) * ScrollTurn;
            }

            UpdatePose();
            var mat = valid ? validMaterial : invalidMaterial;
            foreach (var r in ghostRenderers)
            {
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++) mats[i] = mat;
                r.sharedMaterials = mats;
            }

            // Ignore the click that picked the item in the inventory.
            if (valid && Time.frameCount > startedFrame + 1 && Cursor.lockState == CursorLockMode.Locked && place.WasPressedThisFrame())
                Place();
        }

        private void UpdatePose()
        {
            valid = false;
            var ray = new Ray(viewPoint.position, viewPoint.forward);
            var hits = Physics.RaycastAll(ray, reach, ~0, QueryTriggerInteraction.Ignore);
            RaycastHit? ground = null;
            foreach (var h in hits)
                if (!h.collider.transform.IsChildOf(transform) && (ground == null || h.distance < ground.Value.distance))
                    ground = h;

            Vector3 point;
            if (ground != null)
            {
                point = ground.Value.point;
                valid = Vector3.Angle(ground.Value.normal, Vector3.up) <= maxSlope;
            }
            else
            {
                // Nothing in reach: float it at arm's length so the player can see it.
                point = ray.GetPoint(reach * 0.6f);
            }

            // Rooms are rectangles square to the world: keep to whole scroll steps of it, so it lines up with the walls.
            yaw = Mathf.Round(yaw / ScrollTurn) * ScrollTurn;
            var rotation = Quaternion.Euler(0f, yaw, 0f);
            ghost.transform.SetPositionAndRotation(point, rotation);
            if (valid) valid = !Blocked(point, rotation);

            // Only inside a property the player owns (every corner of the footprint, and the middle), and only
            // if the property can supply what it draws.
            string reason = null;
            if (!InsideOwnedProperty(point, rotation)) reason = "Structures can only go inside a property you own";
            string caution = null;
            if (reason == null && !Properties.Property.OwnedAt(point + Vector3.up * 0.1f, 0.05f).Fits(item, out string shortfall, out caution))
                reason = shortfall + " Bribe the Watchers for more.";
            if (reason != problem || caution != warning)
            {
                problem = reason;
                warning = caution;
                SetHint(true);
            }
            valid &= reason == null;
        }

        private bool InsideOwnedProperty(Vector3 position, Quaternion rotation)
        {
            var c = localBounds.center;
            var e = localBounds.extents;
            footprint[0] = position + rotation * new Vector3(c.x - e.x, 0.1f, c.z - e.z);
            footprint[1] = position + rotation * new Vector3(c.x + e.x, 0.1f, c.z - e.z);
            footprint[2] = position + rotation * new Vector3(c.x - e.x, 0.1f, c.z + e.z);
            footprint[3] = position + rotation * new Vector3(c.x + e.x, 0.1f, c.z + e.z);
            footprint[4] = position + Vector3.up * 0.1f;
            return Properties.Property.AllInsideOwned(footprint);
        }

        /// <summary>True if the footprint overlaps anything other than the ground.</summary>
        private bool Blocked(Vector3 position, Quaternion rotation)
        {
            var center = position + rotation * localBounds.center;
            var half = localBounds.extents - new Vector3(0.05f, 0.05f, 0.05f);
            // Lift slightly so the ground under it doesn't count.
            center += Vector3.up * 0.1f;
            foreach (var c in Physics.OverlapBox(center, half, rotation, ~0, QueryTriggerInteraction.Ignore))
            {
                if (c is TerrainCollider) continue;
                return true;
            }
            return false;
        }

        private void Place()
        {
            LastPlacedFrame = Time.frameCount;
            if (moving != null)
            {
                var target = moving;
                moving = null;
                target.transform.SetPositionAndRotation(ghost.transform.position, ghost.transform.rotation);
                target.SetActive(true);
                Cancel();
                Placed?.Invoke(target);
                return;
            }

            var placed = Instantiate(item.PlaceablePrefab, ghost.transform.position, ghost.transform.rotation, placedParent);
            placed.name = item.PlaceablePrefab.name;
            var marker = placed.GetComponent<PlacedStructure>();
            if (marker == null) marker = placed.AddComponent<PlacedStructure>();
            marker.Setup(item);
            inventory.Remove(item, 1);
            Placed?.Invoke(placed);
            var placedItem = item;
            Cancel();
            // Keep placing if there are more of the same.
            if (inventory.Count(placedItem) > 0) Begin(placedItem);
        }
    }
}
