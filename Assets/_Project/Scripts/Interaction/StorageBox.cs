using System.Collections.Generic;
using ThePitt.Core;
using ThePitt.Items;
using ThePitt.UI;
using UnityEngine;

namespace ThePitt.Interaction
{
    /// <summary>A placed box with a few slots of its own. Has to be emptied before it can be picked up.</summary>
    [RequireComponent(typeof(Inventory))]
    public class StorageBox : MonoBehaviour, IInteractable, IPickupBlocker, ISaveState
    {
        [SerializeField] private string boxName = "Storage Box";

        private Inventory contents;

        public string BoxName => boxName;
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

        public string GetPrompt(PlayerInteractor interactor) => $"Open {boxName} ({Used}/{Contents.SlotCount})";

        public bool CanInteract(PlayerInteractor interactor) => StorageUI.Instance != null;

        public void Interact(PlayerInteractor interactor) => StorageUI.Instance.Open(this, interactor);

        public bool CanPickUp(out string reason)
        {
            reason = Used > 0 ? "empty it first" : null;
            return reason == null;
        }

        public string CaptureState()
        {
            var save = new SaveState();
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
        }
    }
}
