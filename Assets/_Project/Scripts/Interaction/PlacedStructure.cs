using ThePitt.Items;
using UnityEngine;

namespace ThePitt.Interaction
{
    /// <summary>Marks something the player placed, and which item it goes back into when picked up.</summary>
    public class PlacedStructure : MonoBehaviour
    {
        [SerializeField] private ItemDefinition item;

        public ItemDefinition Item => item;
        public string DisplayName => item != null ? item.DisplayName : name;

        public void Setup(ItemDefinition source) => item = source;

        /// <summary>Whether it can be picked up right now, asking any component on it that might object.</summary>
        public bool CanPickUp(out string reason)
        {
            foreach (var blocker in GetComponentsInChildren<IPickupBlocker>())
                if (!blocker.CanPickUp(out reason)) return false;
            reason = null;
            return true;
        }
    }

    /// <summary>A component that can stop its structure being picked up (busy station, growing crop).</summary>
    public interface IPickupBlocker
    {
        bool CanPickUp(out string reason);
    }
}
