using System.Collections.Generic;
using ThePitt.Core;
using ThePitt.Economy;
using ThePitt.Factions;
using ThePitt.Interaction;
using ThePitt.Items;
using ThePitt.UI;
using UnityEngine;

namespace ThePitt.NPC
{
    /// <summary>
    /// An individual NPC who buys goods. Has their own cash and per-category demand,
    /// both refreshed each in-game day.
    /// </summary>
    public class NpcBuyer : MonoBehaviour, IInteractable
    {
        [SerializeField] private string npcName = "Stranger";
        [SerializeField] private NpcBuyerProfile profile;
        [Tooltip("Gang members only. Leave empty to belong to whichever gang holds the ground they stand on.")]
        [SerializeField] private FactionDefinition faction;

        private readonly Dictionary<ItemCategory, float> demand = new();

        public string NpcName => npcName;
        public NpcBuyerProfile Profile => profile;
        public int Cash { get; private set; }

        /// <summary>The gang a gang member buys for, or null for everyone else.</summary>
        public FactionManager.FactionState Faction
        {
            get
            {
                var manager = FactionManager.Instance;
                if (manager == null || profile.NpcType != NpcType.GangMember) return null;
                return faction != null ? manager.Get(faction) : manager.OwnerAt(transform.position);
            }
        }

        /// <summary>Sets up a spawned NPC. Call before its first frame (Start restocks from the profile).</summary>
        public void Setup(string displayName, NpcBuyerProfile buyerProfile, FactionDefinition gang = null)
        {
            npcName = displayName;
            profile = buyerProfile;
            faction = gang;
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
            float variance = Random.Range(-profile.CashVariance, profile.CashVariance);
            Cash = Mathf.RoundToInt(profile.DailyCash * (1f + variance));
            demand.Clear();
        }

        private float DemandFor(ItemCategory category) => demand.TryGetValue(category, out var d) ? d : 1f;

        /// <summary>Price this NPC pays for one unit, or 0 if they won't buy it.</summary>
        public int OfferFor(ItemDefinition item, ItemQuality quality)
        {
            if (item == null) return 0;
            int rank = profile.RankOf(item.Category);
            if (rank < 0) return 0;
            float multiplier = profile.MultiplierForRank(rank);
            if (Faction is { } gang)
            {
                multiplier *= FactionManager.Instance.PriceMultiplier(gang) * FactionManager.Instance.TradeMultiplier(gang);
                // A friendly or desperate gang pays more, but never more for something than they'd pay for what
                // they want more: a gang member never outbids a junkie for drugs.
                if (rank > 0) multiplier = Mathf.Min(multiplier, profile.MultiplierForRank(rank - 1));
            }
            float price = item.ValueAt(quality) * multiplier * DemandFor(item.Category);
            return Mathf.Max(1, Mathf.RoundToInt(price));
        }

        /// <summary>Buys one unit from an inventory slot. Returns the price paid, or 0 if no sale.</summary>
        public int BuyOne(Inventory inventory, int slot, PlayerWallet wallet)
        {
            var stack = inventory.GetSlot(slot);
            if (stack.IsEmpty) return 0;
            int price = OfferFor(stack.item, stack.quality);
            if (price <= 0 || price > Cash) return 0;

            inventory.RemoveAt(slot, 1);
            Cash -= price;
            wallet.Add(price);

            var category = stack.item.Category;
            demand[category] = Mathf.Max(profile.MinimumDemand, DemandFor(category) - profile.DemandDropPerUnit);
            if (Faction is { } gang) FactionManager.Instance.RecordSupply(gang, category, price);
            return price;
        }

        /// <summary>Recruited into the player's gang: off to join the crew, no more trading.</summary>
        public bool Recruited { get; private set; }

        /// <summary>Leaves to join the player's crew: walkers head indoors, NPCs at a post just go.</summary>
        public void JoinPlayerGang()
        {
            Recruited = true;
            if (TryGetComponent<NpcWalker>(out var walker) && walker.enabled) walker.LeaveNow();
            else Destroy(gameObject);
        }

        public string GetPrompt(PlayerInteractor interactor) =>
            !profile.Buys ? null :
            Recruited ? $"{npcName} · off to join your crew" :
            Refuses ? $"{npcName} ({Faction.Name}) won't deal with you"
                    : $"Trade with {npcName} ({(Faction is { } gang ? gang.Name : profile.TypeName)})";

        public bool CanInteract(PlayerInteractor interactor) =>
            profile.Buys && TradeUI.Instance != null && (!TryGetComponent<NpcCombat>(out var combat) || combat.IsCalm) && !Refuses && !Recruited;

        /// <summary>A gang member whose gang won't deal with the player.</summary>
        private bool Refuses => Faction is { } gang && !FactionManager.Instance.WillTrade(gang);

        public void Interact(PlayerInteractor interactor) => TradeUI.Instance.Open(this, interactor);
    }
}
