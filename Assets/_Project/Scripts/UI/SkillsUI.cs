using System.Collections.Generic;
using ThePitt.Audio;
using ThePitt.Core;
using ThePitt.Items;
using ThePitt.Progression;
using UnityEngine;
using UnityEngine.UI;

namespace ThePitt.UI
{
    /// <summary>
    /// The Skills tab of the player menu. A card per skill, like the faction cards, with its icon, name, level,
    /// xp bar and what trains it; clicking one opens that skill's tree:
    /// <list type="bullet">
    /// <item>top middle, the skill's name, level and xp bar;</item>
    /// <item>two branches of eight ranks each, production speed and yield (+5% a rank), each with its own generic icon;</item>
    /// <item>the milestones, unlocked at their level, each teaching a recipe (shown with the item it makes);</item>
    /// <item>bottom middle, the points left to spend (one per level).</item>
    /// </list>
    /// Also announces level-ups with a popup.
    /// </summary>
    public class SkillsUI : MonoBehaviour
    {
        private static readonly Color CardColour = new(0.14f, 0.13f, 0.12f, 0.95f);
        private static readonly Color Muted = new(0.69f, 0.66f, 0.56f);
        private static readonly Color TextColour = new(0.94f, 0.91f, 0.85f);
        private static readonly Color Accent = new(1f, 0.66f, 0.3f);
        private static readonly Color Locked = new(0.1f, 0.095f, 0.09f, 1f);

        private class Card
        {
            public Skill skill;
            public Text level, value, total;
            public RectTransform fill;
        }

        private class Node
        {
            public GameObject root;
            public Image background, icon;
            public Outline outline;
            public Text label;
            public Button button;
            public Image link;
        }

        private readonly List<Card> cards = new();
        private RectTransform area, cardsRoot, treeRoot;
        private Font font;

        // The open tree.
        private Skill treeSkill;
        private Text treeName, treeLevel, treeValue, detail, points, speedCaption, yieldCaption;
        private RectTransform treeFill;
        private Image treeAccent;
        private readonly Node[] speedNodes = new Node[Skills.PerkRanks];
        private readonly Node[] yieldNodes = new Node[Skills.PerkRanks];
        private readonly Node[] milestoneNodes = new Node[2];
        private readonly Text[] milestoneTitles = new Text[2];
        private readonly Text[] milestoneLevels = new Text[2];

        private static Sprite speedIcon, yieldIcon;

        public bool TreeOpen => treeRoot != null && treeRoot.gameObject.activeSelf;

        private void Awake()
        {
            Skills.LevelledUp += OnLevelUp;
        }

        private void OnDestroy()
        {
            Skills.LevelledUp -= OnLevelUp;
            Skills.Gained -= OnGained;
            Skills.TreeChanged -= OnTreeChanged;
        }

        private void OnLevelUp(Skill skill, int level)
        {
            UnlockPopupUI.Notify("SKILL UP", $"{Skills.Name(skill)} · level {level}",
                $"A skill point to spend in its tree. {Skills.ToNext(level)} xp to level {level + 1}.", Skills.Colour(skill), 6f);
        }

        /// <summary>Builds the cards and the tree into the menu's Skills tab. Called by the player menu.</summary>
        public void HostIn(RectTransform tab)
        {
            area = tab;
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            cardsRoot = Fill(NewRect("Cards", area));
            treeRoot = Fill(NewRect("Tree", area));
            const float width = 630f, height = 250f, gapX = 24f, gapY = 21f;
            for (int i = 0; i < Skills.All.Length; i++)
            {
                float x = i % 2 == 0 ? -(width + gapX) / 2f : (width + gapX) / 2f;
                float y = -(i / 2) * (height + gapY);
                cards.Add(BuildCard(Skills.All[i], x, y, width, height));
            }
            BuildTree();
            treeRoot.gameObject.SetActive(false);
            Skills.Gained += OnGained;
            Skills.TreeChanged += OnTreeChanged;
            Refresh();
        }

        private void OnGained(Skill skill, int amount)
        {
            if (area != null && area.gameObject.activeInHierarchy) Refresh();
        }

        private void OnTreeChanged(Skill skill)
        {
            if (area != null && area.gameObject.activeInHierarchy) Refresh();
        }

        /// <summary>Back to the cards.</summary>
        public void ShowCards()
        {
            if (treeRoot == null) return;
            treeRoot.gameObject.SetActive(false);
            cardsRoot.gameObject.SetActive(true);
            Refresh();
        }

