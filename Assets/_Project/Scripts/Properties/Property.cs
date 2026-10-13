using System;
using System.Collections.Generic;
using ThePitt.Interaction;
using ThePitt.Items;
using UnityEngine;

namespace ThePitt.Properties
{
    /// <summary>
    /// A room the player can own, reached through a door on a building in the city (the door works as a
    /// portal to the room, which is built out of sight under its district). Owned properties are where
    /// stations and other structures can be put down. One per district; the player's own opens onto a thoroughfare in the middle of the city.
    /// Each has a cap on the water, electricity and gas its stations may draw, and bricked-up side rooms;
    /// the Watchers can be bribed to raise the caps and to open the rooms up.
    /// </summary>
    public class Property : MonoBehaviour
    {
        private static readonly List<Property> all = new();

        [SerializeField] private string id;
        [SerializeField] private string displayName = "Property";
        [SerializeField] private string district;
        [SerializeField, Min(0)] private int price = 1000;
        [SerializeField] private bool ownedAtStart;
        [Tooltip("Inside floor area, world XZ.")]
        [SerializeField] private Rect interior;
        [SerializeField] private float floorY;
        [SerializeField, Min(1f)] private float height = 3.5f;
        [Tooltip("Lit sign over the door: warm when owned, red while for sale.")]
        [SerializeField] private Renderer sign;
        [Header("Utilities")]
        [Tooltip("Water, electricity and gas the property can supply before any bribes.")]
        [SerializeField] private Vector3Int baseCaps = new(5, 6, 5);

        [Header("Expansions")]
        [Tooltip("Side rooms, sealed until the Watchers allow them to be opened up (inside floor area, world XZ).")]
        [SerializeField] private Rect[] annexes = Array.Empty<Rect>();
        [SerializeField] private GameObject[] annexRoots = Array.Empty<GameObject>();
        [Tooltip("The bricked-up doorway into each side room.")]
        [SerializeField] private GameObject[] seals = Array.Empty<GameObject>();

        [Header("Doors")]
        [Tooltip("The city door: on the ground at the wall face.")]
        [SerializeField] private Vector3 entrance;
        [Tooltip("Out of the building, toward the street.")]
        [SerializeField] private Vector2 entranceNormal;
        [Tooltip("Where the player stands after coming in, and which way they face.")]
        [SerializeField] private Vector3 arrival;
        [SerializeField] private float arrivalYaw;

        private static readonly Color OwnedColor = new(1f, 0.82f, 0.55f);
        private static readonly Color ForSaleColor = new(1f, 0.15f, 0.1f);
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private MaterialPropertyBlock block;

        /// <summary>How much each Watcher-approved raise adds to a utility cap.</summary>
        public const int CapStep = 2;
        public static readonly string[] UtilityNames = { "Water", "Electricity", "Gas" };
        /// <summary>Each utility's colour on the HUD.</summary>
        public static readonly Color[] UtilityColors = { new(0.4f, 0.7f, 1f), new(1f, 0.85f, 0.3f), new(1f, 0.5f, 0.25f) };

        /// <summary>"Water 1 · Electricity 2", coloured per utility, or "No utilities".</summary>
        public static string DrawText(Vector3Int draw)
        {
            var parts = new List<string>();
            for (int i = 0; i < 3; i++)
                if (draw[i] > 0) parts.Add($"<color=#{ColorUtility.ToHtmlStringRGB(UtilityColors[i])}>{UtilityNames[i]} {draw[i]}</color>");
            return parts.Count > 0 ? string.Join("  ·  ", parts) : "No utilities";
        }

        private Vector3Int raises;

        public static IReadOnlyList<Property> All => all;
        /// <summary>The property the player last went into; where they wake up after loading.</summary>
        public static Property LastVisited { get; set; }

        /// <summary>The property whose room contains a point, if any.</summary>
        public static Property At(Vector3 point)
        {
            foreach (var p in all)
                if (p.Contains(point)) return p;
            return null;
        }

        // Statics survive between play sessions when domain reload is off in the editor.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetLastVisited() => LastVisited = null;
        /// <summary>Fired when a property's caps or rooms change.</summary>
        public static event Action<Property> Upgraded;
        /// <summary>Fired when any property is bought (or ownership is restored from a save).</summary>
        public static event Action<Property> OwnershipChanged;

