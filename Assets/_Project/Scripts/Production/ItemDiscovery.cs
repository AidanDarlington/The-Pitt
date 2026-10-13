using ThePitt.Items;
using UnityEngine;

namespace ThePitt.Production
{
    /// <summary>
    /// On the player: everything that enters their inventory counts as discovered for
    /// <see cref="RecipeUnlocks"/>. What they start with is known quietly.
    /// </summary>
    [RequireComponent(typeof(Inventory))]
    public class ItemDiscovery : MonoBehaviour
    {
        private Inventory inventory;

        private void Awake() => inventory = GetComponent<Inventory>();

        private void OnEnable() => inventory.ItemAdded += OnItemAdded;
        private void OnDisable() => inventory.ItemAdded -= OnItemAdded;

        private void Start() => DiscoverCarried();

        /// <summary>Quietly counts everything currently carried (new game, or after loading a save).</summary>
        public void DiscoverCarried()
        {
            for (int i = 0; i < inventory.SlotCount; i++)
            {
                var s = inventory.GetSlot(i);
                if (!s.IsEmpty) RecipeUnlocks.Discover(s.item, announce: false);
            }
        }

        private void OnItemAdded(ItemDefinition item, int amount, ItemQuality quality) => RecipeUnlocks.Discover(item);
    }
}
