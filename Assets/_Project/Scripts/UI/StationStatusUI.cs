using UnityEngine;
using UnityEngine.UI;

namespace ThePitt.UI
{
    /// <summary>
    /// A disc floating over a station while it works: the icon of what's being made in the middle and a ring
    /// round the edge that fills clockwise with progress. Turns to face the camera; only shown up close.
    /// Built at runtime by the station.
    /// </summary>
    public class StationStatusUI : MonoBehaviour
    {
        private const float Size = 0.5f, Pixels = 128f, ShowDistance = 14f;
        private static Sprite disc, ring;

        private Image background, track, fill, icon;
        private bool wanted;

        public static readonly Color DiscColour = new(0.06f, 0.06f, 0.07f, 0.85f);
        private static readonly Color RingColour = new(1f, 0.66f, 0.3f);

        /// <summary>Makes one as a child of <paramref name="station"/>, at a local point above it.</summary>
        public static StationStatusUI Create(Transform station, Vector3 localPosition)
        {
            var go = new GameObject("StatusRing", typeof(RectTransform), typeof(Canvas));
            var rt = (RectTransform)go.transform;
            rt.SetParent(station, false);
            rt.localPosition = localPosition;
            rt.sizeDelta = new Vector2(Pixels, Pixels);
            // A world-space canvas Size metres across, whatever the station's own scale.
            var lossy = station.lossyScale;
            float parentScale = Mathf.Max(0.0001f, (Mathf.Abs(lossy.x) + Mathf.Abs(lossy.y) + Mathf.Abs(lossy.z)) / 3f);
            rt.localScale = Vector3.one * (Size / Pixels / parentScale);
            go.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            var ui = go.AddComponent<StationStatusUI>();
            ui.Build();
            ui.SetVisible(false);
            return ui;
        }

        private void Build()
        {
            background = NewImage("Disc", Disc(), DiscColour, 1f);
            track = NewImage("Track", Ring(), new Color(1f, 1f, 1f, 0.12f), 1f);
            fill = NewImage("Fill", Ring(), RingColour, 1f);
            fill.type = Image.Type.Filled;
            fill.fillMethod = Image.FillMethod.Radial360;
            fill.fillOrigin = (int)Image.Origin360.Top;
            fill.fillClockwise = true;
            icon = NewImage("Icon", null, Color.white, 0.62f);
            icon.preserveAspect = true;
        }

        /// <summary>Shows it (when the player's near) or hides it.</summary>
        public void SetVisible(bool visible)
        {
            wanted = visible;
            if (!visible) gameObject.SetActive(false);
        }

        /// <summary>What's being made, how far along (0-1), and the disc's colour behind it.</summary>
        public void Set(Sprite sprite, float progress, Color discColour)
        {
            icon.sprite = sprite;
            icon.enabled = sprite != null;
            fill.fillAmount = Mathf.Clamp01(progress);
            background.color = discColour;
        }

        /// <summary>Called by the station every frame it might be shown, since a hidden canvas runs no Update.</summary>
        public void Tick()
        {
            var cam = Camera.main;
            bool near = wanted && cam != null && (cam.transform.position - transform.position).sqrMagnitude < ShowDistance * ShowDistance;
            if (gameObject.activeSelf != near) gameObject.SetActive(near);
            if (!near) return;
            // Face the camera.
            transform.rotation = Quaternion.LookRotation(transform.position - cam.transform.position, Vector3.up);
        }

        private Image NewImage(string name, Sprite sprite, Color color, float fraction)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            var rt = (RectTransform)go.transform;
            rt.SetParent(transform, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = Vector2.one * Pixels * fraction;
            var image = go.GetComponent<Image>();
            image.sprite = sprite;
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        private static Sprite Disc() => disc != null ? disc : disc = Circle(0f, 0.86f);
        private static Sprite Ring() => ring != null ? ring : ring = Circle(0.86f, 1f);

        /// <summary>A white annulus between two radii (as fractions of the half-width), soft-edged.</summary>
        private static Sprite Circle(float inner, float outer)
        {
            const int size = 128;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[size * size];
            float half = size / 2f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float r = new Vector2(x + 0.5f - half, y + 0.5f - half).magnitude / half;
                float a = Mathf.Clamp01((outer - r) * half) * (inner <= 0f ? 1f : Mathf.Clamp01((r - inner) * half));
                px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255));
            }
            tex.SetPixels32(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
        }
    }
}
