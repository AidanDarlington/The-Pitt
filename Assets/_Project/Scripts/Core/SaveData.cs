using System;
using System.Collections.Generic;
using UnityEngine;

namespace ThePitt.Core
{
    /// <summary>A component that stores its own state in a save, as a JSON string it reads back on load.</summary>
    public interface ISaveState
    {
        string CaptureState();
        void RestoreState(string json);
    }

    /// <summary>Everything written to a save file. Assets are referenced by name through <see cref="SaveRegistry"/>.</summary>
    [Serializable]
    public class SaveData
    {
        public const int CurrentVersion = 1;

        public int version = CurrentVersion;
        public string savedAt;
        /// <summary>Whose save this is (the folder it lives in).</summary>
        public string character;
        /// <summary>The seed the character's world is built from.</summary>
        public int seed;
        /// <summary><see cref="World.PittGreybox.GeneratorVersion"/> when saved: another version builds a different city from the seed.</summary>
        public int generatorVersion;

        public int day = 1;
        public float timeOfDay;

        public PlayerSave player = new();
        public DebtSave debt = new();
        public FactionsSave factions = new();
        public List<StructureSave> structures = new();
        public ScrapPitSave scrapPit = new();
        public CorpsePileSave corpsePiles = new();
        /// <summary>Null in saves from before Watcher raids.</summary>
        public WatcherRaidsSave raids;
        /// <summary>Ids of the properties the player owns. Null in saves from before properties existed.</summary>
        public List<string> ownedProperties;
        /// <summary>The property the player was last in; loading puts them back inside it. Empty if none.</summary>
        public string lastProperty;
        /// <summary>Gang recipes the player has learned. Null in older saves.</summary>
        public List<string> learnedRecipes;
        /// <summary>Every item the player has ever had (what unlocks recipes). Null in older saves.</summary>
        public List<string> discoveredItems;
        /// <summary>Kinds of station the player has set up (what unlocks their recipes). Null in older saves.</summary>
        public List<int> knownStations;
        /// <summary>Caps raised and rooms opened at each property. Null in older saves.</summary>
        public List<PropertySave> propertyUpgrades;
        /// <summary>Each business's work, stock and quality, by its property. Null in older saves.</summary>
        public List<BusinessSave> businesses;
        /// <summary>Experience in each skill. Null in older saves.</summary>
        public List<Progression.SkillSave> skills;
    }

    [Serializable]
    public class ItemStackSave
    {
        public string item;
        public int quantity;
        public int quality;
    }

    [Serializable]
    public class PlayerSave
    {
        public Vector3 position;
        public float yaw;
        public float pitch;
        public float health, hunger, thirst;
        public int money;
        public List<ItemStackSave> inventory = new();
        public List<EffectSave> effects = new();
        /// <summary>The weapon in hand (empty = fists), and what's loaded in each firearm.</summary>
        public string equipped;
        public int equippedQuality;
        public List<ItemStackSave> loaded = new();
        /// <summary>Selected hotbar slot (-1 for none, as in older saves) and whether the hands were down.</summary>
        public int hotbarSlot = -1;
        public bool handsDown;
    }

    [Serializable]
    public class EffectSave
    {
        public string item;
        public int minutesLeft;
    }

    [Serializable]
    public class DebtSave
    {
        public int remaining;
        public int paidThisPeriod;
        public int dueDay;
        public int missedPayments;
        public int overdue;
        public int overduePeriods;
    }

    [Serializable]
    public class FactionsSave
    {
        public List<FactionSave> factions = new();
        /// <summary>Owner of each territory cell, as an index into <see cref="factions"/> (-1 = unclaimed).</summary>
        public int[] owners;
        public string dominant;
        public List<OpinionSave> opinions = new();
        public PlayerGangSave playerGang = new();
    }

    [Serializable]
    public class FactionSave
    {
        public string id;
        public float power;
        public float standing;
        public float pendingSupply;
        public float recentSupply;
        public float expansionCarry;
        public bool eliminated;
        public float hostileUntil;
        public bool allied;
        public bool atWar;
        public int requestCategory;
        /// <summary>The item asked for (empty in saves from when requests were for any goods of a kind).</summary>
        public string requestItem;
        public int requestCount;
        public int requestReward;
        public bool requestOpen;
    }

    [Serializable]
    public class BusinessSave
    {
        public string property;
        public string json;
    }

    [Serializable]
    public class PropertySave
    {
        public string id;
        public Vector3Int raises;
        public int expansions;
    }

    [Serializable]
    public class OpinionSave
    {
        public string a, b;
        public float opinion;
    }

    [Serializable]
    public class PlayerGangSave
    {
        public bool founded;
        public string name;
        public Color colour;
        public int members;
        public float strength;
        public int stance;
        public List<string> news = new();
    }

    [Serializable]
    public class StructureSave
    {
        public string item;
        public Vector3 position;
        public Quaternion rotation;
        /// <summary>The property it stands in, and where relative to the middle of its main room, so it stays put when the city is regenerated.</summary>
        public string property;
        public Vector3 local;
        /// <summary>One entry per <see cref="ISaveState"/> component on the structure, in hierarchy order.</summary>
        public List<ComponentSave> components = new();
    }

    [Serializable]
    public class ComponentSave
    {
        public string type;
        public string json;
    }

    [Serializable]
    public class ScrapPitSave
    {
        public int minutesUntilSpawn;
        public List<ScrapSave> pieces = new();
        public int minutesUntilNode;
        public List<ScrapNodeSave> nodes = new();
    }

    [Serializable]
    public class WatcherRaidsSave
    {
        public int lastRollDay;
        public List<PlannedRaidSave> planned = new();
    }

    [Serializable]
    public class PlannedRaidSave
    {
        public string property;
        public int day, hour;
        public bool utilities, debt;
    }

    [Serializable]
    public class CorpsePileSave
    {
        public int minutesUntilPile;
        public List<ScrapNodeSave> piles = new();
    }

    /// <summary>A harvest node (scrap heap or corpse pile) and how much of it is left.</summary>
    [Serializable]
    public class ScrapNodeSave
    {
        public Vector3 position;
        public float yaw;
        public float fraction;
    }

    [Serializable]
    public class ScrapSave
    {
        public int loot;
        public int quantity;
        public Vector3 position;
        public float yaw;
    }
}
