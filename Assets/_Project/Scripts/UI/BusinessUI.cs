using ThePitt.Audio;
using ThePitt.Economy;
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
    /// Running a business: what it's working on, what's waiting, the goods ready to collect, and buttons to
    /// collect, load scrap (metalworks), pay wages and buy better quality. Built at runtime on its own canvas.
    /// </summary>
    public class BusinessUI : MonoBehaviour
    {
        public static BusinessUI Instance { get; private set; }

        private static readonly Color Accent = new(0.95f, 0.72f, 0.3f);
        private static readonly Color Muted = new(0.72f, 0.68f, 0.62f);

        private GameObject panel;
        private Text header, subheader, about, work, goods, status;
        private Button collect, load, pay, upgrade;
        private Text loadLabel, payLabel, upgradeLabel;
        private Font font;

        private Business business;
        private Inventory inventory;
        private PlayerWallet wallet;
        private FirstPersonController controller;
        private float statusUntil;

        public bool IsOpen => panel != null && panel.activeSelf;

        private void Awake()
        {
            Instance = this;
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            Build();
            panel.SetActive(false);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        public void Open(Business target, PlayerInteractor interactor)
        {
            business = target;
            inventory = interactor.Inventory;
            wallet = interactor.GetComponent<PlayerWallet>();
            controller = interactor.GetComponent<FirstPersonController>();
            business.Changed += Refresh;
            if (wallet != null) wallet.Changed += OnMoney;
            panel.SetActive(true);
            if (controller != null) controller.InputEnabled = false;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            status.text = "";
            Refresh();
        }

        public void Close()
        {
            if (!IsOpen) return;
            if (business != null) business.Changed -= Refresh;
            if (wallet != null) wallet.Changed -= OnMoney;
            panel.SetActive(false);
            if (controller != null) controller.InputEnabled = true;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        private void OnMoney(int _) => Refresh();

        private void Update()
        {
            if (!IsOpen) return;
            if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame) Close();
            if (status.text.Length > 0 && Time.unscaledTime > statusUntil) status.text = "";
        }

        private void Say(string message)
        {
            status.text = message;
            statusUntil = Time.unscaledTime + 4f;
        }

        // ---------------------------------------------------------------- actions

        private void Collect()
        {
            int taken = business.Collect(inventory);
            if (taken > 0) AudioManager.Play2D(AudioManager.Library?.click, 0.5f);
            Say(taken > 0 ? $"Took {taken}." + (business.StockCount > 0 ? " No room for the rest." : "")
                : business.StockCount > 0 ? "No room in your inventory." : "Nothing ready yet.");
        }

        private void Load()
        {
            int n = business.Load(inventory);
            if (n > 0) AudioManager.Play2D(AudioManager.Library?.craftDone, 0.5f);
            Say(n > 0 ? $"Tipped {n} scrap metal into the hopper." : "You're not carrying any scrap metal.");
        }

        private void Pay()
        {
            if (business.PayWages(wallet))
            {
                AudioManager.Play2D(AudioManager.Library?.click, 0.5f);
                Say("Wages paid. Back to work.");
            }
            else Say($"Wages are {business.Wages} scrip.");
        }

        private void Upgrade()
        {
            var next = business.NextQuality;
            int cost = business.UpgradeCost;
            if (business.Upgrade(wallet))
            {
                AudioManager.Play2D(AudioManager.Library?.craftDone, 0.7f);
                Say($"Better tools, better hands. Everything from now on comes out {next}.");
            }
            else Say(business.CanUpgrade ? $"That costs {cost} scrip." : "It's as good as it gets.");
        }

        // ---------------------------------------------------------------- refresh

        private void Refresh()
        {
            if (business == null) return;
            var b = business;
            string q = $"<color=#{ColorUtility.ToHtmlStringRGB(b.Quality.DisplayColor())}>{b.Quality}</color>";
            header.text = $"<b>{b.Property.DisplayName.ToUpperInvariant()}</b>";
            subheader.text = $"{q} quality   ·   wages {b.Wages} scrip a day   ·   you have {wallet?.Money ?? 0} scrip";

            about.text = b.Kind == BusinessKind.Morgue
                ? $"The dead come here: one from the streets every {b.HoursPerBody:0} hours, and anyone killed out there once their body is cleared away. " +
                  $"Each is butchered into {b.Yield.x}-{b.Yield.y} {b.Product.DisplayName} over {b.MinutesPerJob} minutes, sometimes with something from their pockets."
                : $"Load scrap metal of any quality into the hopper. Every {b.BatchSize} comes out as {b.Yield.x} refined {b.Product.DisplayName} " +
                  $"after {b.MinutesPerJob} minutes. Tools and parts made from it come out better.";

            string line;
            if (b.Unpaid) line = $"<color=#E0503C>Stopped: the staff haven't been paid ({b.Wages} scrip).</color>";
            else if (b.Full) line = "<color=#E0A040>Stopped: full. Collect what's ready.</color>";
            else if (b.Working) line = $"Working: {b.Progress:P0}" + (b.Kind == BusinessKind.Morgue ? " through a body" : " through a batch");
            else line = b.Kind == BusinessKind.Morgue ? "Idle: waiting for bodies." : $"Idle: needs {b.BatchSize} scrap in the hopper.";
            line += b.Kind == BusinessKind.Morgue
                ? $"\n<color=#B8B2A8>Bodies waiting: {b.Bodies}</color>"
                : $"\n<color=#B8B2A8>In the hopper: {b.HopperCount} scrap metal</color>";
            work.text = line;

            if (b.StockCount == 0) goods.text = "<color=#77736c>Nothing ready yet.</color>";
            else
            {
                var sb = new System.Text.StringBuilder();
                foreach (var s in b.Stock)
                {
                    string quality = s.item.HasQuality ? $" <color=#{ColorUtility.ToHtmlStringRGB(s.quality.DisplayColor())}>({s.quality})</color>" : "";
                    sb.Append($"{s.item.DisplayName}{quality}  ×{s.quantity}\n");
                }
                goods.text = sb.ToString().TrimEnd();
            }

            collect.interactable = b.StockCount > 0;
            load.gameObject.SetActive(b.Kind == BusinessKind.Metalworks);
            int carrying = b.Feedstock != null && inventory != null ? inventory.Count(b.Feedstock) : 0;
            loadLabel.text = $"Load scrap metal ({carrying})";
            load.interactable = carrying > 0;
            pay.gameObject.SetActive(b.Unpaid);
            payLabel.text = $"Pay wages · {b.Wages} scrip";
            pay.interactable = wallet != null && wallet.Money >= b.Wages;
            upgradeLabel.text = b.CanUpgrade ? $"Upgrade to {b.NextQuality} · {b.UpgradeCost} scrip" : "Top quality";
            upgrade.interactable = b.CanUpgrade && wallet != null && wallet.Money >= b.UpgradeCost;
        }

        // ---------------------------------------------------------------- building

        private void Build()
        {
            var canvasGo = new GameObject("BusinessPanel", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 70;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            UIScale.Apply(scaler);

            var rt = NewRect("Panel", canvasGo.transform);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(820f, 640f);
            rt.gameObject.AddComponent<Image>().color = new Color(0.08f, 0.075f, 0.07f, 0.96f);
            panel = rt.gameObject;
            var edge = NewRect("Accent", rt);
            edge.anchorMin = Vector2.zero;
            edge.anchorMax = new Vector2(0f, 1f);
            edge.pivot = new Vector2(0f, 0.5f);
            edge.sizeDelta = new Vector2(5f, 0f);
            edge.gameObject.AddComponent<Image>().color = Accent;

            header = NewText(rt, "Header", "", 24, TextAnchor.MiddleLeft);
            Place(header.rectTransform, 0f, -14f, 760f, 34f);
            subheader = NewText(rt, "Subheader", "", 16, TextAnchor.MiddleLeft);
            subheader.color = new Color(0.72f, 0.7f, 0.66f);
            Place(subheader.rectTransform, 0f, -48f, 760f, 24f);
            about = NewText(rt, "About", "", 15, TextAnchor.UpperLeft);
            about.color = Muted;
            about.horizontalOverflow = HorizontalWrapMode.Wrap;
            Place(about.rectTransform, 0f, -82f, 760f, 52f);

            work = NewText(rt, "Work", "", 18, TextAnchor.UpperLeft);
            Place(work.rectTransform, 0f, -144f, 760f, 52f);

            var caption = NewText(rt, "ReadyCaption", "READY TO COLLECT", 13, TextAnchor.MiddleLeft);
            caption.color = Accent;
            caption.fontStyle = FontStyle.Bold;
            Place(caption.rectTransform, 0f, -206f, 760f, 20f);
            var box = NewRect("Goods", rt);
            Place(box, 0f, -230f, 760f, 194f);
            box.gameObject.AddComponent<Image>().color = new Color(0.14f, 0.13f, 0.12f, 0.95f);
            goods = NewText(box, "Text", "", 18, TextAnchor.UpperLeft);
            goods.rectTransform.anchorMin = Vector2.zero;
            goods.rectTransform.anchorMax = Vector2.one;
            goods.rectTransform.offsetMin = new Vector2(14f, 10f);
            goods.rectTransform.offsetMax = new Vector2(-14f, -10f);

            float x = -380f;
            collect = NewButton(rt, ref x, -440f, 180f, "Collect all", Collect, out _);
            load = NewButton(rt, ref x, -440f, 200f, "Load scrap metal", Load, out loadLabel);
            pay = NewButton(rt, ref x, -440f, 180f, "Pay wages", Pay, out payLabel);
            float ux = -380f;
            upgrade = NewButton(rt, ref ux, -480f, 760f, "Upgrade", Upgrade, out upgradeLabel);

            status = NewText(rt, "Status", "", 16, TextAnchor.MiddleCenter);
            status.color = new Color(1f, 0.88f, 0.7f);
            status.horizontalOverflow = HorizontalWrapMode.Wrap;
            var sr = status.rectTransform;
            sr.anchorMin = sr.anchorMax = new Vector2(0.5f, 0f);
            sr.pivot = new Vector2(0.5f, 0f);
            sr.anchoredPosition = new Vector2(0f, 56f);
            sr.sizeDelta = new Vector2(760f, 40f);

            var close = NewRect("Close", rt);
            close.anchorMin = close.anchorMax = new Vector2(0.5f, 0f);
            close.pivot = new Vector2(0.5f, 0f);
            close.anchoredPosition = new Vector2(0f, 14f);
            close.sizeDelta = new Vector2(200f, 34f);
            MakeButton(close, "Close (Esc)", Close, out _);
        }

        private static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        private static void Place(RectTransform rt, float x, float y, float width, float height)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(x, y);
            rt.sizeDelta = new Vector2(width, height);
        }

        private Text NewText(Transform parent, string name, string text, int size, TextAnchor anchor)
        {
            var t = NewRect(name, parent).gameObject.AddComponent<Text>();
            t.font = font;
            t.fontSize = size;
            t.alignment = anchor;
            t.text = text;
            t.color = new Color(0.94f, 0.91f, 0.85f);
            t.supportRichText = true;
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            return t;
        }

        private Button NewButton(Transform parent, ref float left, float y, float width, string label, UnityEngine.Events.UnityAction onClick, out Text text)
        {
            var rt = NewRect("Button", parent);
            Place(rt, left + width / 2f, y, width, 32f);
            left += width + 10f;
            return MakeButton(rt, label, onClick, out text);
        }

        private Button MakeButton(RectTransform rt, string label, UnityEngine.Events.UnityAction onClick, out Text text)
        {
            var image = rt.gameObject.AddComponent<Image>();
            image.color = new Color(0.4f, 0.31f, 0.17f, 1f);
            var button = rt.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            var colours = button.colors;
            colours.highlightedColor = new Color(1.3f, 1.3f, 1.3f);
            colours.disabledColor = new Color(0.5f, 0.5f, 0.5f, 0.45f);
            button.colors = colours;
            button.onClick.AddListener(onClick);
            text = NewText(rt, "Label", label, 15, TextAnchor.MiddleCenter);
            text.rectTransform.anchorMin = Vector2.zero;
            text.rectTransform.anchorMax = Vector2.one;
            text.rectTransform.offsetMin = text.rectTransform.offsetMax = Vector2.zero;
            return button;
        }
    }
}
