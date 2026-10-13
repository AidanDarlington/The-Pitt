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
        [Tooltip("In-game minutes to craft. One in-game hour is 37.5 real seconds.")]
        [SerializeField, Min(0)] private int craftMinutes = 45;

        public string DisplayName => displayName;
        public string Description => description;
        public CraftingStation Station => station;
        public Ingredient[] Ingredients => ingredients;
        public ItemDefinition Output => output;
        public int OutputAmount => outputAmount;
        public int CraftMinutes => craftMinutes;

        public bool CanAfford(Inventory inventory)
        {
            if (inventory == null || ingredients == null) return false;
            foreach (var i in ingredients)
                if (i.item == null || inventory.Count(i.item) < i.amount) return false;
            return true;
        }

        /// <summary>
        /// Removes the ingredients, best quality first. The product's quality is the average quality of
        /// the ingredients that have one (crops into drugs); Standard if none do.
        /// Returns false (and removes nothing) if any are missing.
        /// </summary>
        public bool Consume(Inventory inventory, out ItemQuality quality) => Consume(inventory, out quality, null);

        /// <summary>As above, also listing exactly what was taken (for a refund).</summary>
        public bool Consume(Inventory inventory, out ItemQuality quality, List<ItemStack> taken)
        {
            quality = ItemQuality.Standard;
            if (!CanAfford(inventory)) return false;

            int tierTotal = 0, units = 0;
            foreach (var i in ingredients)
            {
                if (!i.item.HasQuality)
                {
                    int removed = inventory.Remove(i.item, i.amount);
                    taken?.Add(new ItemStack(i.item, removed, ItemQuality.Standard));
                    continue;
                }
                int remaining = i.amount;
                for (var q = ItemQuality.Premium; q >= ItemQuality.Poor && remaining > 0; q--)
                {
                    int got = inventory.Remove(i.item, Mathf.Min(remaining, inventory.Count(i.item, q)), q);
                    if (got > 0) taken?.Add(new ItemStack(i.item, got, q));
                    remaining -= got;
                    tierTotal += (int)q * got;
                    units += got;
                }
            }
            if (units > 0) quality = (ItemQuality)Mathf.RoundToInt(tierTotal / (float)units);
            return true;
        }
    }
}
