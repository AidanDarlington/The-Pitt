using System;
using UnityEngine;

namespace ThePitt.Core
{
    /// <summary>
    /// The game clock. Every time-driven system (production, NPC routines, gang
    /// expansion, debt, Watcher checks) should subscribe to these events rather
    /// than keeping its own timer.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class TimeManager : MonoBehaviour
    {
        public static TimeManager Instance { get; private set; }

        [Tooltip("How many real-time minutes one full in-game day lasts.")]
        [SerializeField, Min(0.1f)] private float realMinutesPerDay = 15f;
        [SerializeField, Range(0f, 24f)] private float startHour = 8f;
        [SerializeField, Range(0f, 24f)] private float dawnHour = 6f;
        [SerializeField, Range(0f, 24f)] private float duskHour = 20f;
        [SerializeField] private bool paused;

        /// <summary>Current day, starting at 1.</summary>
        public int Day { get; private set; } = 1;
        /// <summary>Hours into the current day, 0 (inclusive) to 24 (exclusive).</summary>
        public float TimeOfDay { get; private set; }
        public float NormalizedTime => TimeOfDay / 24f;
        public int Hour => Mathf.FloorToInt(TimeOfDay);
        public int Minute => Mathf.FloorToInt((TimeOfDay - Hour) * 60f);
        public bool IsNight => TimeOfDay < dawnHour || TimeOfDay >= duskHour;
        public float DawnHour => dawnHour;
        public float DuskHour => duskHour;
        public bool Paused { get => paused; set => paused = value; }

        /// <summary>In-game hours that pass per real second.</summary>
        public float HoursPerSecond => 24f / (realMinutesPerDay * 60f);

        /// <summary>Fired once per in-game minute, with the total minutes into the day.</summary>
        public event Action<int> MinuteChanged;
        /// <summary>Fired once per in-game hour, with the new hour (0-23).</summary>
        public event Action<int> HourChanged;
        /// <summary>Fired at midnight, with the new day number.</summary>
        public event Action<int> DayChanged;
        public event Action DawnStarted;
        public event Action DuskStarted;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("Duplicate TimeManager destroyed.", this);
                Destroy(gameObject);
                return;
            }
            Instance = this;
            TimeOfDay = startHour % 24f;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            if (!paused) Advance(Time.deltaTime * HoursPerSecond);
        }

        /// <summary>Moves the clock forward, firing every event crossed on the way (e.g. when sleeping).</summary>
        public void Advance(float hours)
        {
            if (hours <= 0f) return;

            // Step at most one minute at a time so no event is skipped at high speeds.
            const float minuteInHours = 1f / 60f;
            while (hours > 0f)
            {
                float step = Mathf.Min(hours, minuteInHours);
                hours -= step;
                Step(step);
            }
        }

        private void Step(float hours)
        {
            float previous = TimeOfDay;
            int previousMinute = Mathf.FloorToInt(previous * 60f);
            float next = previous + hours;

            if (next >= 24f)
            {
                next -= 24f;
                Day++;
                TimeOfDay = next;
                FireCrossings(previous, 24f);
                DayChanged?.Invoke(Day);
                FireCrossings(0f, next, includeStart: true);
                return;
            }

            TimeOfDay = next;
            if (Mathf.FloorToInt(next * 60f) != previousMinute) FireCrossings(previous, next);
        }

        private void FireCrossings(float from, float to, bool includeStart = false)
        {
            int fromMinute = Mathf.FloorToInt(from * 60f);
            int toMinute = Mathf.FloorToInt(to * 60f);
            int first = includeStart ? fromMinute : fromMinute + 1;

            for (int m = first; m <= toMinute && m < 24 * 60; m++)
            {
                MinuteChanged?.Invoke(m);
                if (m % 60 == 0) HourChanged?.Invoke(m / 60);
                if (m == Mathf.RoundToInt(dawnHour * 60f)) DawnStarted?.Invoke();
                if (m == Mathf.RoundToInt(duskHour * 60f)) DuskStarted?.Invoke();
            }
        }

        /// <summary>Jumps to a time of day on the current day without firing events (debug/loading).</summary>
        public void SetTime(float hour, int day = -1)
        {
            TimeOfDay = Mathf.Repeat(hour, 24f);
            if (day > 0) Day = day;
        }
    }
}
