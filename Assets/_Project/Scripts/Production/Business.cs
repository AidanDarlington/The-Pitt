using System;
using System.Collections.Generic;
using ThePitt.Audio;
using ThePitt.Core;
using ThePitt.Economy;
using ThePitt.Interaction;
using ThePitt.Items;
using ThePitt.Properties;
using ThePitt.UI;
using UnityEngine;

namespace ThePitt.Production
{
    public enum BusinessKind
    {
        /// <summary>Takes in the city's dead and butchers them into better meat than a corpse pile gives.</summary>
        Morgue,
        /// <summary>Refines scrap metal from the pit into better scrap, so tools and parts made from it are better.</summary>
        Metalworks,
    }

    /// <summary>
    /// A production business: the works in a business property's room, running on the in-game clock while the
    /// player owns the place. Output stacks up on it to be collected. Each runs at a quality tier that can be
    /// bought up (Good, Excellent, Premium) and has staff whose wages come out of the wallet every midnight;
    /// unpaid, the work stops until the wages are settled.
    /// <list type="bullet">
    /// <item>Morgue: bodies come in by themselves (the city's dead, and anyone killed in the streets once their
    /// body is cleared away) and are butchered one at a time into meat, sometimes with something from their pockets.</item>
    /// <item>Metalworks: the player loads scrap metal of any quality into the hopper; it comes out refined, a little
    /// less of it, at the works' quality.</item>
    /// </list>
    /// </summary>
    public class Business : MonoBehaviour, IInteractable, ISaveState
    {
        private static readonly List<Business> all = new();

        [SerializeField] private BusinessKind kind;
        [SerializeField] private Property property;
        [Tooltip("What it makes: meat at a morgue, scrap metal at a metalworks.")]
        [SerializeField] private ItemDefinition product;
        [Tooltip("Metalworks: what goes in the hopper.")]
        [SerializeField] private ItemDefinition feedstock;
        [Tooltip("Morgue: what turns up in the pockets now and then.")]
        [SerializeField] private ItemDefinition[] pocketFinds = Array.Empty<ItemDefinition>();

        [Header("Work")]
        [Tooltip("In-game minutes per body (morgue) or per batch (metalworks).")]
        [SerializeField, Min(1)] private int minutesPerJob = 60;
        [Tooltip("Morgue: meat from each body. Metalworks: refined scrap from each batch.")]
        [SerializeField] private Vector2Int yield = new(3, 5);
        [Tooltip("Metalworks: scrap that goes into each batch.")]
        [SerializeField, Min(1)] private int batchSize = 5;
        [Tooltip("Morgue: hours between bodies brought in from the streets on their own.")]
        [SerializeField, Min(1f)] private float hoursPerBody = 6f;
        [SerializeField, Range(0f, 1f)] private float pocketChance = 0.3f;
        [Tooltip("Most finished goods it holds; it stops when full.")]
        [SerializeField, Min(1)] private int capacity = 80;

        [Header("Money")]
        [Tooltip("Scrip a day in wages, paid at midnight.")]
        [SerializeField, Min(0)] private int wages = 50;
        [Tooltip("Scrip to raise the quality to Excellent, then to Premium.")]
        [SerializeField] private int[] upgradeCosts = { 1500, 3000 };

        private static readonly ItemQuality[] Tiers = { ItemQuality.Good, ItemQuality.Excellent, ItemQuality.Premium };
        private static readonly Color Accent = new(0.95f, 0.72f, 0.3f);

        private int tier;
        private bool unpaid;
        private int bodies;
        private int minutesToBody;
        private int minutesLeft;
        private bool working;
        private readonly List<ItemStack> hopper = new();
        private readonly List<ItemStack> stock = new();

