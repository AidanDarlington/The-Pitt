using System.Collections.Generic;
using ThePitt.Economy;
using ThePitt.Factions;
using ThePitt.Items;
using ThePitt.Production;
using UnityEngine;
using UnityEngine.UI;

namespace ThePitt.UI
{
    /// <summary>
    /// The Gangs panel beside the inventory, in two tabs. Rivals: where the player stands with each gang,
    /// who that gang hates, its daily request, and what can be done about it (tribute, alliance, war or
    /// peace, buying its recipe). Your Gang: founding a gang from an owned property, then its crew, wages,
    /// stance, supplies and recent news. Built at runtime: inside the player menu's Rivals / Your Gang tabs
    /// (see <see cref="PlayerMenuUI"/>), or on its own docked to the right of the inventory panel.
    /// </summary>
    public class GangRelationsUI : MonoBehaviour
    {
        [SerializeField] private InventoryUI inventory;
        [Tooltip("The inventory panel this docks beside (when there's no host).")]
        [SerializeField] private RectTransform inventoryPanel;
        [Tooltip("Optional: fills this area instead (the player menu's tab content), with the menu's own tabs choosing Rivals or Your Gang.")]
        [SerializeField] private RectTransform host;
        [Tooltip("Scales every size and font, for a bigger host.")]
        [SerializeField, Min(0.5f)] private float scale = 1f;
        [SerializeField] private PlayerWallet wallet;
        [SerializeField] private Inventory playerInventory;
        [SerializeField, Min(1)] private int tributeAmount = 100;
        [SerializeField, Min(1)] private int supplyItems = 5;
        [SerializeField] private string[] gangNames =
            { "The Lowborn", "Gutter Saints", "The Debtors", "Pitt Rats", "The Undertow", "Ash Kings", "The Lamplighters", "Rust Orphans" };
        [SerializeField] private Color[] gangColours =
        {
            new(0.95f, 0.85f, 0.3f), new(0.3f, 0.95f, 0.85f), new(1f, 0.55f, 0.75f),
            new(0.95f, 0.95f, 0.95f), new(0.55f, 1f, 0.35f), new(1f, 0.5f, 0.15f),
        };

        private class Row
        {
            public FactionManager.FactionState faction;
            public Text header, details, enemies, recipe, request;
            public Image standingFill;
            public Button tribute, alliance, war, recipeButton, deliver;
            public Text allianceLabel, warLabel, recipeButtonLabel, tributeLabel, deliverLabel;
        }

        private readonly List<Row> rows = new();
        private RectTransform rivalsTab, gangTab, foundView, runView;
        private Button rivalsTabButton, gangTabButton;
        private Text status, foundText, nameLabel, gangHeader, gangCrew, gangNews, foundButtonLabel;
        private Image colourSwatch;
        private Button foundButton, supplyButton;
        private readonly Button[] stanceButtons = new Button[3];
        private Font font;
        private bool wasOpen, showingGang;
        private float statusUntil;
        private int nameIndex, colourIndex;

        private static readonly Color PanelColour = new(0.08f, 0.075f, 0.07f, 0.94f);
        private static readonly Color RowColour = new(0.14f, 0.13f, 0.12f, 0.95f);
        private static readonly Color ButtonColour = new(0.3f, 0.27f, 0.23f, 1f);
        private static readonly Color SelectedColour = new(0.55f, 0.42f, 0.2f, 1f);
        private static readonly Color Muted = new(0.69f, 0.66f, 0.56f);

        private void Start()
        {
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (wallet == null) wallet = FindFirstObjectByType<PlayerWallet>();
            if (playerInventory == null && wallet != null) playerInventory = wallet.GetComponent<Inventory>();
            Build();
            if (FactionManager.Instance != null)
            {
                FactionManager.Instance.RelationsChanged += Refresh;
                FactionManager.Instance.TerritoryChanged += Refresh;
            }
            if (wallet != null) wallet.Changed += OnMoney;
            if (playerInventory != null) playerInventory.Changed += Refresh;
            Properties.Property.OwnershipChanged += OnProperty;
        }

