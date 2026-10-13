using System;
using System.Collections.Generic;
using ThePitt.Core;
using ThePitt.Factions;
using ThePitt.Items;
using UnityEngine;

namespace ThePitt.Production
{
    /// <summary>
    /// Recipes are hidden until the player has got their hands on every ingredient at least once (pick up
    /// scrap and the workbench appears) and, for a station's recipes, has built and placed that station
    /// (no forge recipes before there's a forge). Tracks which items the player has ever had and which
    /// stations they've set up, and announces recipes as they come within reach.
    /// </summary>
    public static class RecipeUnlocks
    {
        private static readonly HashSet<ItemDefinition> discovered = new();
        private static readonly HashSet<CraftingStation> stations = new();
        private static List<RecipeDefinition> catalogue;
        private static HashSet<RecipeDefinition> signatures;

        public static IReadOnlyCollection<ItemDefinition> Discovered => discovered;
        public static IReadOnlyCollection<CraftingStation> Stations => stations;

        /// <summary>A recipe has just become available.</summary>
        public static event Action<RecipeDefinition> Unlocked;

        /// <summary>Whether the player has set up this kind of station. Hand crafting needs none.</summary>
        public static bool HasStation(CraftingStation station) => station == CraftingStation.Hand || stations.Contains(station);

        /// <summary>Whether the player has had every ingredient and has the station it's made at.</summary>
        public static bool IsUnlocked(RecipeDefinition recipe)
        {
            if (recipe == null || !HasStation(recipe.Station)) return false;
            if (recipe.Ingredients == null) return true;
            foreach (var i in recipe.Ingredients)
                if (i.item != null && !discovered.Contains(i.item)) return false;
            return true;
        }

        /// <summary>
        /// Not unlocked, but on the way: the station is set up and at least one ingredient has been found.
        /// Shown greyed out as "???" with what's still to find.
        /// </summary>
        public static bool IsPartlyKnown(RecipeDefinition recipe)
        {
            if (recipe == null || recipe.Ingredients == null || !HasStation(recipe.Station) || IsUnlocked(recipe)) return false;
            foreach (var i in recipe.Ingredients)
                if (i.item != null && discovered.Contains(i.item)) return true;
            return false;
        }

        /// <summary>The ingredients not found yet, with how many the recipe needs.</summary>
        public static List<RecipeDefinition.Ingredient> Missing(RecipeDefinition recipe)
        {
            var list = new List<RecipeDefinition.Ingredient>();
            if (recipe?.Ingredients == null) return list;
            foreach (var i in recipe.Ingredients)
                if (i.item != null && !discovered.Contains(i.item)) list.Add(i);
            return list;
        }

        /// <summary>"Find Slag Ingot ×2 and Spring ×1 to unlock."</summary>
        public static string MissingText(RecipeDefinition recipe)
        {
            var parts = new List<string>();
            foreach (var i in Missing(recipe)) parts.Add($"{i.item.DisplayName} ×{i.amount}");
            return parts.Count > 0 ? $"Find {string.Join(" and ", parts)} to unlock" : "";
        }

        /// <summary>
        /// The recipes worth showing, in order: unlocked ones first, then partly known ones (which a list
        /// shows greyed out). Gang recipes not yet learned are left out; lists only pass in ones they offer.
        /// </summary>
        public static List<RecipeDefinition> Visible(IEnumerable<RecipeDefinition> recipes)
        {
            var unlocked = new List<RecipeDefinition>();
            var partial = new List<RecipeDefinition>();
            if (recipes == null) return unlocked;
            foreach (var r in recipes)
            {
                if (r == null) continue;
                if (IsUnlocked(r)) unlocked.Add(r);
                else if (IsPartlyKnown(r)) partial.Add(r);
            }
            unlocked.AddRange(partial);
            return unlocked;
        }

        /// <summary>The player got hold of an item. Announces any recipe it completes.</summary>
        public static void Discover(ItemDefinition item, bool announce = true)
        {
            if (item == null || !discovered.Add(item) || !announce) return;
            foreach (var recipe in Catalogue())
                if (Uses(recipe, item)) Announce(recipe);
        }

        /// <summary>A station of this kind has been placed. Announces the recipes it opens up.</summary>
        public static void AddStation(CraftingStation station, bool announce = true)
        {
            if (!stations.Add(station) || !announce) return;
            foreach (var recipe in Catalogue())
                if (recipe.Station == station) Announce(recipe);
        }

        private static void Announce(RecipeDefinition recipe)
        {
            if (!IsUnlocked(recipe)) return;
            // Gang recipes stay secret until learned from the gang.
            if (Signatures().Contains(recipe) && !RecipeBook.Knows(recipe)) return;
            Unlocked?.Invoke(recipe);
        }

        /// <summary>Replaces everything discovered and set up (loading a save), without announcing anything.</summary>
        public static void Restore(IEnumerable<ItemDefinition> items, IEnumerable<CraftingStation> placedStations = null)
        {
            discovered.Clear();
            stations.Clear();
            if (items != null)
                foreach (var item in items)
                    if (item != null) discovered.Add(item);
            if (placedStations != null)
                foreach (var station in placedStations) stations.Add(station);
        }

        private static bool Uses(RecipeDefinition recipe, ItemDefinition item)
        {
            if (recipe.Ingredients == null) return false;
            foreach (var i in recipe.Ingredients)
                if (i.item == item) return true;
            return false;
        }

        private static List<RecipeDefinition> Catalogue()
        {
            if (catalogue != null) return catalogue;
            catalogue = new List<RecipeDefinition>();
            if (SaveRegistry.Instance != null)
                foreach (var asset in SaveRegistry.Instance.Assets)
                    if (asset is RecipeDefinition r) catalogue.Add(r);
            return catalogue;
        }

        private static HashSet<RecipeDefinition> Signatures()
        {
            if (signatures != null) return signatures;
            signatures = new HashSet<RecipeDefinition>();
            if (SaveRegistry.Instance != null)
                foreach (var asset in SaveRegistry.Instance.Assets)
                    if (asset is FactionDefinition f && f.SignatureRecipe != null) signatures.Add(f.SignatureRecipe);
            return signatures;
        }

        // Statics survive between play sessions when domain reload is off in the editor.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            discovered.Clear();
            stations.Clear();
            catalogue = null;
            signatures = null;
        }
    }
}