        public static IReadOnlyList<Business> All => all;
        public BusinessKind Kind => kind;
        public Property Property => property;
        public string Name => kind == BusinessKind.Morgue ? "Morgue" : "Metalworks";
        public ItemDefinition Product => product;
        public ItemDefinition Feedstock => feedstock;
        public ItemQuality Quality => Tiers[Mathf.Clamp(tier, 0, Tiers.Length - 1)];
        public int Tier => tier;
        public bool CanUpgrade => tier < Tiers.Length - 1 && tier < upgradeCosts.Length;
        public int UpgradeCost => CanUpgrade ? upgradeCosts[tier] : 0;
        public ItemQuality NextQuality => Tiers[Mathf.Min(tier + 1, Tiers.Length - 1)];
        public int Wages => wages;
        public bool Unpaid => unpaid;
        public bool Owned => property != null && property.Owned;
        /// <summary>Bodies waiting at the morgue, not counting the one on the slab.</summary>
        public int Bodies => bodies;
        public int HopperCount
        {
            get
            {
                int n = 0;
                foreach (var s in hopper) n += s.quantity;
                return n;
            }
        }
        public int StockCount
        {
            get
            {
                int n = 0;
                foreach (var s in stock) n += s.quantity;
                return n;
            }
        }
        public IReadOnlyList<ItemStack> Stock => stock;
        public bool Full => StockCount >= capacity;
        public bool Working => working;
        public float Progress => working ? 1f - minutesLeft / (float)minutesPerJob : 0f;
        public int MinutesPerJob => minutesPerJob;
        public int BatchSize => batchSize;
        public Vector2Int Yield => yield;
        public float HoursPerBody => hoursPerBody;

        /// <summary>Fired whenever work advances, goods are collected or anything is bought.</summary>
        public event Action Changed;

        /// <summary>The business run from a property, if it is one.</summary>
        public static Business Of(Property property)
        {
            foreach (var b in all)
                if (b.property == property) return b;
            return null;
        }

        /// <summary>Called by the generator.</summary>
        public void Setup(BusinessKind businessKind, Property owner, ItemDefinition makes, ItemDefinition takes, ItemDefinition[] finds, int dailyWages, int[] costs)
        {
            kind = businessKind;
            property = owner;
            product = makes;
            feedstock = takes;
            pocketFinds = finds ?? Array.Empty<ItemDefinition>();
            wages = dailyWages;
            upgradeCosts = costs;
            if (kind == BusinessKind.Metalworks)
            {
                minutesPerJob = 45;
                yield = new Vector2Int(4, 4);
                batchSize = 5;
            }
            else
            {
                minutesPerJob = 60;
                yield = new Vector2Int(3, 5);
            }
        }

        private void Awake()
        {
            minutesToBody = Mathf.RoundToInt(hoursPerBody * 60f);
        }

        private void OnEnable()
        {
            all.Add(this);
            NPC.NpcCorpse.Cleared += OnBodyCleared;
            Subscribe(true);
        }

        private void OnDisable()
        {
            all.Remove(this);
            NPC.NpcCorpse.Cleared -= OnBodyCleared;
            Subscribe(false);
        }

        private void Start()
        {
            // The clock may have come up after this did.
            Subscribe(false);
            Subscribe(true);
        }

        private void Subscribe(bool on)
        {
            var time = TimeManager.Instance;
            if (time == null) return;
            if (on)
            {
                time.MinuteChanged += OnMinute;
                time.DayChanged += OnDay;
            }
            else
            {
                time.MinuteChanged -= OnMinute;
                time.DayChanged -= OnDay;
            }
        }

        /// <summary>Says what the player just bought and how it works. Called by the door on purchase.</summary>
        public void AnnounceBought()
        {
            UnlockPopupUI.Notify("BUSINESS", $"{property.DisplayName} is yours",
                kind == BusinessKind.Morgue
                    ? $"The dead come to you now. Bodies are butchered into {Quality} meat. Wages {wages} scrip a day."
                    : $"Load scrap metal into the hopper; it comes out {Quality}. Wages {wages} scrip a day.",
                Accent, 10f);
        }

