using ThePitt.Core;
using ThePitt.Economy;
using UnityEngine;
using UnityEngine.UI;

namespace ThePitt.UI
{
    /// <summary>
    /// Day, time, scrip balance and debt status, on a card (styled like the unlock popups) in the bottom-left
    /// of the player menu's Inventory tab, opposite the vitals. Builds the card itself; the old corner label is hidden.
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
        private Text body, time;

        /// <summary>The card, so other corner UI can make room for it.</summary>
        public RectTransform Card { get; private set; }

        private void Awake()
        {
            if (label != null) label.enabled = false;
            BuildCard();
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
            if (!open) return;
            Redraw();
            RedrawTime();
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

        private void OnWalletChanged(int _) => Redraw();
        private void OnDayChanged(int _) => Redraw();
        private void OnMinute(int _)
        {
            if (Card.gameObject.activeSelf) RedrawTime();
        }

        /// <summary>The day and time: the menu is the only place they show.</summary>
        private void RedrawTime()
        {
            var clock = TimeManager.Instance;
            if (time == null || clock == null) return;
            time.text = $"Day {clock.Day}  ·  {clock.Hour:00}:{clock.Minute:00}";
        }

        private void Redraw()
        {
            if (body == null) return;
            string text = $"<size=26>{(wallet != null ? wallet.Money : 0):N0} scrip</size>";

            if (ledger != null)
            {
                text += $"\n<color=#B8B2A8>Debt  {ledger.Remaining:N0}</color>";
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

            // The orange edge the unlock cards have.
            var accent = new GameObject("Accent", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
            var ar = (RectTransform)accent.transform;
            ar.SetParent(Card, false);
            accent.GetComponent<LayoutElement>().ignoreLayout = true;
            ar.anchorMin = Vector2.zero;
            ar.anchorMax = new Vector2(0f, 1f);
            ar.pivot = new Vector2(0f, 0.5f);
            ar.sizeDelta = new Vector2(4f, 0f);
            accent.GetComponent<Image>().color = Accent;

            // Sized to match the vitals labels beside it in the menu.
            time = CardText(font, 24, new Color(0.96f, 0.94f, 0.9f), FontStyle.Bold);
            var caption = CardText(font, 14, Accent, FontStyle.Bold);
            caption.text = "SCRIP & DEBT";
            body = CardText(font, 19, new Color(0.92f, 0.9f, 0.86f), FontStyle.Normal);
            body.horizontalOverflow = HorizontalWrapMode.Wrap;
        }

        private Text CardText(Font font, int size, Color color, FontStyle style)
        {
            var go = new GameObject("Text", typeof(RectTransform), typeof(Text));
            go.transform.SetParent(Card, false);
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
