using System;
using System.Collections.Generic;
using ThePitt.Core;
using ThePitt.Economy;
using ThePitt.Items;
using ThePitt.Production;
using ThePitt.Properties;
using ThePitt.World;
using UnityEngine;

namespace ThePitt.Factions
{
    /// <summary>Where the player stands with a gang, worst first.</summary>
    public enum Relation { War, Hostile, Unfriendly, Neutral, Friendly, Allied }

    /// <summary>How the player's own gang behaves on its borders.</summary>
    public enum GangStance { Hold, Expand, Push }

    /// <summary>
    /// The gangs and the ground they hold. The city is split into a grid of territory cells; each day
    /// every gang grows, claims free ground on its borders and fights rivals for theirs. Player supply
    /// feeds a gang's strength and its expansion. Gangs have opinions of each other that sour along
    /// shared borders and in turf wars; sworn enemies fight harder and friends leave each other alone.
    /// Each gang posts a daily request the player can fill. The player can found a gang of their own,
    /// whose turf grows only out from the properties they own. The Overseer quietly bleeds any gang that
    /// gets too big; one that holds the dominance share anyway is reported for the Hunter Killers.
    /// The Watchers' outposts hang off the edge of the city, outside the territory grid: their ground is
    /// their own, nobody can claim it and it touches no one.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public class FactionManager : MonoBehaviour
    {
        public const int Unclaimed = -1;

        /// <summary>A gang's live state in the simulation.</summary>
        public class FactionState
        {
            public FactionDefinition definition;
            public int index;
            public float power;
            public int cells;
            /// <summary>The player's standing with this gang, -100 to 100.</summary>
            public float standing;
            /// <summary>Power owed from player supply, applied at the next daily tick.</summary>
            public float pendingSupply;
            /// <summary>Recent player supply, decaying daily; drives expansion surges.</summary>
            public float recentSupply;
            public float expansionCarry;
            public bool eliminated;
            /// <summary>In-game hour (since day 1) until which its members attack the player on sight.</summary>
            public float hostileUntil;
            /// <summary>An alliance the player agreed with them.</summary>
            public bool allied;
            /// <summary>Open war with the player, until peace is bought.</summary>
            public bool atWar;

            /// <summary>Today's request: so many of one item (food, a drug or a weapon), for a reward.</summary>
            public ItemDefinition requestItem;
            public ItemCategory requestCategory;
            public int requestCount;
            public int requestReward;
            public bool requestOpen;

            public string Name => definition.DisplayName;
            public bool Alive => !eliminated;
            public bool IsPlayer => definition.IsPlayerGang;
        }

        [Serializable]
        public class Rivalry
        {
            public FactionDefinition a;
            public FactionDefinition b;
            [Range(-100f, 100f)] public float opinion;
        }

        public static FactionManager Instance { get; private set; }

        [SerializeField] private PittGreybox greybox;
        [SerializeField] private FactionDefinition[] factions;
        [Tooltip("Side length of a territory cell, in metres.")]
        [SerializeField, Min(2f)] private float cellSize = 10f;
        [Tooltip("Random seed for the starting layout and the daily simulation. 0 = random each run.")]
        [SerializeField] private int seed;

        [Header("Expansion")]
        [Tooltip("Power spent to claim one free cell.")]
        [SerializeField, Min(0f)] private float claimCost = 1.5f;
        [Tooltip("Power the loser of a fight for a cell loses, as a fraction of the winner's attack.")]
        [SerializeField, Range(0f, 1f)] private float battleLoss = 0.08f;
        [Tooltip("Power the winner of a fight for a cell loses, as a fraction of the loser's strength.")]
        [SerializeField, Range(0f, 1f)] private float battleCost = 0.04f;
        [Tooltip("Strength every cell's own crew adds to its defence, on top of the gang's power. Keeps a beaten gang from collapsing overnight.")]
        [SerializeField, Min(0f)] private float garrison = 20f;
        [Tooltip("Fraction of recent supply kept each day.")]
        [SerializeField, Range(0f, 1f)] private float supplyDecay = 0.5f;

        [Header("Gangs and each other")]
        [Tooltip("Starting opinions between gangs (-100 sworn enemies to 100 firm friends). Unlisted pairs start at 0.")]
        [SerializeField] private Rivalry[] rivalries;
        [Tooltip("Opinion lost each day by gangs that share a border.")]
        [SerializeField, Min(0f)] private float borderFriction = 0.5f;
        [Tooltip("Opinion lost by both sides each time they fight over a cell.")]
        [SerializeField, Min(0f)] private float fightFriction = 2f;
        [Tooltip("Gangs that think at least this well of each other don't fight over turf.")]
        [SerializeField, Range(0f, 100f)] private float truceOpinion = 40f;
        [Tooltip("Gangs that think this badly of each other or worse count as enemies: allying with one upsets the other.")]
        [SerializeField, Range(-100f, 0f)] private float enemyOpinion = -30f;
        [Tooltip("Standing gained with a gang's enemies when the player declares war on it.")]
        [SerializeField, Min(0f)] private float enemyOfMyFriend = 10f;
        [Tooltip("Standing lost with a gang's enemies when the player allies with it.")]
        [SerializeField, Min(0f)] private float allianceFallout = 70f;

        [Header("Player Supply")]
        [Tooltip("Power a gang gains per scrip of goods bought from the player.")]
        [SerializeField, Min(0f)] private float powerPerScrip = 0.05f;
        [Tooltip("Multiplier when the goods are the gang's favoured kind.")]
        [SerializeField, Min(1f)] private float favouredMultiplier = 2f;
        [SerializeField, Min(0f)] private float standingPerScrip = 0.02f;
        [Tooltip("Below this share of the map a gang is desperate and pays more.")]
        [SerializeField, Range(0f, 1f)] private float desperationShare = 0.06f;
        [Tooltip("Price multiplier a gang on its last legs pays.")]
        [SerializeField, Min(1f)] private float maxDesperationPrice = 1.6f;

        [Header("Daily Requests")]
        [Tooltip("How many are asked for of something cheap (under 15 scrip), mid-priced (under 50), and dear.")]
        [SerializeField] private Vector2Int requestCountCheap = new(6, 10);
        [SerializeField] private Vector2Int requestCount = new(3, 6);
        [SerializeField] private Vector2Int requestCountDear = new(1, 3);
        [Tooltip("Reward per item as a multiple of its value, before the gang's generosity.")]
        [SerializeField, Min(0f)] private float requestPremium = 1.6f;
        [Tooltip("Standing from which a gang will sell its recipe: the only way to get it.")]
        [SerializeField, Range(-100f, 100f)] private float recipeStanding = 45f;
        [Tooltip("Standing for a filled request, before the gang's goodwill.")]
        [SerializeField, Min(0f)] private float requestStanding = 6f;

        [Header("Player Aggression")]
        [Tooltip("Standing lost each time the player hurts one of a gang's members.")]
        [SerializeField, Min(0f)] private float standingPerHit = 4f;
        [SerializeField, Min(0f)] private float standingPerKill = 15f;
        [Tooltip("Power a gang loses when the player kills one of its members.")]
        [SerializeField, Min(0f)] private float powerPerKill = 3f;
        [Tooltip("In-game hours a gang stays out for blood after the player attacks it.")]
        [SerializeField, Min(0f)] private float hostileHours = 12f;

        [Header("Relations")]
        [Tooltip("At or below this standing a gang is Hostile: attacks on sight and won't trade.")]
        [SerializeField, Range(-100f, 0f)] private float hostileStanding = -50f;
        [Tooltip("Below this a gang is Unfriendly and pays less.")]
        [SerializeField, Range(-100f, 0f)] private float unfriendlyStanding = -15f;
        [Tooltip("At or above this a gang is Friendly: pays more.")]
        [SerializeField, Range(0f, 100f)] private float friendlyStanding = 30f;
        [Tooltip("Price multipliers for Unfriendly, Neutral, Friendly and Allied gangs buying from the player.")]
        [SerializeField] private Vector4 tradeMultipliers = new(0.8f, 1f, 1.15f, 1.3f);
        [Tooltip("Standing per scrip of tribute, before each gang's goodwill.")]
        [SerializeField, Min(0f)] private float standingPerTributeScrip = 0.06f;
        [Tooltip("Standing lost when the player walks out of an alliance.")]
        [SerializeField, Min(0f)] private float breakAlliancePenalty = 20f;
        [Tooltip("Standing that drifts back toward neutral each day (not for allies or enemies at war).")]
        [SerializeField, Min(0f)] private float dailyDrift = 1f;
        [Tooltip("Peace costs this much scrip, plus the gang's power times the next value.")]
        [SerializeField, Min(0)] private int peaceBasePrice = 300;
        [SerializeField, Min(0f)] private float peacePricePerPower = 2f;

        [Header("Your Gang")]
        [SerializeField, Min(0)] private int foundingCost = 500;
        [Tooltip("Standing lost with every gang when the player founds one: a new player in the game.")]
        [SerializeField, Min(0f)] private float foundingTension = 15f;
        [Tooltip("Daily power per point of crew strength (a civilian recruit is 1, a junkie 0.6).")]
        [SerializeField, Min(0f)] private float powerPerCrew = 2.5f;
        [Tooltip("Scrip paid to each crew member at midnight. Unpaid crews walk out.")]
        [SerializeField, Min(0)] private int wagePerMember = 10;
        [SerializeField, Min(0)] private int civilianRecruitCost = 150;
        [SerializeField, Min(0)] private int junkieRecruitCost = 75;
        [Tooltip("Expansion attempts per day per 100 power when expanding or pushing.")]
        [SerializeField, Min(0f)] private float playerExpansionRate = 4f;
        [SerializeField, Range(0f, 2f)] private float pushAggression = 0.8f;
        [Tooltip("Power gained per scrip of goods handed to the crew. Drugs count for half as much again.")]
        [SerializeField, Min(0f)] private float crewSupplyPower = 0.15f;

        [Header("The Overseer")]
        [Tooltip("Above this share of the map, The Overseer starts sabotaging a gang.")]
        [SerializeField, Range(0f, 1f)] private float sabotageShare = 0.4f;
        [Tooltip("Fraction of power drained per day at the dominance share; scales up from the sabotage share.")]
        [SerializeField, Range(0f, 1f)] private float sabotageRate = 0.3f;
        [Tooltip("Share of the map that triggers the endgame (Hunter Killers).")]
        [SerializeField, Range(0f, 1f)] private float dominanceShare = 0.75f;

        private readonly List<FactionState> states = new();
        private readonly Dictionary<long, float> opinions = new();
        private readonly List<string> news = new();
        private int[] owner;
        /// <summary>Cells of the Watchers' outposts: never claimed by anyone.</summary>
        private bool[] reserved;
        private int resolution;
        private System.Random rng;

        public IReadOnlyList<FactionState> Factions => states;
        public int Resolution => resolution;
        public float CellSize => cellSize;
        public int CellCount => owner?.Length ?? 0;
        public float DominanceShare => dominanceShare;
        /// <summary>The gang that has reached the dominance share, if any.</summary>
        public FactionState Dominant { get; private set; }

        /// <summary>The player's own gang, once founded.</summary>
        public FactionState PlayerGang { get; private set; }
        public int CrewMembers { get; private set; }
        public float CrewStrength { get; private set; }
        public GangStance Stance { get; private set; } = GangStance.Hold;
        public int FoundingCost => foundingCost;
        public int DailyWages => CrewMembers * wagePerMember;
        /// <summary>Recent goings-on for the player's gang, newest first.</summary>
        public IReadOnlyList<string> News => news;

        /// <summary>Fired whenever any cell changes hands.</summary>
        public event Action TerritoryChanged;
        /// <summary>Fired after each daily tick.</summary>
        public event Action DaySimulated;
        /// <summary>Fired with the winner, the loser and the cell index, for each cell taken in a turf war.</summary>
        public event Action<FactionState, FactionState, int> CellTaken;
        public event Action<FactionState> FactionEliminated;
        /// <summary>Fired when a gang first reaches the dominance share. The Hunter Killers hook in here.</summary>
        public event Action<FactionState> DominanceReached;
        /// <summary>Fired when the player attacks a gang that wasn't already out for them.</summary>
        public event Action<FactionState> TurnedHostile;
        /// <summary>Fired whenever standing, an alliance, a war, a request or the player's gang changes.</summary>
        public event Action RelationsChanged;

        /// <summary>A gang's name with "the" in front, once ("the Rust Bandits", "the Shadows").</summary>
        public static string The(string name, bool capital = false)
        {
            if (name.StartsWith("The ")) name = name.Substring(4);
            return (capital ? "The " : "the ") + name;
        }

        /// <summary>In-game hours since the start of day 1.</summary>
        public static float GameHours => TimeManager.Instance != null ? (TimeManager.Instance.Day - 1) * 24f + TimeManager.Instance.TimeOfDay : 0f;

        private void Awake()
        {
            // The city lives in the World scene; no reference can reach across.
            if (greybox == null) greybox = PittGreybox.Current;
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("Duplicate FactionManager destroyed.", this);
                Destroy(gameObject);
                return;
            }
            Instance = this;
            rng = seed != 0 ? new System.Random(seed) : new System.Random();
            Setup();
        }