        public void OpenTree(Skill skill)
        {
            if (!Skills.Available(skill)) return;
            treeSkill = skill;
            cardsRoot.gameObject.SetActive(false);
            treeRoot.gameObject.SetActive(true);
            detail.text = "Click a node to spend a point on it. Point at one to see what it does.";
            AudioManager.Play2D(AudioManager.Library?.click, 0.4f);
            Refresh();
        }

        public void Refresh()
        {
            foreach (var c in cards)
            {
                if (!Skills.Available(c.skill)) continue;
                int level = Skills.Level(c.skill);
                Skills.Progress(c.skill, out int into, out int needed);
                c.level.text = $"Level {level}";
                c.fill.anchorMax = new Vector2(needed > 0 ? Mathf.Clamp01(into / (float)needed) : 1f, 1f);
                c.value.text = needed > 0 ? $"{into:N0} / {needed:N0} xp" : "Mastered";
                int pts = Skills.Points(c.skill);
                string spare = pts > 0 ? $"   ·   <color=#{Hex(Accent)}>{pts} point{(pts == 1 ? "" : "s")} to spend</color>" : "";
                c.total.text = (level < Skills.MaxLevel ? $"{needed - into:N0} xp to level {level + 1}" : $"{Skills.Xp(c.skill):N0} xp total") + spare;
            }
            if (TreeOpen) RefreshTree();
        }

        // ---------------------------------------------------------------- the tree

        private void RefreshTree()
        {
            var skill = treeSkill;
            var colour = Skills.Colour(skill);
            int level = Skills.Level(skill);
            Skills.Progress(skill, out int into, out int needed);
            treeName.text = Skills.Name(skill).ToUpperInvariant();
            treeName.color = colour;
            treeAccent.color = colour;
            treeLevel.text = level < Skills.MaxLevel ? $"Level {level}   ·   {needed - into:N0} xp to level {level + 1}" : $"Level {level}   ·   mastered";
            treeFill.GetComponent<Image>().color = colour;
            treeFill.anchorMax = new Vector2(needed > 0 ? Mathf.Clamp01(into / (float)needed) : 1f, 1f);
            treeValue.text = needed > 0 ? $"{into:N0} / {needed:N0} xp" : $"{Skills.Xp(skill):N0} xp";

            int pts = Skills.Points(skill);
            points.text = pts.ToString();
            points.color = pts > 0 ? Accent : Muted;

            speedCaption.text = $"PRODUCTION SPEED   <color=#{Hex(TextColour)}>+{Skills.Bonus(skill, Perk.Speed) * 100f:0}%</color>";
            yieldCaption.text = $"YIELD   <color=#{Hex(TextColour)}>+{Skills.Bonus(skill, Perk.Yield) * 100f:0}%</color>";
            RefreshBranch(speedNodes, skill, Perk.Speed, colour);
            RefreshBranch(yieldNodes, skill, Perk.Yield, colour);

            var list = Skills.Milestones(skill);
            for (int i = 0; i < milestoneNodes.Length; i++)
            {
                var node = milestoneNodes[i];
                bool exists = i < list.Length;
                node.root.SetActive(exists);
                milestoneTitles[i].gameObject.SetActive(exists);
                milestoneLevels[i].gameObject.SetActive(exists);
                if (!exists) continue;
                var m = list[i];
                var output = m.Recipe != null ? m.Recipe.Output : null;
                node.icon.sprite = output != null ? output.Icon : null;
                node.icon.enabled = node.icon.sprite != null;
                bool taken = Skills.HasMilestone(skill, i);
                bool reached = level >= m.level;
                Style(node, taken, Skills.CanTakeMilestone(skill, i), reached, colour);
                node.label.text = "";
                milestoneTitles[i].text = m.title;
                milestoneTitles[i].color = taken || reached ? TextColour : Muted;
                milestoneLevels[i].text = taken ? "Learned" : $"Level {m.level}";
                milestoneLevels[i].color = taken ? colour : reached ? Accent : Muted;
            }
        }

