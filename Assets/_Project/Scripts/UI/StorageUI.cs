using System.Collections.Generic;
using ThePitt.Audio;
using ThePitt.Interaction;
using ThePitt.Items;
using ThePitt.Player;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace ThePitt.UI
{
    /// <summary>
    /// A <see cref="StorageBox"/>'s slots above the player's inventory. Clicking a stack moves as much of it
    /// as fits to the other side. Built at runtime on its own canvas.
    /// </summary>
    public class StorageUI : MonoBehaviour
    {
        public static StorageUI Instance { get; private set; }

        private const int Columns = 6;
        private const float Cell = 92f, Spacing = 8f;

        private static readonly Color Accent = new(0.95f, 0.72f, 0.3f);
        private static readonly Color Empty = new(0.12f, 0.12f, 0.12f, 0.85f);
        private static readonly Color Filled = new(0.2f, 0.19f, 0.18f, 0.95f);
        private static readonly Color Hover = new(0.36f, 0.27f, 0.14f, 0.95f);

        private GameObject panel;
        private RectTransform root;
        private Text header, boxCaption, details;
        private Font font;
        private readonly List<(Image background, Image icon, Text count)> boxViews = new();
        private readonly List<(Image background, Image icon, Text count)> playerViews = new();
        private RectTransform boxGrid, playerGrid;

        private StorageBox box;
        private Inventory player;
        private FirstPersonController controller;
        private InputAction interact;
        private int openedFrame;
        private Inventory hoveredSide;
        private int hoveredIndex = -1;

        public bool IsOpen => panel != null && panel.activeSelf;

        private void Awake()
        {
            Instance = this;
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            Build();
            panel.SetActive(false);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        public void Open(StorageBox target, PlayerInteractor interactor)
        {
            box = target;
            player = interactor.Inventory;
            controller = interactor.GetComponent<FirstPersonController>();
            interact = interactor.InteractAction;
            openedFrame = Time.frameCount;
            hoveredSide = null;
            hoveredIndex = -1;

            Fill(boxGrid, boxViews, box.Contents.SlotCount, box.Contents);
            Fill(playerGrid, playerViews, player.SlotCount, player);
            Resize();
            box.Contents.Changed += Refresh;
            player.Changed += Refresh;

            panel.SetActive(true);
            if (controller != null) controller.InputEnabled = false;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            AudioManager.Play2D(AudioManager.Library?.click, 0.4f);
            Refresh();
        }

        public void Close()
        {
            if (!IsOpen) return;
            if (box != null) box.Contents.Changed -= Refresh;
            if (player != null) player.Changed -= Refresh;
            panel.SetActive(false);
            box = null;
            if (controller != null) controller.InputEnabled = true;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        private void Update()
        {
            if (!IsOpen) return;
            // Picked up or destroyed while open.
            if (box == null)
            {
                Close();
                return;
            }
            bool escape = Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame;
            bool again = interact != null && interact.WasPressedThisFrame() && Time.frameCount != openedFrame;
            if (escape || again) Close();
        }

        /// <summary>Moves as much of one stack as fits into the other inventory.</summary>
        private void Transfer(Inventory from, int index)
        {
            if (box == null) return;
            var to = from == player ? box.Contents : player;
            var stack = from.GetSlot(index);
            if (stack.IsEmpty) return;
            int moved = to.Add(stack.item, stack.quantity, stack.quality);
            if (moved > 0)
            {
                from.RemoveAt(index, moved);
                AudioManager.Play2D(AudioManager.Library?.click, 0.4f);
            }
            else details.text = to == player ? "<color=#E06048>No room in your inventory.</color>" : $"<color=#E06048>The {box.BoxName.ToLowerInvariant()} is full.</color>";
        }

        // ---------------------------------------------------------------- refresh

        private void Refresh()
        {
            if (box == null) return;
            header.text = $"<b>{box.BoxName.ToUpperInvariant()}</b>";
            Draw(boxViews, box.Contents);
            Draw(playerViews, player);
            RefreshDetails();
        }

        private void Draw(List<(Image background, Image icon, Text count)> views, Inventory inventory)
        {
            for (int i = 0; i < views.Count && i < inventory.SlotCount; i++)
            {
                var (background, icon, count) = views[i];
                var stack = inventory.GetSlot(i);
                bool hover = hoveredSide == inventory && hoveredIndex == i;
                if (stack.IsEmpty)
                {
                    background.color = Empty;
                    icon.enabled = false;
                    count.text = "";
                    continue;
                }
                background.color = hover ? Hover : Filled;
                icon.enabled = stack.item.Icon != null;
                icon.sprite = stack.item.Icon;
                if (icon.enabled)
                {
                    count.alignment = TextAnchor.LowerRight;
                    count.text = stack.quantity > 1 ? $"<b>x{stack.quantity}</b>" : "";
                }
                else
                {
                    count.alignment = TextAnchor.MiddleCenter;
                    count.text = $"{stack.item.DisplayName}\n<size=16>x{stack.quantity}</size>";
                }
                count.color = stack.quality.DisplayColor();
            }
        }

        private void RefreshDetails()
        {
            if (hoveredSide == null || hoveredIndex < 0 || hoveredIndex >= hoveredSide.SlotCount || hoveredSide.GetSlot(hoveredIndex).IsEmpty)
            {
                details.text = "";
                return;
            }
            var stack = hoveredSide.GetSlot(hoveredIndex);
            string quality = stack.item.HasQuality ? $"  ·  <color=#{ColorUtility.ToHtmlStringRGB(stack.quality.DisplayColor())}>{stack.quality}</color>" : "";
            string where = hoveredSide == player ? "into the box" : "into your inventory";
            details.text = $"<b>{stack.item.DisplayName}</b> ×{stack.quantity}{quality}\n<color=#B0A890>Click to move it {where}</color>";
        }

        private void SetHover(Inventory side, int index)
        {
            hoveredSide = side;
            hoveredIndex = index;
            Refresh();
        }

        // ---------------------------------------------------------------- building

        private void Build()
        {
            var canvasGo = new GameObject("StoragePanel", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 70;
            UIScale.Apply(canvasGo.GetComponent<CanvasScaler>());

            root = NewRect("Panel", canvasGo.transform);
            root.anchorMin = root.anchorMax = new Vector2(0.5f, 0.5f);
            root.gameObject.AddComponent<Image>().color = new Color(0.08f, 0.075f, 0.07f, 0.96f);
            panel = root.gameObject;
            var edge = NewRect("Accent", root);
            edge.anchorMin = Vector2.zero;
            edge.anchorMax = new Vector2(0f, 1f);
            edge.pivot = new Vector2(0.5f, 0.5f);
            edge.sizeDelta = new Vector2(5f, 0f);
            edge.anchoredPosition = new Vector2(2.5f, 0f);
            edge.gameObject.AddComponent<Image>().color = Accent;

            header = NewText(root, "Header", 24, TextAnchor.MiddleLeft);
            boxCaption = NewText(root, "BoxCaption", 13, TextAnchor.MiddleLeft);
            boxCaption.color = Accent;
            boxCaption.fontStyle = FontStyle.Bold;
            boxCaption.text = "IN THE BOX";
            boxGrid = NewRect("BoxGrid", root);
            var playerCaption = NewText(root, "PlayerCaption", 13, TextAnchor.MiddleLeft);
            playerCaption.color = Accent;
            playerCaption.fontStyle = FontStyle.Bold;
            playerCaption.text = "YOUR INVENTORY";
            playerGrid = NewRect("PlayerGrid", root);
            details = NewText(root, "Details", 16, TextAnchor.UpperLeft);
            details.color = new Color(0.94f, 0.91f, 0.85f);
            details.horizontalOverflow = HorizontalWrapMode.Wrap;

            var close = NewRect("Close", root);
            close.anchorMin = close.anchorMax = new Vector2(0.5f, 0f);
            close.pivot = new Vector2(0.5f, 0f);
            close.anchoredPosition = new Vector2(0f, 14f);
            close.sizeDelta = new Vector2(200f, 34f);
            var image = close.gameObject.AddComponent<Image>();
            image.color = new Color(0.4f, 0.31f, 0.17f, 1f);
            var button = close.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            var colours = button.colors;
            colours.highlightedColor = new Color(1.3f, 1.3f, 1.3f);
            button.colors = colours;
            button.onClick.AddListener(Close);
            var label = NewText(close, "Label", 15, TextAnchor.MiddleCenter);
            label.text = "Close (Esc)";
            label.rectTransform.anchorMin = Vector2.zero;
            label.rectTransform.anchorMax = Vector2.one;
            label.rectTransform.offsetMin = label.rectTransform.offsetMax = Vector2.zero;
        }

        /// <summary>Lays the panel out top to bottom around however many slots each side has.</summary>
        private void Resize()
        {
            const float margin = 28f;
            float width = Columns * Cell + (Columns - 1) * Spacing;
            float y = -14f;
            Place(header.rectTransform, y, width, 34f); y -= 44f;
            Place(boxCaption.rectTransform, y, width, 20f); y -= 26f;
            float boxHeight = GridHeight(box.Contents.SlotCount);
            Place(boxGrid, y, width, boxHeight); y -= boxHeight + 22f;
            Place((RectTransform)root.Find("PlayerCaption"), y, width, 20f); y -= 26f;
            float playerHeight = GridHeight(player.SlotCount);
            Place(playerGrid, y, width, playerHeight); y -= playerHeight + 12f;
            Place(details.rectTransform, y, width, 48f); y -= 48f;
            root.sizeDelta = new Vector2(width + margin * 2f, -y + 60f);
        }

        private static float GridHeight(int slots)
        {
            int rows = Mathf.CeilToInt(slots / (float)Columns);
            return rows * Cell + Mathf.Max(0, rows - 1) * Spacing;
        }

        /// <summary>Makes sure there are exactly <paramref name="count"/> slot views, wired to this inventory.</summary>
        private void Fill(RectTransform grid, List<(Image background, Image icon, Text count)> views, int count, Inventory side)
        {
            foreach (Transform child in grid) Destroy(child.gameObject);
            views.Clear();
            for (int i = 0; i < count; i++)
            {
                var slot = NewRect($"Slot_{i}", grid);
                slot.anchorMin = slot.anchorMax = slot.pivot = new Vector2(0f, 1f);
                slot.sizeDelta = new Vector2(Cell, Cell);
                slot.anchoredPosition = new Vector2(i % Columns * (Cell + Spacing), -(i / Columns) * (Cell + Spacing));
                var background = slot.gameObject.AddComponent<Image>();

                var iconRt = NewRect("Icon", slot);
                iconRt.anchorMin = Vector2.zero;
                iconRt.anchorMax = Vector2.one;
                iconRt.offsetMin = new Vector2(12f, 12f);
                iconRt.offsetMax = new Vector2(-12f, -12f);
                var icon = iconRt.gameObject.AddComponent<Image>();
                icon.preserveAspect = true;
                icon.raycastTarget = false;

                var text = NewText(slot, "Count", 18, TextAnchor.LowerRight);
                text.rectTransform.anchorMin = Vector2.zero;
                text.rectTransform.anchorMax = Vector2.one;
                text.rectTransform.offsetMin = new Vector2(6f, 4f);
                text.rectTransform.offsetMax = new Vector2(-6f, -4f);
                text.horizontalOverflow = HorizontalWrapMode.Wrap;

                int index = i;
                var trigger = slot.gameObject.AddComponent<EventTrigger>();
                AddTrigger(trigger, EventTriggerType.PointerEnter, () => SetHover(side, index));
                AddTrigger(trigger, EventTriggerType.PointerExit, () => { if (hoveredSide == side && hoveredIndex == index) SetHover(null, -1); });
                AddTrigger(trigger, EventTriggerType.PointerClick, () => Transfer(side, index));
                views.Add((background, icon, text));
            }
        }

        private static void AddTrigger(EventTrigger trigger, EventTriggerType type, System.Action action)
        {
            var entry = new EventTrigger.Entry { eventID = type };
            entry.callback.AddListener(_ => action());
            trigger.triggers.Add(entry);
        }

        private static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        private static void Place(RectTransform rt, float y, float width, float height)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, y);
            rt.sizeDelta = new Vector2(width, height);
        }

        private Text NewText(Transform parent, string name, int size, TextAnchor anchor)
        {
            var t = NewRect(name, parent).gameObject.AddComponent<Text>();
            t.font = font;
            t.fontSize = size;
            t.alignment = anchor;
            t.color = new Color(0.94f, 0.91f, 0.85f);
            t.supportRichText = true;
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            return t;
        }
    }
}