        private void OnDestroy()
        {
            if (FactionManager.Instance != null)
            {
                FactionManager.Instance.RelationsChanged -= Refresh;
                FactionManager.Instance.TerritoryChanged -= Refresh;
            }
            if (wallet != null) wallet.Changed -= OnMoney;
            if (playerInventory != null) playerInventory.Changed -= Refresh;
            Properties.Property.OwnershipChanged -= OnProperty;
        }

        private void OnMoney(int _) => Refresh();
        private void OnProperty(Properties.Property _) => Refresh();

        private void Update()
        {
            bool open = inventory != null && inventory.IsOpen;
            if (open && !wasOpen) Refresh();
            wasOpen = open;
            if (status != null && status.text.Length > 0 && Time.unscaledTime > statusUntil) status.text = "";
        }

        // ---------------------------------------------------------------- actions

        private void Say(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            status.text = message;
            statusUntil = Time.unscaledTime + 5f;
        }

        /// <summary>Hosts its panel in the given area at the given scale. Call before Start.</summary>
        public void HostIn(RectTransform area, float uiScale)
        {
            host = area;
            scale = uiScale;
        }

        /// <summary>Shows Rivals (false) or Your Gang (true): for the player menu's tabs.</summary>
        public void ShowSection(bool gang)
        {
            if (rivalsTab != null) ShowTab(gang);
            else showingGang = gang;
        }

        private void ShowTab(bool gang)
        {
            showingGang = gang;
            rivalsTab.gameObject.SetActive(!gang);
            gangTab.gameObject.SetActive(gang);
            rivalsTabButton.image.color = gang ? ButtonColour : SelectedColour;
            gangTabButton.image.color = gang ? SelectedColour : ButtonColour;
            Refresh();
        }

        private void Tribute(Row row)
        {
            if (FactionManager.Instance.PayTribute(row.faction, wallet, tributeAmount)) Say($"{FactionManager.The(row.faction.Name, true)} take your {tributeAmount} scrip.");
            else Say(row.faction.atWar ? "They won't take your money while you're at war." : "You can't afford that.");
        }

        private void Alliance(Row row)
        {
            var fm = FactionManager.Instance;
            if (row.faction.allied)
            {
                fm.BreakAlliance(row.faction);
                Say($"You walk away from {FactionManager.The(row.faction.Name)}. They won't forget it.");
                return;
            }
            string refusal = fm.ProposeAlliance(row.faction, out string fallout);
            Say(refusal ?? $"{FactionManager.The(row.faction.Name, true)} agree. You're allies now." + (fallout != null ? $" {Capitalise(fallout)} don't like it." : ""));
        }

        private void War(Row row)
        {
            var fm = FactionManager.Instance;
            if (row.faction.atWar)
            {
                int price = fm.PeacePrice(row.faction);
                Say(fm.MakePeace(row.faction, wallet) ? $"Peace with {FactionManager.The(row.faction.Name)}, for {price} scrip." : $"Peace costs {price} scrip.");
                return;
            }
            string pleased = fm.DeclareWar(row.faction);
            Say($"War with {FactionManager.The(row.faction.Name)}." + (pleased != null ? $" {Capitalise(pleased)} approve." : " Watch yourself on their streets."));
        }

        private void Recipe(Row row)
        {
            var recipe = row.faction.definition.SignatureRecipe;
            if (FactionManager.Instance.BuyRecipe(row.faction, wallet)) Say($"You've learned {recipe.DisplayName}. Look for it at the {StationName(recipe.Station)}.");
            else Say("You can't afford that.");
        }

        private void Deliver(Row row)
        {
            int reward = row.faction.requestReward;
            if (FactionManager.Instance.FillRequest(row.faction, playerInventory, wallet)) Say($"Delivered. {FactionManager.The(row.faction.Name, true)} pay {reward} scrip.");
            else Say("You don't have enough of what they want.");
        }

