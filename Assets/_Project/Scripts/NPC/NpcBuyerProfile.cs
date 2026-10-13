using ThePitt.Items;
using UnityEngine;

namespace ThePitt.NPC
{
    public enum NpcType
    {
        Civilian,
        Junkie,
        GangMember,
        Dealer,
    }

    /// <summary>How a type of NPC values goods. Create via Assets > Create > The Pitt > NPC Buyer Profile.</summary>
    [CreateAssetMenu(menuName = "The Pitt/NPC Buyer Profile", fileName = "Buyer_")]
    public class NpcBuyerProfile : ScriptableObject
    {
        [SerializeField] private NpcType npcType;
        [Tooltip("Most wanted first. Categories not listed are not bought at all; none listed, they don't trade.")]
        [SerializeField] private ItemCategory[] priorities = { ItemCategory.Food, ItemCategory.Drug, ItemCategory.Weapon };
        [Tooltip("Price multiplier for each priority rank (1st, 2nd, 3rd...).")]
        [SerializeField] private float[] priorityMultipliers = { 1.3f, 1f, 0.7f };
        [Tooltip("Average scrip carried each day. Individuals vary around this. What they pay comes from their priorities, not from how much they carry.")]
        [SerializeField, Min(0)] private int dailyCash = 120;
        [SerializeField, Range(0f, 1f)] private float cashVariance = 0.3f;
        [Tooltip("How much each unit bought lowers demand for that category until the next day.")]
        [SerializeField, Range(0f, 1f)] private float demandDropPerUnit = 0.08f;
        [SerializeField, Range(0f, 1f)] private float minimumDemand = 0.4f;

        public NpcType NpcType => npcType;
        public int DailyCash => dailyCash;
        public float CashVariance => cashVariance;
        public float DemandDropPerUnit => demandDropPerUnit;
        public float MinimumDemand => minimumDemand;

        /// <summary>0-based rank of a category, or -1 if this NPC won't buy it.</summary>
        public int RankOf(ItemCategory category) => System.Array.IndexOf(priorities, category);

        /// <summary>Whether they buy anything at all.</summary>
        public bool Buys => priorities != null && priorities.Length > 0;

        public float MultiplierForRank(int rank) =>
            rank < 0 ? 0f : priorityMultipliers[Mathf.Min(rank, priorityMultipliers.Length - 1)];

        public string TypeName => npcType == NpcType.GangMember ? "Gang Member" : npcType.ToString();
    }
}
