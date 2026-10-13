using ThePitt.Interaction;
using ThePitt.Items;
using ThePitt.Player;
using ThePitt.Production;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace ThePitt.UI
{
    /// <summary>
    /// Recipe list for a <see cref="CraftingBench"/>: what's needed, what you have, and a craft button. A
    /// bar under the header shows the bench's queue, collects finished items, and picks how many to queue.
    /// </summary>
    public class CraftingUI : MonoBehaviour
    {
        public static CraftingUI Instance { get; private set; }

        [SerializeField] private GameObject panel;
        [SerializeField] private Text headerLabel;
        [SerializeField] private RectTransform list;
        [Tooltip("Inactive row with a Button, an Image and a child Text.")]
        [SerializeField] private GameObject rowTemplate;
        [SerializeField] private Text detailsLabel;
        [SerializeField] private Text statusLabel;
        [SerializeField] private Button closeButton;
        [SerializeField] private Color haveColor = new Color(0.6f, 0.85f, 0.5f);
        [SerializeField] private Color missingColor = new Color(0.85f, 0.35f, 0.3f);

        private CraftingBench bench;
        private Inventory inventory;
        private FirstPersonController controller;

        private const int MaxQuantity = 50;
        // How many of each recipe to queue. Fresh (x1) every time the panel opens, and back to x1 once queued.
        private readonly System.Collections.Generic.Dictionary<RecipeDefinition, int> quantities = new();
        private Text queueLabel;
        private Button collectButton, cancelButton;

        public bool IsOpen => panel.activeSelf;

        private void Awake()
        {
            Instance = this;
            rowTemplate.SetActive(false);
            panel.SetActive(false);
            if (closeButton != null) closeButton.onClick.AddListener(Close);
            BuildQueueBar();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            if (IsOpen && Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame) Close();
        }

        public void Open(CraftingBench target, PlayerInteractor interactor)
        {
            bench = target;
            inventory = interactor.Inventory;
            controller = interactor.GetComponent<FirstPersonController>();

            panel.SetActive(true);
            if (controller != null) controller.InputEnabled = false;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            quantities.Clear();
            inventory.Changed += Refresh;
            // Only the bar each in-game minute; rebuilding the rows that often would fight the mouse.
            bench.Changed += RefreshQueueBar;
            statusLabel.text = "";
            detailsLabel.text = "";
            Refresh();
        }

        public void Close()
        {
            if (!IsOpen) return;
            panel.SetActive(false);
            inventory.Changed -= Refresh;
            if (bench != null) bench.Changed -= RefreshQueueBar;
            if (controller != null) controller.InputEnabled = true;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            bench = null;
        }

        private string Hex(Color c) => ColorUtility.ToHtmlStringRGB(c);

        private int QuantityOf(RecipeDefinition recipe) => quantities.TryGetValue(recipe, out int q) ? q : 1;

        /// <summary>The most of a recipe that can be queued: what the inventory covers and the queue has room for.</summary>
        private int MaxOf(RecipeDefinition recipe)
        {
            int most = Mathf.Min(MaxQuantity, bench.QueueSpace);
            foreach (var i in recipe.Ingredients)
                if (i.item != null && i.amount > 0) most = Mathf.Min(most, inventory.Count(i.item) / i.amount);
            return Mathf.Max(1, most);
        }

        /// <summary>Ingredients for the chosen quantity: have / need.</summary>
        private string IngredientLine(RecipeDefinition recipe)
        {
            int quantity = QuantityOf(recipe);
            var sb = new System.Text.StringBuilder();
            foreach (var i in recipe.Ingredients)
            {
                if (i.item == null) continue;
                int have = inventory.Count(i.item);
                int need = i.amount * quantity;
                var color = have >= need ? haveColor : missingColor;
                if (sb.Length > 0) sb.Append("  ·  ");
                sb.Append($"{i.item.DisplayName} <color=#{Hex(color)}>{have}/{need}</color>");
            }
            return sb.ToString();
        }

        private void Refresh()
        {
            headerLabel.text = $"<b>{bench.BenchName.ToUpperInvariant()}</b>";
            RefreshQueueBar();

            for (int i = list.childCount - 1; i >= 0; i--)
            {
                var child = list.GetChild(i).gameObject;
                if (child != rowTemplate) Destroy(child);
            }

            // What the player has had every ingredient for, then (greyed out, as "???") what they've found
            // some of the ingredients for.
            foreach (var recipe in RecipeUnlocks.Visible(bench.Recipes))
            {
                var row = Instantiate(rowTemplate, list);
                row.SetActive(true);
                var label = row.GetComponentInChildren<Text>();
                var button = row.GetComponent<Button>();
                if (!RecipeUnlocks.IsUnlocked(recipe))
                {
                    label.text = $"<color=#77736c><b>???</b>\n<size=16>{RecipeUnlocks.MissingText(recipe)}</size></color>";
                    button.interactable = false;
                    continue;
                }

                int quantity = QuantityOf(recipe);
                // Asking for more than can be afforded still queues as many as it can.
                bool canCraft = bench.QueueSpace > 0 && recipe.CanAfford(inventory);
                string amount = recipe.OutputAmount > 1 ? $" x{recipe.OutputAmount}" : "";
                string count = quantity > 1 ? $"<color=#{Hex(haveColor)}>{quantity} × </color>" : "";
                int minutes = bench.MinutesFor(recipe);
                string time = quantity > 1 ? $"{minutes} min each · {minutes * quantity} min" : $"{minutes} min";
                label.text =
                    $"<b>{count}{recipe.DisplayName}{amount}</b>   <size=16>{time}</size>\n" +
                    $"<size=16>{IngredientLine(recipe)}</size>";
                // Leave room for the quantity controls on the right.
                label.rectTransform.offsetMax = new Vector2(label.rectTransform.offsetMax.x - 230f, label.rectTransform.offsetMax.y);

                button.interactable = canCraft;
                var captured = recipe;
                button.onClick.AddListener(() => Craft(captured));
                AddQuantityControls((RectTransform)row.transform, recipe);

                // Hover only: an EventTrigger would also swallow the scroll wheel meant for the list.
                row.AddComponent<HoverRelay>().Entered = () => ShowDetails(captured);
            }
        }

        /// <summary>Min · − · x3 · + · Max on the right of a recipe row. Shift steps by 10.</summary>
        private void AddQuantityControls(RectTransform row, RecipeDefinition recipe)
        {
            var font = headerLabel.font;
            var box = new GameObject("Quantity", typeof(RectTransform)).transform as RectTransform;
            box.SetParent(row, false);
            box.anchorMin = box.anchorMax = box.pivot = new Vector2(1f, 0.5f);
            box.anchoredPosition = new Vector2(-10f, 0f);
            box.sizeDelta = new Vector2(220f, 30f);

            int quantity = QuantityOf(recipe);
            int max = MaxOf(recipe);
            BarButton(box, font, "Min", new Vector2(0f, 0f), 42f, () => SetQuantity(recipe, 1));
            BarButton(box, font, "−", new Vector2(46f, 0f), 28f, () => SetQuantity(recipe, QuantityOf(recipe) - Step));
            var count = BarText(box, font, $"x{quantity}", TextAnchor.MiddleCenter, new Vector2(76f, 0f), new Vector2(40f, 30f), 17);
            count.color = quantity > max ? missingColor : new Color(0.9f, 0.88f, 0.84f);
            BarButton(box, font, "+", new Vector2(118f, 0f), 28f, () => SetQuantity(recipe, QuantityOf(recipe) + Step));
            BarButton(box, font, "Max", new Vector2(150f, 0f), 46f, () => SetQuantity(recipe, MaxOf(recipe)));
        }

        private void ShowDetails(RecipeDefinition recipe)
        {
            var output = recipe.Output;
            string value = output != null ? $"  ·  sells for ~{output.ValueAt(ItemQuality.Standard)}" : "";
            string draws = output != null && output.IsPlaceable ? $"  ·  draws {Properties.Property.DrawText(output.UtilityDraw)}" : "";
            detailsLabel.text = $"<b>{recipe.DisplayName}</b>{value}{draws}\n{recipe.Description}";
        }

        private void Craft(RecipeDefinition recipe)
        {
            int quantity = QuantityOf(recipe);
            int queued = bench.StartCraft(recipe, inventory, quantity);
            if (queued == 0)
            {
                statusLabel.text = bench.QueueSpace == 0 ? "The queue is full." : "Missing ingredients.";
                return;
            }
            string what = queued > 1 ? $"{queued} × {recipe.DisplayName}" : recipe.DisplayName;
            string shortBy = queued < quantity ? $" (only enough for {queued})" : "";
            statusLabel.text = $"Queued {what}{shortBy}.";
            // The next one starts back at x1.
            quantities.Remove(recipe);
            Refresh();
        }

        // ---------------------------------------------------------------- queue bar

        private void SetQuantity(RecipeDefinition recipe, int value)
        {
            quantities[recipe] = Mathf.Clamp(value, 1, MaxQuantity);
            if (bench != null) Refresh();
        }

        /// <summary>Hold Shift to step by 10.</summary>
        private int Step => Keyboard.current != null && Keyboard.current.shiftKey.isPressed ? 10 : 1;

        private void RefreshQueueBar()
        {
            if (bench.IsBusy)
            {
                int minutes = bench.MinutesToClear;
                string left = minutes >= 60 ? $"{minutes / 60}h {minutes % 60:00}m" : $"{minutes} min";
                string waiting = bench.Waiting > 0 ? $" · {bench.Waiting} queued" : "";
                queueLabel.text = $"Crafting <b>{bench.Current.DisplayName}</b> {bench.Progress:P0}{waiting} · {left} left";
            }
            else queueLabel.text = "<color=#8a857c>Idle</color>";

            int ready = 0;
            foreach (var s in bench.Finished) ready += s.quantity;
            collectButton.gameObject.SetActive(ready > 0);
            collectButton.GetComponentInChildren<Text>().text = $"Collect ({ready})";
            cancelButton.gameObject.SetActive(bench.IsBusy);
            cancelButton.GetComponentInChildren<Text>().text = bench.Waiting > 0 ? $"Stop all ({bench.Waiting + 1}) · refund" : "Stop · refund";
        }

        private void BuildQueueBar()
        {
            var font = headerLabel.font;
            var bar = new GameObject("QueueBar", typeof(RectTransform)).transform as RectTransform;
            bar.SetParent(panel.transform, false);
            bar.anchorMin = bar.anchorMax = bar.pivot = new Vector2(0.5f, 1f);
            bar.anchoredPosition = new Vector2(0f, -84f);
            bar.sizeDelta = new Vector2(820f, 32f);
            var hint = panel.transform.Find("Hint");
            if (hint != null && hint.TryGetComponent<Text>(out var hintText))
                hintText.text = "Click a recipe to queue it  ·  set how many on the right";
            LayOutPanel();

            queueLabel = BarText(bar, font, "", TextAnchor.MiddleLeft, new Vector2(0f, 0f), new Vector2(430f, 32f), 17);
            collectButton = BarButton(bar, font, "Collect", new Vector2(440f, 0f), 140f, () =>
            {
                if (bench == null) return;
                int taken = bench.Collect(inventory);
                statusLabel.text = taken > 0 ? $"Collected {taken}." : "No room in your inventory.";
            });
            // Stops the item being made and everything queued behind it, giving their ingredients back.
            cancelButton = BarButton(bar, font, "Stop · refund", new Vector2(600f, 0f), 220f, () =>
            {
                if (bench == null) return;
                int stopped = bench.CancelQueue(inventory);
                statusLabel.text = stopped > 0 ? $"Stopped {stopped} and got the ingredients back." : "Nothing to stop.";
                Refresh();
            });
        }

        /// <summary>
        /// The list scrolls in the middle of the panel; the recipe description and status line sit along the
        /// bottom, left of the Leave button, so nothing overlaps however many recipes there are.
        /// </summary>
        private void LayOutPanel()
        {
            const float top = 124f, bottom = 118f, side = 20f;
            float width = list.sizeDelta.x;

            var viewport = new GameObject("ListViewport", typeof(RectTransform), typeof(RectMask2D), typeof(Image), typeof(ScrollRect)).transform as RectTransform;
            viewport.SetParent(panel.transform, false);
            viewport.SetSiblingIndex(list.GetSiblingIndex());
            viewport.anchorMin = new Vector2(0.5f, 0f);
            viewport.anchorMax = new Vector2(0.5f, 1f);
            viewport.pivot = new Vector2(0.5f, 1f);
            viewport.offsetMin = new Vector2(-width / 2f, bottom);
            viewport.offsetMax = new Vector2(width / 2f, -top);
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

            float leaveLeft = closeButton != null ? ((RectTransform)closeButton.transform).sizeDelta.x + 40f : 160f;
            var details = detailsLabel.rectTransform;
            details.anchorMin = details.anchorMax = details.pivot = Vector2.zero;
            details.anchoredPosition = new Vector2(side, 14f);
            details.sizeDelta = new Vector2(panelWidth - side - leaveLeft, 70f);
            detailsLabel.alignment = TextAnchor.LowerLeft;
            detailsLabel.fontSize = 16;

            var status = statusLabel.rectTransform;
            status.anchorMin = status.anchorMax = status.pivot = Vector2.zero;
            status.anchoredPosition = new Vector2(side, 88f);
            status.sizeDelta = new Vector2(panelWidth - side * 2f, 24f);
            statusLabel.alignment = TextAnchor.MiddleLeft;
            statusLabel.fontSize = 17;
        }

        private float panelWidth => ((RectTransform)panel.transform).sizeDelta.x;

        private static Text BarText(RectTransform bar, Font font, string text, TextAnchor anchor, Vector2 position, Vector2 size, int fontSize)
        {
            var go = new GameObject("Text", typeof(RectTransform), typeof(Text));
            var rt = (RectTransform)go.transform;
            rt.SetParent(bar, false);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 0.5f);
            rt.anchoredPosition = position;
            rt.sizeDelta = size;
            var t = go.GetComponent<Text>();
            t.font = font;
            t.fontSize = fontSize;
            t.alignment = anchor;
            t.color = new Color(0.9f, 0.88f, 0.84f);
            t.supportRichText = true;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.raycastTarget = false;
            t.text = text;
            return t;
        }

        private static Button BarButton(RectTransform bar, Font font, string text, Vector2 position, float width, UnityEngine.Events.UnityAction onClick)
        {
            var go = new GameObject("Button", typeof(RectTransform), typeof(Image), typeof(Button));
            var rt = (RectTransform)go.transform;
            rt.SetParent(bar, false);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 0.5f);
            rt.anchoredPosition = position;
            rt.sizeDelta = new Vector2(width, 30f);
            var image = go.GetComponent<Image>();
            image.color = new Color(0.2f, 0.19f, 0.18f, 0.95f);
            var button = go.GetComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(onClick);
            var label = BarText(rt, font, text, TextAnchor.MiddleCenter, Vector2.zero, new Vector2(width, 30f), 16);
            var lr = (RectTransform)label.transform;
            lr.anchorMin = lr.anchorMax = lr.pivot = new Vector2(0.5f, 0.5f);
            lr.anchoredPosition = Vector2.zero;
            return button;
        }
    }
}
