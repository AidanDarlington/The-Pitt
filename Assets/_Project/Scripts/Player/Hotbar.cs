using System;
using ThePitt.Items;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ThePitt.Player
{
    /// <summary>
    /// The first six inventory slots, switched between with 1-6 (or the scroll wheel). Selecting a slot takes
    /// its weapon or tool in hand, or holds up food, drink, a drug or a station to be used with a click (eaten,
    /// or placed); anything else brings the fists up, and so does the item in the selected slot running out. Picking an empty slot on purpose lowers the hands entirely, and pressing
    /// the selected slot's key again puts it away without bringing the fists up.
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
        // The selected slot was empty when picked: keep the hands down until something lands in it.
        private bool choseEmpty;

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
            if (!combat.CanAct) return;
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
            else if (combat.HeldUsable != null && combat.AttackAction != null && combat.AttackAction.WasPressedThisFrame()) UseSelected();
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
            choseEmpty = inventory.GetSlot(index).IsEmpty;
            Apply();
        }

        /// <summary>No slot selected; leaves whatever is in hand alone.</summary>
        public void Clear()
        {
            if (selected < 0) return;
            selected = -1;
            choseEmpty = false;
            Changed?.Invoke();
        }

        private void Scroll(int step)
        {
            int from = selected < 0 ? (step > 0 ? -1 : 0) : selected;
            Select((from + step + Size) % Size);
        }

        /// <summary>Puts the selected slot's contents in hand, after a selection or any inventory change.</summary>
        private void Apply()
        {
            if (selected >= 0)
            {
                var slot = inventory.GetSlot(selected);
                if (slot.IsEmpty)
                {
                    if (choseEmpty) combat.PutAway();
                    else combat.Hold(null, ItemQuality.Standard);
                }
                else
                {
                    choseEmpty = false;
                    if (slot.item.IsWeapon) combat.Hold(slot.item, slot.quality);
                    else if (UseVerb(slot.item) != null) combat.HoldUsable(slot.item, slot.quality);
                    else combat.Hold(null, ItemQuality.Standard);
                }
            }
            Changed?.Invoke();
        }

        /// <summary>Puts back the selection from a save.</summary>
        public void Restore(int index, bool handsDown)
        {
            selected = index >= 0 && index < Size ? index : -1;
            choseEmpty = selected >= 0 && inventory.GetSlot(selected).IsEmpty;
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