        public string Id => id;
        public string DisplayName => displayName;
        public string District => district;
        public int Price => price;
        public bool Owned { get; private set; }
        public Rect Interior => interior;
        public Vector3 Centre => new(interior.center.x, floorY, interior.center.y);
        public Vector3 Entrance => entrance;
        /// <summary>Just outside the city door, facing away from it.</summary>
        public Vector3 OutsidePoint => entrance + new Vector3(entranceNormal.x, 0f, entranceNormal.y) * 1.3f;
        public float OutsideYaw => Mathf.Atan2(entranceNormal.x, entranceNormal.y) * Mathf.Rad2Deg;
        public Vector3 InsidePoint => arrival;
        public float InsideYaw => arrivalYaw;
        public Vector3Int Caps => baseCaps + raises * CapStep;
        /// <summary>How many times each utility's cap has been raised.</summary>
        public Vector3Int Raises => raises;
        public int Expansions { get; private set; }
        public int MaxExpansions => annexes.Length;
        public int Rooms => 1 + Expansions;

        /// <summary>Called by the generator.</summary>
        public void Setup(string newId, string name, string districtName, int cost, bool owned, Rect inside, float floor, float roomHeight, Renderer signRenderer = null)
        {
            id = newId;
            displayName = name;
            district = districtName;
            price = cost;
            ownedAtStart = owned;
            interior = inside;
            floorY = floor;
            height = roomHeight;
            sign = signRenderer;
        }

        public void SetSign(Renderer signRenderer) => sign = signRenderer;

        public void SetAnnexes(Rect[] rects, GameObject[] roots, GameObject[] sealObjects)
        {
            annexes = rects;
            annexRoots = roots;
            seals = sealObjects;
        }

        public void SetEntrance(Vector3 point, Vector2 normal)
        {
            entrance = point;
            entranceNormal = normal;
        }

        public void SetArrival(Vector3 point, float yaw)
        {
            arrival = point;
            arrivalYaw = yaw;
        }

        private void Awake()
        {
            Owned = ownedAtStart;
            UpdateSign();
            ApplyExpansions();
        }

        // ---------------------------------------------------------------- utilities

        /// <summary>What the structures installed here draw: water, electricity, gas.</summary>
        public Vector3Int Usage()
        {
            var total = Vector3Int.zero;
            foreach (var s in FindObjectsByType<PlacedStructure>(FindObjectsSortMode.None))
                if (s.Item != null && Contains(s.transform.position + Vector3.up * 0.2f, -0.5f)) total += s.Item.UtilityDraw;
            return total;
        }

        /// <summary>
        /// Twice the caps: the most the property can physically supply. Anything between the caps and this
        /// runs the risk of a Watcher raid.
        /// </summary>
        public Vector3Int HardCaps => Caps * 2;

        /// <summary>
        /// Daily chance (0-1) of a Watcher raid for drawing this much: how far over its cap the worst utility
        /// is, as a fraction of the cap. 25% over is a 25% chance; at the hard cap a raid is certain.
        /// </summary>
        public float RaidRisk(Vector3Int usage)
        {
            var caps = Caps;
            float worst = 0f;
            for (int i = 0; i < 3; i++)
                if (usage[i] > caps[i]) worst = Mathf.Max(worst, caps[i] > 0 ? (usage[i] - caps[i]) / (float)caps[i] : 1f);
            return Mathf.Clamp01(worst);
        }

        public float RaidRisk() => RaidRisk(Usage());

        /// <summary>
        /// Whether the property can supply another of this item: refused only past the hard cap. Going over the
        /// caps is allowed but risky; <paramref name="warning"/> then says by how much.
        /// </summary>
        public bool Fits(ItemDefinition item, out string shortfall, out string warning)
        {
            shortfall = warning = null;
            if (item == null) return true;
            var used = Usage();
            var caps = Caps;
            var hard = HardCaps;
            var draw = item.UtilityDraw;
            var after = used + draw;
            for (int i = 0; i < 3; i++)
            {
                if (draw[i] <= 0 || after[i] <= hard[i]) continue;
                shortfall = $"{displayName} can't supply that much {UtilityNames[i].ToLowerInvariant()} " +
                            $"({used[i]} in use, needs {draw[i]}, hard limit {hard[i]}).";
                return false;
            }
            float risk = RaidRisk(after);
            if (risk > 0f)
            {
                var over = new List<string>();
                for (int i = 0; i < 3; i++)
                    if (after[i] > caps[i]) over.Add($"{UtilityNames[i].ToLowerInvariant()} {after[i]}/{caps[i]}");
                warning = $"Over the limit ({string.Join(", ", over)}): {risk:P0} chance a day of a Watcher raid.";
            }
            return true;
        }

