using System;
using UnityEngine;

namespace ThePitt.Items
{
    [Serializable]
    public struct ItemStack
    {
        public ItemDefinition item;
        public int quantity;
        public ItemQuality quality;

        public ItemStack(ItemDefinition item, int quantity, ItemQuality quality)
        {
            this.item = item;
            this.quantity = quantity;
            this.quality = item != null && item.HasQuality ? quality : ItemQuality.Standard;
        }

        public bool IsEmpty => item == null || quantity <= 0;
        public static ItemStack Empty => default;

        public bool CanStackWith(ItemDefinition other, ItemQuality otherQuality) =>
            !IsEmpty && item == other && quality == otherQuality && quantity < item.MaxStack;
    }

    /// <summary>A fixed number of slots holding item stacks. Used by the player and by storage.</summary>
    public class Inventory : MonoBehaviour
    {
        [SerializeField, Min(1)] private int slotCount = 24;
        [SerializeField] private ItemStack[] slots;

        /// <summary>Fired whenever any slot changes.</summary>
        public event Action Changed;
        /// <summary>Fired after items are added: item, amount actually added, quality.</summary>
        public event Action<ItemDefinition, int, ItemQuality> ItemAdded;

        public int SlotCount => Slots.Length;
        public ItemStack GetSlot(int index) => Slots[index];

        // Sized on first use so other components' Awake can query us regardless of execution order.
        private ItemStack[] Slots
        {
            get
            {
                if (slots == null || slots.Length != slotCount) Array.Resize(ref slots, slotCount);
                return slots;
            }
        }

        private void OnValidate()
        {
            if (slots == null || slots.Length != slotCount) Array.Resize(ref slots, slotCount);
        }

        /// <summary>Adds as much as fits. Returns how many were added.</summary>
        public int Add(ItemDefinition item, int amount, ItemQuality quality = ItemQuality.Standard)
        {
            if (item == null || amount <= 0) return 0;
            if (!item.HasQuality) quality = ItemQuality.Standard;

            int remaining = amount;
            // Top up matching stacks first, then fill empty slots.
            for (int i = 0; i < Slots.Length && remaining > 0; i++)
            {
                if (!Slots[i].CanStackWith(item, quality)) continue;
                int moved = Mathf.Min(remaining, item.MaxStack - Slots[i].quantity);
                Slots[i].quantity += moved;
                remaining -= moved;
            }
            for (int i = 0; i < Slots.Length && remaining > 0; i++)
            {
                if (!Slots[i].IsEmpty) continue;
                int moved = Mathf.Min(remaining, item.MaxStack);
                Slots[i] = new ItemStack(item, moved, quality);
                remaining -= moved;
            }

            int added = amount - remaining;
            if (added > 0)
            {
                Changed?.Invoke();
                ItemAdded?.Invoke(item, added, quality);
            }
            return added;
        }

        /// <summary>How many more of this item and quality would fit.</summary>
        public int SpaceFor(ItemDefinition item, ItemQuality quality = ItemQuality.Standard)
        {
            if (item == null) return 0;
            if (!item.HasQuality) quality = ItemQuality.Standard;
            int space = 0;
            foreach (var slot in Slots)
            {
                if (slot.IsEmpty) space += item.MaxStack;
                else if (slot.item == item && slot.quality == quality) space += item.MaxStack - slot.quantity;
            }
            return space;
        }

        /// <summary>Total of an item across all slots, optionally only one quality.</summary>
        public int Count(ItemDefinition item, ItemQuality? quality = null)
        {
            int total = 0;
            foreach (var slot in Slots)
                if (!slot.IsEmpty && slot.item == item && (quality == null || slot.quality == quality))
                    total += slot.quantity;
            return total;
        }

        /// <summary>
        /// Removes up to <paramref name="amount"/> of an item, lowest quality first unless a quality is given.
        /// Returns how many were removed.
        /// </summary>
        public int Remove(ItemDefinition item, int amount, ItemQuality? quality = null)
        {
            if (item == null || amount <= 0) return 0;
            int remaining = amount;
            for (var q = ItemQuality.Poor; q <= ItemQuality.Premium && remaining > 0; q++)
            {
                if (quality != null && q != quality) continue;
                for (int i = 0; i < Slots.Length && remaining > 0; i++)
                {
                    if (Slots[i].IsEmpty || Slots[i].item != item || Slots[i].quality != q) continue;
                    remaining -= TakeFrom(i, remaining);
                }
            }

            int removed = amount - remaining;
            if (removed > 0) Changed?.Invoke();
            return removed;
        }

        /// <summary>Removes up to <paramref name="amount"/> from one slot. Returns how many were removed.</summary>
        public int RemoveAt(int index, int amount)
        {
            int removed = TakeFrom(index, amount);
            if (removed > 0) Changed?.Invoke();
            return removed;
        }

        /// <summary>
        /// Moves a stack onto another slot: tops up a matching stack there (what doesn't fit stays behind),
        /// otherwise swaps the two.
        /// </summary>
        public void Move(int from, int to)
        {
            if (from == to || from < 0 || to < 0 || from >= Slots.Length || to >= Slots.Length || Slots[from].IsEmpty) return;
            var a = Slots[from];
            var b = Slots[to];
            if (!b.IsEmpty && b.CanStackWith(a.item, a.quality))
            {
                int moved = Mathf.Min(a.quantity, a.item.MaxStack - b.quantity);
                if (moved <= 0) return;
                b.quantity += moved;
                a.quantity -= moved;
                Slots[to] = b;
                Slots[from] = a.quantity > 0 ? a : ItemStack.Empty;
            }
            else
            {
                Slots[from] = b;
                Slots[to] = a;
            }
            Changed?.Invoke();
        }

        /// <summary>Replaces every slot (loading a save). Extra stacks beyond the slot count are dropped.</summary>
        public void SetAll(System.Collections.Generic.IList<ItemStack> stacks)
        {
            for (int i = 0; i < Slots.Length; i++) Slots[i] = i < stacks.Count ? stacks[i] : ItemStack.Empty;
            Changed?.Invoke();
        }

        private int TakeFrom(int index, int amount)
        {
            if (Slots[index].IsEmpty || amount <= 0) return 0;
            int taken = Mathf.Min(amount, Slots[index].quantity);
            Slots[index].quantity -= taken;
            if (Slots[index].quantity <= 0) Slots[index] = ItemStack.Empty;
            return taken;
        }
    }
}