        private void RefreshBranch(Node[] nodes, Skill skill, Perk perk, Color colour)
        {
            int rank = Skills.Rank(skill, perk);
            for (int i = 0; i < nodes.Length; i++)
            {
                bool owned = i < rank;
                bool next = i == rank;
                Style(nodes[i], owned, next && Skills.CanBuy(skill, perk), next, colour);
                nodes[i].label.text = $"+{(i + 1) * Skills.PerRank * 100f:0}%";
                nodes[i].label.color = owned ? TextColour : Muted;
                if (nodes[i].link != null) nodes[i].link.color = owned ? colour : new Color(1f, 1f, 1f, 0.12f);
            }
        }

        /// <summary>Owned: filled in the skill's colour. Buyable now: outlined and bright. Otherwise dim.</summary>
        private static void Style(Node node, bool owned, bool buyable, bool nextUp, Color colour)
        {
            node.background.color = owned ? new Color(colour.r * 0.55f, colour.g * 0.55f, colour.b * 0.55f, 1f) : Locked;
            node.outline.enabled = owned || buyable;
            node.outline.effectColor = owned ? colour : Accent;
            node.icon.color = owned || buyable ? Color.white : nextUp ? new Color(1f, 1f, 1f, 0.45f) : new Color(1f, 1f, 1f, 0.2f);
            node.button.interactable = buyable;
        }

        private void BuyPerk(Perk perk)
        {
            if (!Skills.Buy(treeSkill, perk)) return;
            AudioManager.Play2D(AudioManager.Library?.craftDone, 0.5f);
            Describe(perk, Skills.Rank(treeSkill, perk) - 1);
        }

        private void TakeMilestone(int index)
        {
            if (!Skills.TakeMilestone(treeSkill, index)) return;
            AudioManager.Play2D(AudioManager.Library?.craftDone, 0.7f);
            var m = Skills.Milestones(treeSkill)[index];
            UnlockPopupUI.Notify("RECIPE LEARNED", m.title, m.blurb, Skills.Colour(treeSkill), 8f);
            Describe(index);
        }

        private static string PerkEffect(Skill skill, Perk perk, float percent) => (skill, perk) switch
        {
            (Skill.Harvesting, Perk.Speed) => $"Every swing at a scrap heap or corpse pile does {percent:0}% more.",
            (Skill.Harvesting, Perk.Yield) => $"{percent:0}% more from every node.",
            (Skill.Growing, Perk.Speed) => $"Crops grow {percent:0}% faster.",
            (Skill.Growing, Perk.Yield) => $"{percent:0}% bigger harvests.",
            (_, Perk.Speed) => $"{Skills.Name(skill)} recipes are made {percent:0}% faster at every station.",
            _ => $"{percent:0}% more out of every {Skills.Name(skill).ToLowerInvariant()} recipe (any fraction is a chance of one extra).",
        };

        private void Describe(Perk perk, int index)
        {
            int rank = Skills.Rank(treeSkill, perk);
            float percent = (index + 1) * Skills.PerRank * 100f;
            string name = perk == Perk.Speed ? "Production speed" : "Yield";
            string state = index < rank ? "Bought." : index == rank ? (Skills.Points(treeSkill) > 0 ? "Click to buy: 1 point." : "Needs a skill point.") : "Buy the ranks before it first.";
            detail.text = $"<b>{name} {Roman(index + 1)}</b>   {PerkEffect(treeSkill, perk, percent)}   <color=#{Hex(Muted)}>{state}</color>";
        }

        private void Describe(int milestone)
        {
            var list = Skills.Milestones(treeSkill);
            if (milestone >= list.Length) return;
            var m = list[milestone];
            string state = Skills.HasMilestone(treeSkill, milestone) ? "Learned."
                : Skills.Level(treeSkill) < m.level ? $"Unlocks at level {m.level}."
                : Skills.Points(treeSkill) > 0 ? "Click to learn: 1 point." : "Needs a skill point.";
            detail.text = $"<b>{m.title}</b>   {m.blurb}   <color=#{Hex(Muted)}>{state}</color>";
        }

        private static string Roman(int n) => n switch { 1 => "I", 2 => "II", 3 => "III", 4 => "IV", 5 => "V", 6 => "VI", 7 => "VII", _ => "VIII" };