        private void Start()
        {
            if (TimeManager.Instance != null) TimeManager.Instance.DayChanged += OnDayChanged;
            Property.OwnershipChanged += OnPropertyChanged;
        }

        private void OnDestroy()
        {
            if (TimeManager.Instance != null) TimeManager.Instance.DayChanged -= OnDayChanged;
            Property.OwnershipChanged -= OnPropertyChanged;
            if (Instance == this) Instance = null;
        }

        private void OnDayChanged(int _) => SimulateDay();

        /// <summary>A newly bought property starts a patch of the player's turf at its door straight away.</summary>
        private void OnPropertyChanged(Property _)
        {
            if (ReseedPlayerTurf())
            {
                TerritoryChanged?.Invoke();
                RelationsChanged?.Invoke();
            }
        }

        // ---------------------------------------------------------------- grid

        public Vector2 CellCenter(int cell)
        {
            float half = greybox.HalfSize;
            return new Vector2(-half + (cell % resolution + 0.5f) * cellSize, -half + (cell / resolution + 0.5f) * cellSize);
        }

        /// <summary>Cell index at a world XZ position, or -1 if off the city.</summary>
        public int CellAt(Vector2 p)
        {
            if (owner == null) return -1;
            int x = Mathf.FloorToInt((p.x + greybox.HalfSize) / cellSize);
            int y = Mathf.FloorToInt((p.y + greybox.HalfSize) / cellSize);
            return x < 0 || y < 0 || x >= resolution || y >= resolution ? -1 : y * resolution + x;
        }

