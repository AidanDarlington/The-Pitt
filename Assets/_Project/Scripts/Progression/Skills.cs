using System;
using System.Collections.Generic;
using ThePitt.Items;
using ThePitt.Production;
using UnityEngine;

namespace ThePitt.Progression
{
    public enum Skill
    {
        Harvesting,
        Weaponsmithing,
        /// <summary>Nothing trains it yet: there's no armour to make.</summary>
        Armoursmithing,
        Cooking,
        DrugProduction,
        Growing,
    }

    /// <summary>The two general upgrades every skill's tree has.</summary>
    public enum Perk
    {
        /// <summary>Work goes faster: crafts take less time, crops grow sooner, nodes come apart quicker.</summary>
        Speed,
        /// <summary>More comes out of it: a chance of extra output on every craft, crop and haul.</summary>
        Yield,
    }

    /// <summary>A milestone in a skill's tree: from this level a point teaches a recipe.</summary>
    public struct Milestone
    {
        public int level;
        public string recipe;
        public string title;
        public string blurb;

        public Milestone(int level, string recipe, string title, string blurb)
        {
            this.level = level;
            this.recipe = recipe;
            this.title = title;
            this.blurb = blurb;
        }

        public RecipeDefinition Recipe => Core.SaveRegistry.Lookup<RecipeDefinition>(recipe);
    }

    /// <summary>
    /// The player's skills: experience earned by doing each kind of work, and the level it adds up to. Each
    /// level takes a little more than the last (50 xp for level 2, then 30 more per level), up to <see cref="MaxLevel"/>.
    /// <list type="bullet">
    /// <item>Harvesting: 2 xp for each item taken from any node, 3 for the rarer extras, 10 more for bringing it down.</item>
    /// <item>Crafting: 5 xp per item made plus 1 per 3 seconds it takes, to the skill of the station (forge and firearms
    /// bench: weaponsmithing; stove: cooking; drug lab: drug production) or, at a workbench or by hand, of the item (weapons).</item>
    /// <item>Growing: 3 xp for planting, and for each crop harvested 3 xp more the better it was kept (3 Poor to 11 Premium).</item>
    /// </list>
    /// Every level gives a point to spend in that skill's tree: ranks of speed and yield (+5% each, up to 8 of
    /// each), and at milestone levels a recipe nobody else can teach: a better tool or dish, or an upgraded station.
    /// </summary>
    public static class Skills
    {
        public const int MaxLevel = 50;
        public const int PerkRanks = 8;
        public const float PerRank = 0.05f;

        private static readonly int Count = Enum.GetValues(typeof(Skill)).Length;
        private static readonly int[] xp = new int[Count];
        private static readonly int[,] ranks = new int[Count, 2];
        private static readonly bool[,] milestonesTaken = new bool[Count, 2];

        /// <summary>A point was spent in a skill's tree.</summary>
        public static event Action<Skill> TreeChanged;

        private static readonly Milestone[] None = Array.Empty<Milestone>();

        public static Milestone[] Milestones(Skill skill) => skill switch
        {
            Skill.Harvesting => new[]
            {
                new Milestone(10, "Recipe_ReinforcedPickaxe", "Reinforced Pickaxe", "A steel-capped pickaxe that tears into scrap heaps. Made at the workbench from your scrap pickaxe."),
                new Milestone(25, "Recipe_ReinforcedShovel", "Reinforced Shovel", "A steel-edged shovel that cuts through corpse piles. Made at the workbench from your scrap shovel."),
            },
            Skill.Weaponsmithing => new[]
            {
                new Milestone(10, "Recipe_HeavyRebarClub", "Heavy Rebar Club", "A rebar club weighted with slag: hits half again as hard. Made at the workbench."),
                new Milestone(25, "Recipe_BuildReinforcedForge", "Reinforced Forge", "An upgraded forge that works half again as fast. Made at the workbench from a forge."),
            },
            Skill.Cooking => new[]
            {
                new Milestone(10, "Recipe_HeartyStew", "Hearty Stew", "A thick, meaty stew: fills you up and sells for far more. Cooked at the stove."),
                new Milestone(25, "Recipe_BuildRangeStove", "Range Stove", "An upgraded stove that cooks half again as fast. Made at the workbench from a stove."),
            },
            Skill.DrugProduction => new[]
            {
                new Milestone(10, "Recipe_GreyPrime", "Grey Prime", "Grey cut cleaner: a longer, stronger high that sells for much more. Made at the drug lab."),
                new Milestone(25, "Recipe_BuildRefinedDrugLab", "Refined Drug Lab", "An upgraded drug lab that works half again as fast. Made at the workbench from a drug lab."),
            },
            Skill.Growing => new[]
            {
                new Milestone(10, "Recipe_BuildTendedGrowVat", "Tended Grow Vat", "An upgraded grow vat: crops grow 30% faster. Made at the workbench from a grow vat."),
                new Milestone(25, "Recipe_BuildHydroVat", "Hydro Vat", "The best vat there is: crops grow 60% faster, and one more on every harvest. Made at the workbench from a tended grow vat."),
            },
            _ => None,
        };

