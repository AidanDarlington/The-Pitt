using UnityEngine;

namespace ThePitt.Items
{
    public enum ItemCategory
    {
        Food,
        Drug,
        Weapon,
        Material,
        Seed,
        Misc,
        Structure,
    }

    public enum WeaponType
    {
        None,
        Melee,
        Firearm,
    }

    /// <summary>What a held item can work, beyond fighting.</summary>
    public enum ToolType
    {
        None,
        /// <summary>Breaks up scrap heaps in the pit.</summary>
        Pickaxe,
        /// <summary>Digs through corpse piles in the alleys.</summary>
        Shovel,
    }

    /// <summary>Static data for one kind of item. Create via Assets > Create > The Pitt > Item.</summary>
    [CreateAssetMenu(menuName = "The Pitt/Item", fileName = "Item_")]
    public class ItemDefinition : ScriptableObject
    {
        [SerializeField] private string displayName = "New Item";
        [SerializeField, TextArea] private string description;
        [SerializeField] private ItemCategory category = ItemCategory.Misc;
        [SerializeField] private Sprite icon;
        [SerializeField, Min(1)] private int maxStack = 20;
        [Tooltip("Sale value at Standard quality before any buyer modifiers.")]
        [SerializeField, Min(0)] private int baseValue = 1;
        [Tooltip("Whether this item's quality varies. If false it is always Standard.")]
        [SerializeField] private bool hasQuality = true;

        [Header("Consumable (all zero = not edible)")]
        [SerializeField] private float nutrition;
        [SerializeField] private float hydration;
        [Tooltip("Health change when consumed. Negative for things like raw meat.")]
        [SerializeField] private float healthEffect;

        [Tooltip("Model shown when it's lying on the ground, baked from its icon model. Empty = a plain box.")]
        [SerializeField] private GameObject worldModel;

        [Header("Placeable (stations and structures)")]
        [Tooltip("If set, clicking this item in the inventory places this prefab in the world.")]
        [SerializeField] private GameObject placeablePrefab;
        [Tooltip("Utilities it draws from its property while installed: water, electricity, gas.")]
        [SerializeField] private Vector3Int utilityDraw;

        [Header("Effect over time (drugs)")]
        [Tooltip("In-game minutes the effect lasts. 0 = no lasting effect.")]
        [SerializeField, Min(0)] private int effectMinutes;
        [Tooltip("Movement speed while active (1 = normal).")]
        [SerializeField, Min(0.1f)] private float moveSpeedMultiplier = 1f;
        [Tooltip("Stat changes when the effect wears off: the comedown.")]
        [SerializeField] private float comedownHealth;
        [SerializeField] private float comedownHunger;
        [SerializeField] private float comedownThirst;

        [Header("Weapon (equip from the inventory)")]
        [SerializeField] private WeaponType weaponType;
        [Tooltip("Damage per hit at Standard quality; better made weapons hit harder.")]
        [SerializeField, Min(0f)] private float damage = 20f;
        [Tooltip("Reach for melee, maximum distance for firearms (metres).")]
        [SerializeField, Min(0.5f)] private float range = 2f;
        [Tooltip("Seconds between swings or shots.")]
        [SerializeField, Min(0.05f)] private float attackInterval = 0.6f;
        [Tooltip("Firearms: what they load.")]
        [SerializeField] private ItemDefinition ammo;
        [Tooltip("Firearms: rounds held before reloading.")]
        [SerializeField, Min(1)] private int magazineSize = 1;
        [SerializeField, Min(0f)] private float reloadSeconds = 1.5f;
        [Tooltip("Firearms: inaccuracy cone, degrees.")]
        [SerializeField, Min(0f)] private float spread = 1.5f;

        [Header("Tool")]
        [SerializeField] private ToolType toolType;
        [Tooltip("How much of a node each strike takes, at Standard quality.")]
        [SerializeField, Min(0f)] private float gatherPower = 10f;

        public string DisplayName => displayName;
        public string Description => description;
        public ItemCategory Category => category;
        public Sprite Icon => icon;
        public int MaxStack => maxStack;
        public int BaseValue => baseValue;
        public bool HasQuality => hasQuality;
        public float Nutrition => nutrition;
        public float Hydration => hydration;
        public float HealthEffect => healthEffect;
        public GameObject WorldModel => worldModel;
        public GameObject PlaceablePrefab => placeablePrefab;
        public bool IsPlaceable => placeablePrefab != null;
        /// <summary>Water, electricity and gas this structure draws while installed.</summary>
        public Vector3Int UtilityDraw => utilityDraw;
        public int EffectMinutes => effectMinutes;
        public float MoveSpeedMultiplier => moveSpeedMultiplier;
        public float ComedownHealth => comedownHealth;
        public float ComedownHunger => comedownHunger;
        public float ComedownThirst => comedownThirst;
        public bool HasLastingEffect => effectMinutes > 0;
        public WeaponType WeaponType => weaponType;
        public bool IsWeapon => weaponType != WeaponType.None;
        public float Damage => damage;
        public float Range => range;
        public float AttackInterval => attackInterval;
        public ItemDefinition Ammo => ammo;
        public int MagazineSize => magazineSize;
        public float ReloadSeconds => reloadSeconds;
        public float Spread => spread;
        public ToolType ToolType => toolType;
        public float GatherPower => gatherPower;

        /// <summary>Damage per hit for a weapon of this quality: Poor hits 20% softer, Premium 30% harder.</summary>
        public float DamageAt(ItemQuality quality) =>
            damage * (hasQuality ? 0.8f + 0.125f * (int)quality : 1f);
        public bool IsConsumable => nutrition != 0f || hydration != 0f || healthEffect != 0f || effectMinutes > 0;

        /// <summary>Base value adjusted for quality.</summary>
        public int ValueAt(ItemQuality quality) =>
            Mathf.RoundToInt(baseValue * (hasQuality ? quality.ValueMultiplier() : 1f));
    }
}
