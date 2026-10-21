using ThePitt.Core;
using ThePitt.Economy;
using UnityEngine;
using UnityEngine.UI;

namespace ThePitt.UI
{
    /// <summary>
    /// Debt status, on a card (styled like the unlock popups) in the bottom-left of the player menu's
    /// Inventory tab; and the day and time with the scrip balance under it on a small card in the top-left of
    /// the HUD, which stays up while the menu is open. Builds both cards itself; the old corner label is hidden.
    /// </summary>
    public class EconomyHUD : MonoBehaviour
    {
        [SerializeField] private PlayerWallet wallet;
        [Tooltip("The old always-on corner label. Hidden; the card replaces it.")]
        [SerializeField] private Text label;
        [SerializeField] private Color urgentColor = new Color(0.85f, 0.3f, 0.25f);

        private static readonly Color Accent = new(1f, 0.66f, 0.3f);

        private DebtLedger ledger;
        private InventoryUI inventoryUI;
        private Text body, time, scrip;

        /// <summary>The card, so other corner UI can make room for it.</summary>
        public RectTransform Card { get; private set; }

        private void Awake()
        {
            if (label != null) label.enabled = false;
            BuildCard();
            BuildTimeCard();
            // Into the player menu, which lays itself out first (it runs earlier).
            var menu = FindAnyObjectByType<PlayerMenuUI>();
            if (menu != null) menu.HostScripCard(Card);
            inventoryUI = GetComponent<InventoryUI>();
            if (inventoryUI == null) inventoryUI = FindFirstObjectByType<InventoryUI>();
            if (inventoryUI != null) inventoryUI.OpenChanged += OnInventoryOpen;
            Card.gameObject.SetActive(false);
        }

        private void OnInventoryOpen(bool open)
        {
            Card.gameObject.SetActive(open);
            if (open) Redraw();
        }

        private void Start()
        {
            ledger = DebtLedger.Instance;
            if (wallet != null) wallet.Changed += OnWalletChanged;
            if (ledger != null) ledger.Changed += Redraw;
            if (TimeManager.Instance != null)
            {
                TimeManager.Instance.DayChanged += OnDayChanged;
                TimeManager.Instance.MinuteChanged += OnMinute;
            }
            Redraw();
            RedrawTime();
        }

        private void OnDestroy()
        {
            if (inventoryUI != null) inventoryUI.OpenChanged -= OnInventoryOpen;
            if (wallet != null) wallet.Changed -= OnWalletChanged;
            if (ledger != null) ledger.Changed -= Redraw;
            if (TimeManager.Instance != null)
            {
                TimeManager.Instance.DayChanged -= OnDayChanged;
                TimeManager.Instance.MinuteChanged -= OnMinute;
            }
        }

        private void OnWalletChanged(int _)
        {
            Redraw();
            RedrawTime();
        }
        private void OnDayChanged(int _) => Redraw();
        private void OnMinute(int _)
        {
            RedrawTime();
        }

        /// <summary>The day and time, on the top-left card.</summary>
        private void RedrawTime()
        {
            var clock = TimeManager.Instance;
            if (time == null || clock == null) return;
            time.text = $"Day {clock.Day}  ·  {clock.Hour:00}:{clock.Minute:00}";
            if (scrip != null) scrip.text = $"{(wallet != null ? wallet.Money : 0):N0} scrip";
        }

        private void Redraw()
        {
            if (body == null) return;
            string text = "";

            if (ledger != null)
            {
                text += $"<size=26>{ledger.Remaining:N0} owed</size>";
                if (ledger.IsCleared)
                {
                    text += "\n<color=#8FD07A>Debt cleared</color>";
                }
                else if (ledger.AmountDue > 0)
                {
                    int today = TimeManager.Instance != null ? TimeManager.Instance.Day : 1;
                    int daysLeft = ledger.DueDay - today;
                    string line = $"{ledger.AmountDue:N0} due by Day {ledger.DueDay} ({daysLeft}d)";
                    text += daysLeft <= 1
                        ? $"\n<color=#{ColorUtility.ToHtmlStringRGB(urgentColor)}>{line}</color>"
                        : $"\n{line}";
                }
                else
                {
                    text += $"\nPaid up until Day {ledger.DueDay}";
                }
                if (ledger.MissedPayments > 0) text += $"\nMissed payments: {ledger.MissedPayments}";
                if (ledger.Overdue > 0)
                    text += $"\n<color=#{ColorUtility.ToHtmlStringRGB(urgentColor)}>{ledger.Overdue:N0} overdue · the Watchers may raid</color>";
            }

            body.text = text;
        }

