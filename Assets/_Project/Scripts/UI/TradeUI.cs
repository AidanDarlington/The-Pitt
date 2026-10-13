using ThePitt.Economy;
using ThePitt.Interaction;
using ThePitt.Items;
using ThePitt.NPC;
using ThePitt.Player;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace ThePitt.UI
{
    /// <summary>
    /// Trade window for an <see cref="NpcBuyer"/>, plus their <see cref="NpcVendor"/> stock if they sell
    /// anything. Click buys or sells one; Shift+click buys five or sells the stack.
    /// </summary>
    public class TradeUI : MonoBehaviour
    {
        public static TradeUI Instance { get; private set; }

        [SerializeField] private GameObject panel;
        [SerializeField] private Text headerLabel;
        [SerializeField] private RectTransform list;
        [Tooltip("Inactive row with a Button, an Image and a child Text.")]
        [SerializeField] private GameObject rowTemplate;
        [SerializeField] private Text statusLabel;
        [SerializeField] private Button closeButton;

        private NpcBuyer buyer;
        private NpcVendor vendor;
        private Inventory inventory;
        private PlayerWallet wallet;
        private FirstPersonController controller;

        public bool IsOpen => panel.activeSelf;
        /// <summary>Who the player is trading with, or null.</summary>
        public NpcBuyer CurrentBuyer => IsOpen ? buyer : null;

        private void Awake()
        {
            Instance = this;
            rowTemplate.SetActive(false);
            panel.SetActive(false);
            if (closeButton != null) closeButton.onClick.AddListener(Close);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            if (IsOpen && Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame) Close();
        }

        public void Open(NpcBuyer npc, PlayerInteractor interactor)
        {
            buyer = npc;
            vendor = npc.GetComponent<NpcVendor>();
            inventory = interactor.Inventory;
            wallet = interactor.GetComponent<PlayerWallet>();
            controller = interactor.GetComponent<FirstPersonController>();

            panel.SetActive(true);
            if (controller != null) controller.InputEnabled = false;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            inventory.Changed += Refresh;
            statusLabel.text = "";
            Refresh();
        }

        public void Close()
        {
            if (!IsOpen) return;
            panel.SetActive(false);
            inventory.Changed -= Refresh;
            if (controller != null) controller.InputEnabled = true;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            buyer = null;
            vendor = null;
        }

        private GameObject AddRow(string text, bool interactable, UnityEngine.Events.UnityAction onClick)
        {
            var row = Instantiate(rowTemplate, list);
            row.SetActive(true);
            row.GetComponentInChildren<Text>().text = text;
            var button = row.GetComponent<Button>();
            button.interactable = interactable;
            if (onClick != null) button.onClick.AddListener(onClick);
            return row;
        }

        /// <summary>A section label: a non-clickable row drawn without a background.</summary>
        private void AddHeading(string text)
        {
            var row = AddRow($"<color=#B0A890><b>{text}</b></color>", false, null);
            row.GetComponent<Image>().enabled = false;
        }

        private void Refresh()
        {
            var p = buyer.Profile;
            headerLabel.text = $"<b>{buyer.NpcName}</b>  ·  {buyer.Faction?.Name ?? p.TypeName}  ·  carrying {buyer.Cash} scrip" +
                               $"   <color=#B0A890>you: {wallet.Money}</color>";

            for (int i = list.childCount - 1; i >= 0; i--)
            {
                var child = list.GetChild(i).gameObject;
                if (child != rowTemplate) Destroy(child);
            }

            // Taking them on into the player's own gang.
            var gangs = Factions.FactionManager.Instance;
            int recruitCost = gangs != null && gangs.PlayerGang != null && buyer.Faction == null ? gangs.RecruitCost(p.NpcType) : 0;
            if (recruitCost > 0)
            {
                AddHeading("YOUR GANG");
                bool can = wallet.Money >= recruitCost;
                AddRow($"Recruit {buyer.NpcName} into {gangs.PlayerGang.Name}   —   {recruitCost} scrip up front, " +
                       $"wages daily{(can ? "" : "  <color=#C05040>(not enough scrip)</color>")}", can, Recruit);
            }

            if (vendor != null && vendor.Stock != null && vendor.Stock.Length > 0)
            {
                AddHeading("FOR SALE");
                for (int i = 0; i < vendor.Stock.Length; i++)
                {
                    var s = vendor.Stock[i];
                    if (s.item == null) continue;
                    bool can = s.remaining > 0 && wallet.Money >= s.price;
                    string note = s.remaining <= 0 ? "  <color=#C05040>(sold out today)</color>"
                        : wallet.Money < s.price ? "  <color=#C05040>(not enough scrip)</color>" : "";
                    int captured = i;
                    AddRow($"{s.item.DisplayName}   —   {s.price} each   <size=16>{s.remaining} left</size>{note}", can,
                        () => Buy(captured));
                }
                if (p.Buys) AddHeading("THEY'LL BUY");
            }
            // Sells only (a dealer): nothing of the player's to list.
            if (!p.Buys) return;

            int rows = 0;
            for (int slot = 0; slot < inventory.SlotCount; slot++)
            {
                var stack = inventory.GetSlot(slot);
                if (stack.IsEmpty) continue;
                int offer = buyer.OfferFor(stack.item, stack.quality);
                if (offer <= 0) continue;

                var row = Instantiate(rowTemplate, list);
                row.SetActive(true);
                string tier = stack.item.HasQuality
                    ? $" <color=#{ColorUtility.ToHtmlStringRGB(stack.quality.DisplayColor())}>({stack.quality})</color>"
                    : "";
                bool affordable = offer <= buyer.Cash;
                row.GetComponentInChildren<Text>().text =
                    $"{stack.item.DisplayName}{tier} x{stack.quantity}" +
                    $"   —   {offer} each{(affordable ? "" : "  <color=#C05040>(can't afford)</color>")}";

                int captured = slot;
                var button = row.GetComponent<Button>();
                button.interactable = affordable;
                button.onClick.AddListener(() => Sell(captured));
                rows++;
            }

            if (rows == 0 && string.IsNullOrEmpty(statusLabel.text))
                statusLabel.text = $"{buyer.NpcName} isn't interested in anything you're carrying.";
        }

        private void Recruit()
        {
            string refusal = Factions.FactionManager.Instance.Recruit(buyer.Profile.NpcType, wallet);
            if (refusal != null)
            {
                statusLabel.text = refusal;
                Refresh();
                return;
            }
            var recruit = buyer;
            Close();
            recruit.JoinPlayerGang();
        }

        private void Buy(int index)
        {
            var s = vendor.Stock[index];
            bool five = Keyboard.current != null && Keyboard.current.shiftKey.isPressed;
            int bought = 0, spent = 0;
            for (int n = 0; n < (five ? 5 : 1); n++)
            {
                if (!vendor.SellOne(index, inventory, wallet)) break;
                bought++;
                spent += s.price;
            }
            statusLabel.text = bought > 0
                ? $"Bought {bought} {s.item.DisplayName} for {spent} scrip."
                : inventory.SpaceFor(s.item) <= 0 ? "No room in your inventory." : "Can't buy that.";
            Refresh();
        }

        private void Sell(int slot)
        {
            var stack = inventory.GetSlot(slot);
            if (stack.IsEmpty) return;
            bool all = Keyboard.current != null && Keyboard.current.shiftKey.isPressed;
            string name = stack.item.DisplayName;

            int sold = 0, earned = 0;
            do
            {
                int price = buyer.BuyOne(inventory, slot, wallet);
                if (price <= 0) break;
                sold++;
                earned += price;
            } while (all && !inventory.GetSlot(slot).IsEmpty);

            statusLabel.text = sold > 0
                ? $"Sold {sold} {name} for {earned} scrip."
                : $"{buyer.NpcName} can't afford that.";
            Refresh();
        }
    }
}
