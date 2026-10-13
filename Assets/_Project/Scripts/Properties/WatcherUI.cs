using System.Collections.Generic;
using ThePitt.Audio;
using ThePitt.Economy;
using ThePitt.Interaction;
using ThePitt.Player;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace ThePitt.Properties
{
    /// <summary>
    /// The Watchers' office. They don't care what goes on in your rooms, only how much water, electricity
    /// and gas it takes. For a consideration they'll raise a property's caps, or permit knocking through
    /// into another room. Each favour costs more than the last. Built at runtime on its own canvas.
    /// </summary>
    public class WatcherUI : MonoBehaviour
    {
        public static WatcherUI Instance { get; private set; }

        [Tooltip("Scrip for the first raise of a utility cap at a property; each further raise costs this much more.")]
        [SerializeField, Min(0)] private int raiseCost = 200;
        [Tooltip("Scrip to permit the first extra room at a property; each further room costs this much more.")]
        [SerializeField, Min(0)] private int expandCost = 800;

        private GameObject panel;
        private RectTransform list;
        private Text status, header;
        private Font font;
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

        public int RaiseCost(Property p, int utility) => raiseCost * (p.Raises[utility] + 1);
        public int ExpandCost(Property p) => expandCost * (p.Expansions + 1);

        public void Open(PlayerInteractor interactor)
        {
            wallet = interactor.GetComponent<PlayerWallet>();
            controller = interactor.GetComponent<FirstPersonController>();
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
            panel.SetActive(false);
            if (controller != null) controller.InputEnabled = true;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

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

        private void Raise(Property p, int utility)
        {
            int cost = RaiseCost(p, utility);
            if (wallet == null || !wallet.TrySpend(cost))
            {
                Say($"That favour costs {cost} scrip.");
                return;
            }
            p.RaiseCap(utility);
            AudioManager.Play2D(AudioManager.Library?.click, 0.5f);
            Say($"The meters at {p.DisplayName} will read a little lower from now on. {Property.UtilityNames[utility]} cap raised to {p.Caps[utility]}.");
            Refresh();
        }

        private void Expand(Property p)
        {
            int cost = ExpandCost(p);
            if (p.Expansions >= p.MaxExpansions)
            {
                Say("There's nowhere left to knock through.");
                return;
            }
            if (wallet == null || !wallet.TrySpend(cost))
            {
                Say($"A permit costs {cost} scrip.");
                return;
            }
            p.Expand();
            AudioManager.Play2D(AudioManager.Library?.craftDone, 0.6f);
            Say($"Permit granted. The wall's coming down at {p.DisplayName}: {p.Rooms} rooms now.");
            Refresh();
        }

        // ---------------------------------------------------------------- refresh

        private void Refresh()
        {
            header.text = $"<b>THE WATCHERS</b>   <size=15><color=#9FB4C8>utilities and permits   ·   you have {wallet?.Money ?? 0} scrip</color></size>";
            for (int i = list.childCount - 1; i >= 0; i--) Destroy(list.GetChild(i).gameObject);

            var owned = new List<Property>();
            foreach (var p in Property.All)
                if (p.Owned) owned.Add(p);
            if (owned.Count == 0)
            {
                var none = NewText(list, "None", "You don't own anything we keep a meter on.", 16, TextAnchor.MiddleCenter);
                Place(none.rectTransform, 0f, -20f, 700f, 30f);
                return;
            }

            float y = 0f;
            foreach (var p in owned)
            {
                var box = NewRect("Property", list);
                Place(box, 0f, y, 720f, 112f);
                box.gameObject.AddComponent<Image>().color = new Color(0.13f, 0.15f, 0.18f, 0.95f);
                var title = NewText(box, "Title", $"<b>{p.DisplayName}</b>   <color=#9FB4C8>{p.District} · {p.Rooms} room{(p.Rooms > 1 ? "s" : "")}</color>", 17, TextAnchor.MiddleLeft);
                Place(title.rectTransform, 0f, -4f, 700f, 24f);

                var used = p.Usage();
                var caps = p.Caps;
                float x = -350f;
                for (int u = 0; u < 3; u++)
                {
                    int utility = u;
                    var label = NewText(box, "Utility", $"{Property.UtilityNames[u]}  <b>{used[u]}/{caps[u]}</b>", 15, TextAnchor.MiddleLeft);
                    Place(label.rectTransform, x + 105f, -34f, 210f, 22f);
                    float bx = x;
                    var b = NewButton(box, ref bx, -60f, 220f, $"Raise cap +{Property.CapStep} · {RaiseCost(p, u)} scrip", () => Raise(p, utility));
                    b.interactable = wallet != null && wallet.Money >= RaiseCost(p, u);
                    x += 235f;
                }

                float ex = -350f;
                bool more = p.Expansions < p.MaxExpansions;
                var expand = NewButton(box, ref ex, -88f, 700f,
                    more ? $"Permit another room ({p.Rooms} of {1 + p.MaxExpansions}) · {ExpandCost(p)} scrip" : "No room left to expand into",
                    () => Expand(p));
                expand.interactable = more && wallet != null && wallet.Money >= ExpandCost(p);
                y -= 120f;
            }
        }

        // ---------------------------------------------------------------- building

        private void Build()
        {
            var canvasGo = new GameObject("WatcherOffice", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 70;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            ThePitt.UI.UIScale.Apply(scaler);

            var rt = NewRect("Panel", canvasGo.transform);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(780f, 780f);
            rt.gameObject.AddComponent<Image>().color = new Color(0.07f, 0.08f, 0.1f, 0.96f);
            panel = rt.gameObject;

            header = NewText(rt, "Header", "", 24, TextAnchor.MiddleLeft);
            Place(header.rectTransform, 0f, -14f, 720f, 36f);
            var about = NewText(rt, "About",
                "<color=#9FB4C8>\"We don't care what you do in there. We care what it draws from the mains. Pay, and the meters will read what we say they read.\"</color>",
                14, TextAnchor.UpperLeft);
            about.horizontalOverflow = HorizontalWrapMode.Wrap;
            Place(about.rectTransform, 0f, -54f, 720f, 40f);

            list = NewRect("List", rt);
            Place(list, 0f, -100f, 720f, 600f);

            status = NewText(rt, "Status", "", 15, TextAnchor.MiddleCenter);
            status.color = new Color(0.8f, 0.9f, 1f);
            status.horizontalOverflow = HorizontalWrapMode.Wrap;
            var sr = status.rectTransform;
            sr.anchorMin = sr.anchorMax = new Vector2(0.5f, 0f);
            sr.pivot = new Vector2(0.5f, 0f);
            sr.anchoredPosition = new Vector2(0f, 52f);
            sr.sizeDelta = new Vector2(720f, 40f);

            var close = NewRect("Close", rt);
            close.anchorMin = close.anchorMax = new Vector2(0.5f, 0f);
            close.pivot = new Vector2(0.5f, 0f);
            close.anchoredPosition = new Vector2(0f, 12f);
            close.sizeDelta = new Vector2(200f, 32f);
            MakeButton(close, "Close (Esc)", Close);
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
            t.color = new Color(0.93f, 0.95f, 0.98f);
            t.supportRichText = true;
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            return t;
        }

        private Button NewButton(Transform parent, ref float left, float y, float width, string label, UnityEngine.Events.UnityAction onClick)
        {
            var rt = NewRect("Button", parent);
            Place(rt, left + width / 2f, y, width, 24f);
            left += width + 5f;
            return MakeButton(rt, label, onClick);
        }

        private Button MakeButton(RectTransform rt, string label, UnityEngine.Events.UnityAction onClick)
        {
            var image = rt.gameObject.AddComponent<Image>();
            image.color = new Color(0.28f, 0.33f, 0.4f, 1f);
            var button = rt.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            var colours = button.colors;
            colours.highlightedColor = new Color(1.3f, 1.3f, 1.3f);
            colours.disabledColor = new Color(0.5f, 0.5f, 0.5f, 0.45f);
            button.colors = colours;
            button.onClick.AddListener(onClick);
            var text = NewText(rt, "Label", label, 13, TextAnchor.MiddleCenter);
            text.rectTransform.anchorMin = Vector2.zero;
            text.rectTransform.anchorMax = Vector2.one;
            text.rectTransform.offsetMin = text.rectTransform.offsetMax = Vector2.zero;
            return button;
        }
    }
}