        private void Found()
        {
            string refusal = FactionManager.Instance.FoundGang(gangNames[nameIndex], gangColours[colourIndex], wallet);
            Say(refusal ?? $"{gangNames[nameIndex]} is open for business. Recruit civilians and junkies by talking to them.");
        }

        private void CycleName()
        {
            nameIndex = (nameIndex + 1) % gangNames.Length;
            var fm = FactionManager.Instance;
            if (fm.PlayerGang != null) fm.RenameGang(gangNames[nameIndex]);
            Refresh();
        }

        private void CycleColour()
        {
            colourIndex = (colourIndex + 1) % gangColours.Length;
            Refresh();
        }

        private void Supply() => Say(FactionManager.Instance.SupplyCrew(playerInventory, supplyItems));

        private void Stance(GangStance stance)
        {
            FactionManager.Instance.SetStance(stance);
            Say(stance switch
            {
                GangStance.Hold => "The crew hold what they have.",
                GangStance.Expand => "The crew will claim free ground around your turf.",
                _ => "The crew will push into rival turf. Allies are left alone.",
            });
        }

        // ---------------------------------------------------------------- refresh

        private void Refresh()
        {
            var fm = FactionManager.Instance;
            if (fm == null || rivalsTab == null) return;
            if (showingGang) RefreshGang(fm);
            else RefreshRivals(fm);
        }

        private void RefreshRivals(FactionManager fm)
        {
            foreach (var row in rows)
            {
                var f = row.faction;
                var def = f.definition;
                var relation = fm.RelationTo(f);
                string colour = ColorUtility.ToHtmlStringRGB(def.Color);
                row.header.text = f.eliminated
                    ? $"<color=#{colour}>■</color> <b>{f.Name}</b>   <color=#7A7468>WIPED OUT</color>"
                    : $"<color=#{colour}>■</color> <b>{f.Name}</b>   <color=#{ColorUtility.ToHtmlStringRGB(RelationColour(relation))}>{relation.ToString().ToUpperInvariant()}</color>";

                string trade = relation is Relation.War or Relation.Hostile ? "won't trade" : $"pays ×{fm.TradeMultiplier(f):0.00}";
                row.details.text = $"Standing {f.standing:+0;-0;0}   ·   {fm.ShareOf(f):P0} of the Pitt   ·   {trade}";
                float t = Mathf.InverseLerp(-100f, 100f, f.standing);
                row.standingFill.rectTransform.anchorMax = new Vector2(t, 1f);
                row.standingFill.color = RelationColour(relation);

                var enemy = fm.WorstEnemyOf(f);
                row.enemies.text = enemy != null
                    ? $"Sworn enemy: <color=#{ColorUtility.ToHtmlStringRGB(enemy.definition.Color)}>{enemy.Name}</color> ({fm.Opinion(f, enemy):0})"
                    : "No sworn enemies";

                var recipe = def.SignatureRecipe;
                if (recipe == null) row.recipe.text = "";
                else if (fm.KnowsRecipe(f)) row.recipe.text = $"Recipe: <b>{recipe.DisplayName}</b> <color=#8FD07A>learned</color> · {StationName(recipe.Station)}";
                else row.recipe.text = $"Recipe: <b>{recipe.DisplayName}</b> <color=#{ColorUtility.ToHtmlStringRGB(Muted)}>free at Friendly, or buy it</color>";

                if (f.eliminated || !f.requestOpen) row.request.text = "<color=#7A7468>No request today</color>";
                else
                {
                    int have = playerInventory != null ? FactionManager.CountGoods(playerInventory, f.requestCategory) : 0;
                    row.request.text = $"Wants <b>{f.requestCount} × {Plural(f.requestCategory)}</b> for {f.requestReward} scrip  " +
                                       $"<color=#{(have >= f.requestCount ? "8FD07A" : "C05040")}>(you have {have})</color>";
                }
                row.deliver.gameObject.SetActive(!f.eliminated && f.requestOpen);
                row.deliver.interactable = fm.CanFillRequest(f, playerInventory);

                bool alive = !f.eliminated;
                row.tributeLabel.text = $"Tribute {tributeAmount}";
                row.tribute.interactable = alive && !f.atWar && wallet != null && wallet.Money >= tributeAmount;
                row.allianceLabel.text = f.allied ? "Break alliance" : "Propose alliance";
                row.alliance.interactable = alive && !f.atWar;
                row.warLabel.text = f.atWar ? $"Buy peace {fm.PeacePrice(f)}" : "Declare war";
                row.war.interactable = alive && (!f.atWar || (wallet != null && wallet.Money >= fm.PeacePrice(f)));
                row.recipeButton.gameObject.SetActive(recipe != null && !fm.KnowsRecipe(f));
                row.recipeButtonLabel.text = $"Buy recipe {def.RecipePrice}";
                row.recipeButton.interactable = fm.CanBuyRecipe(f) && wallet != null && wallet.Money >= def.RecipePrice;
            }
        }

