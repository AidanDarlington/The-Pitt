using System.Collections.Generic;
using ThePitt.Economy;
using ThePitt.Interaction;
using ThePitt.Items;
using ThePitt.NPC;
using ThePitt.Player;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace ThePitt.UI
{
    /// <summary>
    /// Trade window for an <see cref="NpcBuyer"/>, plus their <see cref="NpcVendor"/> stock if they sell
    /// anything. Click buys one, Shift+click five. What the player sells is picked in a grid of icons (one per
    /// item and quality) and goes through together with the Sell button, as long as the buyer can pay for it all.
    /// </summary>
    public class TradeUI : MonoBehaviour
    {
        public static TradeUI Instance { get; private set; }

        [SerializeField] private GameObject panel;
        [SerializeField] private Text headerLabel;
        [SerializeField] private RectTransform list;
        [Tooltip("Inactive row with a Button, an Image and a child Text.")]
        [SerializeField] private GameObject rowTemplate;
        [SerializeField] private Text statusLabel;
        [SerializeField] private Button closeButton;

        private NpcBuyer buyer;
        private NpcVendor vendor;
        private Inventory inventory;
        private PlayerWallet wallet;
        private FirstPersonController controller;
        private Button sellButton;
        private Text hintLabel;
        private Text saleLabel;

        private const float CellWidth = 148f, CellHeight = 206f, CellSpacing = 10f;
        private static readonly Color CellColor = new(0.17f, 0.16f, 0.14f, 1f);
        private static readonly Color OverTextColor = new(0.75f, 0.31f, 0.25f, 1f);

        /// <summary>One icon in the sell grid: everything the player carries of one item at one quality.</summary>
        private class SellCell
        {
            public ItemDefinition item;
            public ItemQuality quality;
            public int owned, price, selected;
            public InputField field;
        }

        private readonly List<SellCell> cells = new();
        /// <summary>How many of each item and quality are picked to sell. Kept through refreshes, cleared on open.</summary>
        private readonly Dictionary<(ItemDefinition, ItemQuality), int> selected = new();

        public bool IsOpen => panel.activeSelf;
        /// <summary>Who the player is trading with, or null.</summary>
        public NpcBuyer CurrentBuyer => IsOpen ? buyer : null;

        private void Awake()
        {
            Instance = this;
            rowTemplate.SetActive(false);
            panel.SetActive(false);
            if (closeButton != null) closeButton.onClick.AddListener(Close);
            hintLabel = panel.transform.Find("Hint")?.GetComponent<Text>();
            LayOutPanel();
        }

        /// <summary>
        /// Scrolls the list, and puts a Sell button left of Leave with the status line beside it.
        /// </summary>
        private void LayOutPanel()
        {
            float width = list.sizeDelta.x;
            var viewport = new GameObject("ListViewport", typeof(RectTransform), typeof(RectMask2D), typeof(Image), typeof(ScrollRect)).transform as RectTransform;
            viewport.SetParent(list.parent, false);
            viewport.SetSiblingIndex(list.GetSiblingIndex());
            viewport.anchorMin = list.anchorMin;
            viewport.anchorMax = list.anchorMax;
            viewport.pivot = list.pivot;
            viewport.anchoredPosition = list.anchoredPosition;
            viewport.sizeDelta = list.sizeDelta;
            // Invisible, but catches the scroll wheel between rows.
            viewport.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0f);

            list.SetParent(viewport, false);
            list.anchorMin = new Vector2(0f, 1f);
            list.anchorMax = new Vector2(1f, 1f);
            list.pivot = new Vector2(0.5f, 1f);
            list.anchoredPosition = Vector2.zero;
            list.sizeDelta = Vector2.zero;
            var fitter = list.GetComponent<ContentSizeFitter>();
            if (fitter == null) fitter = list.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var scroll = viewport.GetComponent<ScrollRect>();
            scroll.content = list;
            scroll.viewport = viewport;
            scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 30f;
            listWidth = width;

            if (closeButton == null) return;
            // Sell takes Leave's bottom-right corner; Leave goes up to the top-right corner.
            var close = (RectTransform)closeButton.transform;
            sellButton = Instantiate(closeButton, close.parent);
            sellButton.name = "SellButton";
            sellButton.onClick.RemoveAllListeners();
            sellButton.onClick.AddListener(Sell);
            sellButton.GetComponentInChildren<Text>().text = "Sell";
            sellButton.GetComponent<Image>().color = new Color(0.27f, 0.32f, 0.18f, 1f);
            var bottomRight = close.anchoredPosition;
            close.anchorMin = close.anchorMax = close.pivot = new Vector2(1f, 1f);
            close.anchoredPosition = new Vector2(bottomRight.x, -bottomRight.y);

            // Header and hint stop short of Leave.
            float textWidth = width - close.sizeDelta.x - 10f;
            foreach (var label in new[] { headerLabel, hintLabel })
            {
                if (label == null) continue;
                var rect = label.rectTransform;
                float y = rect.anchoredPosition.y;
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
                rect.anchoredPosition = new Vector2(20f, y);
                rect.sizeDelta = new Vector2(textWidth, rect.sizeDelta.y);
            }

            const float saleWidth = 200f;
            float buttonWidth = close.sizeDelta.x;
            saleLabel = MakeText(close.parent, "", 22, TextAnchor.MiddleRight, headerLabel.color);
            Place(saleLabel.rectTransform, new Vector2(1f, 0f),
                new Vector2(bottomRight.x - buttonWidth - 14f, bottomRight.y), new Vector2(saleWidth, close.sizeDelta.y));

            var status = statusLabel.rectTransform;
            status.anchorMin = status.anchorMax = status.pivot = Vector2.zero;
            status.anchoredPosition = new Vector2(20f, 18f);
            status.sizeDelta = new Vector2(width - buttonWidth - saleWidth - 30f, 54f);
        }

        private float listWidth;

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            if (IsOpen && Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame) Close();
        }

        public void Open(NpcBuyer npc, PlayerInteractor interactor)
        {
            buyer = npc;
            vendor = npc.GetComponent<NpcVendor>();
            inventory = interactor.Inventory;
            wallet = interactor.GetComponent<PlayerWallet>();
            controller = interactor.GetComponent<FirstPersonController>();

            panel.SetActive(true);
            if (controller != null) controller.InputEnabled = false;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            inventory.Changed += Refresh;
            statusLabel.text = "";
            selected.Clear();
            if (hintLabel != null)
            {
                bool sells = vendor != null && vendor.Stock is { Length: > 0 };
                hintLabel.text = (sells ? "Shift+click to buy five  ·  " : "") +
                                 (npc.Profile.Buys ? "Shift with − or + for ten  ·  " : "") + "Esc to leave";
            }
            Refresh();
        }

        public void Close()
        {
            if (!IsOpen) return;
            panel.SetActive(false);
            inventory.Changed -= Refresh;
            if (controller != null) controller.InputEnabled = true;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            buyer = null;
            vendor = null;
        }

        private GameObject AddRow(string text, bool interactable, UnityEngine.Events.UnityAction onClick)
        {
            var row = Instantiate(rowTemplate, list);
            row.SetActive(true);
            row.GetComponentInChildren<Text>().text = text;
            var button = row.GetComponent<Button>();
            button.interactable = interactable;
            if (onClick != null) button.onClick.AddListener(onClick);
            return row;
        }

        /// <summary>A section label: a non-clickable row drawn without a background.</summary>
        private void AddHeading(string text)
        {
            var row = AddRow($"<color=#B0A890><b>{text}</b></color>", false, null);
            row.GetComponent<Image>().enabled = false;
        }

        private void Refresh()
        {
            var p = buyer.Profile;
            for (int i = list.childCount - 1; i >= 0; i--)
            {
                var child = list.GetChild(i).gameObject;
                if (child != rowTemplate) Destroy(child);
            }

            // Taking them on into the player's own gang.
            var gangs = Factions.FactionManager.Instance;
            int recruitCost = gangs != null && gangs.PlayerGang != null && buyer.Faction == null ? gangs.RecruitCost(p.NpcType) : 0;
            if (recruitCost > 0)
            {
                AddHeading("YOUR GANG");
                bool can = wallet.Money >= recruitCost;
                AddRow($"Recruit {buyer.NpcName} into {gangs.PlayerGang.Name}   —   {recruitCost} scrip up front, " +
                       $"wages daily{(can ? "" : "  <color=#C05040>(not enough scrip)</color>")}", can, Recruit);
            }

            if (vendor != null && vendor.Stock != null && vendor.Stock.Length > 0)
            {
                AddHeading("FOR SALE");
                for (int i = 0; i < vendor.Stock.Length; i++)
                {
                    var s = vendor.Stock[i];
                    if (s.item == null) continue;
                    bool can = s.remaining > 0 && wallet.Money >= s.price;
                    string note = s.remaining <= 0 ? "  <color=#C05040>(sold out today)</color>"
                        : wallet.Money < s.price ? "  <color=#C05040>(not enough scrip)</color>" : "";
                    int captured = i;
                    AddRow($"{s.item.DisplayName}   —   {s.price} each   <size=16>{s.remaining} left</size>{note}", can,
                        () => Buy(captured));
                }
                if (p.Buys) AddHeading("THEY'LL BUY");
            }
            // Sells only (a dealer): nothing of the player's to list.
            cells.Clear();
            if (!p.Buys)
            {
                UpdateSale();
                return;
            }

            BuildSellGrid();
            if (cells.Count == 0 && string.IsNullOrEmpty(statusLabel.text))
                statusLabel.text = $"{buyer.NpcName} isn't interested in anything you're carrying.";
            UpdateSale();
        }

        private void UpdateHeader()
        {
            headerLabel.text = $"<b>{buyer.NpcName}</b>  ·  {buyer.Faction?.Name ?? buyer.Profile.TypeName}  ·  carrying {buyer.Cash} scrip";
        }

        /// <summary>One icon per item and quality the buyer wants, in inventory order.</summary>
        private void BuildSellGrid()
        {
            cells.Clear();
            var seen = new HashSet<(ItemDefinition, ItemQuality)>();
            for (int slot = 0; slot < inventory.SlotCount; slot++)
            {
                var stack = inventory.GetSlot(slot);
                if (stack.IsEmpty || !seen.Add((stack.item, stack.quality))) continue;
                int offer = buyer.OfferFor(stack.item, stack.quality);
                if (offer <= 0) continue;
                int owned = inventory.Count(stack.item, stack.quality);
                selected.TryGetValue((stack.item, stack.quality), out int picked);
                cells.Add(new SellCell
                {
                    item = stack.item, quality = stack.quality, owned = owned, price = offer,
                    selected = Mathf.Clamp(picked, 0, owned),
                });
            }
            if (cells.Count == 0) return;

            int columns = Mathf.Max(1, Mathf.FloorToInt((listWidth + CellSpacing) / (CellWidth + CellSpacing)));
            int gridRows = Mathf.CeilToInt(cells.Count / (float)columns);
            var grid = new GameObject("SellGrid", typeof(RectTransform), typeof(GridLayoutGroup)).GetComponent<RectTransform>();
            grid.SetParent(list, false);
            grid.sizeDelta = new Vector2(listWidth, gridRows * CellHeight + (gridRows - 1) * CellSpacing);
            var layout = grid.GetComponent<GridLayoutGroup>();
            layout.cellSize = new Vector2(CellWidth, CellHeight);
            layout.spacing = new Vector2(CellSpacing, CellSpacing);
            layout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            layout.constraintCount = columns;

            foreach (var cell in cells) BuildCell(cell, grid);
        }

        private void BuildCell(SellCell cell, RectTransform grid)
        {
            var root = new GameObject(cell.item.name, typeof(RectTransform), typeof(Image));
            root.transform.SetParent(grid, false);
            root.GetComponent<Image>().color = CellColor;

            var iconGo = new GameObject("Icon", typeof(RectTransform), typeof(Image));
            iconGo.transform.SetParent(root.transform, false);
            Place((RectTransform)iconGo.transform, new Vector2(0.5f, 1f), new Vector2(0f, -8f), new Vector2(96f, 96f));
            var icon = iconGo.GetComponent<Image>();
            icon.preserveAspect = true;
            icon.raycastTarget = false;
            if (cell.item.Icon != null) icon.sprite = cell.item.Icon;
            else
            {
                // No art yet: a dark square with the name on it.
                icon.color = new Color(0.4f, 0.35f, 0.3f, 1f);
                var noArt = MakeText(iconGo.transform, cell.item.DisplayName, 13, TextAnchor.MiddleCenter, Color.white);
                Stretch(noArt.rectTransform, 4f);
            }

            var count = MakeText(root.transform, $"x{cell.owned}", 15, TextAnchor.UpperRight, new Color(0.9f, 0.88f, 0.84f, 1f));
            Place(count.rectTransform, new Vector2(1f, 1f), new Vector2(-6f, -4f), new Vector2(60f, 20f));

            string tier = cell.item.HasQuality
                ? $"\n<color=#{ColorUtility.ToHtmlStringRGB(cell.quality.DisplayColor())}>{cell.quality}</color>"
                : "";
            var name = MakeText(root.transform, cell.item.DisplayName + tier, 14, TextAnchor.UpperCenter, new Color(0.9f, 0.88f, 0.84f, 1f));
            Place(name.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -108f), new Vector2(CellWidth - 8f, 36f));
            var price = MakeText(root.transform, $"{cell.price} each", 14, TextAnchor.UpperCenter, new Color(0.69f, 0.66f, 0.56f, 1f));
            Place(price.rectTransform, new Vector2(0.5f, 1f), new Vector2(0f, -146f), new Vector2(CellWidth - 8f, 18f));

            // − [n] +
            const float size = 30f, bottom = 8f;
            var minus = MakeButton(root.transform, "−", () => Step(cell, -1));
            Place(minus, new Vector2(0f, 0f), new Vector2(8f, bottom), new Vector2(size, size), new Vector2(0f, 0f));
            var plus = MakeButton(root.transform, "+", () => Step(cell, 1));
            Place(plus, new Vector2(1f, 0f), new Vector2(-8f, bottom), new Vector2(size, size), new Vector2(1f, 0f));

            var fieldGo = new GameObject("Quantity", typeof(RectTransform), typeof(Image), typeof(InputField));
            fieldGo.transform.SetParent(root.transform, false);
            Place((RectTransform)fieldGo.transform, new Vector2(0.5f, 0f), new Vector2(0f, bottom), new Vector2(CellWidth - 2f * (8f + size + 4f), size), new Vector2(0.5f, 0f));
            fieldGo.GetComponent<Image>().color = new Color(0.07f, 0.07f, 0.07f, 1f);
            var fieldText = MakeText(fieldGo.transform, "", 18, TextAnchor.MiddleCenter, new Color(0.9f, 0.88f, 0.84f, 1f));
            fieldText.supportRichText = false;
            Stretch(fieldText.rectTransform, 2f);
            cell.field = fieldGo.GetComponent<InputField>();
            cell.field.textComponent = fieldText;
            cell.field.contentType = InputField.ContentType.IntegerNumber;
            cell.field.characterLimit = 4;
            cell.field.SetTextWithoutNotify(cell.selected.ToString());
            cell.field.onValueChanged.AddListener(text =>
            {
                // Mid-typing an empty box counts as 0; anything over what's carried snaps to it.
                int.TryParse(text, out int n);
                SetSelected(cell, n, text.Length > 0 && n != Mathf.Clamp(n, 0, cell.owned));
            });
            cell.field.onEndEdit.AddListener(_ => cell.field.SetTextWithoutNotify(cell.selected.ToString()));
        }

        /// <summary>The − and + buttons: one at a time, ten with Shift.</summary>
        private void Step(SellCell cell, int direction)
        {
            bool shift = Keyboard.current != null && Keyboard.current.shiftKey.isPressed;
            SetSelected(cell, cell.selected + direction * (shift ? 10 : 1), true);
        }

        private void SetSelected(SellCell cell, int amount, bool rewriteField)
        {
            cell.selected = Mathf.Clamp(amount, 0, cell.owned);
            selected[(cell.item, cell.quality)] = cell.selected;
            if (rewriteField) cell.field.SetTextWithoutNotify(cell.selected.ToString());
            UpdateSale();
        }

        private int SaleTotal()
        {
            int total = 0;
            foreach (var cell in cells) total += cell.selected * cell.price;
            return total;
        }

        /// <summary>Header, the Sell for total and the Sell button, for what's picked right now.</summary>
        private void UpdateSale()
        {
            int total = SaleTotal();
            bool over = total > buyer.Cash;
            UpdateHeader();
            if (saleLabel != null)
            {
                saleLabel.gameObject.SetActive(buyer.Profile.Buys);
                saleLabel.text = over ? $"Sell for: <color=#{ColorUtility.ToHtmlStringRGB(OverTextColor)}>{total}</color>" : $"Sell for: {total}";
            }
            if (sellButton != null)
            {
                sellButton.gameObject.SetActive(buyer.Profile.Buys);
                sellButton.interactable = total > 0 && !over;
            }
        }

        private void Sell()
        {
            int total = SaleTotal();
            if (total <= 0 || total > buyer.Cash) return;

            // Take the order before selling: each sale changes the inventory and rebuilds the grid.
            var order = new List<(ItemDefinition item, ItemQuality quality, int count)>();
            foreach (var cell in cells)
                if (cell.selected > 0) order.Add((cell.item, cell.quality, cell.selected));
            selected.Clear();

            int sold = 0, earned = 0;
            foreach (var (item, quality, count) in order)
            {
                int paid = buyer.Buy(inventory, item, quality, count, wallet);
                if (paid <= 0) continue;
                sold += count;
                earned += paid;
            }
            statusLabel.text = sold > 0
                ? $"Sold {sold} item{(sold == 1 ? "" : "s")} for {earned} scrip."
                : $"{buyer.NpcName} can't afford that.";
            Refresh();
        }

        private Text MakeText(Transform parent, string text, int size, TextAnchor anchor, Color color)
        {
            var go = new GameObject("Text", typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<Text>();
            t.font = headerLabel.font;
            t.fontSize = size;
            t.alignment = anchor;
            t.color = color;
            t.text = text;
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Truncate;
            return t;
        }

        private RectTransform MakeButton(Transform parent, string label, UnityEngine.Events.UnityAction onClick)
        {
            var go = new GameObject(label, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = new Color(0.3f, 0.28f, 0.24f, 1f);
            go.GetComponent<Button>().onClick.AddListener(onClick);
            var text = MakeText(go.transform, label, 22, TextAnchor.MiddleCenter, new Color(0.9f, 0.88f, 0.84f, 1f));
            Stretch(text.rectTransform, 0f);
            return (RectTransform)go.transform;
        }

        /// <summary>Anchors a rect at one point of its parent, with the pivot there too unless given.</summary>
        private static void Place(RectTransform rect, Vector2 anchor, Vector2 position, Vector2 size, Vector2? pivot = null)
        {
            rect.anchorMin = rect.anchorMax = anchor;
            rect.pivot = pivot ?? anchor;
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
        }

        private static void Stretch(RectTransform rect, float inset)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(inset, inset);
            rect.offsetMax = new Vector2(-inset, -inset);
        }

        private void Recruit()
        {
            string refusal = Factions.FactionManager.Instance.Recruit(buyer.Profile.NpcType, wallet);
            if (refusal != null)
            {
                statusLabel.text = refusal;
                Refresh();
                return;
            }
            var recruit = buyer;
            Close();
            recruit.JoinPlayerGang();
        }

        private void Buy(int index)
        {
            var s = vendor.Stock[index];
            bool five = Keyboard.current != null && Keyboard.current.shiftKey.isPressed;
            int bought = 0, spent = 0;
            for (int n = 0; n < (five ? 5 : 1); n++)
            {
                if (!vendor.SellOne(index, inventory, wallet)) break;
                bought++;
                spent += s.price;
            }
            statusLabel.text = bought > 0
                ? $"Bought {bought} {s.item.DisplayName} for {spent} scrip."
                : inventory.SpaceFor(s.item) <= 0 ? "No room in your inventory." : "Can't buy that.";
            Refresh();
        }
    }
}