        public static readonly Skill[] All = (Skill[])Enum.GetValues(typeof(Skill));

        /// <summary>Experience was earned: the skill and how much.</summary>
        public static event Action<Skill, int> Gained;
        /// <summary>A skill reached a new level.</summary>
        public static event Action<Skill, int> LevelledUp;

        // Statics survive between play sessions when domain reload is off in the editor.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Array.Clear(xp, 0, xp.Length);
            Array.Clear(ranks, 0, ranks.Length);
            Array.Clear(milestonesTaken, 0, milestonesTaken.Length);
        }

        public static string Name(Skill skill) => skill switch
        {
            Skill.DrugProduction => "Drug Production",
            _ => skill.ToString(),
        };

        /// <summary>Whether anything in the game trains it yet.</summary>
        public static bool Available(Skill skill) => skill != Skill.Armoursmithing;

        public static Color Colour(Skill skill) => skill switch
        {
            Skill.Harvesting => new Color(0.85f, 0.62f, 0.32f),
            Skill.Weaponsmithing => new Color(0.86f, 0.36f, 0.28f),
            Skill.Armoursmithing => new Color(0.55f, 0.6f, 0.66f),
            Skill.Cooking => new Color(0.95f, 0.78f, 0.32f),
            Skill.DrugProduction => new Color(0.68f, 0.45f, 0.95f),
            _ => new Color(0.5f, 0.85f, 0.42f),
        };

        public static int Xp(Skill skill) => xp[(int)skill];

        /// <summary>Experience to go from this level to the next.</summary>
        public static int ToNext(int level) => 50 + 30 * (level - 1);

        public static int Level(Skill skill)
        {
            int level = 1, left = xp[(int)skill];
            while (level < MaxLevel && left >= ToNext(level))
            {
                left -= ToNext(level);
                level++;
            }
            return level;
        }

        /// <summary>Experience into the current level, and how much the level takes (0 at the top).</summary>
        public static void Progress(Skill skill, out int into, out int needed)
        {
            int level = 1;
            into = xp[(int)skill];
            while (level < MaxLevel && into >= ToNext(level))
            {
                into -= ToNext(level);
                level++;
            }
            needed = level >= MaxLevel ? 0 : ToNext(level);
        }

        public static void Add(Skill skill, int amount)
        {
            if (amount <= 0 || !Available(skill)) return;
            int before = Level(skill);
            xp[(int)skill] += amount;
            Gained?.Invoke(skill, amount);
            int after = Level(skill);
            if (after > before) LevelledUp?.Invoke(skill, after);
        }

        // ---------------------------------------------------------------- the tree

        /// <summary>Points earned in a skill: one for every level past the first.</summary>
        public static int PointsEarned(Skill skill) => Level(skill) - 1;

        public static int PointsSpent(Skill skill)
        {
            int i = (int)skill;
            int spent = ranks[i, 0] + ranks[i, 1];
            for (int m = 0; m < 2; m++)
                if (milestonesTaken[i, m]) spent++;
            return spent;
        }

        public static int Points(Skill skill) => Mathf.Max(0, PointsEarned(skill) - PointsSpent(skill));

        public static int Rank(Skill skill, Perk perk) => ranks[(int)skill, (int)perk];

        /// <summary>The bonus from a perk: 0.05 per rank.</summary>
        public static float Bonus(Skill skill, Perk perk) => Rank(skill, perk) * PerRank;

        /// <summary>1 plus the bonus: what speeds and yields are multiplied by.</summary>
        public static float Multiplier(Skill skill, Perk perk) => 1f + Bonus(skill, perk);

        public static bool CanBuy(Skill skill, Perk perk) => Available(skill) && Rank(skill, perk) < PerkRanks && Points(skill) > 0;

        public static bool Buy(Skill skill, Perk perk)
        {
            if (!CanBuy(skill, perk)) return false;
            ranks[(int)skill, (int)perk]++;
            TreeChanged?.Invoke(skill);
            return true;
        }