        private void RefreshGang(FactionManager fm)
        {
            var gang = fm.PlayerGang;
            foundView.gameObject.SetActive(gang == null);
            runView.gameObject.SetActive(gang != null);
            if (gang == null)
            {
                string home = null;
                foreach (var p in Properties.Property.All)
                    if (p.Owned) home = home == null ? p.DisplayName : $"{home}, {p.DisplayName}";
                foundText.text =
                    "Run your own crew. Your turf can only start from a property you own: it grows out from their doors.\n\n" +
                    (home != null ? $"<color=#8FD07A>✓</color> Run it from: {home}" : "<color=#C05040>✗ You need to own a property first.</color>") +
                    $"\n<color=#{ColorUtility.ToHtmlStringRGB(Muted)}>Every gang will be wary of you once you do.</color>";
                nameLabel.text = gangNames[nameIndex];
                colourSwatch.color = gangColours[colourIndex];
                foundButtonLabel.text = $"Found gang · {fm.FoundingCost} scrip";
                foundButton.interactable = home != null && wallet != null && wallet.Money >= fm.FoundingCost;
                return;
            }

            string colour = ColorUtility.ToHtmlStringRGB(gang.definition.Color);
            gangHeader.text = $"<color=#{colour}>■</color> <b>{gang.Name}</b>\n" +
                              $"<size=14><color=#{ColorUtility.ToHtmlStringRGB(Muted)}>{fm.ShareOf(gang):P0} of the Pitt   ·   power {gang.power:0}</color></size>";
            gangCrew.text = $"Crew: <b>{fm.CrewMembers}</b> (strength {fm.CrewStrength:0.#})   ·   wages {fm.DailyWages} scrip a day\n" +
                            $"<color=#{ColorUtility.ToHtmlStringRGB(Muted)}>Recruit civilians and junkies by talking to them. " +
                            "Supplies (food, drugs, weapons) make the crew stronger; drugs most of all.</color>";
            for (int i = 0; i < stanceButtons.Length; i++)
                stanceButtons[i].image.color = (int)fm.Stance == i ? SelectedColour : ButtonColour;
            supplyButton.interactable = playerInventory != null &&
                                        (FactionManager.CountGoods(playerInventory, ItemCategory.Food) +
                                         FactionManager.CountGoods(playerInventory, ItemCategory.Drug) +
                                         FactionManager.CountGoods(playerInventory, ItemCategory.Weapon)) > 0;
            gangNews.text = fm.News.Count > 0 ? string.Join("\n", fm.News) : "Nothing to report yet.";
        }

