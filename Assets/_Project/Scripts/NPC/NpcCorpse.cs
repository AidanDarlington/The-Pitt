using System;
using System.Collections.Generic;
using ThePitt.Core;
using ThePitt.Economy;
using ThePitt.Interaction;
using ThePitt.Items;
using UnityEngine;

namespace ThePitt.NPC
{
    /// <summary>
    /// A body that can be searched for the scrip and things the NPC was carrying. Whatever doesn't fit
    /// in the inventory stays on the body. Bodies are cleared away after some in-game hours, to the player's
    /// morgue if they own one.
    /// </summary>
    public class NpcCorpse : MonoBehaviour, IInteractable
    {
        [SerializeField, Min(0f)] private float lingerHours = 6f;

        private string npcName;
        private int cash;
        private readonly List<ItemStack> items = new();
        private int minutesLeft;

        public bool IsEmpty => cash <= 0 && items.Count == 0;

        /// <summary>A body has been cleared off the streets (a morgue the player owns takes it in).</summary>
        public static event Action Cleared;

        public void Setup(string name, int carriedCash, IEnumerable<ItemStack> carried)
        {
            npcName = name;
            cash = carriedCash;
            items.AddRange(carried);
            minutesLeft = Mathf.RoundToInt(lingerHours * 60f);
        }

        private void OnEnable()
        {
            if (TimeManager.Instance != null) TimeManager.Instance.MinuteChanged += OnMinute;
        }

        private void OnDisable()
        {
            if (TimeManager.Instance != null) TimeManager.Instance.MinuteChanged -= OnMinute;
        }

        private void OnMinute(int _)
        {
            if (--minutesLeft > 0) return;
            Cleared?.Invoke();
            Destroy(gameObject);
        }

        public string GetPrompt(PlayerInteractor interactor) =>
            IsEmpty ? $"{npcName} (dead) · nothing left" : $"Search {npcName}'s body";

        public bool CanInteract(PlayerInteractor interactor) => !IsEmpty;

        public void Interact(PlayerInteractor interactor)
        {
            if (cash > 0 && interactor.TryGetComponent<PlayerWallet>(out var wallet))
            {
                wallet.Add(cash);
                cash = 0;
            }
            for (int i = items.Count - 1; i >= 0; i--)
            {
                var s = items[i];
                int taken = interactor.Inventory.Add(s.item, s.quantity, s.quality);
                s.quantity -= taken;
                if (s.quantity <= 0) items.RemoveAt(i);
                else items[i] = s;
            }
        }
    }
}