        private void BuildCard()
        {
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var go = new GameObject("EconomyCard", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            Card = (RectTransform)go.transform;
            Card.SetParent(transform, false);
            // Top-left of the screen unless the player menu takes it in (see PlayerMenuUI.HostScripCard).
            Card.anchorMin = Card.anchorMax = Card.pivot = new Vector2(0f, 1f);
            Card.anchoredPosition = new Vector2(20f, -20f);
            Card.sizeDelta = new Vector2(PlayerMenuUI.CornerWidth, 0f);
            go.GetComponent<Image>().color = new Color(0.06f, 0.06f, 0.07f, 0.88f);
            var layout = go.GetComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(16, 12, 10, 12);
            layout.spacing = 2f;
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            go.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            AddAccent(Card);

            var caption = CardText(Card, font, 14, Accent, FontStyle.Bold);
            caption.text = "DEBT";
            body = CardText(Card, font, 19, new Color(0.92f, 0.9f, 0.86f), FontStyle.Normal);
            body.horizontalOverflow = HorizontalWrapMode.Wrap;
        }

        /// <summary>The day and time, and the scrip under them, in the top-left corner of the HUD, sized to its text.</summary>
        private void BuildTimeCard()
        {
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var go = new GameObject("TimeCard", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            var card = (RectTransform)go.transform;
            card.SetParent(transform, false);
            card.anchorMin = card.anchorMax = card.pivot = new Vector2(0f, 1f);
            card.anchoredPosition = new Vector2(24f, -20f);
            var image = go.GetComponent<Image>();
            image.color = new Color(0.06f, 0.06f, 0.07f, 0.88f);
            image.raycastTarget = false;
            var layout = go.GetComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(16, 14, 6, 6);
            layout.spacing = 1f;
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = layout.childForceExpandHeight = false;
            var fitter = go.GetComponent<ContentSizeFitter>();
            fitter.horizontalFit = fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            AddAccent(card);
            time = CardText(card, font, 20, new Color(0.96f, 0.94f, 0.9f), FontStyle.Bold);
            time.horizontalOverflow = HorizontalWrapMode.Overflow;
            scrip = CardText(card, font, 18, Accent, FontStyle.Normal);
            scrip.horizontalOverflow = HorizontalWrapMode.Overflow;
        }

        /// <summary>The orange edge the unlock cards have.</summary>
        private static void AddAccent(RectTransform card)
        {
            var accent = new GameObject("Accent", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
            var ar = (RectTransform)accent.transform;
            ar.SetParent(card, false);
            accent.GetComponent<LayoutElement>().ignoreLayout = true;
            ar.anchorMin = Vector2.zero;
            ar.anchorMax = new Vector2(0f, 1f);
            ar.pivot = new Vector2(0f, 0.5f);
            ar.sizeDelta = new Vector2(4f, 0f);
            var image = accent.GetComponent<Image>();
            image.color = Accent;
            image.raycastTarget = false;
        }

        private static Text CardText(RectTransform card, Font font, int size, Color color, FontStyle style)
        {
            var go = new GameObject("Text", typeof(RectTransform), typeof(Text));
            go.transform.SetParent(card, false);
            var t = go.GetComponent<Text>();
            t.font = font;
            t.fontSize = size;
            t.fontStyle = style;
            t.color = color;
            t.supportRichText = true;
            t.raycastTarget = false;
            return t;
        }
    }
}