        private static string Capitalise(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        private static string Plural(ItemCategory c) => c switch
        {
            ItemCategory.Food => "food",
            ItemCategory.Drug => "drugs",
            ItemCategory.Weapon => "weapons",
            _ => c.ToString().ToLowerInvariant(),
        };

        private static Color RelationColour(Relation r) => r switch
        {
            Relation.War => new Color(0.95f, 0.15f, 0.12f),
            Relation.Hostile => new Color(0.9f, 0.35f, 0.2f),
            Relation.Unfriendly => new Color(0.85f, 0.6f, 0.3f),
            Relation.Friendly => new Color(0.55f, 0.85f, 0.45f),
            Relation.Allied => new Color(0.4f, 0.8f, 1f),
            _ => new Color(0.75f, 0.73f, 0.68f),
        };

        private static string StationName(CraftingStation s) => s switch
        {
            CraftingStation.ChemLab => "Drug Lab",
            CraftingStation.FirearmsBench => "Firearms Bench",
            CraftingStation.Hand => "hand crafting",
            _ => s.ToString(),
        };

        // ---------------------------------------------------------------- building

        private void Build()
        {
            var fm = FactionManager.Instance;
            if ((inventoryPanel == null && host == null) || fm == null) return;

            RectTransform panel;
            if (host != null)
            {
                // Filling the player menu's content area; its tabs pick the section.
                panel = NewRect("GangsPanel", host);
                panel.anchorMin = Vector2.zero;
                panel.anchorMax = Vector2.one;
                panel.offsetMin = panel.offsetMax = Vector2.zero;
            }
            else
            {
                panel = NewRect("GangsPanel", inventoryPanel);
                panel.anchorMin = panel.anchorMax = new Vector2(1f, 1f);
                panel.pivot = new Vector2(0f, 1f);
                panel.anchoredPosition = new Vector2(16f, 0f);
                panel.sizeDelta = new Vector2(460f, 700f);
                panel.gameObject.AddComponent<Image>().color = PanelColour;
            }

            float tx = -220f;
            rivalsTabButton = NewButton(panel, ref tx, -10f, 215f, 30f, out var rl, () => ShowTab(false));
            rl.text = "<b>RIVALS</b>";
            rl.fontSize = Scaled(16);
            gangTabButton = NewButton(panel, ref tx, -10f, 215f, 30f, out var gl, () => ShowTab(true));
            gl.text = "<b>YOUR GANG</b>";
            gl.fontSize = Scaled(16);
            // The player menu has its own tabs.
            rivalsTabButton.gameObject.SetActive(host == null);
            gangTabButton.gameObject.SetActive(host == null);

            rivalsTab = NewRect("Rivals", panel);
            Stretch(rivalsTab, 0f, host != null ? 0f : -48f);
            gangTab = NewRect("YourGang", panel);
            Stretch(gangTab, 0f, host != null ? 0f : -48f);

            BuildRivals(fm);
            BuildGang();

            status = NewText(panel, "Status", "", 14, TextAnchor.MiddleCenter);
            status.color = new Color(0.95f, 0.85f, 0.6f);
            status.horizontalOverflow = HorizontalWrapMode.Wrap;
            var sr = status.rectTransform;
            sr.anchorMin = sr.anchorMax = new Vector2(0.5f, 0f);
            sr.pivot = new Vector2(0.5f, 0f);
            sr.anchoredPosition = new Vector2(0f, 8f * scale);
            sr.sizeDelta = new Vector2(host != null ? 900f : 430f * scale, 44f * scale);
            ShowTab(showingGang);
        }

        private void BuildRivals(FactionManager fm)
        {
            const float height = 148f;
            int index = 0;
            foreach (var f in fm.Factions)
            {
                if (f.IsPlayer) continue;
                var row = new Row { faction = f };
                var box = NewRect("Gang_" + f.definition.name, rivalsTab);
                // In the player menu there's room for two columns.
                if (host != null) Place(box, index % 2 == 0 ? -227f : 227f, -(index / 2) * height, 440f, height - 6f);
                else Place(box, 0f, -index * height, 440f, height - 6f);
                index++;
                box.gameObject.AddComponent<Image>().color = RowColour;

                row.header = NewText(box, "Header", "", 18, TextAnchor.MiddleLeft);
                Place(row.header.rectTransform, 0f, -3f, 420f, 22f);
                row.details = NewText(box, "Details", "", 13, TextAnchor.MiddleLeft);
                row.details.color = Muted;
                Place(row.details.rectTransform, 0f, -25f, 420f, 18f);

                var bar = NewRect("Standing", box);
                Place(bar, 0f, -45f, 420f, 6f);
                bar.gameObject.AddComponent<Image>().color = new Color(0.05f, 0.05f, 0.05f, 1f);
                var fill = NewRect("Fill", bar);
                fill.anchorMin = Vector2.zero;
                fill.anchorMax = new Vector2(0.5f, 1f);
                fill.offsetMin = fill.offsetMax = Vector2.zero;
                row.standingFill = fill.gameObject.AddComponent<Image>();
                var middle = NewRect("Neutral", bar);
                middle.anchorMin = new Vector2(0.5f, 0f);
                middle.anchorMax = new Vector2(0.5f, 1f);
                middle.sizeDelta = new Vector2(2f, 4f) * scale;
                middle.gameObject.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.5f);

                row.enemies = NewText(box, "Enemies", "", 13, TextAnchor.MiddleLeft);
                Place(row.enemies.rectTransform, 0f, -53f, 420f, 18f);
                row.recipe = NewText(box, "Recipe", "", 13, TextAnchor.MiddleLeft);
                Place(row.recipe.rectTransform, 0f, -71f, 420f, 18f);
                row.request = NewText(box, "Request", "", 13, TextAnchor.MiddleLeft);
                Place(row.request.rectTransform, -42f, -89f, 336f, 20f);

                var captured = row;
                float dx = 132f;
                row.deliver = NewButton(box, ref dx, -88f, 78f, 22f, out row.deliverLabel, () => Deliver(captured));
                row.deliverLabel.text = "Deliver";

                float x = -211f;
                row.tribute = NewButton(box, ref x, -114f, 90f, 26f, out row.tributeLabel, () => Tribute(captured));
                row.alliance = NewButton(box, ref x, -114f, 112f, 26f, out row.allianceLabel, () => Alliance(captured));
                row.war = NewButton(box, ref x, -114f, 106f, 26f, out row.warLabel, () => War(captured));
                row.recipeButton = NewButton(box, ref x, -114f, 100f, 26f, out row.recipeButtonLabel, () => Recipe(captured));
                rows.Add(row);
            }
        }

