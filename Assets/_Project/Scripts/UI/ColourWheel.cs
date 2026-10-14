using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ThePitt.UI
{
    /// <summary>
    /// A hue/saturation wheel: hue round the rim, white at the middle, always at full brightness so the
    /// colour reads on the dark map. Click or drag on it to pick; a ring marks the pick.
    /// </summary>
    [RequireComponent(typeof(RawImage))]
    public class ColourWheel : MonoBehaviour, IPointerDownHandler, IDragHandler
    {
        private const int Size = 128;
        private static Texture2D wheel;

        private RectTransform rect, cursor;

        public Color Colour { get; private set; } = Color.white;
        /// <summary>A colour was picked.</summary>
        public event Action<Color> Picked;

        private void Awake()
        {
            rect = (RectTransform)transform;
            var image = GetComponent<RawImage>();
            image.texture = Texture();
            image.raycastTarget = true;

            var go = new GameObject("Cursor", typeof(RectTransform), typeof(Outline), typeof(Image));
            cursor = (RectTransform)go.transform;
            cursor.SetParent(transform, false);
            cursor.sizeDelta = new Vector2(10f, 10f);
            var dot = go.GetComponent<Image>();
            dot.color = Color.clear;
            dot.raycastTarget = false;
            var outline = go.GetComponent<Outline>();
            outline.effectColor = new Color(0.05f, 0.05f, 0.05f, 1f);
            outline.effectDistance = new Vector2(2f, 2f);
            Show(Colour);
        }

        /// <summary>Moves the ring to a colour without announcing it.</summary>
        public void Show(Color colour)
        {
            Colour = colour;
            if (cursor == null) return;
            Color.RGBToHSV(colour, out float h, out float s, out _);
            float angle = h * Mathf.PI * 2f;
            float radius = s * rect.rect.width / 2f;
            cursor.anchoredPosition = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
            if (cursor.TryGetComponent<Image>(out var dot)) dot.color = colour;
        }

        public void OnPointerDown(PointerEventData e) => Pick(e);
        public void OnDrag(PointerEventData e) => Pick(e);

        private void Pick(PointerEventData e)
        {
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, e.position, e.pressEventCamera, out var local)) return;
            float half = rect.rect.width / 2f;
            var offset = local - rect.rect.center;
            float s = Mathf.Clamp01(offset.magnitude / half);
            float h = Mathf.Repeat(Mathf.Atan2(offset.y, offset.x) / (Mathf.PI * 2f), 1f);
            Show(Color.HSVToRGB(h, s, 1f));
            Picked?.Invoke(Colour);
        }

        private static Texture2D Texture()
        {
            if (wheel != null) return wheel;
            wheel = new Texture2D(Size, Size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[Size * Size];
            float half = Size / 2f;
            for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                var offset = new Vector2(x + 0.5f - half, y + 0.5f - half);
                float r = offset.magnitude / half;
                float h = Mathf.Repeat(Mathf.Atan2(offset.y, offset.x) / (Mathf.PI * 2f), 1f);
                var c = Color.HSVToRGB(h, Mathf.Clamp01(r), 1f);
                // Soft rim.
                c.a = Mathf.Clamp01((1f - r) * half);
                px[y * Size + x] = c;
            }
            wheel.SetPixels32(px);
            wheel.Apply();
            return wheel;
        }
    }
}
