using ThePitt.Items;
using ThePitt.Production;
using UnityEngine;

namespace ThePitt.Factions
{
    /// <summary>
    /// A gang's identity and how it behaves in the territory simulation.
    /// Create via Assets > Create > The Pitt > Faction.
    /// </summary>
    [CreateAssetMenu(menuName = "The Pitt/Faction", fileName = "Faction_")]
    public class FactionDefinition : ScriptableObject
    {
        [SerializeField] private string displayName = "Gang";
        [SerializeField] private Color color = Color.red;
        [Tooltip("Name of the district (palette name, e.g. \"Sodium Yards\") this gang is based out of.")]
        [SerializeField] private string homeDistrict;

        [Header("Start")]
        [Tooltip("Abstract strength. Spent on claiming ground and lost in turf wars.")]
        [SerializeField, Min(0f)] private float startingPower = 50f;
        [Tooltip("Cells of the home district within this distance of the gang's starting corner start owned.")]
        [SerializeField, Min(0f)] private float startingRadius = 40f;
        [Tooltip("Lone cells held elsewhere on the map from the start (e.g. safe houses).")]
        [SerializeField, Min(0)] private int outposts;

        [Header("Growth (per in-game day)")]
        [SerializeField, Min(0f)] private float dailyGrowth = 4f;
        [Tooltip("Power earned per territory cell held.")]
        [SerializeField, Min(0f)] private float incomePerCell = 0.05f;
        [Tooltip("Expansion attempts per day for every 100 power.")]
        [SerializeField, Min(0f)] private float expansionRate = 3f;

        [Header("Conflict")]
        [Tooltip("Chance to push into a rival's cell when there is no free ground to take, and attack strength.")]
        [SerializeField, Range(0f, 2f)] private float aggression = 0.5f;
        [Tooltip("Multiplier on strength when holding a cell against an attacker.")]
        [SerializeField, Range(0.1f, 3f)] private float defense = 1f;

        [Header("Player Supply")]
        [Tooltip("The goods this gang values most from the player.")]
        [SerializeField] private ItemCategory favouredSupply = ItemCategory.Weapon;
        [Tooltip("Extra expansion from recent player supply. High values let a gang surge when fed.")]
        [SerializeField, Min(0f)] private float supplySurge = 1f;
        [SerializeField, Range(-100f, 100f)] private float startingStanding;

        [Header("Relations")]
        [Tooltip("Standing needed before they'll agree to an alliance.")]
        [SerializeField, Range(0f, 100f)] private float allianceStanding = 50f;
        [Tooltip("How readily they warm to the player: standing gained from tribute and supply is multiplied by this.")]
        [SerializeField, Min(0f)] private float goodwill = 1f;
        [Tooltip("Their own recipe. Unlocks at Friendly or when allied, or can be bought unless at war.")]
        [SerializeField] private RecipeDefinition signatureRecipe;
        [SerializeField, Min(0)] private int recipePrice = 400;
        [Tooltip("How well their daily requests pay, as a multiplier.")]
        [SerializeField, Min(0f)] private float dealGenerosity = 1f;

        /// <summary>
        /// The player's own gang: made at runtime when they found it. It has no home district and grows only
        /// as fast as its crew and supplies let it.
        /// </summary>
        public static FactionDefinition CreatePlayerGang(string name, Color colour)
        {
            var def = CreateInstance<FactionDefinition>();
            def.name = PlayerGangId;
            def.displayName = name;
            def.color = colour;
            def.homeDistrict = "";
            def.startingPower = 20f;
            def.startingRadius = 0f;
            def.dailyGrowth = 0f;
            def.incomePerCell = 0.05f;
            def.defense = 1.2f;
            def.favouredSupply = ItemCategory.Food;
            def.goodwill = 1f;
            return def;
        }

        public const string PlayerGangId = "Faction_PlayerGang";
        public bool IsPlayerGang => name == PlayerGangId;

        /// <summary>How hard the player's gang expands, set by its stance. Only for the player's gang.</summary>
        public void SetStance(float expansion, float aggressiveness)
        {
            expansionRate = expansion;
            aggression = aggressiveness;
        }

        public void Rename(string newName) => displayName = newName;

        public string DisplayName => displayName;
        public Color Color => color;
        public string HomeDistrict => homeDistrict;
        public float StartingPower => startingPower;
        public float StartingRadius => startingRadius;
        public int Outposts => outposts;
        public float DailyGrowth => dailyGrowth;
        public float IncomePerCell => incomePerCell;
        public float ExpansionRate => expansionRate;
        public float Aggression => aggression;
        public float Defense => defense;
        public ItemCategory FavouredSupply => favouredSupply;
        public float SupplySurge => supplySurge;
        public float StartingStanding => startingStanding;
        public float AllianceStanding => allianceStanding;
        public float Goodwill => goodwill;
        public RecipeDefinition SignatureRecipe => signatureRecipe;
        public int RecipePrice => recipePrice;
        public float DealGenerosity => dealGenerosity;
    }
}