        private void BuildGang()
        {
            // Before founding.
            foundView = NewRect("Found", gangTab);
            Stretch(foundView, 0f, 0f);
            foundText = NewText(foundView, "About", "", 15, TextAnchor.UpperLeft);
            foundText.horizontalOverflow = HorizontalWrapMode.Wrap;
            Place(foundText.rectTransform, 0f, -6f, 420f, 150f);

            var nameRow = NewText(foundView, "NameCaption", "Name", 15, TextAnchor.MiddleLeft);
            Place(nameRow.rectTransform, 0f, -170f, 420f, 26f);
            float nx = -110f;
            NewButton(foundView, ref nx, -170f, 230f, 26f, out nameLabel, CycleName);
            float ax = 125f;
            NewButton(foundView, ref ax, -170f, 85f, 26f, out var nextName, CycleName).name = "NextName";
            nextName.text = "Next ▸";

            var colourRow = NewText(foundView, "ColourCaption", "Colour", 15, TextAnchor.MiddleLeft);
            Place(colourRow.rectTransform, 0f, -206f, 420f, 26f);
            float cx = -110f;
            var swatchButton = NewButton(foundView, ref cx, -206f, 230f, 26f, out _, CycleColour);
            colourSwatch = swatchButton.GetComponent<Image>();
            float bx = 125f;
            NewButton(foundView, ref bx, -206f, 85f, 26f, out var nextColour, CycleColour);
            nextColour.text = "Next ▸";

            float fx = -150f;
            foundButton = NewButton(foundView, ref fx, -256f, 300f, 34f, out foundButtonLabel, Found);
            foundButtonLabel.fontSize = Scaled(15);

            // Once it's running.
            runView = NewRect("Run", gangTab);
            Stretch(runView, 0f, 0f);
            gangHeader = NewText(runView, "Header", "", 20, TextAnchor.UpperLeft);
            Place(gangHeader.rectTransform, 0f, -4f, 420f, 50f);
            float rx = 155f;
            NewButton(runView, ref rx, -6f, 60f, 22f, out var rename, CycleName).name = "Rename";
            rename.text = "Rename";
            gangCrew = NewText(runView, "Crew", "", 14, TextAnchor.UpperLeft);
            gangCrew.horizontalOverflow = HorizontalWrapMode.Wrap;
            Place(gangCrew.rectTransform, 0f, -62f, 420f, 70f);

            var stanceCaption = NewText(runView, "StanceCaption", "Stance on your borders", 14, TextAnchor.MiddleLeft);
            stanceCaption.color = Muted;
            Place(stanceCaption.rectTransform, 0f, -138f, 420f, 20f);
            float sx = -210f;
            string[] stanceNames = { "Hold", "Expand", "Push" };
            for (int i = 0; i < 3; i++)
            {
                var stance = (GangStance)i;
                stanceButtons[i] = NewButton(runView, ref sx, -160f, 136f, 28f, out var label, () => Stance(stance));
                label.text = stanceNames[i];
                label.fontSize = Scaled(14);
            }

            float px = -210f;
            supplyButton = NewButton(runView, ref px, -200f, 420f, 30f, out var supplyLabel, Supply);
            supplyLabel.text = $"Supply the crew ({supplyItems} items: drugs first, then food and weapons)";

            var newsCaption = NewText(runView, "NewsCaption", "Word on the street", 14, TextAnchor.MiddleLeft);
            newsCaption.color = Muted;
            Place(newsCaption.rectTransform, 0f, -246f, 420f, 20f);
            gangNews = NewText(runView, "News", "", 13, TextAnchor.UpperLeft);
            gangNews.horizontalOverflow = HorizontalWrapMode.Wrap;
            Place(gangNews.rectTransform, 0f, -268f, 420f, 300f);
        }

