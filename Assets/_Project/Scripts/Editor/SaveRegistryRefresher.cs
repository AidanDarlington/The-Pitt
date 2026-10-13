using System.Linq;
using ThePitt.Core;
using ThePitt.Factions;
using ThePitt.Items;
using ThePitt.Production;
using UnityEditor;
using UnityEngine;

namespace ThePitt.EditorTools
{
    /// <summary>
    /// Keeps the save registry listing every item, recipe, plant and gang, so new ones are saveable without any setup
    /// (and gang recipes can be told apart from the rest).
    /// </summary>
    public class SaveRegistryRefresher : AssetPostprocessor
    {
        private const string RegistryPath = "Assets/_Project/SaveRegistry.asset";

        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            bool relevant = imported.Concat(deleted).Concat(moved).Any(p => p.EndsWith(".asset") && p != RegistryPath);
            if (relevant) Refresh();
        }

        [MenuItem("Tools/The Pitt/Refresh Save Registry")]
        public static void Refresh()
        {
            var registry = AssetDatabase.LoadAssetAtPath<SaveRegistry>(RegistryPath);
            if (registry == null)
            {
                registry = ScriptableObject.CreateInstance<SaveRegistry>();
                AssetDatabase.CreateAsset(registry, RegistryPath);
            }

            var found = AssetDatabase.FindAssets("t:ItemDefinition t:RecipeDefinition t:PlantDefinition t:FactionDefinition", new[] { "Assets" })
                .Select(g => AssetDatabase.LoadAssetAtPath<ScriptableObject>(AssetDatabase.GUIDToAssetPath(g)))
                .Where(a => a is ItemDefinition || a is RecipeDefinition || a is PlantDefinition || a is FactionDefinition)
                .OrderBy(a => a.name)
                .ToList();
            if (found.SequenceEqual(registry.Assets)) return;

            registry.Assets.Clear();
            registry.Assets.AddRange(found);
            EditorUtility.SetDirty(registry);
            AssetDatabase.SaveAssetIfDirty(registry);
        }
    }
}