        public FactionState OwnerOfCell(int cell) =>
            cell < 0 || cell >= owner.Length || owner[cell] == Unclaimed ? null : states[owner[cell]];

        /// <summary>The gang holding the ground at a world position, or null if no one does.</summary>
        public FactionState OwnerAt(Vector3 world) => OwnerOfCell(CellAt(new Vector2(world.x, world.z)));

        public FactionState Get(FactionDefinition definition)
        {
            foreach (var s in states)
                if (s.definition == definition) return s;
            return null;
        }

        /// <summary>Whether a cell belongs to a Watcher outpost (nobody can claim it).</summary>
        public bool IsReserved(int cell) => reserved != null && cell >= 0 && cell < reserved.Length && reserved[cell];

        private static Rect Grow(Rect r, float amount) =>
            Rect.MinMaxRect(r.xMin - amount, r.yMin - amount, r.xMax + amount, r.yMax + amount);

        public float ShareOf(FactionState faction) => owner == null || owner.Length == 0 ? 0f : (float)faction.cells / owner.Length;

        private void Setup()
        {
            states.Clear();
            opinions.Clear();
            Dominant = null;
            PlayerGang = null;
            resolution = Mathf.Max(1, Mathf.CeilToInt(greybox.citySize / cellSize));
            owner = new int[resolution * resolution];
            reserved = new bool[owner.Length];
            for (int i = 0; i < owner.Length; i++)
            {
                owner[i] = Unclaimed;
            }

            for (int i = 0; i < factions.Length; i++)
            {
                var def = factions[i];
                states.Add(new FactionState { definition = def, index = i, power = def.StartingPower, standing = def.StartingStanding });
            }
            if (rivalries != null)
                foreach (var r in rivalries)
                {
                    var a = Get(r.a);
                    var b = Get(r.b);
                    if (a != null && b != null) SetOpinion(a, b, r.opinion);
                }

            // Each gang starts in a corner of the map, the one in its home district, far from the others.
            var cornersTaken = new List<Vector2>();
            foreach (var s in states)
            {
                int district = HomeDistrictIndex(s.definition);
                if (district < 0)
                {
                    Debug.LogWarning($"Faction {s.Name}: no district named \"{s.definition.HomeDistrict}\".", this);
                    continue;
                }
                var center = StartingCorner(district, s.definition.StartingRadius, cornersTaken);
                for (int c = 0; c < owner.Length; c++)
                {
                    var p = CellCenter(c);
                    if (owner[c] == Unclaimed && !reserved[c] && greybox.DistrictIndexAt(p) == district &&
                        Vector2.Distance(p, center) <= s.definition.StartingRadius)
                        SetOwner(c, s);
                }
            }

            // Then any outposts (safe houses), on free ground away from everyone's main turf.
            foreach (var s in states)
            {
                int placed = 0;
                for (int attempt = 0; attempt < 500 && placed < s.definition.Outposts; attempt++)
                {
                    int c = rng.Next(owner.Length);
                    var p = CellCenter(c);
                    if (owner[c] != Unclaimed || reserved[c] || Vector2.Distance(p, greybox.startPoint) < 20f || NeighbourOwnedBy(c, -2)) continue;
                    SetOwner(c, s);
                    placed++;
                }
            }

            foreach (var s in states) NewRequest(s);
        }

