using System;
using System.Collections.Generic;
using ThePitt.Items;
using UnityEngine;

namespace ThePitt.Production
{
    public enum CraftingStation
    {
        Workbench,
        ChemLab,
        Stove,
        /// <summary>Crafted from the inventory with no station.</summary>
        Hand,
        FirearmsBench,
        Forge,
    }

    /// <summary>Ingredients in, product out, over some in-game time. Create via Assets > Create > The Pitt > Recipe.</summary>
    [CreateAssetMenu(menuName = "The Pitt/Recipe", fileName = "Recipe_")]
    public class RecipeDefinition : ScriptableObject
    {
        [Serializable]
        public struct Ingredient
        {
            public ItemDefinition item;
            [Min(1)] public int amount;
        }

        [SerializeField] private string displayName = "New Recipe";
        [SerializeField, TextArea] private string description;
        [SerializeField] private CraftingStation station = CraftingStation.Workbench;
        [SerializeField] private Ingredient[] ingredients;
        [SerializeField] private ItemDefinition output;
        [SerializeField, Min(1)] private int outputAmount = 1;
        [Tooltip("Real seconds to craft (paused with the game). 0 = instant.")]
        [SerializeField, Min(0f)] private float craftSeconds = 28f;
        [Tooltip("Old: in-game minutes to craft, from before craft times were in seconds.")]
        [SerializeField, HideInInspector] private int craftMinutes = -1;
        [Tooltip("Only the listed ingredients will do: no taking clean water where it asks for dirty (boiling it, say).")]
        [SerializeField] private bool exactIngredients;

        public string DisplayName => displayName;
        public string Description => description;
        public CraftingStation Station => station;
        public Ingredient[] Ingredients => ingredients;
        public ItemDefinition Output => output;
        public int OutputAmount => outputAmount;
        public float CraftSeconds => craftSeconds;

#if UNITY_EDITOR
        /// <summary>Editor only: turns an old in-game-minutes time into seconds (an in-game minute was 0.625 s). Returns whether it changed.</summary>
        public bool MigrateCraftTime()
        {
            if (craftMinutes < 0) return false;
            craftSeconds = craftMinutes <= 0 ? 0f : Mathf.Max(1f, Mathf.Round(craftMinutes * 0.625f));
            craftMinutes = -1;
            return true;
        }
#endif

        public bool CanAfford(Inventory inventory) => CanAfford(new[] { inventory });

        /// <summary>Whether the sources between them hold every ingredient.</summary>
        /// <summary>Whether the sources between them hold every ingredient.</summary>
        public bool CanAfford(IReadOnlyList<Inventory> sources)
        {
            if (sources == null || sources.Count == 0 || sources[0] == null || ingredients == null) return false;
            foreach (var i in ingredients)
                if (i.item == null || Have(sources, i) < i.amount) return false;
            return true;
        }

        // ---------------------------------------------------------------- water

        private static List<ItemDefinition> waters;

        /// <summary>Drinking water of any kind (it quenches thirst and isn't a meal).</summary>
        public static bool IsWater(ItemDefinition item) =>
            item != null && item.Category == ItemCategory.Food && item.Hydration > 0f && item.Nutrition <= 0f;

        /// <summary>
        /// What meets an ingredient, in the order it's taken: water of any kind, cheapest (dirty) first, unless
        /// the recipe wants exactly what it lists; anything else only itself.
        /// </summary>
        public IReadOnlyList<ItemDefinition> Accepted(ItemDefinition item)
        {
            if (exactIngredients || !IsWater(item)) return new[] { item };
            if (waters == null)
            {
                waters = new List<ItemDefinition>();
                if (Core.SaveRegistry.Instance != null)
                    foreach (var asset in Core.SaveRegistry.Instance.Assets)
                        if (asset is ItemDefinition w && IsWater(w)) waters.Add(w);
                waters.Sort((a, b) => a.BaseValue.CompareTo(b.BaseValue));
            }
            return waters.Contains(item) ? waters : new[] { item };
        }

        /// <summary>How many the sources hold of what meets an ingredient.</summary>
        public int Have(IReadOnlyList<Inventory> sources, Ingredient ingredient)
        {
            int total = 0;
            foreach (var item in Accepted(ingredient.item)) total += ItemSources.Count(sources, item);
            return total;
        }

        public int Have(Inventory inventory, Ingredient ingredient) => Have(new[] { inventory }, ingredient);

        /// <summary>"Water" where any will do, otherwise the item's name.</summary>
        public string IngredientName(Ingredient ingredient) =>
            Accepted(ingredient.item).Count > 1 ? "Water" : ingredient.item.DisplayName;

        // Statics survive between play sessions when domain reload is off in the editor.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => waters = null;

        /// <summary>
        /// Removes the ingredients, best quality first. The product's quality is the average quality of
        /// the ingredients that have one (crops into drugs); Standard if none do.
        /// Returns false (and removes nothing) if any are missing.
        /// </summary>
        public bool Consume(Inventory inventory, out ItemQuality quality) => Consume(new[] { inventory }, out quality, null);

        /// <summary>As above, also listing exactly what was taken (for a refund).</summary>
        public bool Consume(Inventory inventory, out ItemQuality quality, List<ItemStack> taken) => Consume(new[] { inventory }, out quality, taken);

        /// <summary>
        /// As above, drawing from several inventories, the first first. Where any water will do, dirty water
        /// goes from every source before clean water is touched.
        /// </summary>
        public bool Consume(IReadOnlyList<Inventory> sources, out ItemQuality quality, List<ItemStack> taken)
        {
            quality = ItemQuality.Standard;
            if (!CanAfford(sources)) return false;

            int tierTotal = 0, units = 0;
            foreach (var i in ingredients)
            {
                int remaining = i.amount;
                foreach (var item in Accepted(i.item))
                {
                    if (remaining <= 0) break;
                    if (!item.HasQuality)
                    {
                        int removed = ItemSources.Remove(sources, item, remaining);
                        if (removed > 0) taken?.Add(new ItemStack(item, removed, ItemQuality.Standard));
                        remaining -= removed;
                        continue;
                    }
                    for (var q = ItemQuality.Premium; q >= ItemQuality.Poor && remaining > 0; q--)
                    {
                        int got = ItemSources.Remove(sources, item, Mathf.Min(remaining, ItemSources.Count(sources, item, q)), q);
                        if (got > 0) taken?.Add(new ItemStack(item, got, q));
                        remaining -= got;
                        tierTotal += (int)q * got;
                        units += got;
                    }
                }
            }
            if (units > 0) quality = (ItemQuality)Mathf.RoundToInt(tierTotal / (float)units);
            return true;
        }
    }
}
