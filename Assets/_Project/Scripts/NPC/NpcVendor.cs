using System;
using ThePitt.Core;
using ThePitt.Economy;
using ThePitt.Items;
using UnityEngine;

namespace ThePitt.NPC
{
    /// <summary>
    /// An NPC who sells goods to the player from a limited stock that refills each in-game day.
    /// Sits beside an <see cref="NpcBuyer"/>; the trade window shows both sides.
    /// </summary>
    public class NpcVendor : MonoBehaviour
    {
        [Serializable]
        public class StockEntry
        {
            public ItemDefinition item;
            [Min(1)] public int price = 10;
            [Tooltip("How many they have each day.")]
            [Min(1)] public int dailyStock = 5;
            [NonSerialized] public int remaining;
        }

        [SerializeField] private StockEntry[] stock;

        public StockEntry[] Stock => stock;

        /// <summary>Gives a spawned vendor its own copy of a stock list. Call before its first frame.</summary>
        public void Setup(StockEntry[] entries)
        {
            stock = new StockEntry[entries.Length];
            for (int i = 0; i < entries.Length; i++)
                stock[i] = new StockEntry { item = entries[i].item, price = entries[i].price, dailyStock = entries[i].dailyStock };
        }

        private void Start()
        {
            Restock();
            if (TimeManager.Instance != null) TimeManager.Instance.DayChanged += OnDayChanged;
        }

        private void OnDestroy()
        {
            if (TimeManager.Instance != null) TimeManager.Instance.DayChanged -= OnDayChanged;
        }

        private void OnDayChanged(int _) => Restock();

        private void Restock()
        {
            if (stock == null) return;
            foreach (var s in stock) s.remaining = s.dailyStock;
        }

        /// <summary>Sells one unit of a stock entry. Returns false if out of stock, unaffordable or no room.</summary>
        public bool SellOne(int index, Inventory inventory, PlayerWallet wallet)
        {
            var s = stock[index];
            if (s.item == null || s.remaining <= 0 || wallet.Money < s.price) return false;
            if (inventory.SpaceFor(s.item) <= 0 || !wallet.TrySpend(s.price)) return false;
            inventory.Add(s.item, 1);
            s.remaining--;
            return true;
        }
    }
}
