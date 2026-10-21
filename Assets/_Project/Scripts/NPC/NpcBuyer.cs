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
    /// An individual NPC who buys goods at fixed prices. Has their own cash, refreshed each in-game day,
    /// so one buyer can't take everything the player carries.
    /// </summary>
    public class NpcBuyer : MonoBehaviour, IInteractable
    {
        [SerializeField] private string npcName = "Stranger";
        [SerializeField] private NpcBuyerProfile profile;
        [Tooltip("Gang members only. Leave empty to belong to whichever gang holds the ground they stand on.")]
        [SerializeField] private FactionDefinition faction;

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
        }

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
            float price = item.ValueAt(quality) * multiplier;
            return Mathf.Max(1, Mathf.RoundToInt(price));
        }

        /// <summary>
        /// Buys <paramref name="count"/> of an item at one quality, all or nothing. Returns the scrip paid, or 0
        /// if they won't buy it, can't afford the lot, or the player doesn't have that many.
        /// </summary>
        public int Buy(Inventory inventory, ItemDefinition item, ItemQuality quality, int count, PlayerWallet wallet)
        {
            if (count <= 0 || inventory.Count(item, quality) < count) return 0;
            int total = OfferFor(item, quality) * count;
            if (total <= 0 || total > Cash) return 0;

            inventory.Remove(item, count, quality);
            Cash -= total;
            wallet.Add(total);
            if (Faction is { } gang) FactionManager.Instance.RecordSupply(gang, item.Category, total);
            return total;
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

        /// <summary>Has something to trade: buys from the player, or sells (a dealer sells but doesn't buy).</summary>
        private bool Trades => profile.Buys || (TryGetComponent<NpcVendor>(out var vendor) && vendor.Stock is { Length: > 0 });

        public string GetPrompt(PlayerInteractor interactor) =>
            !Trades ? null :
            Recruited ? $"{npcName} · off to join your crew" :
            Refuses ? $"{npcName} ({Faction.Name}) won't deal with you"
                    : $"Trade with {npcName} ({(Faction is { } gang ? gang.Name : profile.TypeName)})";

        public bool CanInteract(PlayerInteractor interactor) =>
            Trades && TradeUI.Instance != null && (!TryGetComponent<NpcCombat>(out var combat) || combat.IsCalm) && !Refuses && !Recruited;

        /// <summary>A gang member whose gang won't deal with the player.</summary>
        private bool Refuses => Faction is { } gang && !FactionManager.Instance.WillTrade(gang);

        public void Interact(PlayerInteractor interactor) => TradeUI.Instance.Open(this, interactor);
    }
}
