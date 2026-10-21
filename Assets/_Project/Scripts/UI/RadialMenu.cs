using System;
using System.Collections.Generic;
using ThePitt.Audio;
using ThePitt.Interaction;
using ThePitt.Player;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace ThePitt.UI
{
    /// <summary>One choice on a <see cref="RadialMenu"/>.</summary>
    public struct RadialOption
    {
        public string label;
        /// <summary>Shown in the middle while hovered, e.g. how many are carried.</summary>
        public string detail;
        public Sprite icon;
        /// <summary>False greys the option out; it can be hovered but not chosen.</summary>
        public bool enabled;
        public Action chosen;
    }

    /// <summary>
    /// A ring of choices around the crosshair. The mouse direction from the centre picks a slice, left
    /// click takes it; right click, Esc or Interact again closes it. Built at runtime on its own canvas.
    /// </summary>
    public class RadialMenu : MonoBehaviour
    {
        public static RadialMenu Instance { get; private set; }

        private const float Radius = 260f;
        private const float Inner = 0.46f; // inner edge of the ring, as a fraction of the radius
        private const float GapDegrees = 2f;

        private static readonly Color Accent = new(0.95f, 0.72f, 0.3f);
        private static readonly Color Ink = new(0.94f, 0.91f, 0.85f);
        private static readonly Color Slice = new(0.1f, 0.095f, 0.09f, 0.92f);
        private static readonly Color SliceHover = new(0.42f, 0.31f, 0.15f, 0.95f);
        private static readonly Color SliceDisabled = new(0.07f, 0.07f, 0.07f, 0.8f);
        private static readonly Color SliceDisabledHover = new(0.16f, 0.15f, 0.14f, 0.85f);
        private static readonly Color Greyed = new(0.45f, 0.44f, 0.42f, 0.6f);

        private RectTransform ring;
        private Text title, centreLabel, centreDetail;
        private Font font;
        private Sprite ringSprite, discSprite;
        private readonly List<(Image slice, Image icon, Text label)> views = new();

        private readonly List<RadialOption> options = new();
        private FirstPersonController controller;
        private InputAction interact;
        private int hovered = -1;
        private int openedFrame;

        public bool IsOpen => ring != null && ring.gameObject.activeSelf;

        private void Awake()
        {
            Instance = this;
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            ringSprite = MakeSprite(Inner);
            discSprite = MakeSprite(-1f);
            Build();
            ring.gameObject.SetActive(false);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            foreach (var sprite in new[] { ringSprite, discSprite })
            {
                if (sprite == null) continue;
                Destroy(sprite.texture);
                Destroy(sprite);
            }
        }

        public void Open(string heading, IList<RadialOption> choices, PlayerInteractor interactor)
        {
            if (choices == null || choices.Count == 0) return;
            options.Clear();
            options.AddRange(choices);
            controller = interactor.GetComponent<FirstPersonController>();
            interact = interactor.InteractAction;
            openedFrame = Time.frameCount;
            hovered = -1;
            title.text = heading;
            Layout();

            ring.gameObject.SetActive(true);
            if (controller != null) controller.InputEnabled = false;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            // Start from the middle so nothing is picked until the mouse moves towards it.
            Mouse.current?.WarpCursorPosition(new Vector2(Screen.width, Screen.height) * 0.5f);
            Refresh();
        }

        public void Close()
        {
            if (!IsOpen) return;
            ring.gameObject.SetActive(false);
            options.Clear();
            if (controller != null) controller.InputEnabled = true;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        private void Update()
        {
            if (!IsOpen) return;
            var mouse = Mouse.current;
            bool escape = Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame;
            bool again = interact != null && interact.WasPressedThisFrame() && Time.frameCount != openedFrame;
            if (escape || again || (mouse != null && mouse.rightButton.wasPressedThisFrame))
            {
                Close();
                return;
            }
            if (mouse == null) return;

            int now = SliceUnder(mouse.position.ReadValue());
            if (now != hovered)
            {
                hovered = now;
                if (hovered >= 0) AudioManager.Play2D(AudioManager.Library?.click, 0.15f);
                Refresh();
            }
            if (mouse.leftButton.wasPressedThisFrame && hovered >= 0 && options[hovered].enabled)
            {
                var chosen = options[hovered].chosen;
                AudioManager.Play2D(AudioManager.Library?.click, 0.5f);
                Close();
                chosen?.Invoke();
            }
        }

        /// <summary>The slice in the mouse's direction from the centre, or -1 while it's still in the middle.</summary>
        private int SliceUnder(Vector2 screen)
        {
            var canvas = ring.GetComponentInParent<Canvas>();
            Vector2 offset = (screen - new Vector2(Screen.width, Screen.height) * 0.5f) / canvas.scaleFactor;
            if (offset.magnitude < Radius * Inner * 0.5f) return -1;
            float angle = Mathf.Atan2(offset.x, offset.y) * Mathf.Rad2Deg; // clockwise from straight up
            float segment = 360f / options.Count;
            return Mathf.FloorToInt(Mathf.Repeat(angle + segment * 0.5f, 360f) / segment) % options.Count;
        }

        // ---------------------------------------------------------------- refresh

        private void Layout()
        {
            while (views.Count < options.Count) views.Add(NewSlice(views.Count));
            float segment = 360f / options.Count;
            for (int i = 0; i < views.Count; i++)
            {
                var (slice, icon, label) = views[i];
                bool used = i < options.Count;
                // The icon and name aren't under the slice: hide them too, or a bigger menu's leftovers show.
                slice.gameObject.SetActive(used);
                icon.gameObject.SetActive(used);
                label.gameObject.SetActive(used);
                if (!used) continue;

                // Filled clockwise from the top: turn it back half a slice so slice i is centred on its direction.
                slice.fillAmount = (segment - GapDegrees) / 360f;
                slice.rectTransform.localEulerAngles = new Vector3(0f, 0f, -(i * segment - segment * 0.5f + GapDegrees * 0.5f));
                float a = i * segment * Mathf.Deg2Rad;
                var at = new Vector2(Mathf.Sin(a), Mathf.Cos(a)) * Radius * (1f + Inner) * 0.5f;
                icon.rectTransform.anchoredPosition = at + new Vector2(0f, 14f);
                label.rectTransform.anchoredPosition = at - new Vector2(0f, 40f);

                var o = options[i];
                icon.sprite = o.icon;
                icon.enabled = o.icon != null;
                label.text = o.label;
            }
        }

        private void Refresh()
        {
            for (int i = 0; i < options.Count; i++)
            {
                var (slice, icon, label) = views[i];
                bool on = options[i].enabled;
                bool hover = i == hovered;
                slice.color = on ? (hover ? SliceHover : Slice) : (hover ? SliceDisabledHover : SliceDisabled);
                icon.color = on ? Color.white : Greyed;
                label.color = on ? (hover ? Accent : Ink) : Greyed;
            }
            if (hovered < 0)
            {
                centreLabel.text = "";
                centreDetail.text = "";
                return;
            }
            var o = options[hovered];
            centreLabel.text = o.label;
            centreLabel.color = o.enabled ? Accent : Greyed;
            centreDetail.text = o.detail ?? "";
        }

        // ---------------------------------------------------------------- building

        private void Build()
        {
            var canvasGo = new GameObject("RadialMenu", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 65;
            UIScale.Apply(canvasGo.GetComponent<CanvasScaler>());

            ring = NewRect("Ring", canvasGo.transform);
            ring.anchorMin = ring.anchorMax = new Vector2(0.5f, 0.5f);
            ring.sizeDelta = Vector2.one * Radius * 2f;

            // A dark disc behind the middle for the readout.
            var hub = NewRect("Hub", ring);
            hub.sizeDelta = Vector2.one * Radius * Inner * 2f - Vector2.one * 12f;
            var hubImage = hub.gameObject.AddComponent<Image>();
            hubImage.sprite = discSprite;
            hubImage.color = new Color(0.06f, 0.055f, 0.05f, 0.85f);
            hubImage.raycastTarget = false;

            title = NewText(ring, "Title", 15, FontStyle.Bold);
            title.color = new Color(0.72f, 0.68f, 0.62f);
            title.rectTransform.anchoredPosition = new Vector2(0f, 46f);
            centreLabel = NewText(ring, "Label", 22, FontStyle.Bold);
            centreLabel.rectTransform.anchoredPosition = new Vector2(0f, 6f);
            centreDetail = NewText(ring, "Detail", 16, FontStyle.Normal);
            centreDetail.color = new Color(0.72f, 0.68f, 0.62f);
            centreDetail.rectTransform.anchoredPosition = new Vector2(0f, -26f);
        }

        private (Image, Image, Text) NewSlice(int index)
        {
            var rt = NewRect($"Slice_{index}", ring);
            rt.sizeDelta = Vector2.one * Radius * 2f;
            rt.SetSiblingIndex(index); // under the hub and readout
            var slice = rt.gameObject.AddComponent<Image>();
            slice.sprite = ringSprite;
            slice.type = Image.Type.Filled;
            slice.fillMethod = Image.FillMethod.Radial360;
            slice.fillOrigin = (int)Image.Origin360.Top;
            slice.fillClockwise = true;
            slice.raycastTarget = false;

            var iconRt = NewRect("Icon", ring);
            iconRt.sizeDelta = new Vector2(72f, 72f);
            var icon = iconRt.gameObject.AddComponent<Image>();
            icon.preserveAspect = true;
            icon.raycastTarget = false;

            var label = NewText(ring, "Name", 16, FontStyle.Bold);
            label.rectTransform.sizeDelta = new Vector2(150f, 24f);
            return (slice, icon, label);
        }

        private static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        private Text NewText(Transform parent, string name, int size, FontStyle style)
        {
            var t = NewRect(name, parent).gameObject.AddComponent<Text>();
            t.rectTransform.sizeDelta = new Vector2(Radius * Inner * 1.8f, 30f);
            t.font = font;
            t.fontSize = size;
            t.fontStyle = style;
            t.alignment = TextAnchor.MiddleCenter;
            t.color = Ink;
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            return t;
        }

        /// <summary>A soft-edged ring from <paramref name="inner"/> out to the edge (filled radially, the slices); below zero, a disc.</summary>
        private static Sprite MakeSprite(float inner)
        {
            const int size = 256;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[size * size];
            float half = size * 0.5f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float r = new Vector2(x + 0.5f - half, y + 0.5f - half).magnitude / half;
                // One pixel of fade on each edge.
                float edge = 1f / half;
                float a = Mathf.Clamp01((1f - r) / edge) * Mathf.Clamp01((r - inner) / edge);
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
            }
            tex.SetPixels32(pixels);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
        }
    }
}