        private void BuildTree()
        {
            // Back to the cards, top left.
            var back = NewRect("Back", treeRoot);
            Place(back, -547f, -4f, 190f, 44f);
            MakeButton(back, "◀  All skills", ShowCards, 20);

            // Top middle: name, level and the xp bar.
            treeName = NewCentred(treeRoot, "Name", 34, FontStyle.Bold, 0f, -2f, 700f, 42f);
            treeLevel = NewCentred(treeRoot, "Level", 19, FontStyle.Normal, 0f, -44f, 700f, 26f);
            treeLevel.color = Muted;
            var track = NewRect("XpBar", treeRoot);
            Place(track, 0f, -76f, 620f, 26f);
            track.gameObject.AddComponent<Image>().color = new Color(0.2f, 0.19f, 0.18f, 1f);
            treeFill = NewRect("Fill", track);
            treeFill.anchorMin = Vector2.zero;
            treeFill.anchorMax = new Vector2(0f, 1f);
            treeFill.offsetMin = treeFill.offsetMax = Vector2.zero;
            treeFill.gameObject.AddComponent<Image>();
            treeValue = NewCentred(track, "Value", 17, FontStyle.Bold, 0f, 0f, 620f, 26f);
            Fill(treeValue.rectTransform);
            treeValue.gameObject.AddComponent<Shadow>().effectColor = new Color(0f, 0f, 0f, 0.8f);
            var rule = NewRect("Rule", treeRoot);
            Place(rule, 0f, -118f, 1200f, 2f);
            treeAccent = rule.gameObject.AddComponent<Image>();

            speedCaption = NewLeft(treeRoot, "SpeedCaption", 18, -600f, -138f, 600f, 26f);
            speedCaption.color = Muted;
            BuildBranch(speedNodes, Perk.Speed, -172f);
            yieldCaption = NewLeft(treeRoot, "YieldCaption", 18, -600f, -318f, 600f, 26f);
            yieldCaption.color = Muted;
            BuildBranch(yieldNodes, Perk.Yield, -352f);

            var mc = NewLeft(treeRoot, "MilestoneCaption", 18, -600f, -498f, 700f, 26f);
            mc.text = "MILESTONES   <color=#B8B2A8>a recipe nobody else can teach you</color>";
            mc.color = Muted;
            var milestoneLink = NewRect("MilestoneLink", treeRoot);
            Place(milestoneLink, 0f, -578f, 440f, 4f);
            milestoneLink.gameObject.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.12f);
            for (int i = 0; i < milestoneNodes.Length; i++)
            {
                int index = i;
                float x = i == 0 ? -220f : 220f;
                milestoneNodes[i] = BuildNode(treeRoot, x, -532f, 96f, () => TakeMilestone(index), () => Describe(index), null);
                // The item fills its tile: these icons have their own margin.
                milestoneNodes[i].icon.rectTransform.offsetMin = new Vector2(2f, 2f);
                milestoneNodes[i].icon.rectTransform.offsetMax = new Vector2(-2f, -2f);
                milestoneTitles[i] = NewCentred(treeRoot, "MilestoneTitle", 19, FontStyle.Bold, x, -634f, 300f, 26f);
                milestoneLevels[i] = NewCentred(treeRoot, "MilestoneLevel", 16, FontStyle.Normal, x, -660f, 300f, 22f);
            }

            // What the pointed-at node does.
            var detailBox = NewRect("Detail", treeRoot);
            Place(detailBox, 0f, -694f, 1220f, 56f);
            detailBox.gameObject.AddComponent<Image>().color = new Color(0.1f, 0.095f, 0.09f, 0.9f);
            detail = NewCentred(detailBox, "Text", 17, FontStyle.Normal, 0f, 0f, 1200f, 56f);
            Fill(detail.rectTransform);
            detail.horizontalOverflow = HorizontalWrapMode.Wrap;

            // Bottom middle: points to spend.
            var box = NewRect("Points", treeRoot);
            box.anchorMin = box.anchorMax = box.pivot = new Vector2(0.5f, 0f);
            box.anchoredPosition = Vector2.zero;
            box.sizeDelta = new Vector2(300f, 76f);
            box.gameObject.AddComponent<Image>().color = new Color(0.08f, 0.075f, 0.07f, 1f);
            box.gameObject.AddComponent<Outline>().effectColor = new Color(Accent.r, Accent.g, Accent.b, 0.8f);
            var caption = NewText(box, "Caption", "SKILL POINTS\n<size=13>one per level</size>", 15, TextAnchor.MiddleLeft, 18f, -10f, 180f, 56f);
            caption.color = Muted;
            caption.fontStyle = FontStyle.Bold;
            points = NewCentred(box, "Value", 44, FontStyle.Bold, 0f, 0f, 100f, 76f);
            var pr = points.rectTransform;
            pr.anchorMin = pr.anchorMax = pr.pivot = new Vector2(1f, 0.5f);
            pr.anchoredPosition = new Vector2(-12f, 0f);
            pr.sizeDelta = new Vector2(100f, 76f);
        }

