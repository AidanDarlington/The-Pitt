using System;
using ThePitt.Core;
using UnityEngine;

namespace ThePitt.Economy
{
    /// <summary>
    /// The inherited blood debt owed to The Overseer, collected through The Watchers.
    /// A minimum payment is due every period; missing one adds a penalty and leaves the unpaid amount
    /// overdue until it's paid. While anything is overdue the Watchers may raid.
    /// </summary>
    public class DebtLedger : MonoBehaviour
    {
        public static DebtLedger Instance { get; private set; }

        [SerializeField, Min(0)] private int startingDebt = 50000;
        [SerializeField, Min(1)] private int paymentIntervalDays = 7;
        [SerializeField, Min(0)] private int minimumPayment = 250;
        [Tooltip("Fraction of the unpaid minimum added to the debt when a payment is missed.")]
        [SerializeField, Min(0f)] private float missedPenalty = 0.2f;

        public int Remaining { get; private set; }
        public int MinimumPayment => minimumPayment;
        public int PaidThisPeriod { get; private set; }
        /// <summary>The day on whose midnight the current period's payment is checked.</summary>
        public int DueDay { get; private set; }
        public int MissedPayments { get; private set; }
        /// <summary>Missed minimum payments not yet made up. Payments go to this first.</summary>
        public int Overdue { get; private set; }
        /// <summary>Missed periods not yet made up (resets when the overdue amount is cleared).</summary>
        public int OverduePeriods { get; private set; }
        public bool IsCleared => Remaining <= 0;
        /// <summary>How much is still needed this period to avoid a missed payment.</summary>
        public int AmountDue => Mathf.Min(Remaining, Mathf.Max(0, minimumPayment - PaidThisPeriod));

        public event Action Changed;
        /// <summary>Fired with the unpaid amount and the penalty added.</summary>
        public event Action<int, int> PaymentMissed;
        public event Action Cleared;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;
            Remaining = startingDebt;
        }

        private void Start()
        {
            var time = TimeManager.Instance;
            DueDay = (time != null ? time.Day : 1) + paymentIntervalDays;
            if (time != null) time.DayChanged += OnDayChanged;
            Changed?.Invoke();
        }

        private void OnDestroy()
        {
            if (TimeManager.Instance != null) TimeManager.Instance.DayChanged -= OnDayChanged;
            if (Instance == this) Instance = null;
        }

        private void OnDayChanged(int day)
        {
            if (day < DueDay || IsCleared) return;

            int unpaid = AmountDue;
            if (unpaid > 0)
            {
                int penalty = Mathf.CeilToInt(unpaid * missedPenalty);
                Remaining += penalty;
                MissedPayments++;
                Overdue += unpaid;
                OverduePeriods++;
                Debug.Log($"Debt payment missed: {unpaid} unpaid, {penalty} penalty added.");
                PaymentMissed?.Invoke(unpaid, penalty);
            }

            PaidThisPeriod = 0;
            DueDay = day + paymentIntervalDays;
            Changed?.Invoke();
        }

        public DebtSave Capture() => new()
        {
            remaining = Remaining,
            paidThisPeriod = PaidThisPeriod,
            dueDay = DueDay,
            missedPayments = MissedPayments,
            overdue = Overdue,
            overduePeriods = OverduePeriods,
        };

        public void Restore(DebtSave save)
        {
            Remaining = save.remaining;
            PaidThisPeriod = save.paidThisPeriod;
            DueDay = save.dueDay;
            MissedPayments = save.missedPayments;
            Overdue = save.overdue;
            OverduePeriods = Overdue > 0 ? Mathf.Max(1, save.overduePeriods) : 0;
            Changed?.Invoke();
        }

        /// <summary>
        /// The Watchers collecting by force: <paramref name="amount"/> (scrip, or goods worth it) goes against
        /// the overdue amount and the debt itself.
        /// </summary>
        public void Collect(int amount)
        {
            amount = Mathf.Min(amount, Overdue, Remaining);
            if (amount <= 0) return;
            Remaining -= amount;
            ReduceOverdue(amount);
            Changed?.Invoke();
            if (IsCleared) Cleared?.Invoke();
        }

        private void ReduceOverdue(int amount)
        {
            Overdue = Mathf.Max(0, Overdue - amount);
            if (Overdue == 0) OverduePeriods = 0;
        }

        /// <summary>Pays up to <paramref name="amount"/> from the wallet. Returns the amount actually paid.</summary>
        public int Pay(PlayerWallet wallet, int amount)
        {
            amount = Mathf.Min(amount, Remaining, wallet.Money);
            if (amount <= 0 || !wallet.TrySpend(amount)) return 0;

            Remaining -= amount;
            // Make up what's overdue before this period's payment.
            int toOverdue = Mathf.Min(amount, Overdue);
            ReduceOverdue(toOverdue);
            PaidThisPeriod += amount - toOverdue;
            Changed?.Invoke();
            if (IsCleared) Cleared?.Invoke();
            return amount;
        }
    }
}