        public static bool HasMilestone(Skill skill, int index) => index >= 0 && index < 2 && milestonesTaken[(int)skill, index];

        public static bool CanTakeMilestone(Skill skill, int index)
        {
            var list = Milestones(skill);
            return index >= 0 && index < list.Length && !HasMilestone(skill, index) && Level(skill) >= list[index].level && Points(skill) > 0;
        }

        /// <summary>Spends a point on a milestone and teaches its recipe.</summary>
        public static bool TakeMilestone(Skill skill, int index)
        {
            if (!CanTakeMilestone(skill, index)) return false;
            milestonesTaken[(int)skill, index] = true;
            var recipe = Milestones(skill)[index].Recipe;
            if (recipe != null) RecipeBook.Learn(recipe);
            TreeChanged?.Invoke(skill);
            return true;
        }

        /// <summary>
        /// A yield bonus applied to an amount: the whole part always, the rest as a chance. 3 with +20% is 3,
        /// with a 60% chance of 4.
        /// </summary>
        public static int RollYield(Skill skill, int amount)
        {
            if (amount <= 0) return amount;
            float exact = amount * Multiplier(skill, Perk.Yield);
            int whole = Mathf.FloorToInt(exact);
            return whole + (UnityEngine.Random.value < exact - whole ? 1 : 0);
        }

        /// <summary>A recipe's output with the yield bonus of the skill that makes it (none for recipes no skill covers).</summary>
        public static int RollYield(RecipeDefinition recipe, int amount) => For(recipe) is { } skill ? RollYield(skill, amount) : amount;

        /// <summary>How much faster the skill that makes a recipe works it (1 if none does).</summary>
        public static float SpeedMultiplier(RecipeDefinition recipe) => For(recipe) is { } skill ? Multiplier(skill, Perk.Speed) : 1f;

        // ---------------------------------------------------------------- what trains what

        /// <summary>The skill a recipe trains, if any (building stations and plain tools train nothing).</summary>
        public static Skill? For(RecipeDefinition recipe)
        {
            if (recipe == null) return null;
            switch (recipe.Station)
            {
                case CraftingStation.Forge:
                case CraftingStation.FirearmsBench: return Skill.Weaponsmithing;
                case CraftingStation.Stove: return Skill.Cooking;
                case CraftingStation.ChemLab: return Skill.DrugProduction;
            }
            var output = recipe.Output;
            if (output == null) return null;
            return output.Category switch
            {
                ItemCategory.Weapon => Skill.Weaponsmithing,
                ItemCategory.Food => Skill.Cooking,
                ItemCategory.Drug => Skill.DrugProduction,
                _ => null,
            };
        }

        /// <summary>Experience for making a recipe once.</summary>
        public static int XpFor(RecipeDefinition recipe) => 5 + Mathf.FloorToInt(recipe.CraftSeconds / 3f);

        /// <summary>Credits a finished recipe to its skill.</summary>
        public static void Crafted(RecipeDefinition recipe)
        {
            if (For(recipe) is { } skill) Add(skill, XpFor(recipe));
        }

        // ---------------------------------------------------------------- saving

        public static List<SkillSave> Capture()
        {
            var list = new List<SkillSave>();
            foreach (var s in All)
            {
                int i = (int)s;
                var save = new SkillSave { skill = s.ToString(), xp = xp[i], speed = ranks[i, 0], yield = ranks[i, 1] };
                for (int m = 0; m < 2; m++)
                    if (milestonesTaken[i, m]) save.milestones.Add(m);
                list.Add(save);
            }
            return list;
        }

        public static void Restore(List<SkillSave> saved)
        {
            ResetStatics();
            if (saved != null)
                foreach (var s in saved)
                {
                    if (!Enum.TryParse(s.skill, out Skill skill)) continue;
                    int i = (int)skill;
                    xp[i] = Mathf.Max(0, s.xp);
                    ranks[i, 0] = Mathf.Clamp(s.speed, 0, PerkRanks);
                    ranks[i, 1] = Mathf.Clamp(s.yield, 0, PerkRanks);
                    if (s.milestones != null)
                        foreach (int m in s.milestones)
                            if (m >= 0 && m < 2) milestonesTaken[i, m] = true;
                }
            foreach (var s in All) TreeChanged?.Invoke(s);
        }
    }

    [Serializable]
    public class SkillSave
    {
        public string skill;
        public int xp;
        /// <summary>Ranks bought in the speed and yield branches.</summary>
        public int speed, yield;
        /// <summary>Milestones taken (0 and 1).</summary>
        public List<int> milestones = new();
    }
}