        private void BuildBranch(Node[] nodes, Perk perk, float y)
        {
            const float spacing = 150f, size = 84f;
            float start = -spacing * (nodes.Length - 1) / 2f;
            for (int i = 0; i < nodes.Length; i++)
            {
                int index = i;
                float x = start + i * spacing;
                Image link = null;
                if (i > 0)
                {
                    // The line from the node before.
                    var l = NewRect("Link", treeRoot);
                    Place(l, x - spacing / 2f, y - size / 2f + 2f, spacing - size, 4f);
                    link = l.gameObject.AddComponent<Image>();
                    link.raycastTarget = false;
                }
                nodes[i] = BuildNode(treeRoot, x, y, size, () => BuyPerk(perk), () => Describe(perk, index), perk == Perk.Speed ? SpeedIcon() : YieldIcon());
                nodes[i].link = link;
            }
        }

        private Node BuildNode(Transform parent, float x, float y, float size, UnityEngine.Events.UnityAction onClick, System.Action onHover, Sprite icon)
        {
            var node = new Node();
            var rt = NewRect("Node", parent);
            Place(rt, x, y, size, size);
            node.root = rt.gameObject;
            node.background = rt.gameObject.AddComponent<Image>();
            node.outline = rt.gameObject.AddComponent<Outline>();
            node.outline.effectDistance = new Vector2(3f, -3f);
            node.button = rt.gameObject.AddComponent<Button>();
            node.button.targetGraphic = node.background;
            var colours = node.button.colors;
            colours.highlightedColor = new Color(1.4f, 1.4f, 1.4f);
            colours.disabledColor = Color.white;
            node.button.colors = colours;
            node.button.onClick.AddListener(onClick);
            rt.gameObject.AddComponent<HoverRelay>().Entered = onHover;
            var ic = NewRect("Icon", rt);
            ic.anchorMin = Vector2.zero;
            ic.anchorMax = Vector2.one;
            ic.offsetMin = new Vector2(12f, 16f);
            ic.offsetMax = new Vector2(-12f, -8f);
            node.icon = ic.gameObject.AddComponent<Image>();
            node.icon.sprite = icon;
            node.icon.preserveAspect = true;
            node.icon.raycastTarget = false;
            node.label = NewCentred(rt, "Label", 14, FontStyle.Bold, 0f, 0f, size, 18f);
            var lr = node.label.rectTransform;
            lr.anchorMin = lr.anchorMax = lr.pivot = new Vector2(0.5f, 0f);
            lr.anchoredPosition = new Vector2(0f, 1f);
            return node;
        }

        // ---------------------------------------------------------------- generic icons

        /// <summary>Two chevrons pointing on: faster.</summary>
        private static Sprite SpeedIcon()
        {
            if (speedIcon != null) return speedIcon;
            speedIcon = Draw((x, y) => Mathf.Min(Chevron(x, y, 0.16f), Chevron(x, y, 0.46f)) < 0.075f);
            return speedIcon;
        }

        private static float Chevron(float x, float y, float left) =>
            Mathf.Min(Segment(x, y, left, 0.18f, left + 0.3f, 0.5f), Segment(x, y, left + 0.3f, 0.5f, left, 0.82f));

        /// <summary>Three rising bars and a plus: more.</summary>
        private static Sprite YieldIcon()
        {
            if (yieldIcon != null) return yieldIcon;
            yieldIcon = Draw((x, y) =>
            {
                bool bars = (x > 0.12f && x < 0.3f && y > 0.12f && y < 0.42f) ||
                            (x > 0.38f && x < 0.56f && y > 0.12f && y < 0.6f) ||
                            (x > 0.64f && x < 0.82f && y > 0.12f && y < 0.78f);
                bool plus = (Mathf.Abs(x - 0.25f) < 0.045f && Mathf.Abs(y - 0.74f) < 0.14f) ||
                            (Mathf.Abs(y - 0.74f) < 0.045f && Mathf.Abs(x - 0.25f) < 0.14f);
                return bars || plus;
            });
            return yieldIcon;
        }

        private static float Segment(float px, float py, float ax, float ay, float bx, float by)
        {
            var p = new Vector2(px, py);
            var a = new Vector2(ax, ay);
            var ab = new Vector2(bx, by) - a;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
            return Vector2.Distance(p, a + ab * t);
        }

