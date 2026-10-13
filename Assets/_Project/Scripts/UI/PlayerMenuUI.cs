using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace ThePitt.UI
{
    /// <summary>
    /// The player menu (Tab or I): the inventory panel at 1.5x, with tabs along the top for Inventory,
    /// Crafting, Skills, Factions and Your Gang, each filling the same block. Lays the panel out and moves the existing
    /// pieces into their tabs when the game starts (the inventory grid, item details and vitals; the hand
    /// crafting list; the gang panel). The scrip card (<see cref="EconomyHUD"/>) sits in the Inventory tab's
    /// bottom-left corner, mirroring the vitals in the bottom-right. While it's open the rest of the HUD
    /// (clock, crosshair, prompts, feed, utilities, hotbar) is hidden; the hotbar's slots are the grid's top row.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public class PlayerMenuUI : MonoBehaviour
    {
        [SerializeField] private InventoryUI inventory;
        [SerializeField] private RectTransform panel;
        [Tooltip("The hand crafting panel, moved into the Crafting tab.")]
        [SerializeField] private RectTransform craftingPanel;
        [SerializeField] private GangRelationsUI gangs;
        [SerializeField] private SkillsUI skills;
        [Tooltip("The health/hunger/thirst bars, moved into the Inventory tab.")]
        [SerializeField] private RectTransform vitals;
        [Tooltip("HUD pieces hidden while the menu is open. Defaults to the crosshair, prompts, feeds and banners.")]
        [SerializeField] private List<GameObject> hideWhileOpen = new();

        [Header("Layout")]
        [SerializeField] private Vector2 panelSize = new(1350f, 960f);
        [Tooltip("Up from the centre of the screen, to clear the hotbar.")]
        [SerializeField] private float panelRaise = 48f;
        [SerializeField] private float gangScale = 1.4f;

        private static readonly string[] TabNames = { "INVENTORY", "CRAFTING", "SKILLS", "FACTIONS", "YOUR GANG" };
        private static readonly Color TabColour = new(0.2f, 0.19f, 0.18f, 0.95f);
        private static readonly Color TabSelected = new(0.55f, 0.42f, 0.2f, 1f);

        private readonly List<Image> tabs = new();
        private RectTransform inventoryTab, craftingTab, skillsTab, gangTab;
        private int current;
        private readonly Dictionary<GameObject, bool> hidden = new();
        private HotbarUI hotbarUI;

        public const float Margin = 33f, TabBarHeight = 56f, ContentTop = 88f;
        /// <summary>Width of the scrip card and the vitals, in the Inventory tab's bottom corners.</summary>
        public const float CornerWidth = 320f;

        private void Awake()
        {
            if (inventory == null) inventory = GetComponent<InventoryUI>();
            if (panel == null) panel = Find<RectTransform>("InventoryPanel");
            if (craftingPanel == null) craftingPanel = Find<RectTransform>("InventoryPanel/CraftingSubPanel");
            if (vitals == null) vitals = Find<RectTransform>("Vitals");
            if (gangs == null) gangs = GetComponent<GangRelationsUI>();
            hotbarUI = FindAnyObjectByType<HotbarUI>();
            if (skills == null) skills = GetComponent<SkillsUI>();
            if (skills == null) skills = gameObject.AddComponent<SkillsUI>();
            if (hideWhileOpen.Count == 0)
                foreach (var name in new[] { "Crosshair", "InteractPrompt", "PickupFeed", "DistrictBanner", "Effects" })
                {
                    var t = transform.Find(name);
                    if (t != null) hideWhileOpen.Add(t.gameObject);
                }
            // The time is only shown in the menu, on the scrip card (EconomyHUD), not on the HUD.
            var clock = transform.Find("Clock");
            if (clock != null) clock.gameObject.SetActive(false);
            if (panel == null) return;

            Layout();
            if (inventory != null) inventory.OpenChanged += OnOpen;
        }

        private void OnDestroy()
        {
            if (inventory != null) inventory.OpenChanged -= OnOpen;
        }

        private T Find<T>(string path) where T : Component
        {
            var t = transform.Find(path);
            return t != null ? t.GetComponent<T>() : null;
        }

        // ---------------------------------------------------------------- open / close

        private void OnOpen(bool open)
        {
            if (hotbarUI != null) hotbarUI.SetVisible(!open);
            if (open)
            {
                hidden.Clear();
                foreach (var go in HiddenPieces())
                {
                    hidden[go] = go.activeSelf;
                    go.SetActive(false);
                }
                // Always opens on the inventory, whichever tab was last looked at.
                Select(0);
            }
            else
            {
                foreach (var pair in hidden)
                    if (pair.Key != null) pair.Key.SetActive(pair.Value);
                hidden.Clear();
            }
        }

        private IEnumerable<GameObject> HiddenPieces()
        {
            foreach (var go in hideWhileOpen)
                if (go != null) yield return go;
            // The weapon label, hit marker and hurt flash live on their own canvas.
            var combat = GameObject.Find("CombatHUD");
            if (combat != null) yield return combat;
        }

        /// <summary>Shows one tab: 0 Inventory, 1 Crafting, 2 Skills, 3 Factions, 4 Your Gang.</summary>
        public void Select(int index)
        {
            current = Mathf.Clamp(index, 0, TabNames.Length - 1);
            inventoryTab.gameObject.SetActive(current == 0);
            craftingTab.gameObject.SetActive(current == 1);
            skillsTab.gameObject.SetActive(current == 2);
            if (current == 2 && skills != null) skills.ShowCards();
            gangTab.gameObject.SetActive(current >= 3);
            if (current >= 3 && gangs != null) gangs.ShowSection(current == 4);
            for (int i = 0; i < tabs.Count; i++) tabs[i].color = i == current ? TabSelected : TabColour;
        }

        // ---------------------------------------------------------------- layout

        private void Layout()
        {
            panel.sizeDelta = panelSize;
            panel.anchoredPosition = new Vector2(0f, panelRaise);
            var title = panel.Find("Title");
            if (title != null) title.gameObject.SetActive(false);

            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            BuildTabBar(font);

            var content = NewRect("Content", panel);
            content.anchorMin = Vector2.zero;
            content.anchorMax = Vector2.one;
            content.offsetMin = new Vector2(Margin, 24f);
            content.offsetMax = new Vector2(-Margin, -ContentTop);

            inventoryTab = Fill(NewRect("InventoryTab", content));
            craftingTab = Fill(NewRect("CraftingTab", content));
            skillsTab = Fill(NewRect("SkillsTab", content));
            gangTab = Fill(NewRect("GangTab", content));

            LayOutInventory();
            LayOutCrafting();
            if (gangs != null) gangs.HostIn(gangTab, gangScale);
            if (skills != null) skills.HostIn(skillsTab);
            Select(0);
        }

        private void BuildTabBar(Font font)
        {
            float width = panelSize.x - Margin * 2f;
            const float gap = 10f;
            float tabWidth = (width - gap * (TabNames.Length - 1)) / TabNames.Length;
            for (int i = 0; i < TabNames.Length; i++)
            {
                var rt = NewRect("Tab_" + TabNames[i], panel);
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
                rt.anchoredPosition = new Vector2(Margin + i * (tabWidth + gap), -16f);
                rt.sizeDelta = new Vector2(tabWidth, TabBarHeight);
                var image = rt.gameObject.AddComponent<Image>();
                image.color = TabColour;
                var button = rt.gameObject.AddComponent<Button>();
                button.targetGraphic = image;
                var colours = button.colors;
                colours.highlightedColor = new Color(1.25f, 1.2f, 1.1f);
                button.colors = colours;
                int index = i;
                button.onClick.AddListener(() => Select(index));
                tabs.Add(image);

                var label = Fill(NewRect("Label", rt)).gameObject.AddComponent<Text>();
                label.font = font;
                label.fontSize = 22;
                label.fontStyle = FontStyle.Bold;
                label.alignment = TextAnchor.MiddleCenter;
                label.color = new Color(0.94f, 0.91f, 0.85f);
                label.raycastTarget = false;
                label.text = TabNames[i];
            }
        }

        /// <summary>The slot grid at 1.5x across the top, item details below it, vitals beside them.</summary>
        private void LayOutInventory()
        {
            var grid = panel.Find("Grid") as RectTransform;
            if (grid != null)
            {
                grid.SetParent(inventoryTab, false);
                grid.anchorMin = grid.anchorMax = grid.pivot = new Vector2(0.5f, 1f);
                grid.anchoredPosition = Vector2.zero;
                var layout = grid.GetComponent<GridLayoutGroup>();
                if (layout != null)
                {
                    layout.cellSize = new Vector2(204f, 144f);
                    layout.spacing = new Vector2(12f, 12f);
                    int columns = layout.constraint == GridLayoutGroup.Constraint.FixedColumnCount ? layout.constraintCount : 6;
                    grid.sizeDelta = new Vector2(columns * 204f + (columns - 1) * 12f, 4 * 144f + 3 * 12f);
                }
            }
            // Slots are made from the template after this; make them bigger too.
            var template = panel.Find("SlotTemplate") as RectTransform;
            if (template != null)
            {
                template.sizeDelta = new Vector2(204f, 144f);
                var label = template.GetComponentInChildren<Text>(true);
                if (label != null) label.fontSize = 24;
            }

            var details = panel.Find("Details") as RectTransform;
            if (details != null)
            {
                details.SetParent(inventoryTab, false);
                // Between the scrip card (bottom-left) and the vitals (bottom-right).
                details.anchorMin = details.anchorMax = details.pivot = Vector2.zero;
                details.anchoredPosition = new Vector2(CornerWidth + 30f, 0f);
                details.sizeDelta = new Vector2(panelSize.x - Margin * 2f - (CornerWidth + 30f) * 2f, 210f);
                var text = details.GetComponent<Text>();
                if (text != null) text.fontSize = 24;
            }

            if (vitals != null)
            {
                vitals.SetParent(inventoryTab, false);
                vitals.anchorMin = vitals.anchorMax = vitals.pivot = new Vector2(1f, 0f);
                vitals.anchoredPosition = new Vector2(0f, 20f);
            }
        }

        /// <summary>Puts the scrip card in the Inventory tab's bottom-left corner, level with the vitals.</summary>
        public void HostScripCard(RectTransform card)
        {
            if (inventoryTab == null) return;
            card.SetParent(inventoryTab, false);
            card.anchorMin = card.anchorMax = card.pivot = Vector2.zero;
            card.anchoredPosition = new Vector2(0f, 20f);
            card.sizeDelta = new Vector2(CornerWidth, card.sizeDelta.y);
        }

        /// <summary>The hand crafting list, full width: its own title goes (the tab names it).</summary>
        private void LayOutCrafting()
        {
            if (craftingPanel == null) return;
            craftingPanel.SetParent(craftingTab, false);
            Fill(craftingPanel);
            var background = craftingPanel.GetComponent<Image>();
            if (background != null) background.color = Color.clear;

            var title = craftingPanel.Find("Title");
            if (title != null) title.gameObject.SetActive(false);
            var hint = craftingPanel.Find("Hint");
            if (hint != null) hint.gameObject.SetActive(false);
            var list = craftingPanel.Find("List") as RectTransform;
            if (list != null)
            {
                list.anchorMin = new Vector2(0f, 0f);
                list.anchorMax = new Vector2(1f, 1f);
                list.pivot = new Vector2(0.5f, 1f);
                list.offsetMin = new Vector2(0f, 100f);
                list.offsetMax = Vector2.zero;
                var template = list.Find("RowTemplate") as RectTransform;
                if (template != null)
                {
                    template.sizeDelta = new Vector2(template.sizeDelta.x, 104f);
                    var label = template.GetComponentInChildren<Text>(true);
                    if (label != null) label.fontSize = 26;
                }
            }
            var status = craftingPanel.Find("Status") as RectTransform;
            if (status != null)
            {
                status.anchorMin = new Vector2(0f, 0f);
                status.anchorMax = new Vector2(1f, 0f);
                status.pivot = new Vector2(0.5f, 0f);
                status.offsetMin = new Vector2(0f, 0f);
                status.offsetMax = new Vector2(0f, 90f);
                var text = status.GetComponent<Text>();
                if (text != null) text.fontSize = 22;
            }
        }

        private static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        private static RectTransform Fill(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            return rt;
        }
    }
}
