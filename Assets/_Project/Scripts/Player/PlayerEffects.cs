using System;
using System.Collections.Generic;
using ThePitt.Core;
using ThePitt.Items;
using UnityEngine;

namespace ThePitt.Player
{
    /// <summary>
    /// Lasting effects from drugs, timed on the in-game clock. Taking the same thing again
    /// refreshes it rather than stacking. When an effect ends its comedown is applied.
    /// </summary>
    public class PlayerEffects : MonoBehaviour
    {
        public class ActiveEffect
        {
            public ItemDefinition source;
            public int minutesLeft;
        }

        [SerializeField] private PlayerVitals vitals;

        private readonly List<ActiveEffect> active = new();

        public IReadOnlyList<ActiveEffect> Active => active;

        /// <summary>Product of every active effect's speed multiplier.</summary>
        public float SpeedMultiplier { get; private set; } = 1f;

        public event Action Changed;

        private void Awake()
        {
            if (vitals == null) vitals = GetComponent<PlayerVitals>();
        }

        private void OnEnable()
        {
            if (TimeManager.Instance != null) TimeManager.Instance.MinuteChanged += OnMinute;
        }

        private void Start()
        {
            OnDisable();
            OnEnable();
        }

        private void OnDisable()
        {
            if (TimeManager.Instance != null) TimeManager.Instance.MinuteChanged -= OnMinute;
        }

        public void Apply(ItemDefinition item)
        {
            if (item == null || !item.HasLastingEffect) return;
            var existing = active.Find(e => e.source == item);
            if (existing != null) existing.minutesLeft = item.EffectMinutes;
            else active.Add(new ActiveEffect { source = item, minutesLeft = item.EffectMinutes });
            Recalculate();
        }

        /// <summary>Replaces the active effects (loading a save). No comedowns are applied.</summary>
        public void Restore(IEnumerable<(ItemDefinition item, int minutesLeft)> effects)
        {
            active.Clear();
            foreach (var (item, minutes) in effects)
                if (item != null && minutes > 0) active.Add(new ActiveEffect { source = item, minutesLeft = minutes });
            Recalculate();
        }

        private void OnMinute(int _)
        {
            if (active.Count == 0) return;
            for (int i = active.Count - 1; i >= 0; i--)
            {
                var e = active[i];
                if (--e.minutesLeft > 0) continue;
                active.RemoveAt(i);
                var s = e.source;
                if (vitals != null && (s.ComedownHealth != 0f || s.ComedownHunger != 0f || s.ComedownThirst != 0f))
                    vitals.Adjust(s.ComedownHealth, s.ComedownHunger, s.ComedownThirst);
            }
            Recalculate();
        }

        private void Recalculate()
        {
            float speed = 1f;
            foreach (var e in active) speed *= e.source.MoveSpeedMultiplier;
            SpeedMultiplier = speed;
            Changed?.Invoke();
        }
    }
}