        /// <summary>A white-on-clear 128px sprite of whatever shape <paramref name="inside"/> describes (0-1 coordinates), smoothed.</summary>
        private static Sprite Draw(System.Func<float, float, bool> inside)
        {
            const int size = 128, samples = 3;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int hits = 0;
                for (int sy = 0; sy < samples; sy++)
                for (int sx = 0; sx < samples; sx++)
                    if (inside((x + (sx + 0.5f) / samples) / size, (y + (sy + 0.5f) / samples) / size)) hits++;
                px[y * size + x] = new Color(1f, 1f, 1f, hits / (float)(samples * samples));
            }
            tex.SetPixels(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        }

        // ---------------------------------------------------------------- cards

        private static string Source(Skill skill) => skill switch
        {
            Skill.Harvesting => "Mining scrap heaps and digging through corpse piles.",
            Skill.Weaponsmithing => "Forging parts, and making weapons and ammunition.",
            Skill.Armoursmithing => "Nothing to make yet. Armour is coming later.",
            Skill.Cooking => "Cooking at a stove.",
            Skill.DrugProduction => "Cooking drugs at a drug lab.",
            _ => "Planting and harvesting crops. Well-kept plants teach more.",
        };

        private static string IconItem(Skill skill) => skill switch
        {
            Skill.Harvesting => "Item_ScrapPickaxe",
            Skill.Weaponsmithing => "Item_Scattergun",
            Skill.Armoursmithing => "Item_SlagIngot",
            Skill.Cooking => "Item_PitStew",
            Skill.DrugProduction => "Item_Grey",
            _ => "Item_Pitcap",
        };

        private Card BuildCard(Skill skill, float x, float y, float width, float height)
        {
            bool available = Skills.Available(skill);
            var colour = Skills.Colour(skill);
            var card = new Card { skill = skill };
            var box = NewRect(Skills.Name(skill), cardsRoot);
            Place(box, x, y, width, height);
            var bg = box.gameObject.AddComponent<Image>();
            bg.color = CardColour;
            if (available)
            {
                // The whole card opens the skill's tree.
                var button = box.gameObject.AddComponent<Button>();
                button.targetGraphic = bg;
                var colours = button.colors;
                colours.highlightedColor = new Color(1.25f, 1.22f, 1.18f);
                colours.pressedColor = new Color(0.9f, 0.9f, 0.9f);
                button.colors = colours;
                button.onClick.AddListener(() => OpenTree(skill));
            }
            var edge = NewRect("Accent", box);
            edge.anchorMin = Vector2.zero;
            edge.anchorMax = new Vector2(0f, 1f);
            edge.pivot = new Vector2(0f, 0.5f);
            edge.sizeDelta = new Vector2(6f, 0f);
            var edgeImage = edge.gameObject.AddComponent<Image>();
            edgeImage.color = available ? colour : new Color(colour.r, colour.g, colour.b, 0.35f);
            edgeImage.raycastTarget = false;

            // The icon on a dark tile, on the left.
            var tile = NewRect("IconTile", box);
            tile.anchorMin = tile.anchorMax = tile.pivot = new Vector2(0f, 1f);
            tile.anchoredPosition = new Vector2(24f, -24f);
            tile.sizeDelta = new Vector2(150f, 150f);
            var tileImage = tile.gameObject.AddComponent<Image>();
            tileImage.color = new Color(0.08f, 0.075f, 0.07f, 1f);
            tileImage.raycastTarget = false;
            var icon = NewRect("Icon", tile);
            icon.anchorMin = Vector2.zero;
            icon.anchorMax = Vector2.one;
            icon.offsetMin = new Vector2(10f, 10f);
            icon.offsetMax = new Vector2(-10f, -10f);
            var image = icon.gameObject.AddComponent<Image>();
            var item = SaveRegistry.Lookup<ItemDefinition>(IconItem(skill));
            image.sprite = item != null ? item.Icon : null;
            image.preserveAspect = true;
            image.raycastTarget = false;
            image.color = available ? Color.white : new Color(0.45f, 0.45f, 0.45f, 0.6f);
            if (image.sprite == null) image.enabled = false;

            const float left = 198f;
            float right = width - left - 24f;
            var name = NewText(box, "Name", Skills.Name(skill).ToUpperInvariant(), 28, TextAnchor.MiddleLeft, left, -22f, right, 36f);
            name.fontStyle = FontStyle.Bold;
            name.color = available ? colour : Muted;
            card.level = NewText(box, "Level", "", 24, TextAnchor.MiddleRight, left, -22f, right, 36f);

            // The bar: dark track, filled in the skill's colour.
            var track = NewRect("XpBar", box);
            track.anchorMin = track.anchorMax = track.pivot = new Vector2(0f, 1f);
            track.anchoredPosition = new Vector2(left, -74f);
            track.sizeDelta = new Vector2(right, 22f);
            var trackImage = track.gameObject.AddComponent<Image>();
            trackImage.color = new Color(0.05f, 0.05f, 0.05f, 1f);
            trackImage.raycastTarget = false;
            card.fill = NewRect("Fill", track);
            card.fill.anchorMin = Vector2.zero;
            card.fill.anchorMax = new Vector2(0f, 1f);
            card.fill.offsetMin = card.fill.offsetMax = Vector2.zero;
            var fillImage = card.fill.gameObject.AddComponent<Image>();
            fillImage.color = colour;
            fillImage.raycastTarget = false;
            card.value = NewText(track, "Value", available ? "" : "Coming later", 16, TextAnchor.MiddleCenter, 0f, 0f, right, 22f);
            Fill(card.value.rectTransform);
            card.value.fontStyle = FontStyle.Bold;
            if (!available) card.value.color = Muted;
            card.value.gameObject.AddComponent<Shadow>().effectColor = new Color(0f, 0f, 0f, 0.8f);

            card.total = NewText(box, "Total", "", 18, TextAnchor.MiddleLeft, left, -104f, right, 26f);
            card.total.color = Muted;
            var source = NewText(box, "Source", Source(skill) + (available ? $"\n<color=#{Hex(Accent)}>Click to open its skill tree.</color>" : ""),
                18, TextAnchor.UpperLeft, left, -140f, right, 90f);
            source.color = Muted;
            source.horizontalOverflow = HorizontalWrapMode.Wrap;
            return card;
        }