        // ---------------------------------------------------------------- the clock

        private void OnMinute(int _)
        {
            if (!Owned || unpaid) return;
            if (kind == BusinessKind.Morgue && --minutesToBody <= 0)
            {
                minutesToBody = Mathf.RoundToInt(hoursPerBody * 60f);
                bodies++;
            }

            if (!working) TryStart();
            else if (--minutesLeft <= 0) Finish();
            Changed?.Invoke();
        }

        private void TryStart()
        {
            if (Full) return;
            if (kind == BusinessKind.Morgue)
            {
                if (bodies <= 0) return;
                bodies--;
            }
            else
            {
                if (HopperCount < batchSize) return;
                TakeFromHopper(batchSize);
            }
            working = true;
            minutesLeft = minutesPerJob;
        }

        private void Finish()
        {
            working = false;
            AddStock(product, UnityEngine.Random.Range(yield.x, yield.y + 1), Quality);
            if (kind == BusinessKind.Morgue && pocketFinds.Length > 0 && UnityEngine.Random.value < pocketChance)
            {
                var find = pocketFinds[UnityEngine.Random.Range(0, pocketFinds.Length)];
                if (find != null) AddStock(find, UnityEngine.Random.Range(1, 3), Quality);
            }
            TryStart();
        }

        private void OnDay(int _)
        {
            if (!Owned) return;
            if (!PayWages(FindFirstObjectByType<PlayerWallet>()))
                UnlockPopupUI.Notify("BUSINESS", $"{property.DisplayName}: wages unpaid",
                    $"You couldn't cover the {wages} scrip. Work has stopped until you pay up at the {Name.ToLowerInvariant()}.",
                    new Color(0.9f, 0.3f, 0.25f), 10f);
        }

        /// <summary>Pays the day's wages (and clears anything owed). False if the wallet can't cover it.</summary>
        public bool PayWages(PlayerWallet wallet)
        {
            if (wallet == null || !wallet.TrySpend(wages))
            {
                unpaid = true;
                Changed?.Invoke();
                return false;
            }
            unpaid = false;
            Changed?.Invoke();
            return true;
        }

        /// <summary>A body cleared off the streets goes to the morgue, if the player owns one.</summary>
        private void OnBodyCleared()
        {
            if (kind != BusinessKind.Morgue || !Owned) return;
            // Only the first owned morgue takes it.
            foreach (var b in all)
                if (b.kind == BusinessKind.Morgue && b.Owned)
                {
                    if (b == this) bodies++;
                    return;
                }
        }

        // ---------------------------------------------------------------- the player

        /// <summary>Metalworks: tips every bit of scrap the player carries into the hopper. Returns how much went in.</summary>
        public int Load(Inventory inventory)
        {
            if (kind != BusinessKind.Metalworks || feedstock == null || inventory == null) return 0;
            int loaded = 0;
            for (var q = ItemQuality.Poor; q <= ItemQuality.Premium; q++)
            {
                int n = inventory.Remove(feedstock, inventory.Count(feedstock, q), q);
                if (n <= 0) continue;
                AddTo(hopper, feedstock, n, q);
                loaded += n;
            }
            if (loaded > 0 && !working) TryStart();
            Changed?.Invoke();
            return loaded;
        }

        /// <summary>Moves finished goods into the inventory. Returns how many were taken; what doesn't fit stays.</summary>
        public int Collect(Inventory inventory)
        {
            if (inventory == null) return 0;
            int taken = 0;
            for (int i = stock.Count - 1; i >= 0; i--)
            {
                var s = stock[i];
                int n = inventory.Add(s.item, s.quantity, s.quality);
                taken += n;
                s.quantity -= n;
                if (s.quantity <= 0) stock.RemoveAt(i);
                else stock[i] = s;
            }
            if (taken > 0 && !working) TryStart();
            Changed?.Invoke();
            return taken;
        }

