using ThePitt.Player;
using UnityEngine;
using UnityEngine.UI;

namespace ThePitt.UI
{
    /// <summary>
    /// Health / hunger / thirst on a card in the bottom-right corner of the HUD, styled like the utilities
    /// card, and kept up while the player menu is open. Builds the card itself; the old bars are hidden and
    /// only lend their colours.
    /// </summary>
    public class VitalsUI : MonoBehaviour
    {
        [SerializeField] private PlayerVitals vitals;
        [Tooltip("The old bars. Hidden; their colours are used for the card's bars.")]
        [SerializeField] private Image healthFill;
        [SerializeField] private Image hungerFill;
        [SerializeField] private Image thirstFill;
        [Tooltip("Bars flash this colour when below the warning threshold.")]
        [SerializeField] private Color warningColor = new Color(0.9f, 0.25f, 0.2f);
        [SerializeField, Range(0f, 1f)] private float warningThreshold = 0.2f;

        [Tooltip("The old bars and their labels, hidden. Defaults to the \"Vitals\" child.")]
        [SerializeField] private GameObject group;

        private static readonly Color Accent = new(1f, 0.66f, 0.3f);
        private static readonly string[] Names = { "Health", "Hunger", "Thirst" };

        private readonly Color[] colors = { new(0.8f, 0.25f, 0.22f), new(0.85f, 0.62f, 0.3f), new(0.35f, 0.62f, 0.95f) };
        private readonly Image[] fills = new Image[3];
        private readonly Text[] values = new Text[3];
        private readonly float[] shown = new float[3];
        private readonly int[] shownValue = { -1, -1, -1 };

        private void Awake()
        {
            var old = new[] { healthFill, hungerFill, thirstFill };
            for (int i = 0; i < 3; i++)
                if (old[i] != null) colors[i] = old[i].color;
            if (group == null && healthFill != null)
            {
                // The "Vitals" object the old bars sit in.
                var t = healthFill.transform;
                while (t.parent != null && t.name != "Vitals") t = t.parent;
                group = t.name == "Vitals" ? t.gameObject : healthFill.transform.parent.parent.gameObject;
            }
            if (group != null) group.SetActive(false);
            Build();
            Snap();
        }

        /// <summary>Jumps the bars to their values rather than sliding from empty.</summary>
        private void Snap()
        {
            if (vitals == null) return;
            for (int i = 0; i < 3; i++) shown[i] = Fraction(i);
        }

        private float Current(int i) => i == 0 ? vitals.Health : i == 1 ? vitals.Hunger : vitals.Thirst;
        private float Max(int i) => i == 0 ? vitals.MaxHealth : i == 1 ? vitals.MaxHunger : vitals.MaxThirst;
        private float Fraction(int i) => Mathf.Clamp01(Current(i) / Max(i));

        private void Update()
        {
            if (vitals == null) return;
            // Smooth the bars toward their value; cheap enough to do every frame for three bars.
            for (int i = 0; i < 3; i++)
            {
                float value = Fraction(i);
                shown[i] = Mathf.MoveTowards(shown[i], value, Time.unscaledDeltaTime);
                fills[i].rectTransform.anchorMax = new Vector2(shown[i], 1f);
                bool warn = value <= warningThreshold;
                fills[i].color = warn ? Color.Lerp(colors[i], warningColor, Mathf.PingPong(Time.unscaledTime * 2f, 1f)) : colors[i];
                int whole = Mathf.CeilToInt(Current(i));
                if (whole == shownValue[i]) continue;
                shownValue[i] = whole;
                values[i].text = whole.ToString();
                values[i].color = warn ? warningColor : new Color(0.92f, 0.9f, 0.86f);
            }
        }

        private void Build()
        {
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var go = new GameObject("VitalsCard", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            var card = (RectTransform)go.transform;
            card.SetParent(transform, false);
            // Low enough to sit under the player menu's panel, which stays clear of the bottom 108 px.
            card.anchorMin = card.anchorMax = card.pivot = new Vector2(1f, 0f);
            card.anchoredPosition = new Vector2(-24f, 20f);
            card.sizeDelta = new Vector2(300f, 0f);
            go.GetComponent<Image>().color = new Color(0.06f, 0.06f, 0.07f, 0.88f);
            go.GetComponent<Image>().raycastTarget = false;
            var layout = go.GetComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(14, 12, 8, 8);
            layout.spacing = 3f;
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            go.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            // The orange edge the other cards have.
            var accent = new GameObject("Accent", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
            var ar = (RectTransform)accent.transform;
            ar.SetParent(card, false);
            accent.GetComponent<LayoutElement>().ignoreLayout = true;
            ar.anchorMin = Vector2.zero;
            ar.anchorMax = new Vector2(0f, 1f);
            ar.pivot = new Vector2(0f, 0.5f);
            ar.sizeDelta = new Vector2(4f, 0f);
            var accentImage = accent.GetComponent<Image>();
            accentImage.color = Accent;
            accentImage.raycastTarget = false;

            for (int i = 0; i < 3; i++) BuildRow(card, font, i);
        }

        private void BuildRow(RectTransform card, Font font, int index)
        {
            var row = new GameObject(Names[index], typeof(RectTransform), typeof(LayoutElement)).transform as RectTransform;
            row.SetParent(card, false);
            row.GetComponent<LayoutElement>().preferredHeight = 18f;

            var label = NewText(row, font, 15, colors[index], FontStyle.Bold, TextAnchor.MiddleLeft);
            label.text = Names[index];
            Place(label.rectTransform, 0f, 72f);

            var track = NewImage(row, "Track", new Color(1f, 1f, 1f, 0.08f));
            Place(track.rectTransform, 76f, 150f, 8f);
            // Sized by anchors: an Image with no sprite ignores fillAmount.
            fills[index] = NewImage(track.rectTransform, "Fill", colors[index]);
            var rt = fills[index].rectTransform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = new Vector2(0f, 1f);
            rt.offsetMin = rt.offsetMax = Vector2.zero;

            values[index] = NewText(row, font, 15, new Color(0.92f, 0.9f, 0.86f), FontStyle.Normal, TextAnchor.MiddleRight);
            Place(values[index].rectTransform, 230f, 44f);
        }

        private static void Place(RectTransform rt, float x, float width, float height = 18f)
        {
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 0.5f);
            rt.anchoredPosition = new Vector2(x, 0f);
            rt.sizeDelta = new Vector2(width, height);
        }

        private static Image NewImage(Transform parent, string name, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var image = go.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        private static Text NewText(Transform parent, Font font, int size, Color color, FontStyle style, TextAnchor anchor)
        {
            var go = new GameObject("Text", typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<Text>();
            t.font = font;
            t.fontSize = size;
            t.fontStyle = style;
            t.color = color;
            t.alignment = anchor;
            t.supportRichText = true;
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            return t;
        }
    }
}