        // ---------------------------------------------------------------- helpers

        private static string Hex(Color c) => ColorUtility.ToHtmlStringRGB(c);

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

        /// <summary>Anchored to the top middle of its parent, x from the middle and y down from the top.</summary>
        private static void Place(RectTransform rt, float x, float y, float width, float height)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(x, y);
            rt.sizeDelta = new Vector2(width, height);
        }

        private Text NewCentred(Transform parent, string name, int size, FontStyle style, float x, float y, float width, float height)
        {
            var rt = NewRect(name, parent);
            Place(rt, x, y, width, height);
            var t = Style(rt.gameObject.AddComponent<Text>(), size, TextAnchor.MiddleCenter);
            t.fontStyle = style;
            return t;
        }

        /// <summary>Left-aligned text whose left edge is x from the middle of its parent.</summary>
        private Text NewLeft(Transform parent, string name, int size, float x, float y, float width, float height)
        {
            var rt = NewRect(name, parent);
            Place(rt, x + width / 2f, y, width, height);
            return Style(rt.gameObject.AddComponent<Text>(), size, TextAnchor.MiddleLeft);
        }

        private Text NewText(Transform parent, string name, string text, int size, TextAnchor anchor, float x, float y, float width, float height)
        {
            var rt = NewRect(name, parent);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, y);
            rt.sizeDelta = new Vector2(width, height);
            var t = Style(rt.gameObject.AddComponent<Text>(), size, anchor);
            t.text = text;
            return t;
        }

        private Text Style(Text t, int size, TextAnchor anchor)
        {
            t.font = font;
            t.fontSize = size;
            t.alignment = anchor;
            t.color = TextColour;
            t.supportRichText = true;
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            return t;
        }

        private void MakeButton(RectTransform rt, string label, UnityEngine.Events.UnityAction onClick, int size)
        {
            var image = rt.gameObject.AddComponent<Image>();
            image.color = new Color(0.22f, 0.21f, 0.2f, 1f);
            var button = rt.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            var colours = button.colors;
            colours.highlightedColor = new Color(1.3f, 1.3f, 1.3f);
            button.colors = colours;
            button.onClick.AddListener(onClick);
            var text = NewCentred(rt, "Label", size, FontStyle.Bold, 0f, 0f, 0f, 0f);
            Fill(text.rectTransform);
            text.text = label;
        }
    }
}