        /// <summary>Buys the next quality tier. False if there isn't one or the wallet can't cover it.</summary>
        public bool Upgrade(PlayerWallet wallet)
        {
            if (!CanUpgrade || wallet == null || !wallet.TrySpend(UpgradeCost)) return false;
            tier++;
            Changed?.Invoke();
            return true;
        }

        private void TakeFromHopper(int amount)
        {
            // Worst first: refining is what it's for.
            hopper.Sort((a, b) => a.quality.CompareTo(b.quality));
            for (int i = 0; i < hopper.Count && amount > 0;)
            {
                var s = hopper[i];
                int n = Mathf.Min(s.quantity, amount);
                s.quantity -= n;
                amount -= n;
                if (s.quantity <= 0) hopper.RemoveAt(i);
                else
                {
                    hopper[i] = s;
                    i++;
                }
            }
        }

        private void AddStock(ItemDefinition item, int amount, ItemQuality quality) => AddTo(stock, item, amount, quality);

        private static void AddTo(List<ItemStack> list, ItemDefinition item, int amount, ItemQuality quality)
        {
            if (item == null || amount <= 0) return;
            var q = item.HasQuality ? quality : ItemQuality.Standard;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].item != item || list[i].quality != q) continue;
                var s = list[i];
                s.quantity += amount;
                list[i] = s;
                return;
            }
            list.Add(new ItemStack(item, amount, q));
        }

        // ---------------------------------------------------------------- interaction

        public string GetPrompt(PlayerInteractor interactor)
        {
            if (!Owned) return null;
            int ready = StockCount;
            return ready > 0 ? $"Run the {Name.ToLowerInvariant()} · {ready} ready" : $"Run the {Name.ToLowerInvariant()}";
        }

        public bool CanInteract(PlayerInteractor interactor) => Owned && BusinessUI.Instance != null && !BusinessUI.Instance.IsOpen;

        public void Interact(PlayerInteractor interactor)
        {
            AudioManager.Play2D(AudioManager.Library?.click, 0.5f);
            BusinessUI.Instance.Open(this, interactor);
        }

        // ---------------------------------------------------------------- saving

        [Serializable]
        private class State
        {
            public int tier;
            public bool unpaid;
            public int bodies;
            public int minutesToBody;
            public int minutesLeft;
            public bool working;
            public List<ItemStackSave> hopper = new();
            public List<ItemStackSave> stock = new();
        }

        public string CaptureState()
        {
            var s = new State { tier = tier, unpaid = unpaid, bodies = bodies, minutesToBody = minutesToBody, minutesLeft = minutesLeft, working = working };
            foreach (var h in hopper) s.hopper.Add(ToSave(h));
            foreach (var h in stock) s.stock.Add(ToSave(h));
            return JsonUtility.ToJson(s);
        }

        public void RestoreState(string json)
        {
            var s = JsonUtility.FromJson<State>(json);
            if (s == null) return;
            tier = Mathf.Clamp(s.tier, 0, Tiers.Length - 1);
            unpaid = s.unpaid;
            bodies = s.bodies;
            minutesToBody = s.minutesToBody > 0 ? s.minutesToBody : Mathf.RoundToInt(hoursPerBody * 60f);
            minutesLeft = s.minutesLeft;
            working = s.working;
            hopper.Clear();
            stock.Clear();
            foreach (var h in s.hopper) FromSave(h, hopper);
            foreach (var h in s.stock) FromSave(h, stock);
            Changed?.Invoke();
        }

        private static ItemStackSave ToSave(ItemStack s) => new() { item = SaveRegistry.IdOf(s.item), quantity = s.quantity, quality = (int)s.quality };

        private static void FromSave(ItemStackSave s, List<ItemStack> into)
        {
            var item = SaveRegistry.Lookup<ItemDefinition>(s.item);
            if (item != null && s.quantity > 0) AddTo(into, item, s.quantity, (ItemQuality)s.quality);
        }
    }
}
