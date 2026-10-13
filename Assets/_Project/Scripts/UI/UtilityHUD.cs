using ThePitt.Player;
using ThePitt.Properties;
using UnityEngine;
using UnityEngine.UI;

namespace ThePitt.UI
{
    /// <summary>
    /// While the player is inside a property they own: a card in the bottom-left corner with how much water,
    /// electricity and gas its stations draw against its caps (bars run to the hard cap, with the cap marked
    /// halfway), and the chance of a Watcher raid. While placing a station there, it also shows what that
    /// station would add, in red if it would go over. Builds itself under the HUD canvas, and lifts the
    /// pickup feed above it while it's up.
    /// </summary>
    public class UtilityHUD : MonoBehaviour
    {
        [SerializeField] private Transform player;
        [Tooltip("Optional: shows the draw of whatever is being placed.")]
        [SerializeField] private PlacementController placement;
        [Tooltip("Lifted above the card while it's showing.")]
        [SerializeField] private RectTransform pickupFeed;
        [SerializeField, Min(0.05f)] private float refreshSeconds = 0.25f;

        private static readonly Color Accent = new(1f, 0.66f, 0.3f);
        private static readonly Color Over = new(0.88f, 0.38f, 0.28f);
        private static readonly Color Fits = new(0.6f, 0.85f, 0.5f);

        private RectTransform card;
        private Text caption, riskLabel;
        private readonly Image[] fills = new Image[3], previews = new Image[3];
        private readonly Text[] values = new Text[3];
        private float nextRefresh;
        private InventoryUI inventoryUI;
        private Vector2 feedHome;

        private void Awake()
        {
            if (player == null)
            {
                var controller = FindFirstObjectByType<FirstPersonController>();
                if (controller != null) player = controller.transform;
            }
            if (placement == null && player != null) placement = player.GetComponent<PlacementController>();
            if (pickupFeed == null)
            {
                var feed = transform.Find("PickupFeed");
                if (feed != null) pickupFeed = (RectTransform)feed;
            }
            if (pickupFeed != null) feedHome = pickupFeed.anchoredPosition;
            Build();
            Show(false);
        }

        private void Update()
        {
            if (Time.unscaledTime < nextRefresh) return;
            nextRefresh = Time.unscaledTime + refreshSeconds;

            var property = player != null ? Property.At(player.position) : null;
            // Hidden behind the player menu like the rest of the HUD.
            if (inventoryUI == null) inventoryUI = FindFirstObjectByType<InventoryUI>();
            bool menuOpen = inventoryUI != null && inventoryUI.IsOpen;
            if (property == null || !property.Owned || menuOpen)
            {
                Show(false);
                return;
            }
            Show(true);

            var used = property.Usage();
            var caps = property.Caps;
            var hard = property.HardCaps;
            var adding = placement != null && placement.PlacingItem != null ? placement.PlacingItem.UtilityDraw : Vector3Int.zero;
            caption.text = $"{property.DisplayName.ToUpperInvariant()}  ·  UTILITIES";
            // Bars run to the hard cap; the tick halfway is the cap itself.
            for (int i = 0; i < 3; i++)
            {
                float max = Mathf.Max(1, hard[i]);
                bool over = used[i] > caps[i];
                bool tooMuch = used[i] + adding[i] > caps[i];
                SetWidth(fills[i], used[i] / max);
                fills[i].color = over ? Over : Property.UtilityColors[i];
                SetWidth(previews[i], (used[i] + adding[i]) / max);
                previews[i].color = tooMuch ? new Color(Over.r, Over.g, Over.b, 0.6f) : new Color(Fits.r, Fits.g, Fits.b, 0.55f);
                previews[i].enabled = adding[i] > 0;
                string add = adding[i] > 0 ? $"  <color=#{ColorUtility.ToHtmlStringRGB(tooMuch ? Over : Fits)}>+{adding[i]}</color>" : "";
                string count = over ? $"<color=#{ColorUtility.ToHtmlStringRGB(Over)}>{used[i]}</color>" : used[i].ToString();
                values[i].text = $"{count}/{caps[i]}{add}";
            }

            float risk = property.RaidRisk(used);
            float debtRisk = WatcherRaids.Instance != null ? WatcherRaids.Instance.DebtRisk : 0f;
            var lines = new System.Collections.Generic.List<string>();
            if (WatcherRaids.Instance != null && WatcherRaids.Instance.IsComing(property)) lines.Add("Watchers are coming today");
            if (risk > 0f) lines.Add($"Over the limit: {risk:P0} raid chance a day");
            if (debtRisk > 0f) lines.Add($"Debt overdue: {debtRisk:P0} raid chance a day");
            riskLabel.gameObject.SetActive(lines.Count > 0);
            riskLabel.text = string.Join("\n", lines);
            // The card's height changes with the risk lines; keep the pickup feed above it.
            if (pickupFeed != null) pickupFeed.anchoredPosition = feedHome + new Vector2(0f, card.rect.height + 12f);
        }

