using System;
using System.Collections.Generic;
using ThePitt.Core;
using ThePitt.Economy;
using ThePitt.Interaction;
using ThePitt.UI;
using UnityEngine;
using Random = UnityEngine.Random;

namespace ThePitt.Properties
{
    /// <summary>
    /// The Watchers raid the player's properties. Every morning each owned property that draws more than its
    /// utility caps rolls its raid risk (how far over the worst utility is: 25% over, 25% chance; at the hard
    /// cap, certain), and while debt payments are overdue a raid on the player's best-stocked property is
    /// rolled too. A warning goes out, and the raid lands later that day.
    /// <list type="bullet">
    /// <item>Over a cap: they seize stations until every utility is back within its cap, biggest draws first.</item>
    /// <item>Overdue debt: they collect what's owed plus a fine, from the wallet first, then by seizing stations.</item>
    /// </list>
    /// Anything put right before they arrive is checked again: they can leave empty-handed.
    /// </summary>
    public class WatcherRaids : MonoBehaviour
    {
        public static WatcherRaids Instance { get; private set; }

        [SerializeField] private PlayerWallet wallet;
        [Tooltip("Hour each morning when the day's raids are decided and warned of.")]
        [SerializeField, Range(0, 23)] private int rollHour = 6;
        [Tooltip("Raids land at a random hour in this range, the same day.")]
        [SerializeField] private Vector2Int raidHours = new(10, 20);

        [Header("Overdue Debt")]
        [Tooltip("Daily chance of a raid while a payment is overdue.")]
        [SerializeField, Range(0f, 1f)] private float debtChance = 0.3f;
        [Tooltip("Added for each further missed period still owed.")]
        [SerializeField, Range(0f, 1f)] private float debtChancePerPeriod = 0.2f;
        [SerializeField, Range(0f, 1f)] private float debtChanceMax = 0.9f;
        [Tooltip("On top of what's overdue, as a fraction of it.")]
        [SerializeField, Min(0f)] private float debtFine = 0.5f;

        private static readonly Color Warning = new(0.95f, 0.72f, 0.25f);
        private static readonly Color Raid = new(0.9f, 0.3f, 0.25f);

        private class Planned
        {
            public Property property;
            public int day, hour;
            public bool utilities, debt;
        }

        private readonly List<Planned> planned = new();
        private int lastRollDay;

        /// <summary>A raid has happened: the property, and a line saying what was taken.</summary>
        public event Action<Property, string> Raided;

