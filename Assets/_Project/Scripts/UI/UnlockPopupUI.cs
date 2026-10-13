using System.Collections.Generic;
using ThePitt.Audio;
using ThePitt.Production;
using UnityEngine;
using UnityEngine.UI;

namespace ThePitt.UI
{
    /// <summary>
    /// Cards in the top-right corner announcing recipes as they become available ("Recipe unlocked:
    /// Workbench"), stacking downward and fading out after a few seconds. Builds its own canvas.
    /// </summary>
    public class UnlockPopupUI : MonoBehaviour
    {
        [SerializeField, Min(0.5f)] private float holdTime = 4.5f;
        [SerializeField, Min(0.05f)] private float fadeTime = 0.6f;
        [SerializeField, Min(1)] private int maxCards = 6;

        private class Card
        {
            public RectTransform rect;
            public CanvasGroup group;
            public float shownAt;
            public float hold;
        }

        private static readonly Color Accent = new(1f, 0.66f, 0.3f);

        public static UnlockPopupUI Instance { get; private set; }

        private readonly List<Card> cards = new();
        private RectTransform column;
        private Font font;

        private void Awake()
        {
            Instance = this;
            Build();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void OnEnable()
        {
            RecipeUnlocks.Unlocked += OnUnlocked;
            RecipeBook.Learnt += OnLearnt;
        }

        private void OnDisable()
        {
            RecipeUnlocks.Unlocked -= OnUnlocked;
            RecipeBook.Learnt -= OnLearnt;
        }

        private void OnUnlocked(RecipeDefinition recipe) => Show("RECIPE UNLOCKED", recipe);

        // A gang recipe learned once its ingredients are already known shows up straight away.
        private void OnLearnt(RecipeDefinition recipe)
        {
            if (RecipeUnlocks.IsUnlocked(recipe)) Show("RECIPE LEARNED", recipe);
        }

        private void Show(string caption, RecipeDefinition recipe)
        {
            if (recipe == null) return;
            AddCard(caption, $"{recipe.DisplayName}  <size=13><color=#9a958c>· {Where(recipe.Station)}</color></size>", null, Accent, holdTime);
            AudioManager.Play2D(AudioManager.Library?.craftDone, 0.45f);
        }

        /// <summary>
        /// Any other announcement in the same corner: a caption, a title line and optional wrapped detail, with
        /// its own accent colour and how long it stays up.
        /// </summary>
        public static void Notify(string caption, string title, string detail, Color accent, float seconds = 8f)
        {
            if (Instance != null) Instance.AddCard(caption, title, detail, accent, seconds);
        }

        private void AddCard(string caption, string title, string detail, Color accent, float hold)
        {
            while (cards.Count >= maxCards)
            {
                Destroy(cards[0].rect.gameObject);
                cards.RemoveAt(0);
            }

            var card = new GameObject("Card", typeof(RectTransform), typeof(Image), typeof(CanvasGroup), typeof(VerticalLayoutGroup));
            var rect = (RectTransform)card.transform;
            rect.SetParent(column, false);
            card.GetComponent<Image>().color = new Color(0.06f, 0.06f, 0.07f, 0.88f);
            var layout = card.GetComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(14, 10, 8, 9);
            layout.spacing = 1f;
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            var bar = new GameObject("Accent", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
            var ar = (RectTransform)bar.transform;
            ar.SetParent(rect, false);
            bar.GetComponent<LayoutElement>().ignoreLayout = true;
            ar.anchorMin = new Vector2(0f, 0f);
            ar.anchorMax = new Vector2(0f, 1f);
            ar.pivot = new Vector2(0f, 0.5f);
            ar.sizeDelta = new Vector2(4f, 0f);
            bar.GetComponent<Image>().color = accent;

            Label(rect, caption, 12, accent, FontStyle.Bold);
            Label(rect, title, 18, new Color(0.92f, 0.9f, 0.86f), FontStyle.Normal);
            if (!string.IsNullOrEmpty(detail)) Label(rect, detail, 14, new Color(0.72f, 0.69f, 0.64f), FontStyle.Normal);

            cards.Add(new Card { rect = rect, group = card.GetComponent<CanvasGroup>(), shownAt = Time.unscaledTime, hold = hold });
        }

        private static string Where(CraftingStation station) => station switch
        {
            CraftingStation.Hand => "Hand crafted",
            CraftingStation.Workbench => "Workbench",
            CraftingStation.ChemLab => "Drug Lab",
            CraftingStation.Stove => "Stove",
            CraftingStation.FirearmsBench => "Firearms Bench",
            CraftingStation.Forge => "Forge",
            _ => station.ToString(),
        };

        private void Update()
        {

            for (int i = cards.Count - 1; i >= 0; i--)
            {
                var c = cards[i];
                float t = Time.unscaledTime - c.shownAt;
                c.group.alpha = t < fadeTime ? t / fadeTime
                    : t < fadeTime + c.hold ? 1f
                    : 1f - (t - fadeTime - c.hold) / fadeTime;
                if (t < fadeTime * 2f + c.hold) continue;
                Destroy(c.rect.gameObject);
                cards.RemoveAt(i);
            }
        }

        private void Label(RectTransform parent, string text, int size, Color color, FontStyle style)
        {
            var go = new GameObject("Text", typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var label = go.GetComponent<Text>();
            label.font = font;
            label.fontSize = size;
            label.fontStyle = style;
            label.color = color;
            label.supportRichText = true;
            label.raycastTarget = false;
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            label.text = text;
        }

        private void Build()
        {
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var canvasGo = new GameObject("UnlockPopups", typeof(Canvas), typeof(CanvasScaler));
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 150;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            UIScale.Apply(scaler);

            var col = new GameObject("Column", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            col.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            column = (RectTransform)col.transform;
            column.SetParent(canvasGo.transform, false);
            column.anchorMin = column.anchorMax = column.pivot = new Vector2(1f, 1f);
            column.anchoredPosition = new Vector2(-24f, -24f);
            column.sizeDelta = new Vector2(340f, 0f);
            var layout = col.GetComponent<VerticalLayoutGroup>();
            layout.spacing = 8f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.childAlignment = TextAnchor.UpperRight;
        }
    }
}