        /// <summary>
        /// Middle of a gang's starting patch: pulled in from a corner of the map far enough that the whole
        /// patch is on the map. Prefers a free corner inside the district; otherwise the free corner nearest it.
        /// </summary>
        private Vector2 StartingCorner(int district, float radius, List<Vector2> taken)
        {
            float inset = greybox.HalfSize - radius - cellSize;
            var home = greybox.districts[district].center;
            Vector2 best = home;
            float bestScore = float.MaxValue;
            foreach (var sign in new[] { new Vector2(-1f, 1f), new Vector2(1f, 1f), new Vector2(-1f, -1f), new Vector2(1f, -1f) })
            {
                var p = sign * inset;
                if (taken.Contains(sign)) continue;
                float score = Vector2.Distance(p, home) + (greybox.DistrictIndexAt(p) == district ? 0f : 10000f);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = p;
                }
            }
            if (bestScore < float.MaxValue) taken.Add(new Vector2(Mathf.Sign(best.x), Mathf.Sign(best.y)));
            return best;
        }

        /// <summary>Index into the greybox districts of a gang's home district, or -1.</summary>
        public int HomeDistrictOf(FactionDefinition def) => def != null ? HomeDistrictIndex(def) : -1;

        private int HomeDistrictIndex(FactionDefinition def)
        {
            if (string.IsNullOrEmpty(def.HomeDistrict)) return -1;
            for (int i = 0; i < greybox.districts.Count; i++)
            {
                var d = greybox.districts[i];
                if (string.Equals(d.name, def.HomeDistrict, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(greybox.palettes[d.palette % greybox.palettes.Length].name, def.HomeDistrict,
                        StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        private void SetOwner(int cell, FactionState faction)
        {
            int previous = owner[cell];
            if (previous != Unclaimed) states[previous].cells--;
            owner[cell] = faction?.index ?? Unclaimed;
            if (faction != null) faction.cells++;
        }

        /// <summary>True if any 4-neighbour is owned by <paramref name="index"/> (-2 = by anyone).</summary>
        private bool NeighbourOwnedBy(int cell, int index)
        {
            int x = cell % resolution, y = cell / resolution;
            return Owned(x - 1, y) || Owned(x + 1, y) || Owned(x, y - 1) || Owned(x, y + 1);

            bool Owned(int nx, int ny)
            {
                if (nx < 0 || ny < 0 || nx >= resolution || ny >= resolution) return false;
                int o = owner[ny * resolution + nx];
                return index == -2 ? o != Unclaimed : o == index;
            }
        }

        // ---------------------------------------------------------------- gangs and each other

        private static long PairKey(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

        /// <summary>What two gangs think of each other, -100 to 100.</summary>
        public float Opinion(FactionState a, FactionState b) =>
            a == null || b == null || a == b ? 0f : opinions.TryGetValue(PairKey(a.index, b.index), out float o) ? o : 0f;

        private void SetOpinion(FactionState a, FactionState b, float value) =>
            opinions[PairKey(a.index, b.index)] = Mathf.Clamp(value, -100f, 100f);

        private void ChangeOpinion(FactionState a, FactionState b, float delta) => SetOpinion(a, b, Opinion(a, b) + delta);

        /// <summary>The gang another gang hates most (never the player's), or null.</summary>
        public FactionState WorstEnemyOf(FactionState f)
        {
            FactionState worst = null;
            float lowest = enemyOpinion;
            foreach (var o in states)
            {
                if (o == f || o.eliminated || o.IsPlayer) continue;
                float op = Opinion(f, o);
                if (op <= lowest)
                {
                    lowest = op;
                    worst = o;
                }
            }
            return worst;
        }

        /// <summary>Whether a gang would fight the owner of a cell. Friends hold back; enemies try harder.</summary>
        private float WillAttack(FactionState attacker, FactionState defender)
        {
            if (attacker.IsPlayer)
                return defender.allied ? 0f : attacker.definition.Aggression;
            if (defender.IsPlayer)
            {
                if (attacker.allied) return 0f;
                // They push harder against a player they dislike.
                return attacker.definition.Aggression * (1f + Mathf.Max(0f, -attacker.standing) / 100f);
            }
            float op = Opinion(attacker, defender);
            if (op >= truceOpinion) return 0f;
            return attacker.definition.Aggression * (1f + Mathf.Max(0f, -op) / 100f);
        }

        // ---------------------------------------------------------------- simulation

        /// <summary>Runs one day of the territory simulation. Called at midnight.</summary>
        [ContextMenu("Simulate Day")]
        public void SimulateDay()
        {
            if (owner == null) return;
            PayCrew();
            bool changed = ReseedPlayerTurf();

            foreach (var f in states)
            {
                if (f.eliminated) continue;
                float growth = f.IsPlayer ? CrewStrength * powerPerCrew : f.definition.DailyGrowth;
                f.power += growth + f.definition.IncomePerCell * f.cells + f.pendingSupply;
                f.pendingSupply = 0f;

                // The Overseer keeps any one gang from getting too big.
                float share = ShareOf(f);
                if (share > sabotageShare)
                {
                    float t = Mathf.InverseLerp(sabotageShare, dominanceShare, share);
                    f.power *= 1f - sabotageRate * t;
                }
            }

            // Gangs take turns in a random order so no one always moves first.
            var order = new List<FactionState>(states);
            Shuffle(order);
            var lostToday = new Dictionary<FactionState, int>();
            foreach (var f in order)
            {
                if (f.eliminated) continue;
                float attempts = f.expansionCarry + f.definition.ExpansionRate * f.power / 100f *
                                 (1f + f.recentSupply * f.definition.SupplySurge / 100f);
                int whole = Mathf.FloorToInt(attempts);
                f.expansionCarry = attempts - whole;
                if (whole > 0) changed |= Expand(f, whole, lostToday);
                f.recentSupply *= supplyDecay;
            }
            foreach (var kv in lostToday)
                AddNews($"{The(kv.Key.Name, true)} took {kv.Value} block{(kv.Value > 1 ? "s" : "")} of your turf.");

            foreach (var f in states)
            {
                f.power = Mathf.Max(0f, f.power);
                // The player's gang can always come back from its properties; rival gangs can be wiped out.
                if (!f.eliminated && !f.IsPlayer && f.cells == 0)
                {
                    f.eliminated = true;
                    f.power = 0f;
                    Debug.Log($"{f.Name} have been wiped out.");
                    FactionEliminated?.Invoke(f);
                }
            }

            BorderFriction();

            // Old favours and old grudges fade a little each day; tomorrow's requests go up.
            foreach (var f in states)
            {
                if (!f.eliminated && !f.allied && !f.atWar && !f.IsPlayer)
                    f.standing = Mathf.MoveTowards(f.standing, 0f, dailyDrift);
                NewRequest(f);
            }
            RelationsChanged?.Invoke();

            CheckDominance();
            if (changed) TerritoryChanged?.Invoke();
            DaySimulated?.Invoke();
        }

        private bool Expand(FactionState f, int attempts, Dictionary<FactionState, int> lostToday)
        {
            var free = new List<int>();
            var rival = new List<int>();
            for (int c = 0; c < owner.Length; c++)
            {
                if (owner[c] == f.index || reserved[c] || !NeighbourOwnedBy(c, f.index)) continue;
                (owner[c] == Unclaimed ? free : rival).Add(c);
            }
            Shuffle(free);
            Shuffle(rival);

            bool changed = false;
            for (int i = 0; i < attempts; i++)
            {
                if (free.Count > 0)
                {
                    if (f.power < claimCost) break;
                    int c = free[^1];
                    free.RemoveAt(free.Count - 1);
                    f.power -= claimCost;
                    SetOwner(c, f);
                    if (f.IsPlayer) CheckEncroachment(c);
                    changed = true;
                }
                else if (rival.Count > 0)
                {
                    int c = rival[^1];
                    rival.RemoveAt(rival.Count - 1);
                    if (owner[c] == f.index || owner[c] == Unclaimed) continue;
                    var defender = states[owner[c]];
                    if (rng.NextDouble() >= WillAttack(f, defender)) continue;
                    if (!Fight(f, defender, c)) continue;
                    changed = true;
                    if (defender.IsPlayer) lostToday[f] = lostToday.TryGetValue(f, out int n) ? n + 1 : 1;
                    if (f.IsPlayer)
                    {
                        defender.standing = Mathf.Clamp(defender.standing - 3f, -100f, 100f);
                        CheckEncroachment(c);
                    }
                }
            }
            return changed;
        }

        /// <summary>A turf war over one cell. Returns true if the attacker took it.</summary>
        private bool Fight(FactionState attacker, FactionState defender, int cell)
        {
            float attack = attacker.power * (0.5f + attacker.definition.Aggression) * Roll();
            float defend = (defender.power + garrison) * defender.definition.Defense * Roll();
            if (attack <= 0f) return false;
            if (!attacker.IsPlayer && !defender.IsPlayer) ChangeOpinion(attacker, defender, -fightFriction);

            bool won = attack > defend;
            var winner = won ? attacker : defender;
            var loser = won ? defender : attacker;
            float winnerStrength = won ? attack : defend, loserStrength = won ? defend : attack;
            loser.power = Mathf.Max(0f, loser.power - winnerStrength * battleLoss);
            winner.power = Mathf.Max(0f, winner.power - loserStrength * battleCost);
            if (!won) return false;

            SetOwner(cell, attacker);
            CellTaken?.Invoke(attacker, defender, cell);
            return true;

            float Roll() => 0.5f + (float)rng.NextDouble();
        }

        /// <summary>Neighbours rub each other up the wrong way.</summary>
        private void BorderFriction()
        {
            var touching = new HashSet<long>();
            for (int c = 0; c < owner.Length; c++)
            {
                int a = owner[c];
                if (a == Unclaimed) continue;
                int x = c % resolution, y = c / resolution;
                if (x + 1 < resolution) Touch(a, owner[c + 1]);
                if (y + 1 < resolution) Touch(a, owner[c + resolution]);
            }
            foreach (long key in touching)
            {
                var a = states[(int)(key >> 32)];
                var b = states[(int)(key & 0xffffffff)];
                if (!a.eliminated && !b.eliminated) ChangeOpinion(a, b, -borderFriction);
            }

            void Touch(int a, int b)
            {
                if (b == Unclaimed || b == a || states[a].IsPlayer || states[b].IsPlayer) return;
                touching.Add(PairKey(a, b));
            }
        }

        private void CheckDominance()
        {
            if (Dominant != null && ShareOf(Dominant) < dominanceShare) Dominant = null;
            if (Dominant != null) return;
            foreach (var f in states)
            {
                if (f.eliminated || ShareOf(f) < dominanceShare) continue;
                Dominant = f;
                Debug.Log($"{f.Name} hold {ShareOf(f):P0} of The Pitt. The Overseer takes notice.");
                DominanceReached?.Invoke(f);
                return;
            }
        }

        private void Shuffle<T>(List<T> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        // ---------------------------------------------------------------- aggression

        /// <summary>The player hurt (or killed) one of this gang's members: they remember, and come for the player.</summary>
        public void ReportAttack(FactionState faction, bool killed)
        {
            if (faction == null || faction.eliminated || faction.IsPlayer) return;
            bool wasHostile = IsHostileToPlayer(faction);
            // Turning on an ally ends the alliance there and then.
            if (faction.allied)
            {
                faction.allied = false;
                faction.standing -= breakAlliancePenalty;
            }
            faction.standing = Mathf.Clamp(faction.standing - (killed ? standingPerKill : standingPerHit), -100f, 100f);
            if (killed) faction.power = Mathf.Max(0f, faction.power - powerPerKill);
            faction.hostileUntil = Mathf.Max(faction.hostileUntil, GameHours + hostileHours);
            if (!wasHostile) TurnedHostile?.Invoke(faction);
            RelationsChanged?.Invoke();
        }

        /// <summary>Whether the gang's members attack the player on sight: at war, Hostile, or fresh from being attacked.</summary>
        public bool IsHostileToPlayer(FactionState faction) =>
            faction != null && !faction.eliminated && !faction.IsPlayer &&
            (faction.atWar || GameHours < faction.hostileUntil || (!faction.allied && faction.standing <= hostileStanding));

        // ---------------------------------------------------------------- relations

        public Relation RelationTo(FactionState f)
        {
            if (f == null) return Relation.Neutral;
            if (f.IsPlayer) return Relation.Allied;
            if (f.atWar) return Relation.War;
            if (f.allied) return Relation.Allied;
            if (f.standing <= hostileStanding) return Relation.Hostile;
            if (f.standing < unfriendlyStanding) return Relation.Unfriendly;
            return f.standing >= friendlyStanding ? Relation.Friendly : Relation.Neutral;
        }

        /// <summary>Whether the gang's members will trade with the player at all.</summary>
        public bool WillTrade(FactionState f) => f == null || f.IsPlayer || (RelationTo(f) > Relation.Hostile && GameHours >= f.hostileUntil);

        /// <summary>Price multiplier for a gang buying from the player, by how they feel about them.</summary>
        public float TradeMultiplier(FactionState f)
        {
            if (f == null || f.IsPlayer) return 1f;
            return RelationTo(f) switch
            {
                Relation.Unfriendly => tradeMultipliers.x,
                Relation.Friendly => tradeMultipliers.z,
                Relation.Allied => tradeMultipliers.w,
                Relation.Neutral => tradeMultipliers.y,
                _ => 0f,
            };
        }

        public float FriendlyStanding => friendlyStanding;
        public float HostileStanding => hostileStanding;

        /// <summary>Scrip handed over to win a gang round. False if at war (peace first) or the player can't pay.</summary>
        public bool PayTribute(FactionState f, PlayerWallet wallet, int amount)
        {
            if (f == null || f.eliminated || f.atWar || f.IsPlayer || wallet == null || !wallet.TrySpend(amount)) return false;
            ChangeStanding(f, amount * standingPerTributeScrip * f.definition.Goodwill);
            return true;
        }

        /// <summary>
        /// Asks for an alliance. Returns why not, or null if they agreed. On success, <paramref name="fallout"/>
        /// names the gangs it upset (the new ally's enemies), if any.
        /// </summary>
        public string ProposeAlliance(FactionState f, out string fallout)
        {
            fallout = null;
            if (f == null || f.eliminated) return "They're gone.";
            if (f.allied) return "You're already allies.";
            if (f.atWar) return "Not while you're at war.";
            if (GameHours < f.hostileUntil) return "They haven't forgotten what you did. Not yet.";
            if (f.standing < f.definition.AllianceStanding)
                return $"They want more respect first (standing {f.definition.AllianceStanding:0} needed).";
            var ally = SwornEnemyAlly(f);
            if (ally != null) return $"Not while you're allied with {The(ally.Name)}. They're sworn enemies.";
            f.allied = true;
            fallout = Ripple(f, -allianceFallout);
            RelationsChanged?.Invoke();
            return null;
        }

        /// <summary>A current ally that is <paramref name="f"/>'s sworn enemy, or whose sworn enemy is <paramref name="f"/>; null if none.</summary>
        public FactionState SwornEnemyAlly(FactionState f)
        {
            if (f == null) return null;
            var enemy = WorstEnemyOf(f);
            foreach (var o in states)
                if (o != f && o.allied && !o.eliminated && !o.IsPlayer && (o == enemy || WorstEnemyOf(o) == f)) return o;
            return null;
        }

        public void BreakAlliance(FactionState f)
        {
            if (f == null || !f.allied) return;
            f.allied = false;
            ChangeStanding(f, -breakAlliancePenalty);
        }

        /// <summary>Declares war. Returns the gangs it pleased (the target's enemies), if any.</summary>
        public string DeclareWar(FactionState f)
        {
            if (f == null || f.eliminated || f.atWar || f.IsPlayer) return null;
            f.atWar = true;
            f.allied = false;
            f.standing = -100f;
            string pleased = Ripple(f, enemyOfMyFriend * 0.8f);
            RelationsChanged?.Invoke();
            return pleased;
        }

        /// <summary>Changes standing with every enemy of a gang; returns their names.</summary>
        private string Ripple(FactionState f, float delta)
        {
            var names = new List<string>();
            foreach (var o in states)
            {
                if (o == f || o.eliminated || o.IsPlayer || Opinion(f, o) > enemyOpinion) continue;
                o.standing = Mathf.Clamp(o.standing + delta, -100f, 100f);
                names.Add(The(o.Name));
            }
            return names.Count > 0 ? string.Join(" and ", names) : null;
        }

        public int PeacePrice(FactionState f) => f == null ? 0 : Mathf.RoundToInt((peaceBasePrice + f.power * peacePricePerPower) / 50f) * 50;

        /// <summary>Buys an end to a war: they settle back to Unfriendly.</summary>
        public bool MakePeace(FactionState f, PlayerWallet wallet)
        {
            if (f == null || !f.atWar || wallet == null || !wallet.TrySpend(PeacePrice(f))) return false;
            f.atWar = false;
            f.hostileUntil = 0f;
            f.standing = Mathf.Max(f.standing, unfriendlyStanding - 25f);
            RelationsChanged?.Invoke();
            return true;
        }

        public bool KnowsRecipe(FactionState f) => f?.definition.SignatureRecipe != null && RecipeBook.Knows(f.definition.SignatureRecipe);

        /// <summary>Standing from which a gang will sell its recipe.</summary>
        public float RecipeSaleStanding => recipeStanding;

        /// <summary>Their recipe is for sale once the player's well enough thought of, and not at war. It's never given away.</summary>
        public bool CanBuyRecipe(FactionState f) =>
            f?.definition.SignatureRecipe != null && !KnowsRecipe(f) && !f.eliminated && !f.atWar && f.standing >= recipeStanding;

        public bool BuyRecipe(FactionState f, PlayerWallet wallet)
        {
            if (!CanBuyRecipe(f) || wallet == null || !wallet.TrySpend(f.definition.RecipePrice)) return false;
            RecipeBook.Learn(f.definition.SignatureRecipe);
            RelationsChanged?.Invoke();
            return true;
        }

        /// <summary>Moves how a gang feels about the player (clamped to -100..100).</summary>
        public void ChangeStanding(FactionState f, float delta)
        {
            f.standing = Mathf.Clamp(f.standing + delta, -100f, 100f);
            RelationsChanged?.Invoke();
        }

        // ---------------------------------------------------------------- daily requests

        private static readonly ItemCategory[] Goods = { ItemCategory.Food, ItemCategory.Drug, ItemCategory.Weapon };

        /// <summary>
        /// Tomorrow's request: one item, usually of the gang's favoured kind. More of cheap things, fewer of
        /// dear ones, and paid by what it's worth.
        /// </summary>
        private void NewRequest(FactionState f)
        {
            f.requestOpen = false;
            if (f.eliminated || f.IsPlayer) return;
            var category = rng.NextDouble() < 0.6 ? f.definition.FavouredSupply : Goods[rng.Next(Goods.Length)];
            if (Array.IndexOf(Goods, category) < 0) category = Goods[rng.Next(Goods.Length)];
            var choices = Requestable(category);
            if (choices.Count == 0)
            {
                foreach (var c in Goods) choices.AddRange(Requestable(c));
                if (choices.Count == 0) return;
            }
            var item = choices[rng.Next(choices.Count)];
            int value = Mathf.Max(1, item.BaseValue);
            var range = value < 15 ? requestCountCheap : value < 50 ? requestCount : requestCountDear;
            f.requestItem = item;
            f.requestCategory = item.Category;
            f.requestCount = rng.Next(range.x, range.y + 1);
            f.requestReward = Mathf.Max(5, Mathf.RoundToInt(f.requestCount * value * requestPremium * f.definition.DealGenerosity / 5f) * 5);
            f.requestOpen = true;
        }

        private List<ItemDefinition> requestable;
        private HashSet<ItemDefinition> taughtOnly;

        /// <summary>
        /// What a gang might ask for of a kind: food that fills you (not water), drugs, and weapons (not ammo).
        /// Nothing only a skill milestone or a gang can teach, unless the player has learned how to make it.
        /// </summary>
        private List<ItemDefinition> Requestable(ItemCategory category)
        {
            if (requestable == null) BuildRequestable();
            var list = new List<ItemDefinition>();
            foreach (var item in requestable)
            {
                if (item.Category != category) continue;
                if (taughtOnly.Contains(item) && !LearnedOutput(item)) continue;
                if (category == ItemCategory.Food && item.Nutrition <= 0f) continue;
                if (category == ItemCategory.Weapon && !item.IsWeapon) continue;
                list.Add(item);
            }
            return list;
        }

        private void BuildRequestable()
        {
            requestable = new List<ItemDefinition>();
            taughtOnly = new HashSet<ItemDefinition>();
            var taught = new HashSet<RecipeDefinition>();
            foreach (var f in states)
                if (f.definition.SignatureRecipe != null) taught.Add(f.definition.SignatureRecipe);
            foreach (var skill in Progression.Skills.All)
                foreach (var m in Progression.Skills.Milestones(skill))
                    if (m.Recipe != null) taught.Add(m.Recipe);
            if (SaveRegistry.Instance == null) return;
            var otherwiseMade = new HashSet<ItemDefinition>();
            foreach (var asset in SaveRegistry.Instance.Assets)
            {
                if (asset is ItemDefinition item) requestable.Add(item);
                else if (asset is RecipeDefinition r && r.Output != null)
                {
                    if (taught.Contains(r)) taughtOnly.Add(r.Output);
                    else otherwiseMade.Add(r.Output);
                }
            }
            taughtOnly.ExceptWith(otherwiseMade);
        }

        private static bool LearnedOutput(ItemDefinition item)
        {
            foreach (var r in RecipeBook.Learned)
                if (r.Output == item) return true;
            return false;
        }

        public static int CountGoods(Inventory inventory, ItemCategory category)
        {
            int n = 0;
            for (int i = 0; i < inventory.SlotCount; i++)
            {
                var s = inventory.GetSlot(i);
                if (!s.IsEmpty && s.item.Category == category) n += s.quantity;
            }
            return n;
        }

        /// <summary>Removes up to so many goods of a kind, cheapest first. Returns their total value and how many were taken.</summary>
        private static int TakeGoods(Inventory inventory, Func<ItemCategory, bool> which, int count) => TakeGoods(inventory, which, count, out _);

        private static int TakeGoods(Inventory inventory, Func<ItemCategory, bool> which, int count, out int taken)
        {
            taken = 0;
            var slots = new List<int>();
            for (int i = 0; i < inventory.SlotCount; i++)
            {
                var s = inventory.GetSlot(i);
                if (!s.IsEmpty && which(s.item.Category)) slots.Add(i);
            }
            slots.Sort((a, b) => inventory.GetSlot(a).item.ValueAt(inventory.GetSlot(a).quality)
                .CompareTo(inventory.GetSlot(b).item.ValueAt(inventory.GetSlot(b).quality)));
            int value = 0;
            foreach (int i in slots)
            {
                if (count <= 0) break;
                var s = inventory.GetSlot(i);
                int take = Mathf.Min(count, s.quantity);
                value += s.item.ValueAt(s.quality) * take;
                inventory.RemoveAt(i, take);
                count -= take;
                taken += take;
            }
            return value;
        }

        public bool CanFillRequest(FactionState f, Inventory inventory) =>
            f != null && f.requestOpen && f.requestItem != null && WillTrade(f) && inventory != null && inventory.Count(f.requestItem) >= f.requestCount;

        /// <summary>Hands over the goods for today's request: scrip, standing, and the goods feed the gang.</summary>
        public bool FillRequest(FactionState f, Inventory inventory, PlayerWallet wallet)
        {
            if (!CanFillRequest(f, inventory) || wallet == null) return false;
            // Any quality will do: the worst goes first.
            int value = f.requestItem.ValueAt(ItemQuality.Standard) * inventory.Remove(f.requestItem, f.requestCount);
            wallet.Add(f.requestReward);
            f.pendingSupply += value * powerPerScrip * (f.requestCategory == f.definition.FavouredSupply ? favouredMultiplier : 1f);
            f.requestOpen = false;
            ChangeStanding(f, requestStanding * f.definition.Goodwill * (f.allied ? 1.5f : 1f));
            return true;
        }

        // ---------------------------------------------------------------- the player's gang

        private static bool OwnsAProperty()
        {
            foreach (var p in Property.All)
                if (p.Owned) return true;
            return false;
        }

        /// <summary>
        /// Founds the player's gang. Needs an owned property (its turf starts there) and the founding cost.
        /// Every gang takes notice. Returns why not, or null on success.
        /// </summary>
        public string FoundGang(string name, Color colour, PlayerWallet wallet)
        {
            if (PlayerGang != null) return "You already run a gang.";
            if (!OwnsAProperty()) return "You need a property to run it from.";
            if (string.IsNullOrWhiteSpace(name)) return "Give it a name.";
            if (wallet == null || !wallet.TrySpend(foundingCost)) return $"Founding a gang costs {foundingCost} scrip.";

            CreatePlayerGang(name.Trim(), colour);
            ReseedPlayerTurf();
            foreach (var f in states)
                if (!f.IsPlayer && !f.eliminated)
                    f.standing = Mathf.Clamp(f.standing - (f.allied ? foundingTension / 3f : foundingTension), -100f, 100f);
            AddNews($"You founded {name.Trim()}. The other gangs are watching.");
            TerritoryChanged?.Invoke();
            RelationsChanged?.Invoke();
            return null;
        }

        private void CreatePlayerGang(string name, Color colour)
        {
            var def = FactionDefinition.CreatePlayerGang(name, colour);
            PlayerGang = new FactionState { definition = def, index = states.Count, power = def.StartingPower };
            states.Add(PlayerGang);
            ApplyStance();
        }

        /// <summary>Every property the player owns anchors their turf: free ground at its door is reclaimed each day.</summary>
        private bool ReseedPlayerTurf()
        {
            if (PlayerGang == null) return false;
            bool changed = false;
            foreach (var p in Property.All)
            {
                if (!p.Owned) continue;
                int c = CellAt(new Vector2(p.Entrance.x, p.Entrance.z));
                if (c < 0 || reserved[c] || owner[c] == PlayerGang.index) continue;
                // A door in someone else's turf is still the player's ground; the gang there won't like it.
                if (owner[c] != Unclaimed) states[owner[c]].standing = Mathf.Clamp(states[owner[c]].standing - 5f, -100f, 100f);
                SetOwner(c, PlayerGang);
                CheckEncroachment(c);
                changed = true;
            }
            return changed;
        }

        /// <summary>Moving in on an ally's home district ends the alliance.</summary>
        private void CheckEncroachment(int cell)
        {
            int district = greybox.DistrictIndexAt(CellCenter(cell));
            foreach (var f in states)
            {
                if (!f.allied || HomeDistrictIndex(f.definition) != district) continue;
                f.allied = false;
                f.standing = Mathf.Clamp(f.standing - breakAlliancePenalty, -100f, 100f);
                AddNews($"You moved in on {The(f.Name)}{(f.Name.EndsWith("s") ? "'" : "'s")} home turf. The alliance is over.");
            }
        }

        public void SetStance(GangStance stance)
        {
            Stance = stance;
            ApplyStance();
            RelationsChanged?.Invoke();
        }

        private void ApplyStance()
        {
            if (PlayerGang == null) return;
            switch (Stance)
            {
                case GangStance.Hold: PlayerGang.definition.SetStance(0f, 0f); break;
                case GangStance.Expand: PlayerGang.definition.SetStance(playerExpansionRate, 0f); break;
                default: PlayerGang.definition.SetStance(playerExpansionRate, pushAggression); break;
            }
        }

        /// <summary>What it costs to take on an NPC, or 0 if they won't join.</summary>
        public int RecruitCost(NPC.NpcType type) => type switch
        {
            NPC.NpcType.Civilian => civilianRecruitCost,
            NPC.NpcType.Junkie => junkieRecruitCost,
            _ => 0,
        };

        /// <summary>Takes someone into the crew. Returns why not, or null on success.</summary>
        public string Recruit(NPC.NpcType type, PlayerWallet wallet)
        {
            if (PlayerGang == null) return "You don't have a gang to bring them into.";
            int cost = RecruitCost(type);
            if (cost <= 0) return "They won't work for you.";
            if (wallet == null || !wallet.TrySpend(cost)) return $"They want {cost} scrip up front.";
            CrewMembers++;
            CrewStrength += type == NPC.NpcType.Junkie ? 0.6f : 1f;
            AddNews($"A new {(type == NPC.NpcType.Junkie ? "junkie" : "recruit")} joins the crew.");
            RelationsChanged?.Invoke();
            return null;
        }

        /// <summary>
        /// Hands food, drugs and weapons to the crew, cheapest first: it all turns into the gang's strength.
        /// Returns what happened, for the player.
        /// </summary>
        public string SupplyCrew(Inventory inventory, int items)
        {
            if (PlayerGang == null) return "You don't have a gang yet.";
            if (inventory == null) return null;
            // Drugs first (they empower the crew most), then food and weapons.
            int drugValue = TakeGoods(inventory, c => c == ItemCategory.Drug, items, out int drugs);
            int otherValue = items > drugs ? TakeGoods(inventory, c => c == ItemCategory.Food || c == ItemCategory.Weapon, items - drugs) : 0;
            float value = drugValue * 1.5f + otherValue;
            if (value <= 0f) return "You've nothing the crew can use: bring food, drugs or weapons.";
            PlayerGang.pendingSupply += value * crewSupplyPower;
            string line = $"The crew are fed and armed (+{value * crewSupplyPower:0} power tomorrow).";
            AddNews(line);
            RelationsChanged?.Invoke();
            return line;
        }

        public void RenameGang(string name)
        {
            if (PlayerGang == null || string.IsNullOrWhiteSpace(name)) return;
            PlayerGang.definition.Rename(name.Trim());
            RelationsChanged?.Invoke();
        }

        /// <summary>Midnight wages. Short-changed crew walk out: a third of them, rounded up.</summary>
        private void PayCrew()
        {
            if (PlayerGang == null || CrewMembers == 0) return;
            var wallet = FindFirstObjectByType<PlayerWallet>();
            if (wallet != null && wallet.TrySpend(DailyWages)) return;
            int leaving = Mathf.CeilToInt(CrewMembers / 3f);
            float each = CrewStrength / CrewMembers;
            CrewMembers -= leaving;
            CrewStrength = Mathf.Max(0f, CrewStrength - each * leaving);
            AddNews($"You couldn't make wages. {leaving} of the crew walked.");
        }

        private void AddNews(string line)
        {
            int day = TimeManager.Instance != null ? TimeManager.Instance.Day : 1;
            news.Insert(0, $"Day {day}: {line}");
            while (news.Count > 6) news.RemoveAt(news.Count - 1);
        }

        // ---------------------------------------------------------------- saving

        public FactionsSave Capture()
        {
            var save = new FactionsSave { owners = (int[])owner.Clone(), dominant = Dominant?.definition.name };
            foreach (var f in states)
                save.factions.Add(new FactionSave
                {
                    id = f.definition.name,
                    power = f.power,
                    standing = f.standing,
                    pendingSupply = f.pendingSupply,
                    recentSupply = f.recentSupply,
                    expansionCarry = f.expansionCarry,
                    eliminated = f.eliminated,
                    hostileUntil = f.hostileUntil,
                    allied = f.allied,
                    atWar = f.atWar,
                    requestCategory = (int)f.requestCategory,
                    requestItem = SaveRegistry.IdOf(f.requestItem),
                    requestCount = f.requestCount,
                    requestReward = f.requestReward,
                    requestOpen = f.requestOpen,
                });
            foreach (var kv in opinions)
                save.opinions.Add(new OpinionSave
                {
                    a = states[(int)(kv.Key >> 32)].definition.name,
                    b = states[(int)(kv.Key & 0xffffffff)].definition.name,
                    opinion = kv.Value,
                });
            if (PlayerGang != null)
                save.playerGang = new PlayerGangSave
                {
                    founded = true,
                    name = PlayerGang.Name,
                    colour = PlayerGang.definition.Color,
                    members = CrewMembers,
                    strength = CrewStrength,
                    stance = (int)Stance,
                    news = new List<string>(news),
                };
            return save;
        }

        /// <summary>Puts the gangs and their ground back as saved. Gangs added since the save keep their fresh start.</summary>
        public void Restore(FactionsSave save)
        {
            if (save?.owners == null || save.owners.Length != owner.Length)
            {
                Debug.LogWarning("Saved territory doesn't match this map; keeping the fresh layout.", this);
                return;
            }

            // The player's gang first, so its saved territory has someone to belong to.
            if (save.playerGang != null && save.playerGang.founded && PlayerGang == null)
            {
                CrewMembers = save.playerGang.members;
                CrewStrength = save.playerGang.strength;
                Stance = (GangStance)save.playerGang.stance;
                CreatePlayerGang(save.playerGang.name, save.playerGang.colour);
                news.Clear();
                if (save.playerGang.news != null) news.AddRange(save.playerGang.news);
            }

            // Saved index -> current faction.
            var map = new FactionState[save.factions.Count];
            for (int i = 0; i < save.factions.Count; i++)
            {
                var s = save.factions[i];
                var f = states.Find(x => x.definition.name == s.id);
                map[i] = f;
                if (f == null) continue;
                f.power = s.power;
                f.standing = s.standing;
                f.pendingSupply = s.pendingSupply;
                f.recentSupply = s.recentSupply;
                f.expansionCarry = s.expansionCarry;
                f.eliminated = s.eliminated;
                f.hostileUntil = s.hostileUntil;
                f.allied = s.allied;
                f.atWar = s.atWar;
                // Saves from before requests existed get a fresh one.
                if (s.requestCount > 0)
                {
                    f.requestCategory = (ItemCategory)s.requestCategory;
                    f.requestItem = SaveRegistry.Lookup<ItemDefinition>(s.requestItem);
                    f.requestCount = s.requestCount;
                    f.requestReward = s.requestReward;
                    f.requestOpen = s.requestOpen;
                    // Saves from when requests were for any goods of a kind get today's afresh, for one item.
                    if (f.requestItem == null && f.requestOpen) NewRequest(f);
                }
            }

            if (save.opinions != null)
                foreach (var o in save.opinions)
                {
                    var a = states.Find(x => x.definition.name == o.a);
                    var b = states.Find(x => x.definition.name == o.b);
                    if (a != null && b != null) SetOpinion(a, b, o.opinion);
                }

            foreach (var f in states) f.cells = 0;
            for (int c = 0; c < owner.Length; c++)
            {
                int saved = save.owners[c];
                var f = saved >= 0 && saved < map.Length ? map[saved] : null;
                owner[c] = f?.index ?? Unclaimed;
                if (f != null) f.cells++;
            }
            Dominant = states.Find(x => x.definition.name == save.dominant);
            TerritoryChanged?.Invoke();
            RelationsChanged?.Invoke();
        }

        // ---------------------------------------------------------------- player supply

        /// <summary>Records goods the player sold to a gang member: feeds the gang's power, expansion and standing.</summary>
        public void RecordSupply(FactionState faction, ItemCategory category, int price)
        {
            if (faction == null || faction.eliminated || price <= 0) return;
            float value = price * (category == faction.definition.FavouredSupply ? favouredMultiplier : 1f);
            faction.pendingSupply += value * powerPerScrip;
            faction.recentSupply += value;
            if (!faction.IsPlayer)
                ChangeStanding(faction, price * standingPerScrip * faction.definition.Goodwill * (faction.allied ? 1.5f : 1f));
        }

        /// <summary>Price multiplier for selling to a gang: gangs close to being wiped out pay more.</summary>
        public float PriceMultiplier(FactionState faction)
        {
            if (faction == null || faction.IsPlayer || desperationShare <= 0f) return 1f;
            float t = 1f - Mathf.Clamp01(ShareOf(faction) / desperationShare);
            return Mathf.Lerp(1f, maxDesperationPrice, t);
        }
    }
}