        private void Show(bool on)
        {
            if (card.gameObject.activeSelf == on) return;
            card.gameObject.SetActive(on);
            if (pickupFeed == null) return;
            // Keep the pickup lines clear of the card.
            Canvas.ForceUpdateCanvases();
            pickupFeed.anchoredPosition = on ? feedHome + new Vector2(0f, card.rect.height + 12f) : feedHome;
        }

        private void Build()
        {
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var go = new GameObject("UtilityCard", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            card = (RectTransform)go.transform;
            card.SetParent(transform, false);
            card.anchorMin = card.anchorMax = card.pivot = Vector2.zero;
            card.anchoredPosition = new Vector2(24f, 24f);
            card.sizeDelta = new Vector2(320f, 0f);
            go.GetComponent<Image>().color = new Color(0.06f, 0.06f, 0.07f, 0.88f);
            var layout = go.GetComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(14, 12, 8, 10);
            layout.spacing = 4f;
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
            accent.GetComponent<Image>().color = Accent;

            caption = NewText(card, font, 12, Accent, FontStyle.Bold, TextAnchor.MiddleLeft);
            for (int i = 0; i < 3; i++) BuildRow(font, i);
            riskLabel = NewText(card, font, 13, Over, FontStyle.Bold, TextAnchor.MiddleLeft);
            riskLabel.horizontalOverflow = HorizontalWrapMode.Wrap;
        }

        private void BuildRow(Font font, int index)
        {
            var row = new GameObject(Property.UtilityNames[index], typeof(RectTransform), typeof(LayoutElement)).transform as RectTransform;
            row.SetParent(card, false);
            row.GetComponent<LayoutElement>().preferredHeight = 20f;

            var label = NewText(row, font, 15, Property.UtilityColors[index], FontStyle.Normal, TextAnchor.MiddleLeft);
            label.text = Property.UtilityNames[index];
            Place(label.rectTransform, 0f, 92f);

            var track = NewImage(row, "Track", new Color(1f, 1f, 1f, 0.08f));
            Place(track.rectTransform, 96f, 120f, 8f);
            // The cap, halfway along; past it is over the limit, up to the hard cap at the end.
            var tick = NewImage(row, "Cap", new Color(1f, 1f, 1f, 0.55f));
            Place(tick.rectTransform, 96f + 60f - 1f, 2f, 14f);
            // What placing would bring it to, behind what's in use now.
            previews[index] = NewImage(track.rectTransform, "Preview", Color.clear);
            Stretch(previews[index]);
            fills[index] = NewImage(track.rectTransform, "Fill", Property.UtilityColors[index]);
            Stretch(fills[index]);

            values[index] = NewText(row, font, 15, new Color(0.92f, 0.9f, 0.86f), FontStyle.Normal, TextAnchor.MiddleRight);
            Place(values[index].rectTransform, 220f, 74f);
        }

        private static void Place(RectTransform rt, float x, float width, float height = 20f)
        {
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 0.5f);
            rt.anchoredPosition = new Vector2(x, 0f);
            rt.sizeDelta = new Vector2(width, height);
        }

        private static void Stretch(Image image)
        {
            var rt = image.rectTransform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = new Vector2(0f, 1f);
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }

        // Sized by anchors: an Image with no sprite ignores fillAmount.
        private static void SetWidth(Image bar, float fraction) =>
            bar.rectTransform.anchorMax = new Vector2(Mathf.Clamp01(fraction), 1f);

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
