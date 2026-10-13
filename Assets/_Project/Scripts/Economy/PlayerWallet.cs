using System;
using UnityEngine;

namespace ThePitt.Economy
{
    /// <summary>The player's cash ("scrip").</summary>
    public class PlayerWallet : MonoBehaviour
    {
        [SerializeField, Min(0)] private int startingMoney = 50;

        public int Money { get; private set; }

        /// <summary>Fired with the new balance.</summary>
        public event Action<int> Changed;

        private void Awake() => Money = startingMoney;

        public void Add(int amount)
        {
            if (amount <= 0) return;
            Money += amount;
            Changed?.Invoke(Money);
        }

        /// <summary>Sets the balance outright (loading a save).</summary>
        public void Set(int amount)
        {
            Money = Mathf.Max(0, amount);
            Changed?.Invoke(Money);
        }

        public bool TrySpend(int amount)
        {
            if (amount < 0 || amount > Money) return false;
            Money -= amount;
            Changed?.Invoke(Money);
            return true;
        }
    }
}
