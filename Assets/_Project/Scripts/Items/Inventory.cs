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

        public bool CanStackWith(ItemDefinition other, ItemQuality otherQuality, int limit) =>
            !IsEmpty && item == other && quality == otherQuality && quantity < limit;
    }

    /// <summary>A fixed number of slots holding item stacks. Used by the player and by storage.</summary>
    public class Inventory : MonoBehaviour
    {
        [SerializeField, Min(1)] private int slotCount = 24;
        [SerializeField] private ItemStack[] slots;

        /// <summary>Most of a tool, weapon or structure one storage slot holds.</summary>
        public const int StoredGearStack = 5;

        /// <summary>
        /// A storage crate: tools, weapons and structures stack to <see cref="StoredGearStack"/>, everything
        /// else to twice what it does on the player.
        /// </summary>
        public bool IsStorage { get; set; }

        /// <summary>How many of an item one slot here holds.</summary>
        public int StackLimit(ItemDefinition item)
        {
            if (item == null) return 1;
            if (!IsStorage) return item.MaxStack;
            bool gear = item.Category is ItemCategory.Tool or ItemCategory.Structure || (item.Category == ItemCategory.Weapon && !item.IsAmmo);
            return gear ? StoredGearStack : item.MaxStack * 2;
        }

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
                if (!Slots[i].CanStackWith(item, quality, StackLimit(item))) continue;
                int moved = Mathf.Min(remaining, StackLimit(item) - Slots[i].quantity);
                Slots[i].quantity += moved;
                remaining -= moved;
            }
            for (int i = 0; i < Slots.Length && remaining > 0; i++)
            {
                if (!Slots[i].IsEmpty) continue;
                int moved = Mathf.Min(remaining, StackLimit(item));
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

        /// <summary>
        /// Adds as much as fits into one slot: an empty one, or a stack of the same item and quality.
        /// Returns how many were added.
        /// </summary>
        public int AddToSlot(int index, ItemDefinition item, int amount, ItemQuality quality = ItemQuality.Standard)
        {
            if (item == null || amount <= 0 || index < 0 || index >= Slots.Length) return 0;
            if (!item.HasQuality) quality = ItemQuality.Standard;
            var slot = Slots[index];
            int moved;
            if (slot.IsEmpty)
            {
                moved = Mathf.Min(amount, StackLimit(item));
                Slots[index] = new ItemStack(item, moved, quality);
            }
            else if (slot.CanStackWith(item, quality, StackLimit(item)))
            {
                moved = Mathf.Min(amount, StackLimit(item) - slot.quantity);
                Slots[index].quantity += moved;
            }
            else return 0;
            Changed?.Invoke();
            ItemAdded?.Invoke(item, moved, quality);
            return moved;
        }

        /// <summary>How many more of this item and quality would fit.</summary>
        public int SpaceFor(ItemDefinition item, ItemQuality quality = ItemQuality.Standard)
        {
            if (item == null) return 0;
            if (!item.HasQuality) quality = ItemQuality.Standard;
            int space = 0;
            foreach (var slot in Slots)
            {
                if (slot.IsEmpty) space += StackLimit(item);
                else if (slot.item == item && slot.quality == quality) space += Mathf.Max(0, StackLimit(item) - slot.quantity);
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
            if (!b.IsEmpty && b.CanStackWith(a.item, a.quality, StackLimit(a.item)))
            {
                int moved = Mathf.Min(a.quantity, StackLimit(a.item) - b.quantity);
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

        /// <summary>
        /// Moves half of a stack (rounded down) into an empty slot, the first after it if there is one. False if
        /// it's a single item or there's no empty slot.
        /// </summary>
        public bool Split(int index) => index >= 0 && index < Slots.Length && Split(index, Slots[index].quantity / 2);

        /// <summary>Moves <paramref name="amount"/> of a stack into the next empty slot; at least one stays behind.</summary>
        public bool Split(int index, int amount)
        {
            if (index < 0 || index >= Slots.Length || Slots[index].IsEmpty || amount < 1 || amount >= Slots[index].quantity) return false;
            int empty = -1;
            for (int i = 1; i < Slots.Length && empty < 0; i++)
            {
                int j = (index + i) % Slots.Length;
                if (Slots[j].IsEmpty) empty = j;
            }
            if (empty < 0) return false;
            var stack = Slots[index];
            stack.quantity -= amount;
            Slots[index] = stack;
            Slots[empty] = new ItemStack(stack.item, amount, stack.quality);
            Changed?.Invoke();
            return true;
        }

        /// <summary>Whether any slot is empty.</summary>
        public bool HasEmptySlot()
        {
            foreach (var s in Slots)
                if (s.IsEmpty) return true;
            return false;
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

    /// <summary>Several inventories counted and drawn from as one: the player's, then storage nearby.</summary>
    public static class ItemSources
    {
        public static int Count(System.Collections.Generic.IReadOnlyList<Inventory> sources, ItemDefinition item, ItemQuality? quality = null)
        {
            int total = 0;
            if (sources != null)
                foreach (var inventory in sources) total += inventory.Count(item, quality);
            return total;
        }

        /// <summary>Removes up to <paramref name="amount"/>, from the first source first. Returns how many were removed.</summary>
        public static int Remove(System.Collections.Generic.IReadOnlyList<Inventory> sources, ItemDefinition item, int amount, ItemQuality? quality = null)
        {
            int removed = 0;
            if (sources != null)
                foreach (var inventory in sources)
                {
                    if (removed >= amount) break;
                    removed += inventory.Remove(item, amount - removed, quality);
                }
            return removed;
        }
    }
}
