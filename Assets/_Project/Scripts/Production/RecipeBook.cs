using System;
using System.Collections.Generic;
using UnityEngine;

namespace ThePitt.Production
{
    /// <summary>
    /// Recipes the player has learned beyond what stations know by default (gang signature recipes).
    /// Stations of the matching kind offer them alongside their own.
    /// </summary>
    public static class RecipeBook
    {
        private static readonly HashSet<RecipeDefinition> learned = new();

        public static IReadOnlyCollection<RecipeDefinition> Learned => learned;
        public static event Action<RecipeDefinition> Learnt;

        public static bool Knows(RecipeDefinition recipe) => recipe != null && learned.Contains(recipe);

        public static void Learn(RecipeDefinition recipe)
        {
            if (recipe == null || !learned.Add(recipe)) return;
            Learnt?.Invoke(recipe);
        }

        /// <summary>Replaces everything learned (loading a save).</summary>
        public static void Restore(IEnumerable<RecipeDefinition> recipes)
        {
            learned.Clear();
            foreach (var r in recipes)
                if (r != null) learned.Add(r);
        }

        /// <summary>A station's own recipes plus any learned ones made at that kind of station.</summary>
        public static RecipeDefinition[] With(RecipeDefinition[] own, CraftingStation station)
        {
            if (learned.Count == 0) return own;
            var all = new List<RecipeDefinition>(own ?? Array.Empty<RecipeDefinition>());
            foreach (var r in learned)
                if (r.Station == station && !all.Contains(r)) all.Add(r);
            return all.ToArray();
        }

        // Statics survive between play sessions when domain reload is off in the editor.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => learned.Clear();
    }
}
