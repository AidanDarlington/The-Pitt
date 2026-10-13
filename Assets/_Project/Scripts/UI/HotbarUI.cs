using System.Collections.Generic;
using ThePitt.Items;
using ThePitt.Player;
using UnityEngine;
using UnityEngine.UI;

namespace ThePitt.UI
{
    /// <summary>
    /// The six hotbar slots along the bottom of the screen: icon, count, hotkey, and the selected one lit.
    /// Builds itself under the HUD canvas.
    /// </summary>
    public class HotbarUI : MonoBehaviour
    {
        [SerializeField] private Hotbar hotbar;
        [SerializeField, Min(20f)] private float slotSize = 72f;
        [SerializeField, Min(0f)] private float spacing = 6f;

        private static readonly Color Empty = new(0.06f, 0.06f, 0.07f, 0.7f);
        private static readonly Color Filled = new(0.1f, 0.1f, 0.11f, 0.82f);
        private static readonly Color Accent = new(1f, 0.66f, 0.3f);

        private Inventory inventory;
        private GameObject root;
        private readonly List<(Image background, Image icon, Text count, Image frame)> slots = new();

        private void Awake()
        {
            if (hotbar == null) hotbar = FindFirstObjectByType<Hotbar>();
            if (hotbar != null) inventory = hotbar.GetComponent<Inventory>();
            Build();
        }

        private void OnEnable()
        {
            if (hotbar == null) return;
            hotbar.Changed += Refresh;
            inventory.Changed += Refresh;
            Refresh();
        }

        private void OnDisable()
        {
            if (hotbar == null) return;
            hotbar.Changed -= Refresh;
            inventory.Changed -= Refresh;
        }

        /// <summary>Shows or hides the slots, e.g. while a full-screen panel like the map is up.</summary>
        public void SetVisible(bool visible)
        {
            if (root != null) root.SetActive(visible);
        }

        private void Refresh()
        {
            if (hotbar == null) return;
            for (int i = 0; i < slots.Count; i++)
            {
                var (background, icon, count, frame) = slots[i];
                var stack = inventory.GetSlot(i);
                bool selected = hotbar.Selected == i;
                frame.enabled = selected;
                background.color = stack.IsEmpty ? Empty : Filled;
                icon.enabled = !stack.IsEmpty && stack.item.Icon != null;
                if (icon.enabled) icon.sprite = stack.item.Icon;
                if (stack.IsEmpty) count.text = "";
                else if (stack.item.Icon == null) count.text = stack.item.DisplayName;
                else count.text = stack.quantity > 1 ? $"x{stack.quantity}" : "";
                count.color = stack.IsEmpty ? Color.white : stack.quality.DisplayColor();
            }
        }

        private void Build()
        {
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var root = new GameObject("Hotbar", typeof(RectTransform)).transform as RectTransform;
            this.root = root.gameObject;
            root.SetParent(transform, false);
            float width = Hotbar.Size * slotSize + (Hotbar.Size - 1) * spacing;
            root.anchorMin = root.anchorMax = root.pivot = new Vector2(0.5f, 0f);
            root.anchoredPosition = new Vector2(0f, 18f);
            root.sizeDelta = new Vector2(width, slotSize);

            for (int i = 0; i < Hotbar.Size; i++)
            {
                // A lit border round the selected slot, drawn behind it.
                var frame = NewImage(root, "Frame", new Vector2(i * (slotSize + spacing) - 2f, -2f), new Vector2(slotSize + 4f, slotSize + 4f));
                frame.rectTransform.anchorMin = frame.rectTransform.anchorMax = frame.rectTransform.pivot = Vector2.zero;
                frame.color = Accent;
                var slot = NewImage(root, $"Slot{i + 1}", new Vector2(i * (slotSize + spacing), 0f), new Vector2(slotSize, slotSize));
                slot.rectTransform.anchorMin = slot.rectTransform.anchorMax = slot.rectTransform.pivot = Vector2.zero;

                var icon = NewImage(slot.rectTransform, "Icon", Vector2.zero, Vector2.zero);
                icon.rectTransform.anchorMin = Vector2.zero;
                icon.rectTransform.anchorMax = Vector2.one;
                icon.rectTransform.offsetMin = new Vector2(8f, 8f);
                icon.rectTransform.offsetMax = new Vector2(-8f, -8f);
                icon.preserveAspect = true;

                var key = NewText(slot.rectTransform, font, (i + 1).ToString(), 14, TextAnchor.UpperLeft, FontStyle.Bold);
                key.color = new Color(Accent.r, Accent.g, Accent.b, 0.85f);
                var count = NewText(slot.rectTransform, font, "", 14, TextAnchor.LowerRight, FontStyle.Bold);

                slots.Add((slot, icon, count, frame));
            }
        }

        private static Image NewImage(RectTransform parent, string name, Vector2 position, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchoredPosition = position;
            rt.sizeDelta = size;
            var image = go.GetComponent<Image>();
            image.raycastTarget = false;
            return image;
        }

        private static Text NewText(RectTransform parent, Font font, string text, int size, TextAnchor anchor, FontStyle style)
        {
            var go = new GameObject("Text", typeof(RectTransform), typeof(Text));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(6f, 3f);
            rt.offsetMax = new Vector2(-6f, -3f);
            var t = go.GetComponent<Text>();
            t.font = font;
            t.fontSize = size;
            t.alignment = anchor;
            t.fontStyle = style;
            t.color = Color.white;
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.text = text;
            return t;
        }
    }
}