        public bool Fits(ItemDefinition item, out string shortfall) => Fits(item, out shortfall, out _);

        /// <summary>Raises one utility's cap (0 water, 1 electricity, 2 gas). The Watchers' price is paid elsewhere.</summary>
        public void RaiseCap(int utility)
        {
            raises[utility]++;
            Upgraded?.Invoke(this);
        }

        /// <summary>Knocks through into the next sealed side room.</summary>
        public bool Expand()
        {
            if (Expansions >= MaxExpansions) return false;
            Expansions++;
            ApplyExpansions();
            Upgraded?.Invoke(this);
            return true;
        }

        private void ApplyExpansions()
        {
            for (int i = 0; i < annexes.Length; i++)
            {
                bool open = i < Expansions;
                if (i < annexRoots.Length && annexRoots[i] != null) annexRoots[i].SetActive(open);
                if (i < seals.Length && seals[i] != null) seals[i].SetActive(!open);
            }
        }

        /// <summary>Puts back caps and rooms from a save.</summary>
        public void RestoreUpgrades(Vector3Int savedRaises, int expansions)
        {
            raises = savedRaises;
            Expansions = Mathf.Clamp(expansions, 0, MaxExpansions);
            ApplyExpansions();
            Upgraded?.Invoke(this);
        }

        private void OnEnable() => all.Add(this);
        private void OnDisable() => all.Remove(this);

        /// <summary>Whether a world point is inside the property: its main room or an opened side room.</summary>
        public bool Contains(Vector3 point, float margin = 0f)
        {
            if (point.y < floorY - 0.5f || point.y > floorY + height) return false;
            if (InRect(interior, point, margin)) return true;
            for (int i = 0; i < Expansions && i < annexes.Length; i++)
                if (InRect(annexes[i], point, margin)) return true;
            return false;
        }

        private static bool InRect(Rect r, Vector3 point, float margin) =>
            point.x >= r.xMin + margin && point.x <= r.xMax - margin && point.z >= r.yMin + margin && point.z <= r.yMax - margin;

        /// <summary>True if every point lies inside a single room of this property.</summary>
        private bool AllInOneRoom(IReadOnlyList<Vector3> points, float margin)
        {
            for (int room = -1; room < Expansions && room < annexes.Length; room++)
            {
                var r = room < 0 ? interior : annexes[room];
                bool all = true;
                foreach (var p in points)
                    all &= p.y >= floorY - 0.5f && p.y <= floorY + height && InRect(r, p, margin);
                if (all) return true;
            }
            return false;
        }

        /// <summary>Spends the price from the wallet and takes ownership. False if already owned or too dear.</summary>
        public bool TryBuy(Economy.PlayerWallet wallet)
        {
            if (Owned || wallet == null || !wallet.TrySpend(price)) return false;
            SetOwned(true);
            return true;
        }

        public void SetOwned(bool owned)
        {
            if (Owned == owned) return;
            Owned = owned;
            UpdateSign();
            OwnershipChanged?.Invoke(this);
        }

        private void UpdateSign()
        {
            if (sign == null) return;
            block ??= new MaterialPropertyBlock();
            sign.GetPropertyBlock(block);
            block.SetColor(ColorId, Owned ? OwnedColor : ForSaleColor);
            sign.SetPropertyBlock(block);
        }

        /// <summary>The owned property containing this point, if any.</summary>
        public static Property OwnedAt(Vector3 point, float margin = 0f)
        {
            foreach (var p in all)
                if (p.Owned && p.Contains(point, margin)) return p;
            return null;
        }

        /// <summary>True if every point lies inside one and the same owned property.</summary>
        public static bool AllInsideOwned(IReadOnlyList<Vector3> points, float margin = 0.05f)
        {
            if (points.Count == 0) return false;
            var p = OwnedAt(points[0], margin);
            return p != null && p.AllInOneRoom(points, margin);
        }
    }
}
