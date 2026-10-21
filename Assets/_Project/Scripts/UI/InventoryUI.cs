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
    /// (numbered 1-6); stacks can be dragged between slots to arrange it. Hovering a stack shows a tooltip by
    /// the cursor; right clicking one opens a small menu (consume, split, drop).
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
        [Tooltip("Optional. Consuming from the right-click menu.")]
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
        private RectTransform tooltip;
        private Text tooltipText;
        private LayoutElement tooltipWidth;
        private Text tooltipCount, tooltipValue;
        private LayoutElement tooltipFooter;
        private const float TooltipMaxWidth = 420f;
        // The right-click menu: a see-through blocker over the screen (a click on it closes the menu) holding the list.
        private GameObject itemMenu;
        private Vector2 itemMenuAt;

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
            // Esc shuts the item menu first, not the whole inventory.
            if (itemMenu != null && closePressed)
            {
                CloseItemMenu();
                return;
            }
            // Don't open over another menu (e.g. trading) that has already taken control.
            bool canOpen = controller == null || controller.InputEnabled;
            // Typing an "i" into a text box in the menu shouldn't shut it.
            if (toggle.WasPressedThisFrame() && (IsOpen || canOpen) && !TextEntry.Active) SetOpen(!IsOpen);
            else if (IsOpen && closePressed) SetOpen(false);
            if (IsOpen) UpdateTooltip();
        }

        public void SetOpen(bool open)
        {
            panel.SetActive(open);
            if (controller != null) controller.InputEnabled = !open;
            Cursor.lockState = open ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = open;
            hovered = -1;
            EndDrag();
            CloseItemMenu();
            if (tooltip != null) tooltip.gameObject.SetActive(false);
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
                AddTrigger(trigger, UnityEngine.EventSystems.EventTriggerType.PointerClick, data =>
                {
                    if (IsRight(data)) OpenItemMenu(index);
                    else if (!IsMiddle(data)) Use(index);
                });
                AddTrigger(trigger, UnityEngine.EventSystems.EventTriggerType.BeginDrag, data => { if (!IsRight(data) && !IsMiddle(data)) BeginDrag(index); });
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
            UnityEngine.EventSystems.EventTriggerType type, System.Action action) => AddTrigger(trigger, type, _ => action());

        private static void AddTrigger(UnityEngine.EventSystems.EventTrigger trigger,
            UnityEngine.EventSystems.EventTriggerType type, System.Action<UnityEngine.EventSystems.BaseEventData> action)
        {
            var entry = new UnityEngine.EventSystems.EventTrigger.Entry { eventID = type };
            entry.callback.AddListener(data => action(data));
            trigger.triggers.Add(entry);
        }

        private static bool IsRight(UnityEngine.EventSystems.BaseEventData data) =>
            data is UnityEngine.EventSystems.PointerEventData p && p.button == UnityEngine.EventSystems.PointerEventData.InputButton.Right;

        private static bool IsMiddle(UnityEngine.EventSystems.BaseEventData data) =>
            data is UnityEngine.EventSystems.PointerEventData p && p.button == UnityEngine.EventSystems.PointerEventData.InputButton.Middle;

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
            // Consumables are eaten from the right-click menu (or the hotbar), not on a click.
        }

        private void Refresh()
        {
            if (!IsOpen) return;
            // The stack it was opened on may have moved or gone.
            CloseItemMenu();
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
            SetTooltip(inventory.GetSlot(hovered));
            var stack = inventory.GetSlot(hovered);
            var item = stack.item;
            string quality = item.HasQuality ? $"  ·  {stack.quality}" : "";
            detailsLabel.text =
                $"<b>{item.DisplayName}</b>  ·  {item.Category}{quality}\n" +
                $"Value: {item.ValueAt(stack.quality)} each  ·  {stack.quantity * item.ValueAt(stack.quality)} total" +
                (vitals != null && item.IsConsumable ? "\n<color=#B0A890>Right click to consume</color>" : "") +
                (item.IsPlaceable ? $"\nDraws {Properties.Property.DrawText(item.UtilityDraw)}" : "") +
                (placement != null && item.IsPlaceable ? "\n<color=#B0A890>Click to place</color>" : "") +
                (combat != null && item.IsWeapon ? WeaponDetails(item, stack.quality) : "") +
                (hotbar != null && hovered >= Hotbar.Size ? "\n<color=#8a857c>Drag it into the top row to put it on the hotbar (1-6)</color>" : "");
        }

        // ---------------------------------------------------------------- tooltip

        private void SetTooltip(ItemStack stack)
        {
            if (tooltip == null) BuildTooltip();
            var item = stack.item;
            string colour = ColorUtility.ToHtmlStringRGB(stack.quality.DisplayColor());
            string quality = item.HasQuality ? $"  ·  <color=#{colour}>{stack.quality}</color>" : "";
            string description = string.IsNullOrEmpty(item.Description) ? "" : $"\n<color=#CFC8B8>{item.Description}</color>";
            tooltipText.text = $"<b><size=27>{item.DisplayName}</size></b>\n" +
                               $"<color=#B0A890>{item.Category}</color>{quality}" + description;
            // Bottom line: how many on the left, what one is worth in the corner.
            tooltipCount.text = stack.quantity > 1 ? $"x{stack.quantity}" : "";
            tooltipValue.text = $"{item.ValueAt(stack.quality)} Scrip";
            tooltipFooter.preferredWidth = tooltipCount.preferredWidth + tooltipValue.preferredWidth + 24f;
            tooltipFooter.preferredHeight = tooltipValue.preferredHeight;
            // Short tooltips hug their text; a long description wraps rather than stretching across the screen.
            tooltipWidth.preferredWidth = -1f;
            tooltipWidth.preferredWidth = Mathf.Min(tooltipText.preferredWidth, TooltipMaxWidth);
        }

        /// <summary>Follows the cursor over a filled slot; kept on screen, flipping to the other side near an edge.</summary>
        private void UpdateTooltip()
        {
            if (tooltip == null) return;
            bool show = hovered >= 0 && dragFrom < 0 && itemMenu == null && !inventory.GetSlot(hovered).IsEmpty && Mouse.current != null;
            if (tooltip.gameObject.activeSelf != show) tooltip.gameObject.SetActive(show);
            if (!show) return;
            var canvas = (RectTransform)tooltip.parent;
            var cam = panel.GetComponentInParent<Canvas>().renderMode == RenderMode.ScreenSpaceOverlay ? null : Camera.main;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(canvas, Mouse.current.position.ReadValue(), cam, out var local)) return;
            var size = tooltip.rect.size;
            var bounds = canvas.rect;
            bool flipX = local.x + 24f + size.x > bounds.xMax;
            bool flipY = local.y - 24f - size.y < bounds.yMin;
            tooltip.pivot = new Vector2(flipX ? 1f : 0f, flipY ? 0f : 1f);
            tooltip.localPosition = local + new Vector2(flipX ? -16f : 24f, flipY ? 16f : -24f);
            tooltip.SetAsLastSibling();
        }

        private void BuildTooltip()
        {
            var go = new GameObject("ItemTooltip", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup),
                typeof(ContentSizeFitter), typeof(CanvasGroup));
            tooltip = (RectTransform)go.transform;
            tooltip.SetParent(panel.GetComponentInParent<Canvas>().transform, false);
            // Never in the way of the slots under it, or hovering would flicker.
            go.GetComponent<CanvasGroup>().blocksRaycasts = false;
            var background = go.GetComponent<Image>();
            background.color = new Color(0.06f, 0.06f, 0.07f, 0.94f);
            background.raycastTarget = false;
            var layout = go.GetComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(18, 18, 10, 12);
            layout.spacing = 2f;
            layout.childControlWidth = layout.childControlHeight = true;
            // Full width, so the value sits in the right-hand corner however wide the text above is.
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            var fitter = go.GetComponent<ContentSizeFitter>();
            fitter.horizontalFit = fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            AddEdge(tooltip);

            tooltipText = NewText(tooltip, "Text", 22, TextAnchor.UpperLeft);
            tooltipText.horizontalOverflow = HorizontalWrapMode.Wrap;
            tooltipWidth = tooltipText.gameObject.AddComponent<LayoutElement>();

            var footer = new GameObject("Footer", typeof(RectTransform), typeof(LayoutElement));
            footer.transform.SetParent(tooltip, false);
            tooltipFooter = footer.GetComponent<LayoutElement>();
            tooltipCount = NewText((RectTransform)footer.transform, "Count", 22, TextAnchor.LowerLeft);
            tooltipValue = NewText((RectTransform)footer.transform, "Value", 22, TextAnchor.LowerRight);
            foreach (var t in new[] { tooltipCount, tooltipValue })
            {
                var rect = t.rectTransform;
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = rect.offsetMax = Vector2.zero;
            }
            go.SetActive(false);
        }

        // ---------------------------------------------------------------- right-click menu

        private void OpenItemMenu(int index)
        {
            CloseItemMenu();
            var stack = inventory.GetSlot(index);
            if (stack.IsEmpty || Mouse.current == null) return;
            EndDrag();

            var canvas = (RectTransform)panel.GetComponentInParent<Canvas>().transform;
            itemMenu = new GameObject("ItemMenu", typeof(RectTransform), typeof(Image));
            var blocker = (RectTransform)itemMenu.transform;
            blocker.SetParent(canvas, false);
            blocker.anchorMin = Vector2.zero;
            blocker.anchorMax = Vector2.one;
            blocker.offsetMin = blocker.offsetMax = Vector2.zero;
            // Clear, but catches clicks: anywhere off the list closes it.
            itemMenu.GetComponent<Image>().color = Color.clear;
            AddTrigger(itemMenu.AddComponent<UnityEngine.EventSystems.EventTrigger>(),
                UnityEngine.EventSystems.EventTriggerType.PointerClick, CloseItemMenu);

            var box = new GameObject("List", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            var list = (RectTransform)box.transform;
            list.SetParent(blocker, false);
            list.anchorMin = list.anchorMax = new Vector2(0.5f, 0.5f);
            list.pivot = new Vector2(0f, 1f);
            box.GetComponent<Image>().color = new Color(0.06f, 0.06f, 0.07f, 0.97f);
            var layout = box.GetComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(10, 8, 8, 8);
            layout.spacing = 4f;
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            var fitter = box.GetComponent<ContentSizeFitter>();
            fitter.horizontalFit = fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            // Takes clicks on the list itself, so the padding and title don't count as clicking off it.
            box.AddComponent<UnityEngine.EventSystems.EventTrigger>();
            AddEdge(list);

            var title = NewText(list, "Title", 20, TextAnchor.MiddleLeft);
            title.text = $"<b>{stack.item.DisplayName}</b>" + (stack.quantity > 1 ? $"  x{stack.quantity}" : "");
            title.color = stack.quality.DisplayColor();
            var titleLayout = title.gameObject.AddComponent<LayoutElement>();
            titleLayout.minHeight = 34f;

            var item = stack.item;
            var quality = stack.quality;
            if (vitals != null && item.IsConsumable)
                AddMenuOption(list, "Consume", true, () =>
                {
                    if (SameStack(index, item, quality))
                    {
                        vitals.Consume(item);
                        inventory.RemoveAt(index, 1);
                    }
                });
            int count = stack.quantity;
            AddMenuOption(list, "Split", count > 1 && inventory.HasEmptySlot(), () =>
                PickAmount(list, $"Split {item.DisplayName}", 1, count - 1, count / 2, n => $"Split off {n}", n =>
                {
                    if (SameStack(index, item, quality)) inventory.Split(index, n);
                }), close: false);
            if (count > 1)
                AddMenuOption(list, "Drop", true, () =>
                    PickAmount(list, $"Drop {item.DisplayName}", 1, count, count, n => $"Drop {n}", n => Drop(index, item, quality, n)),
                    close: false);
            else
                AddMenuOption(list, "Drop", true, () => Drop(index, item, quality, 1));

            // At the cursor, kept on screen.
            var cam = panel.GetComponentInParent<Canvas>().renderMode == RenderMode.ScreenSpaceOverlay ? null : Camera.main;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(blocker, Mouse.current.position.ReadValue(), cam, out itemMenuAt);
            PlaceItemMenu(list);
            if (tooltip != null) tooltip.gameObject.SetActive(false);
        }

        private void PlaceItemMenu(RectTransform list)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(list);
            var size = list.rect.size;
            var bounds = ((RectTransform)itemMenu.transform).rect;
            var at = itemMenuAt;
            at.x = Mathf.Min(at.x, bounds.xMax - size.x - 8f);
            at.y = Mathf.Max(at.y, bounds.yMin + size.y + 8f);
            list.anchoredPosition = at;
        }

        /// <summary>Swaps the menu's options for a slider picking how many of the stack, and a button to do it.</summary>
        private void PickAmount(RectTransform list, string heading, int min, int max, int start,
            System.Func<int, string> confirmLabel, System.Action<int> confirm)
        {
            foreach (Transform child in list)
            {
                if (child.name == "Accent") continue;
                child.gameObject.SetActive(false);
                Destroy(child.gameObject);
            }

            var title = NewText(list, "Title", 20, TextAnchor.MiddleLeft);
            title.text = $"<b>{heading}</b>";
            title.gameObject.AddComponent<LayoutElement>().minHeight = 34f;

            var slider = NewSlider(list, min, max, start);
            Text button = null;
            button = AddMenuOption(list, confirmLabel(start), true, () => confirm(Mathf.RoundToInt(slider.value)));
            slider.onValueChanged.AddListener(v => button.text = confirmLabel(Mathf.RoundToInt(v)));
            AddMenuOption(list, "Cancel", true, () => { });
            PlaceItemMenu(list);
        }

        private Slider NewSlider(RectTransform parent, int min, int max, int value)
        {
            var go = new GameObject("Amount", typeof(RectTransform), typeof(Slider), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            var element = go.GetComponent<LayoutElement>();
            element.minHeight = 40f;
            element.minWidth = 250f;

            var track = NewRect((RectTransform)go.transform, "Track", new Color(0.2f, 0.19f, 0.18f, 0.95f));
            track.anchorMin = new Vector2(0f, 0.5f);
            track.anchorMax = new Vector2(1f, 0.5f);
            track.sizeDelta = new Vector2(-16f, 8f);
            // The slider only drives the x anchors, so these span the height themselves.
            var fill = NewRect(track, "Fill", new Color(1f, 0.66f, 0.3f));
            fill.anchorMin = Vector2.zero;
            fill.anchorMax = Vector2.one;
            fill.sizeDelta = Vector2.zero;

            var area = new GameObject("HandleArea", typeof(RectTransform)).GetComponent<RectTransform>();
            area.SetParent(go.transform, false);
            area.anchorMin = Vector2.zero;
            area.anchorMax = Vector2.one;
            area.offsetMin = new Vector2(8f, 6f);
            area.offsetMax = new Vector2(-8f, -6f);
            var handle = NewRect(area, "Handle", new Color(0.94f, 0.91f, 0.85f));
            handle.anchorMin = Vector2.zero;
            handle.anchorMax = new Vector2(0f, 1f);
            handle.sizeDelta = new Vector2(14f, 0f);

            var slider = go.GetComponent<Slider>();
            slider.fillRect = fill;
            slider.handleRect = handle;
            slider.targetGraphic = handle.GetComponent<Image>();
            slider.direction = Slider.Direction.LeftToRight;
            slider.wholeNumbers = true;
            slider.minValue = min;
            slider.maxValue = max;
            slider.value = value;
            var colours = slider.colors;
            colours.highlightedColor = new Color(1.9f, 1.45f, 0.8f);
            slider.colors = colours;
            return slider;
        }

        private static RectTransform NewRect(RectTransform parent, string name, Color colour)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            go.GetComponent<Image>().color = colour;
            return rt;
        }

        private bool SameStack(int index, ItemDefinition item, ItemQuality quality)
        {
            var now = inventory.GetSlot(index);
            return !now.IsEmpty && now.item == item && now.quality == quality;
        }

        /// <summary>Some of the stack goes on the ground at the player's feet, a little in front.</summary>
        private void Drop(int index, ItemDefinition item, ItemQuality quality, int amount)
        {
            if (!SameStack(index, item, quality)) return;
            amount = Mathf.Min(amount, inventory.GetSlot(index).quantity);
            if (amount < 1) return;
            var player = inventory.transform;
            var forward = Vector3.ProjectOnPlane(player.forward, Vector3.up).normalized;
            var at = player.position + forward * 0.9f + Vector3.up * 0.4f;
            if (Interaction.WorldItem.Drop(item, amount, quality, at, Random.Range(0f, 360f)) == null) return;
            inventory.RemoveAt(index, amount);
            Audio.AudioManager.Play2D(Audio.AudioManager.Library?.click, 0.4f);
        }

        private Text AddMenuOption(RectTransform list, string label, bool enabled, System.Action action, bool close = true)
        {
            var go = new GameObject(label, typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
            go.transform.SetParent(list, false);
            var element = go.GetComponent<LayoutElement>();
            element.minHeight = 46f;
            element.minWidth = 250f;
            var image = go.GetComponent<Image>();
            image.color = new Color(0.2f, 0.19f, 0.18f, 0.95f);
            var button = go.GetComponent<Button>();
            button.targetGraphic = image;
            var colours = button.colors;
            colours.highlightedColor = new Color(1.9f, 1.45f, 0.8f);
            colours.disabledColor = new Color(0.6f, 0.6f, 0.6f, 0.5f);
            button.colors = colours;
            button.interactable = enabled;
            button.onClick.AddListener(() =>
            {
                if (close) CloseItemMenu();
                action();
            });
            var text = NewText((RectTransform)go.transform, "Label", 22, TextAnchor.MiddleLeft);
            text.text = label;
            text.color = enabled ? new Color(0.94f, 0.91f, 0.85f) : new Color(0.55f, 0.53f, 0.5f);
            var rt = text.rectTransform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(12f, 0f);
            rt.offsetMax = new Vector2(-8f, 0f);
            return text;
        }

        private void CloseItemMenu()
        {
            if (itemMenu != null) Destroy(itemMenu);
            itemMenu = null;
        }

        /// <summary>The orange edge the HUD cards have.</summary>
        private static void AddEdge(RectTransform card)
        {
            var go = new GameObject("Accent", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
            var rt = (RectTransform)go.transform;
            rt.SetParent(card, false);
            go.GetComponent<LayoutElement>().ignoreLayout = true;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 0.5f);
            rt.sizeDelta = new Vector2(4f, 0f);
            var image = go.GetComponent<Image>();
            image.color = new Color(1f, 0.66f, 0.3f);
            image.raycastTarget = false;
        }

        private Text NewText(RectTransform parent, string name, int size, TextAnchor anchor)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<Text>();
            t.font = detailsLabel != null ? detailsLabel.font : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.fontSize = size;
            t.alignment = anchor;
            t.color = new Color(0.94f, 0.91f, 0.85f);
            t.supportRichText = true;
            t.raycastTarget = false;
            return t;
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
