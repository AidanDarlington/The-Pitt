using System.Collections.Generic;
using UnityEngine;

namespace ThePitt.Core
{
    /// <summary>
    /// Every item, recipe, plant and gang asset, so a save can refer to them by name. Kept up to date
    /// automatically in the editor whenever assets change (SaveRegistryRefresher).
    /// </summary>
    [CreateAssetMenu(menuName = "The Pitt/Save Registry", fileName = "SaveRegistry")]
    public class SaveRegistry : ScriptableObject
    {
        public static SaveRegistry Instance { get; set; }

        [SerializeField] private List<ScriptableObject> assets = new();

        private Dictionary<string, ScriptableObject> byName;

        public List<ScriptableObject> Assets => assets;

        /// <summary>The asset with this name and type, or null.</summary>
        public T Find<T>(string id) where T : ScriptableObject
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (byName == null || byName.Count != assets.Count)
            {
                byName = new Dictionary<string, ScriptableObject>();
                foreach (var a in assets)
                    if (a != null) byName[a.name] = a;
            }
            return byName.TryGetValue(id, out var found) ? found as T : null;
        }

        public static string IdOf(Object asset) => asset != null ? asset.name : null;

        public static T Lookup<T>(string id) where T : ScriptableObject => Instance != null ? Instance.Find<T>(id) : null;

        private void OnValidate() => byName = null;
    }
}
