using System;
using ThePitt.Items;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ThePitt.Player
{
    /// <summary>
    /// The first six inventory slots, switched between with 1-6 (or the scroll wheel). Selecting a slot takes
    /// its weapon or tool in hand, or holds up food, drink, a drug or a station to be used with a click (eaten,
    /// or placed). An empty slot brings the fists up, whether picked empty or emptied by the item running out;
    /// anything else (scrap, springs and the like) leaves the hands down. Pressing the selected slot's key again
    /// puts it away without bringing the fists up, and so does placing the last of a station.
    /// In build mode only stations can be picked: the keys and the scroll wheel pass over everything else.
    /// </summary>
    [RequireComponent(typeof(Inventory), typeof(PlayerCombat))]
    public class Hotbar : MonoBehaviour
    {
        public const int Size = 6;

        private Inventory inventory;
        private PlayerCombat combat;
        private PlayerVitals vitals;
        private PlacementController placement;
        private int selected = -1;

        /// <summary>The selected slot, or -1 for none.</summary>
        public int Selected => selected;

        public event Action Changed;

        private static readonly Key[] keys = { Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4, Key.Digit5, Key.Digit6 };

        private void Awake()
        {
            inventory = GetComponent<Inventory>();
            combat = GetComponent<PlayerCombat>();
            vitals = GetComponent<PlayerVitals>();
            placement = GetComponent<PlacementController>();
        }

        private void OnEnable() => inventory.Changed += Apply;
        private void OnDisable() => inventory.Changed -= Apply;

        private void Update()
        {
            if (!combat.InControl) return;
            var keyboard = Keyboard.current;
            if (keyboard != null)
                for (int i = 0; i < Size; i++)
                    if (keyboard[keys[i]].wasPressedThisFrame)
                    {
                        Press(i);
                        return;
                    }
            if (combat.PreviousAction != null && combat.PreviousAction.WasPressedThisFrame()) Scroll(-1);
            else if (combat.NextAction != null && combat.NextAction.WasPressedThisFrame()) Scroll(1);
            else if (combat.HeldUsable != null && combat.AttackAction != null && !combat.AttackSwallowed && combat.AttackAction.WasPressedThisFrame()) UseSelected();
        }

        /// <summary>Uses one of what's held up: eaten, drunk or taken, or set down to be placed.</summary>
        public void UseSelected()
        {
            if (selected < 0) return;
            var slot = inventory.GetSlot(selected);
            if (slot.IsEmpty) return;
            if (slot.item.IsPlaceable)
            {
                if (placement != null) placement.Begin(slot.item);
                return;
            }
            if (!slot.item.IsConsumable || vitals == null) return;
            vitals.Consume(slot.item);
            inventory.RemoveAt(selected, 1);
            Audio.AudioManager.Play2D(Audio.AudioManager.Library?.click, 0.4f);
        }

        /// <summary>What a click does with the held item: "eat", "drink", "take", "place", or null.</summary>
        public static string UseVerb(ItemDefinition item)
        {
            if (item == null) return null;
            if (item.IsPlaceable) return "place";
            if (!item.IsConsumable) return null;
            if (item.Category == ItemCategory.Drug) return "take";
            return item.Nutrition <= 0f && item.Hydration > 0f ? "drink" : "eat";
        }

        /// <summary>A hotkey: selects the slot, or puts away what's in hand if it's already selected.</summary>
        public void Press(int index)
        {
            if (combat.InBuildMode && index != selected && !HoldsPlaceable(index)) return;
            if (index == selected)
            {
                Clear();
                combat.PutAway();
                return;
            }
            Select(index);
        }

        /// <summary>Selects a slot and takes up what's in it.</summary>
        public void Select(int index)
        {
            if (index < 0 || index >= Size) return;
            selected = index;
            Apply();
        }

        /// <summary>No slot selected; leaves whatever is in hand alone.</summary>
        public void Clear()
        {
            if (selected < 0) return;
            selected = -1;
            Changed?.Invoke();
        }

        private void Scroll(int step)
        {
            int from = selected < 0 ? (step > 0 ? -1 : 0) : selected;
            if (!combat.InBuildMode)
            {
                Select((from + step + Size) % Size);
                return;
            }
            // Build mode: on to the next slot with a station in it, if there is one.
            for (int i = 1; i <= Size; i++)
            {
                int index = ((from + step * i) % Size + Size) % Size;
                if (!HoldsPlaceable(index)) continue;
                Select(index);
                return;
            }
        }

        private bool HoldsPlaceable(int index)
        {
            var slot = inventory.GetSlot(index);
            return !slot.IsEmpty && slot.item.IsPlaceable;
        }

        /// <summary>Going into build mode: only a station stays in hand; anything else is put away.</summary>
        public void EnterBuildMode()
        {
            if (selected >= 0 && HoldsPlaceable(selected)) return;
            Clear();
            combat.PutAway();
        }

        /// <summary>Puts the selected slot's contents in hand, after a selection or any inventory change.</summary>
        private void Apply()
        {
            if (selected >= 0)
            {
                var slot = inventory.GetSlot(selected);
                // Placed the last one, or in build mode with no station left in the slot: hands down, not fists.
                bool placedLast = slot.IsEmpty && placement != null && placement.LastPlacedFrame == Time.frameCount;
                if (placedLast || (combat.InBuildMode && !HoldsPlaceable(selected)))
                {
                    selected = -1;
                    combat.PutAway();
                }
                // An empty slot is the fists; a material can't be held up, so nothing shows.
                else if (slot.IsEmpty) combat.Hold(null, ItemQuality.Standard);
                else if (slot.item.IsWeapon) combat.Hold(slot.item, slot.quality);
                else if (UseVerb(slot.item) != null) combat.HoldUsable(slot.item, slot.quality);
                else combat.PutAway();
            }
            Changed?.Invoke();
        }

        /// <summary>Puts back the selection from a save.</summary>
        public void Restore(int index, bool handsDown)
        {
            selected = index >= 0 && index < Size ? index : -1;
            if (selected >= 0) Apply();
            else
            {
                if (handsDown) combat.PutAway();
                // Fists were up when saved (the player now starts with hands down).
                else if (combat.Equipped == null) combat.Hold(null, ItemQuality.Standard);
                Changed?.Invoke();
            }
        }
    }
}
