using System.Collections.Generic;
using ThePitt.Items;
using ThePitt.Player;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace ThePitt.UI
{
    /// <summary>
    /// Toggleable grid view of an inventory. Pauses player control while open. The top row is the hotbar
    /// (numbered 1-6); stacks can be dragged between slots to arrange it.
    /// </summary>
    public class InventoryUI : MonoBehaviour
    {
        [SerializeField] private InputActionAsset actions;
        [SerializeField] private Inventory inventory;
        [SerializeField] private FirstPersonController controller;
        [SerializeField] private GameObject panel;
        [SerializeField] private RectTransform grid;
        [Tooltip("Inactive slot object with an Image background and a child Text.")]
        [SerializeField] private GameObject slotTemplate;
        [SerializeField] private Text detailsLabel;
        [Tooltip("Optional. Clicking an edible item consumes one.")]
        [SerializeField] private PlayerVitals vitals;
        [Tooltip("Optional. Clicking a placeable item (a station) starts placing it.")]
        [SerializeField] private PlacementController placement;
        [Tooltip("Optional. Clicking a weapon takes it in hand (or puts it away).")]
        [SerializeField] private PlayerCombat combat;

        private InputAction toggle;
        private readonly List<(Image background, Text text, Image icon)> slotViews = new();
        private int hovered = -1;
        private Hotbar hotbar;
        private int dragFrom = -1;
        private Image dragGhost;

        public bool IsOpen => panel != null && panel.activeSelf;
        /// <summary>Fired when the inventory opens (true) or closes (false).</summary>
        public event System.Action<bool> OpenChanged;

        private void Awake()
        {
            toggle = actions.FindActionMap("Player", true).FindAction("Inventory", true);
            if (combat == null && inventory != null) combat = inventory.GetComponent<PlayerCombat>();
            if (inventory != null) hotbar = inventory.GetComponent<Hotbar>();
            BuildSlots();
            panel.SetActive(false);
        }

        private void OnEnable()
        {
            inventory.Changed += Refresh;
            if (hotbar != null) hotbar.Changed += Refresh;
        }

        private void OnDisable()
        {
            inventory.Changed -= Refresh;
            if (hotbar != null) hotbar.Changed -= Refresh;
        }

        private void Update()
        {
            bool closePressed = Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame;
            // Don't open over another menu (e.g. trading) that has already taken control.
            bool canOpen = controller == null || controller.InputEnabled;
            if (toggle.WasPressedThisFrame() && (IsOpen || canOpen)) SetOpen(!IsOpen);
            else if (IsOpen && closePressed) SetOpen(false);
        }

        public void SetOpen(bool open)
        {
            panel.SetActive(open);
            if (controller != null) controller.InputEnabled = !open;
            Cursor.lockState = open ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = open;
            hovered = -1;
            EndDrag();
            if (open) Refresh();
            OpenChanged?.Invoke(open);
        }

        private void BuildSlots()
        {
            slotTemplate.SetActive(false);
            for (int i = 0; i < inventory.SlotCount; i++)
            {
                var go = Instantiate(slotTemplate, grid);
                go.name = $"Slot_{i}";
                go.SetActive(true);

                // Icon fills the slot; the label drops to a small count in the corner when there is one.
                var iconGo = new GameObject("Icon", typeof(RectTransform));
                iconGo.transform.SetParent(go.transform, false);
                iconGo.transform.SetAsFirstSibling();
                var iconRect = (RectTransform)iconGo.transform;
                iconRect.anchorMin = Vector2.zero;
                iconRect.anchorMax = Vector2.one;
                iconRect.offsetMin = new Vector2(12f, 12f);
                iconRect.offsetMax = new Vector2(-12f, -12f);
                var icon = iconGo.AddComponent<Image>();
                icon.preserveAspect = true;
                icon.raycastTarget = false;
                icon.enabled = false;
                slotViews.Add((go.GetComponent<Image>(), go.GetComponentInChildren<Text>(), icon));
                if (i < Hotbar.Size) AddHotkeyNumber(go.transform, i);

                int index = i;
                var trigger = go.AddComponent<UnityEngine.EventSystems.EventTrigger>();
                AddTrigger(trigger, UnityEngine.EventSystems.EventTriggerType.PointerEnter, () => { hovered = index; RefreshDetails(); });
                AddTrigger(trigger, UnityEngine.EventSystems.EventTriggerType.PointerExit, () => { if (hovered == index) hovered = -1; RefreshDetails(); });
                AddTrigger(trigger, UnityEngine.EventSystems.EventTriggerType.PointerClick, () => Use(index));
                AddTrigger(trigger, UnityEngine.EventSystems.EventTriggerType.BeginDrag, () => BeginDrag(index));
                AddTrigger(trigger, UnityEngine.EventSystems.EventTriggerType.Drag, MoveGhost);
                AddTrigger(trigger, UnityEngine.EventSystems.EventTriggerType.Drop, () => DropOn(index));
                AddTrigger(trigger, UnityEngine.EventSystems.EventTriggerType.EndDrag, EndDrag);
            }
        }

        /// <summary>The hotkey in the corner of a hotbar slot.</summary>
        private void AddHotkeyNumber(Transform slot, int index)
        {
            var go = new GameObject("Hotkey", typeof(RectTransform), typeof(Text));
            var rt = (RectTransform)go.transform;
            rt.SetParent(slot, false);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(10f, -6f);
            rt.sizeDelta = new Vector2(40f, 30f);
            var t = go.GetComponent<Text>();
            t.font = detailsLabel != null ? detailsLabel.font : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.fontSize = 22;
            t.fontStyle = FontStyle.Bold;
            t.color = new Color(1f, 0.66f, 0.3f, 0.85f);
            t.raycastTarget = false;
            t.text = (index + 1).ToString();
        }

        // ---------------------------------------------------------------- dragging

        private void BeginDrag(int index)
        {
            var stack = inventory.GetSlot(index);
            if (stack.IsEmpty) return;
            dragFrom = index;
            if (dragGhost == null)
            {
                var go = new GameObject("DragGhost", typeof(RectTransform), typeof(Image), typeof(CanvasGroup));
                go.transform.SetParent(panel.GetComponentInParent<Canvas>().transform, false);
                go.GetComponent<CanvasGroup>().blocksRaycasts = false;
                dragGhost = go.GetComponent<Image>();
                dragGhost.raycastTarget = false;
                dragGhost.preserveAspect = true;
                ((RectTransform)go.transform).sizeDelta = new Vector2(80f, 80f);
            }
            dragGhost.sprite = stack.item.Icon;
            dragGhost.color = stack.item.Icon != null ? new Color(1f, 1f, 1f, 0.85f) : new Color(0.4f, 0.35f, 0.3f, 0.85f);
            dragGhost.gameObject.SetActive(true);
            dragGhost.transform.SetAsLastSibling();
            MoveGhost();
        }

        private void MoveGhost()
        {
            if (dragGhost == null || dragFrom < 0 || Mouse.current == null) return;
            var canvas = (RectTransform)dragGhost.transform.parent;
            var cam = panel.GetComponentInParent<Canvas>().renderMode == RenderMode.ScreenSpaceOverlay ? null : Camera.main;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(canvas, Mouse.current.position.ReadValue(), cam, out var local))
                ((RectTransform)dragGhost.transform).localPosition = local;
        }

        private void DropOn(int index)
        {
            if (dragFrom < 0) return;
            int from = dragFrom;
            EndDrag();
            inventory.Move(from, index);
        }

        private void EndDrag()
        {
            dragFrom = -1;
            if (dragGhost != null) dragGhost.gameObject.SetActive(false);
        }

        private static void AddTrigger(UnityEngine.EventSystems.EventTrigger trigger,
            UnityEngine.EventSystems.EventTriggerType type, System.Action action)
        {
            var entry = new UnityEngine.EventSystems.EventTrigger.Entry { eventID = type };
            entry.callback.AddListener(_ => action());
            trigger.triggers.Add(entry);
        }

        private void Use(int index)
        {
            var stack = inventory.GetSlot(index);
            if (stack.IsEmpty) return;
            if (stack.item.IsPlaceable && placement != null)
            {
                SetOpen(false);
                placement.Begin(stack.item);
                return;
            }
            if (stack.item.IsWeapon && combat != null)
            {
                // On the hotbar: the same as pressing its key. Elsewhere: straight into the hand.
                if (hotbar != null && index < Hotbar.Size) hotbar.Press(index);
                else
                {
                    if (hotbar != null) hotbar.Clear();
                    combat.Equip(stack.item, stack.quality);
                }
                Refresh();
                return;
            }
            if (vitals == null || !stack.item.IsConsumable) return;
            vitals.Consume(stack.item);
            inventory.RemoveAt(index, 1);
        }

        private void Refresh()
        {
            if (!IsOpen) return;
            for (int i = 0; i < slotViews.Count; i++)
            {
                var stack = inventory.GetSlot(i);
                var (background, text, icon) = slotViews[i];
                bool onHotbar = i < Hotbar.Size;
                bool selected = hotbar != null && hotbar.Selected == i;
                if (stack.IsEmpty)
                {
                    text.text = "";
                    icon.enabled = false;
                    background.color = selected ? new Color(0.3f, 0.22f, 0.12f, 0.9f)
                        : onHotbar ? new Color(0.16f, 0.15f, 0.13f, 0.9f) : new Color(0.12f, 0.12f, 0.12f, 0.85f);
                    continue;
                }
                icon.enabled = stack.item.Icon != null;
                icon.sprite = stack.item.Icon;
                if (icon.enabled)
                {
                    // Quality shows as the count's colour; the name is in the hover details.
                    text.alignment = TextAnchor.LowerRight;
                    text.text = stack.quantity > 1 ? $"<b>x{stack.quantity}</b>" : "";
                }
                else
                {
                    text.alignment = TextAnchor.MiddleCenter;
                    text.text = $"{stack.item.DisplayName}\n<size=20>x{stack.quantity}</size>";
                }
                text.color = stack.quality.DisplayColor();
                bool inHand = combat != null && combat.Equipped == stack.item && combat.EquippedQuality == stack.quality;
                background.color = inHand || selected ? new Color(0.42f, 0.3f, 0.14f, 0.95f)
                    : onHotbar ? new Color(0.24f, 0.22f, 0.2f, 0.95f) : new Color(0.2f, 0.19f, 0.18f, 0.95f);
            }
            RefreshDetails();
        }

        private void RefreshDetails()
        {
            if (detailsLabel == null) return;
            if (hovered < 0 || inventory.GetSlot(hovered).IsEmpty)
            {
                detailsLabel.text = "";
                return;
            }
            var stack = inventory.GetSlot(hovered);
            var item = stack.item;
            string quality = item.HasQuality ? $"  ·  {stack.quality}" : "";
            detailsLabel.text =
                $"<b>{item.DisplayName}</b>  ·  {item.Category}{quality}\n" +
                $"Value: {item.ValueAt(stack.quality)} each  ·  {stack.quantity * item.ValueAt(stack.quality)} total\n" +
                item.Description +
                (vitals != null && item.IsConsumable ? "\n<color=#B0A890>Click to consume</color>" : "") +
                (item.IsPlaceable ? $"\nDraws {Properties.Property.DrawText(item.UtilityDraw)}" : "") +
                (placement != null && item.IsPlaceable ? "\n<color=#B0A890>Click to place</color>" : "") +
                (combat != null && item.IsWeapon ? WeaponDetails(item, stack.quality) : "") +
                (hotbar != null && hovered >= Hotbar.Size ? "\n<color=#8a857c>Drag it into the top row to put it on the hotbar (1-6)</color>" : "");
        }

        private string WeaponDetails(ItemDefinition item, ItemQuality quality)
        {
            bool inHand = combat.Equipped == item && combat.EquippedQuality == quality;
            string stats = $"\nDamage {item.DamageAt(quality):0}  ·  Range {item.Range:0.#} m";
            if (item.WeaponType == WeaponType.Firearm && item.Ammo != null) stats += $"  ·  Loads {item.Ammo.DisplayName}";
            return stats + (inHand ? "\n<color=#B0A890>In hand · click to put away</color>" : "\n<color=#B0A890>Click to equip</color>");
        }
    }
}
