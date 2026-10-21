using System.Collections.Generic;
using ThePitt.Interaction;
using UnityEngine;

namespace ThePitt.Economy
{
    /// <summary>A Watcher collection point where the player pays down the debt.</summary>
    public class DebtTerminal : MonoBehaviour, IInteractable
    {
        [Tooltip("Paid per press once this period's minimum is covered.")]
        [SerializeField, Min(1)] private int extraPaymentStep = 100;

        private static PlayerWallet WalletOf(PlayerInteractor interactor) => interactor.GetComponent<PlayerWallet>();

        private int NextPayment(PlayerInteractor interactor)
        {
            var ledger = DebtLedger.Instance;
            var wallet = WalletOf(interactor);
            if (ledger == null || wallet == null) return 0;
            int wanted = ledger.AmountDue > 0 ? ledger.AmountDue : extraPaymentStep;
            return Mathf.Min(wanted, wallet.Money, ledger.Remaining);
        }

        public string GetPrompt(PlayerInteractor interactor)
        {
            var ledger = DebtLedger.Instance;
            if (ledger == null) return null;
            if (ledger.IsCleared) return "Watcher terminal · Debt cleared";

            int pay = NextPayment(interactor);
            string due = ledger.AmountDue > 0
                ? $"{ledger.AmountDue} due by Day {ledger.DueDay}"
                : "this week's payment made";
            return pay > 0 ? $"Pay {pay} toward debt ({due})" : $"Watcher terminal · {due} · no scrip";
        }

        public bool CanInteract(PlayerInteractor interactor) => NextPayment(interactor) > 0;

        /// <summary>Asks first, on a radial: pay, or keep the scrip.</summary>
        public void Interact(PlayerInteractor interactor)
        {
            var wallet = WalletOf(interactor);
            int pay = NextPayment(interactor);
            if (pay <= 0) return;
            if (UI.RadialMenu.Instance == null)
            {
                DebtLedger.Instance.Pay(wallet, pay);
                return;
            }
            var ledger = DebtLedger.Instance;
            var options = new List<UI.RadialOption>
            {
                new()
                {
                    label = $"Pay {pay:N0}",
                    detail = $"Leaves {wallet.Money - pay:N0} scrip · {ledger.Remaining - pay:N0} still owed",
                    enabled = true,
                    // Re-checked: the wallet or the debt may have changed while the menu was up.
                    chosen = () =>
                    {
                        int now = Mathf.Min(pay, NextPayment(interactor));
                        if (now > 0) DebtLedger.Instance.Pay(wallet, now);
                    },
                },
                new()
                {
                    label = "Cancel",
                    detail = "Keep your scrip",
                    enabled = true,
                    chosen = () => { },
                },
            };
            UI.RadialMenu.Instance.Open("PAY THE WATCHERS", options, interactor);
        }
    }
}