        private void Awake()
        {
            Instance = this;
            if (wallet == null) wallet = FindFirstObjectByType<PlayerWallet>();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void OnEnable()
        {
            if (TimeManager.Instance != null) TimeManager.Instance.MinuteChanged += OnMinute;
        }

        private void Start()
        {
            OnDisable();
            OnEnable();
            // A new game doesn't roll on its first morning.
            if (lastRollDay == 0 && TimeManager.Instance != null) lastRollDay = TimeManager.Instance.Day;
        }

        private void OnDisable()
        {
            if (TimeManager.Instance != null) TimeManager.Instance.MinuteChanged -= OnMinute;
        }

        /// <summary>Daily chance of a debt raid right now (0 if nothing is overdue).</summary>
        public float DebtRisk
        {
            get
            {
                var ledger = DebtLedger.Instance;
                if (ledger == null || ledger.Overdue <= 0) return 0f;
                return Mathf.Min(debtChanceMax, debtChance + debtChancePerPeriod * Mathf.Max(0, ledger.OverduePeriods - 1));
            }
        }

        /// <summary>Whether a raid is coming for this property today.</summary>
        public bool IsComing(Property property) => planned.Exists(p => p.property == property);

        private void OnMinute(int _)
        {
            var time = TimeManager.Instance;
            if (time.Day != lastRollDay && time.Hour >= rollHour) Roll(time.Day);
            for (int i = planned.Count - 1; i >= 0; i--)
            {
                var p = planned[i];
                if (time.Day < p.day || (time.Day == p.day && time.Hour < p.hour)) continue;
                planned.RemoveAt(i);
                Carry(p);
            }
        }

        // ---------------------------------------------------------------- deciding

        private void Roll(int day)
        {
            lastRollDay = day;
            foreach (var property in Property.All)
            {
                if (!property.Owned) continue;
                float risk = property.RaidRisk();
                if (risk > 0f && Random.value < risk) Plan(property, day).utilities = true;
            }

            float debtRisk = DebtRisk;
            if (debtRisk > 0f && Random.value < debtRisk)
            {
                var target = BestStocked();
                if (target != null) Plan(target, day).debt = true;
            }

            foreach (var p in planned)
                if (p.day == day) Warn(p);
        }

        private Planned Plan(Property property, int day)
        {
            var p = planned.Find(x => x.property == property);
            if (p != null) return p;
            int from = Mathf.Max(raidHours.x, TimeManager.Instance.Hour + 1);
            p = new Planned { property = property, day = day, hour = Mathf.Min(23, Random.Range(from, Mathf.Max(from, raidHours.y) + 1)) };
            planned.Add(p);
            return p;
        }

        /// <summary>The owned property with the most worth seizing in it.</summary>
        private static Property BestStocked()
        {
            Property best = null;
            int bestValue = -1;
            foreach (var property in Property.All)
            {
                if (!property.Owned) continue;
                int value = 0;
                foreach (var s in StructuresIn(property)) value += s.Item.ValueAt(Items.ItemQuality.Standard);
                if (value > bestValue)
                {
                    best = property;
                    bestValue = value;
                }
            }
            return best;
        }

        private void Warn(Planned p)
        {
            var reasons = new List<string>();
            if (p.utilities) reasons.Add($"{p.property.DisplayName} is drawing too much {OverUtilities(p.property)}");
            if (p.debt) reasons.Add($"you're {DebtLedger.Instance.Overdue:N0} scrip behind on the debt");
            string fix = p.utilities && p.debt ? "Get under the limits and pay up" : p.utilities ? "Get it under the limits" : "Pay what's overdue";
            UnlockPopupUI.Notify("WATCHERS · WARNING", $"A raid on {p.property.DisplayName} today",
                $"Word is {string.Join(", and ", reasons)}. {fix} before they arrive, around {p.hour:00}:00.", Warning, 12f);
            Audio.AudioManager.Play2D(Audio.AudioManager.Library?.click, 0.6f);
        }

        private static string OverUtilities(Property property)
        {
            var used = property.Usage();
            var caps = property.Caps;
            var over = new List<string>();
            for (int i = 0; i < 3; i++)
                if (used[i] > caps[i]) over.Add($"{Property.UtilityNames[i].ToLowerInvariant()} ({used[i]}/{caps[i]})");
            return over.Count > 0 ? string.Join(" and ", over) : "utilities";
        }

        // ---------------------------------------------------------------- the raid

        private void Carry(Planned p)
        {
            var seized = new List<string>();
            int scripTaken = 0;
            var structures = StructuresIn(p.property);

            if (p.utilities) SeizeOverCap(p.property, structures, seized);
            if (p.debt) Collect(structures, seized, ref scripTaken);

            string detail;
            if (seized.Count == 0 && scripTaken == 0) detail = "They turned the place over and left empty-handed.";
            else
            {
                var parts = new List<string>();
                if (seized.Count > 0) parts.Add($"Seized {string.Join(", ", seized)}");
                if (scripTaken > 0) parts.Add($"took {scripTaken:N0} scrip");
                detail = string.Join(" and ", parts) + ".";
            }
            UnlockPopupUI.Notify("WATCHER RAID", $"{p.property.DisplayName} was raided", detail, Raid, 14f);
            Audio.AudioManager.Play2D(Audio.AudioManager.Library?.craftDone, 0.7f);
            Debug.Log($"Watcher raid on {p.property.DisplayName}: {detail}");
            Raided?.Invoke(p.property, detail);
        }

        /// <summary>Takes stations until every utility is within its cap: the worst-over utility's biggest user first.</summary>
        private static void SeizeOverCap(Property property, List<PlacedStructure> structures, List<string> seized)
        {
            var used = property.Usage();
            var caps = property.Caps;
            while (true)
            {
                int worst = -1;
                float worstOver = 0f;
                for (int i = 0; i < 3; i++)
                {
                    float over = (used[i] - caps[i]) / (float)Mathf.Max(1, caps[i]);
                    if (used[i] > caps[i] && over > worstOver)
                    {
                        worst = i;
                        worstOver = over;
                    }
                }
                if (worst < 0) return;

                PlacedStructure take = null;
                foreach (var s in structures)
                {
                    var d = s.Item.UtilityDraw;
                    if (d[worst] <= 0) continue;
                    if (take == null || d[worst] > take.Item.UtilityDraw[worst] ||
                        (d[worst] == take.Item.UtilityDraw[worst] && Total(d) > Total(take.Item.UtilityDraw))) take = s;
                }
                if (take == null) return;
                used -= take.Item.UtilityDraw;
                Seize(take, structures, seized);
            }
        }

        /// <summary>What's overdue plus the fine: scrip from the wallet first, then stations by value.</summary>
        private void Collect(List<PlacedStructure> structures, List<string> seized, ref int scripTaken)
        {
            var ledger = DebtLedger.Instance;
            if (ledger == null || ledger.Overdue <= 0) return;
            int owed = Mathf.CeilToInt(ledger.Overdue * (1f + debtFine));
            int collected = 0;

            if (wallet != null)
            {
                int take = Mathf.Min(wallet.Money, owed);
                if (take > 0 && wallet.TrySpend(take))
                {
                    scripTaken = take;
                    collected += take;
                }
            }

            structures.Sort((a, b) => b.Item.ValueAt(Items.ItemQuality.Standard).CompareTo(a.Item.ValueAt(Items.ItemQuality.Standard)));
            while (collected < owed && structures.Count > 0)
            {
                var s = structures[0];
                collected += s.Item.ValueAt(Items.ItemQuality.Standard);
                Seize(s, structures, seized);
            }

            // The fine is skimmed off the top; the rest goes against the debt.
            ledger.Collect(Mathf.FloorToInt(collected / (1f + debtFine)));
        }

        private static void Seize(PlacedStructure s, List<PlacedStructure> structures, List<string> seized)
        {
            structures.Remove(s);
            seized.Add(s.DisplayName);
            // Out of the property's usage straight away, not at the end of the frame.
            s.gameObject.SetActive(false);
            Destroy(s.gameObject);
        }

        private static int Total(Vector3Int v) => v.x + v.y + v.z;

        private static List<PlacedStructure> StructuresIn(Property property)
        {
            var list = new List<PlacedStructure>();
            foreach (var s in FindObjectsByType<PlacedStructure>(FindObjectsSortMode.None))
                if (s.Item != null && property.Contains(s.transform.position + Vector3.up * 0.2f, -0.5f)) list.Add(s);
            return list;
        }

        // ---------------------------------------------------------------- saving

        public WatcherRaidsSave Capture()
        {
            var save = new WatcherRaidsSave { lastRollDay = lastRollDay };
            foreach (var p in planned)
                save.planned.Add(new PlannedRaidSave { property = p.property.Id, day = p.day, hour = p.hour, utilities = p.utilities, debt = p.debt });
            return save;
        }

        public void Restore(WatcherRaidsSave save)
        {
            planned.Clear();
            if (save == null) return;
            // Saves from before raids: don't roll again on the day it was saved.
            lastRollDay = save.lastRollDay > 0 ? save.lastRollDay : TimeManager.Instance != null ? TimeManager.Instance.Day : 0;
            foreach (var s in save.planned)
            {
                var property = FindProperty(s.property);
                if (property != null) planned.Add(new Planned { property = property, day = s.day, hour = s.hour, utilities = s.utilities, debt = s.debt });
            }
        }

        private static Property FindProperty(string id)
        {
            foreach (var p in Property.All)
                if (p.Id == id) return p;
            return null;
        }
    }
}