        // ---------------------------------------------------------------- widgets

        private static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        private int Scaled(int size) => Mathf.RoundToInt(size * scale);

        /// <summary>Fills the parent below a top margin.</summary>
        private void Stretch(RectTransform rt, float left, float top)
        {
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.offsetMin = new Vector2(left * scale, 56f * scale);
            rt.offsetMax = new Vector2(-left * scale, top * scale);
        }

        /// <summary>Top-anchored, centred horizontally: x offset from the middle, y down from the top (all scaled).</summary>
        private void Place(RectTransform rt, float x, float y, float width, float height)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(x, y) * scale;
            rt.sizeDelta = new Vector2(width, height) * scale;
        }

        private Text NewText(Transform parent, string name, string text, int size, TextAnchor anchor)
        {
            var t = NewRect(name, parent).gameObject.AddComponent<Text>();
            t.font = font;
            t.fontSize = Scaled(size);
            t.alignment = anchor;
            t.text = text;
            t.color = new Color(0.92f, 0.9f, 0.85f);
            t.supportRichText = true;
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            return t;
        }

        /// <summary>A button whose left edge is at <paramref name="left"/>; advances it past the button.</summary>
        private Button NewButton(Transform parent, ref float left, float y, float width, float height, out Text label, UnityEngine.Events.UnityAction onClick)
        {
            var rt = NewRect("Button", parent);
            Place(rt, left + width / 2f, y, width, height);
            left += width + 5f;
            var image = rt.gameObject.AddComponent<Image>();
            image.color = ButtonColour;
            var button = rt.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            var colours = button.colors;
            colours.highlightedColor = new Color(1.3f, 1.2f, 1f);
            colours.disabledColor = new Color(0.5f, 0.5f, 0.5f, 0.45f);
            button.colors = colours;
            button.onClick.AddListener(onClick);
            label = NewText(rt, "Label", "", 12, TextAnchor.MiddleCenter);
            var lr = label.rectTransform;
            lr.anchorMin = Vector2.zero;
            lr.anchorMax = Vector2.one;
            lr.offsetMin = lr.offsetMax = Vector2.zero;
            return button;
        }
    }
}
