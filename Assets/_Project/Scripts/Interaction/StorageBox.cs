using System.Collections.Generic;
using ThePitt.Core;
using ThePitt.Items;
using ThePitt.UI;
using UnityEngine;

namespace ThePitt.Interaction
{
    /// <summary>
    /// A placed box with a few slots of its own. Has to be emptied before it can be picked up. The player can
    /// label it (in its storage menu); the label shows when looking at it.
    /// </summary>
    [RequireComponent(typeof(Inventory))]
    public class StorageBox : MonoBehaviour, IInteractable, IPickupBlocker, ISaveState
    {
        [SerializeField] private string boxName = "Storage Box";

        private static readonly List<StorageBox> all = new();
        private Inventory contents;

        private void Awake() => Contents.IsStorage = true;

        private void OnEnable() => all.Add(this);
        private void OnDisable() => all.Remove(this);

        /// <summary>
        /// Where a station at a point can take items from: the player's inventory first, then every storage
        /// box in the same property. Just the inventory outside a property.
        /// </summary>
        public static List<Inventory> Sources(Inventory player, Vector3 at)
        {
            var list = new List<Inventory>();
            if (player != null) list.Add(player);
            var property = Properties.Property.At(at);
            if (property == null) return list;
            foreach (var box in all)
                if (box != null && Properties.Property.At(box.transform.position) == property) list.Add(box.Contents);
            return list;
        }

        public string BoxName => boxName;
        /// <summary>The player's own name for it, or empty.</summary>
        public string Label { get; private set; } = "";

        public void SetLabel(string label) => Label = string.IsNullOrWhiteSpace(label) ? "" : label.Trim();
        public Inventory Contents => contents != null ? contents : contents = GetComponent<Inventory>();

        private int Used
        {
            get
            {
                int used = 0;
                for (int i = 0; i < Contents.SlotCount; i++)
                    if (!Contents.GetSlot(i).IsEmpty) used++;
                return used;
            }
        }

        public string GetPrompt(PlayerInteractor interactor) =>
            Label.Length > 0 ? $"Open \"{Label}\" ({Used}/{Contents.SlotCount})" : $"Open {boxName} ({Used}/{Contents.SlotCount})";

        public bool CanInteract(PlayerInteractor interactor) => StorageUI.Instance != null;

        public void Interact(PlayerInteractor interactor) => StorageUI.Instance.Open(this, interactor);

        public bool CanPickUp(out string reason)
        {
            reason = Used > 0 ? "empty it first" : null;
            return reason == null;
        }

        public string CaptureState()
        {
            var save = new SaveState { label = Label };
            for (int i = 0; i < Contents.SlotCount; i++)
            {
                var s = Contents.GetSlot(i);
                save.slots.Add(s.IsEmpty ? new ItemStackSave() : new ItemStackSave
                {
                    item = SaveRegistry.IdOf(s.item),
                    quantity = s.quantity,
                    quality = (int)s.quality,
                });
            }
            return JsonUtility.ToJson(save);
        }

        public void RestoreState(string json)
        {
            var s = JsonUtility.FromJson<SaveState>(json);
            SetLabel(s.label);
            var stacks = new List<ItemStack>();
            if (s.slots != null)
                foreach (var slot in s.slots)
                {
                    var item = SaveRegistry.Lookup<ItemDefinition>(slot.item);
                    stacks.Add(item != null && slot.quantity > 0 ? new ItemStack(item, slot.quantity, (ItemQuality)slot.quality) : ItemStack.Empty);
                }
            Contents.SetAll(stacks);
        }

        [System.Serializable]
        private class SaveState
        {
            public List<ItemStackSave> slots = new();
            public string label;
        }
    }
}
