using System;
using System.Collections.Generic;
using ThePitt.Core;
using ThePitt.Factions;
using ThePitt.Items;
using ThePitt.World;
using UnityEngine;
using UnityEngine.AI;
using Random = UnityEngine.Random;

namespace ThePitt.NPC
{
    /// <summary>
    /// Keeps The Pitt populated. The same number of each kind of NPC (civilian, junkie, gang member) is
    /// out walking at all times: when one goes indoors, another of the same kind steps out of a doorway,
    /// in whichever district currently has the fewest people about. Gang members belong to a gang and
    /// keep to its home district: they come out of its doorways, walk its streets and go back in there. Each gang square is worked by a
    /// dealer and one of each other kind, who stay put. How many are out follows the time of
    /// day: the streets fill up through the day and thin out in the small hours, with people heading
    /// indoors or stepping out to match. Also stands one of each kind in every
    /// gang square. Gang members out walking belong to the gang whose turf they came from and keep to
    /// it where they can.
    /// </summary>
    public class NpcPopulation : MonoBehaviour
    {
        [Serializable]
        public class LootDrop
        {
            public ItemDefinition item;
            [Range(0f, 1f)] public float chance = 0.3f;
            [Min(1)] public int min = 1;
            [Min(1)] public int max = 1;
        }

        [Serializable]
        public class NpcKind
        {
            public NpcBuyerProfile profile;
            public Material material;
            public Vector2 walkSpeed = new(1.2f, 1.6f);
            public string[] names;
            [Header("Combat")]
            [Min(1f)] public float maxHealth = 60f;
            [Tooltip("Stands and fights when attacked (otherwise runs).")]
            public bool fightsBack;
            [Range(0f, 1f)] public float armedChance;
            [Tooltip("What might be found on the body.")]
            public LootDrop[] loot;
        }

        [SerializeField] private PittGreybox greybox;
        [SerializeField] private NpcWalker prefab;
        [SerializeField] private NpcKind[] kinds;
        [Tooltip("The dealer who works each gang square, beside one of each other kind.")]
        [SerializeField] private NpcKind dealer;
        [SerializeField] private NpcVendor.StockEntry[] dealerStock;
        [Tooltip("What an armed NPC carries, and drops (sometimes) when killed.")]
        [SerializeField] private ItemDefinition carriedGun;
        [SerializeField] private ItemDefinition carriedAmmo;
        [Tooltip("How many of each kind are out walking at the busiest time of day.")]
        [SerializeField, Min(0)] private int perKind = 10;
        [Tooltip("Fraction of the busiest crowd out at each hour (0-24).")]
        [SerializeField] private AnimationCurve levelByHour = new(
            new Keyframe(0f, 0.45f), new Keyframe(3f, 0.25f), new Keyframe(5f, 0.2f), new Keyframe(7f, 0.5f),
            new Keyframe(10f, 1f), new Keyframe(18f, 1f), new Keyframe(21f, 0.8f), new Keyframe(24f, 0.45f));
        [Tooltip("Seconds between rounds of NPCs stepping out of (or heading into) doorways.")]
        [SerializeField, Min(0.1f)] private float spawnInterval = 0.5f;
        [Tooltip("Most of each kind that step out (or head in) per round. More go the bigger the gap.")]
        [SerializeField, Min(1)] private int maxPerTick = 4;
        [Tooltip("Stand one of each kind in every gang square.")]
        [SerializeField] private bool populateSquares = true;
        [Tooltip("Walking destinations are at least this far from where the NPC is.")]
        [SerializeField] private Vector2 tripLength = new(15f, 90f);

        private readonly List<NpcWalker>[] walking = { new(), new(), new() };
        private readonly Dictionary<NpcWalker, int> kindOf = new();
        private readonly Dictionary<NpcWalker, FactionDefinition> gangOf = new();
        /// <summary>A place in a gang square and who stands in it; empty spots are refilled at dawn.</summary>
        private class SquareSlot
        {
            public NpcKind kind;
            public Vector3 position;
            public Quaternion rotation;
            public NpcWalker npc;
        }

        private readonly List<SquareSlot> squareSlots = new();
        private readonly List<NpcWalker> guards = new();
        private Transform guardsRoot;
        private string guardSignature = "";
        private Transform bodiesRoot;
        private Transform walkersRoot, squaresRoot;
        private float spawnTimer;
        private bool populated;
        /// <summary>A point in the heart of the street network; anything that can reach it can reach everywhere.</summary>
        private Vector3 hub;
        private MaterialPropertyBlock block;
        private NavMeshPath pathBuffer;
        /// <summary>Doorways connected to the rest of the city (a few can end up walled off).</summary>
        private readonly List<PittGreybox.Doorway> doors = new();

        private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");

        private void Start()
        {
            // The city lives in the World scene; no reference can reach across.
            if (greybox == null) greybox = PittGreybox.Current;
            block = new MaterialPropertyBlock();
            pathBuffer = new NavMeshPath();
            walkersRoot = new GameObject("Walking NPCs").transform;
            walkersRoot.SetParent(transform, false);
            squaresRoot = new GameObject("Square NPCs").transform;
            squaresRoot.SetParent(transform, false);

            bodiesRoot = new GameObject("Bodies").transform;
            bodiesRoot.SetParent(transform, false);
            guardsRoot = new GameObject("Crew Guards").transform;
            guardsRoot.SetParent(transform, false);
            if (FactionManager.Instance != null)
            {
                FactionManager.Instance.TerritoryChanged += RecolourSquareGangs;
                FactionManager.Instance.RelationsChanged += RefreshGuards;
            }
            Properties.Property.OwnershipChanged += OnPropertyChanged;
            if (TimeManager.Instance != null) TimeManager.Instance.DawnStarted += RefillSquares;
        }

        /// <summary>Places everyone once the baked NavMesh has loaded (it isn't queryable on the first frames).</summary>
        private void FirstPopulate()
        {
            // The street outside the player's home: always on a thoroughfare, so it's on the main network
            // (the middle of the map can be a walled-off courtyard).
            var center = greybox.startPoint;
            if (!NavMesh.SamplePosition(new Vector3(center.x, greybox.HeightAt(center) + 0.5f, center.y), out var hubHit, 5f, NavMesh.AllAreas)) return;
            hub = hubHit.position;
            populated = true;
            if (greybox.npcLineup)
            {
                PopulateLineup();
                RefreshGuards();
                return;
            }
            FindConnectedDoors();
            if (populateSquares) PopulateSquares();
            RefreshGuards();
            // The day doesn't start empty: the first crowd is already out on the streets.
            int target = Target;
            for (int k = 0; k < kinds.Length && k < walking.Length; k++)
                for (int i = 0; i < target; i++)
                {
                    var gang = IsGangKind(k) ? NextGang() : null;
                    if (IsGangKind(k) && gang == null) break;
                    if (TryRandomStreetPoint(HomeDistrict(gang), out var p)) SpawnOnStreet(k, p, gang);
                }
        }

        private void OnDestroy()
        {
            if (FactionManager.Instance != null)
            {
                FactionManager.Instance.TerritoryChanged -= RecolourSquareGangs;
                FactionManager.Instance.RelationsChanged -= RefreshGuards;
            }
            Properties.Property.OwnershipChanged -= OnPropertyChanged;
            if (TimeManager.Instance != null) TimeManager.Instance.DawnStarted -= RefillSquares;
        }

        private void Update()
        {
            if (!populated)
            {
                FirstPopulate();
                return;
            }
            if (greybox.npcLineup) return;
            spawnTimer -= Time.deltaTime;
            if (spawnTimer > 0f) return;
            spawnTimer = spawnInterval;

            int target = Target;
            for (int k = 0; k < kinds.Length && k < walking.Length; k++)
            {
                // Those already heading indoors don't count.
                int staying = 0;
                foreach (var w in walking[k])
                    if (!w.IsLeaving) staying++;

                // Too many out for the hour: send a few home, more the further over it is.
                if (staying > target)
                {
                    int send = Mathf.Clamp((staying - target) / 10 + 1, 1, maxPerTick);
                    foreach (var w in walking[k])
                    {
                        if (send <= 0) break;
                        if (w.IsLeaving) continue;
                        w.SendHome();
                        send--;
                    }
                    continue;
                }

                // Short-handed: step out of doorways, more at once the bigger the gap, so a big crowd keeps up.
                int spawn = Mathf.Clamp((target - staying) / 10 + 1, 0, maxPerTick);
                if (staying >= target) spawn = 0;
                for (int i = 0; i < spawn; i++)
                {
                    var gang = IsGangKind(k) ? NextGang() : null;
                    if (IsGangKind(k) && gang == null) break;
                    if (!TryPickSpawnDoor(HomeDistrict(gang), out var door)) break;
                    SpawnFromDoor(k, door, gang);
                }
            }
        }

        /// <summary>How many of each kind should be out right now.</summary>
        public int Target
        {
            get
            {
                float hour = TimeManager.Instance != null ? TimeManager.Instance.TimeOfDay : 12f;
                return Mathf.Clamp(Mathf.RoundToInt(perKind * levelByHour.Evaluate(hour)), 0, perKind);
            }
        }

        // ---------------------------------------------------------------- spawning

        private NpcWalker Create(NpcKind k, Vector3 position, Transform parent, FactionDefinition gang)
        {
            var npc = Instantiate(prefab, position, Quaternion.identity, parent);
            var names = k.names != null && k.names.Length > 0 ? k.names : new[] { "Stranger" };
            npc.GetComponent<NpcBuyer>().Setup(names[Random.Range(0, names.Length)], k.profile, gang);
            npc.name = $"NPC_{k.profile.TypeName.Replace(" ", "")}";

            var renderers = npc.GetComponentsInChildren<Renderer>();
            foreach (var r in renderers)
                if (r.sharedMaterial != null && r.sharedMaterial.name.StartsWith("NPC_") && !r.sharedMaterial.name.Contains("Eyes"))
                    r.sharedMaterial = k.material;
            if (gang != null) Tint(renderers, gang.Color);

            var combat = npc.GetComponent<NpcCombat>();
            if (combat != null)
            {
                combat.Setup(k.maxHealth, k.fightsBack, Random.value < k.armedChance);
                combat.Died += dead => OnDied(npc, k, dead);
            }
            return npc;
        }

        private void SpawnFromDoor(int kind, PittGreybox.Doorway door, FactionDefinition gang)
        {
            var npc = Create(kinds[kind], door.Inside, walkersRoot, gang);
            Track(npc, kind);
            npc.BeginFromDoor(this, door, Speed(kind));
        }

        private void SpawnOnStreet(int kind, Vector3 position, FactionDefinition gang)
        {
            var npc = Create(kinds[kind], position, walkersRoot, gang);
            Track(npc, kind);
            npc.BeginOnStreet(this, position, Speed(kind));
        }

        private void Track(NpcWalker npc, int kind)
        {
            walking[kind].Add(npc);
            kindOf[npc] = kind;
            var gang = npc.Buyer.Faction;
            if (gang != null) gangOf[npc] = gang.definition;
        }

        private float Speed(int kind) => Random.Range(kinds[kind].walkSpeed.x, kinds[kind].walkSpeed.y);

        /// <summary>Removes an NPC that has gone indoors; a replacement of the same kind follows.</summary>
        public void Despawn(NpcWalker npc)
        {
            if (kindOf.TryGetValue(npc, out int kind)) walking[kind].Remove(npc);
            kindOf.Remove(npc);
            gangOf.Remove(npc);
            Destroy(npc.gameObject);
        }

        private bool IsGangKind(int kind) => kinds[kind].profile.NpcType == NpcType.GangMember;

        /// <summary>The gang a new gang member joins: whichever surviving gang has the fewest out walking.</summary>
        private FactionDefinition NextGang()
        {
            var factions = FactionManager.Instance;
            if (factions == null) return null;
            FactionDefinition best = null;
            int fewest = int.MaxValue;
            foreach (var f in factions.Factions)
            {
                if (!f.Alive || HomeDistrict(f.definition) < 0) continue;
                int count = 0;
                foreach (var g in gangOf.Values)
                    if (g == f.definition) count++;
                if (count < fewest || (count == fewest && Random.value < 0.5f))
                {
                    fewest = count;
                    best = f.definition;
                }
            }
            return best;
        }

        /// <summary>A gang's home district (index into the greybox districts), or -1 for no gang / none found.</summary>
        private static int HomeDistrict(FactionDefinition gang) =>
            gang != null && FactionManager.Instance != null ? FactionManager.Instance.HomeDistrictOf(gang) : -1;

        private int DistrictOf(Vector3 p) => greybox.DistrictIndexAt(new Vector2(p.x, p.z));

        /// <summary>The gang that calls a square's district home, if it's still standing.</summary>
        private FactionDefinition HomeGangAt(Vector3 position)
        {
            var factions = FactionManager.Instance;
            if (factions == null) return null;
            int district = DistrictOf(position);
            foreach (var f in factions.Factions)
                if (f.Alive && factions.HomeDistrictOf(f.definition) == district) return f.definition;
            return null;
        }

        private void Tint(Renderer[] renderers, Color color)
        {
            foreach (var r in renderers)
            {
                if (r.sharedMaterial == null || r.sharedMaterial.name.Contains("Eyes")) continue;
                r.GetPropertyBlock(block);
                block.SetColor(BaseColor, Color.Lerp(r.sharedMaterial.GetColor(BaseColor), color, 0.6f));
                r.SetPropertyBlock(block);
            }
        }

        // ---------------------------------------------------------------- gang squares

        private void PopulateSquares()
        {
            squareSlots.Clear();
            foreach (var square in greybox.gangSquares)
            {
                var center = square.rect.center;
                float start = Random.Range(0f, 360f);
                var crew = new List<NpcKind>(kinds);
                if (dealer?.profile != null) crew.Add(dealer);
                for (int k = 0; k < crew.Count; k++)
                {
                    // A loose huddle round the middle of the square, facing in.
                    float angle = (start + k * 360f / crew.Count + Random.Range(-15f, 15f)) * Mathf.Deg2Rad;
                    var offset = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * Random.Range(1.8f, 2.8f);
                    var p2 = center + offset;
                    var position = new Vector3(p2.x, square.height, p2.y);
                    // On the square's own floor: a NavMesh point well above or below it is some other level.
                    if (!NavMesh.SamplePosition(position, out var hit, 1.5f, NavMesh.AllAreas) || Mathf.Abs(hit.position.y - square.height) > 0.5f) continue;
                    position = hit.position;

                    var slot = new SquareSlot
                    {
                        kind = crew[k],
                        position = position,
                        rotation = Quaternion.LookRotation(new Vector3(-offset.x, 0f, -offset.y)),
                    };
                    squareSlots.Add(slot);
                    FillSlot(slot);
                }
            }
            RecolourSquareGangs();
        }

        /// <summary>The dev world's line-up: one of each kind and a dealer, standing still side by side.</summary>
        private void PopulateLineup()
        {
            squareSlots.Clear();
            var crew = new List<NpcKind>(kinds);
            if (dealer?.profile != null) crew.Add(dealer);
            var facing = new Vector3(greybox.npcLineupFacing.x, 0f, greybox.npcLineupFacing.y);
            for (int k = 0; k < crew.Count; k++)
            {
                var p2 = greybox.npcLineupStart + Vector2.right * k * 2f;
                var position = new Vector3(p2.x, greybox.HeightAt(p2), p2.y);
                if (NavMesh.SamplePosition(position + Vector3.up * 0.5f, out var hit, 1.5f, NavMesh.AllAreas)) position = hit.position;
                var slot = new SquareSlot { kind = crew[k], position = position, rotation = Quaternion.LookRotation(facing) };
                squareSlots.Add(slot);
                FillSlot(slot);
            }
            RecolourSquareGangs();
        }

        private void FillSlot(SquareSlot slot)
        {
            // A square's gang member works for the gang whose district it's in (or, in unclaimed districts,
            // follows whoever holds the ground). The dev line-up's, standing on no gang's turf, joins any gang.
            var gang = slot.kind.profile.NpcType == NpcType.GangMember ? HomeGangAt(slot.position) : null;
            if (gang == null && greybox.npcLineup && slot.kind.profile.NpcType == NpcType.GangMember) gang = NextGang();
            var npc = Create(slot.kind, slot.position, squaresRoot, gang);
            npc.transform.rotation = slot.rotation;
            npc.GetComponent<NavMeshAgent>().enabled = false;
            npc.enabled = false;
            if (slot.kind == dealer && dealerStock != null && dealerStock.Length > 0)
                npc.gameObject.AddComponent<NpcVendor>().Setup(dealerStock);
            if (npc.TryGetComponent<NpcCombat>(out var combat)) combat.SetPost(slot.position, slot.rotation);
            slot.npc = npc;
        }

        /// <summary>At dawn, new faces take the places of anyone killed in the gang squares (and at the player's doors).</summary>
        private void RefillSquares()
        {
            foreach (var slot in squareSlots)
                if (slot.npc == null) FillSlot(slot);
            RecolourSquareGangs();
            guardSignature = "";
            RefreshGuards();
        }

        // ---------------------------------------------------------------- the player's crew

        private void OnPropertyChanged(Properties.Property _) => RefreshGuards();

        /// <summary>
        /// The player's crew stand guard at the doors of the player's properties: one per door, two once the
        /// crew is six strong. Rebuilt only when the gang, its size or the properties change.
        /// </summary>
        private void RefreshGuards()
        {
            var fm = FactionManager.Instance;
            if (!populated || guardsRoot == null || fm == null) return;
            var gang = fm.PlayerGang;
            int perDoor = gang == null || fm.CrewMembers == 0 ? 0 : fm.CrewMembers >= 6 ? 2 : 1;
            var owned = new List<Properties.Property>();
            foreach (var p in Properties.Property.All)
                if (p.Owned) owned.Add(p);
            string signature = $"{perDoor}:{gang?.Name}:{string.Join(",", owned.ConvertAll(p => p.Id))}";
            if (signature == guardSignature) return;
            guardSignature = signature;

            foreach (var g in guards)
                if (g != null && !g.GetComponent<NpcCombat>().IsDead) Destroy(g.gameObject);
            guards.Clear();
            if (perDoor == 0) return;

            NpcKind kind = null;
            foreach (var k in kinds)
                if (k.profile.NpcType == NpcType.GangMember) kind = k;
            if (kind == null) return;

            foreach (var p in owned)
            {
                var normal = new Vector3(Mathf.Sin(p.OutsideYaw * Mathf.Deg2Rad), 0f, Mathf.Cos(p.OutsideYaw * Mathf.Deg2Rad));
                var along = Vector3.Cross(Vector3.up, normal);
                for (int i = 0; i < perDoor; i++)
                {
                    var spot = p.Entrance + normal * 1.0f + along * (i == 0 ? 1.4f : -1.4f);
                    if (NavMesh.SamplePosition(spot, out var hit, 1.5f, NavMesh.AllAreas)) spot = hit.position;
                    var npc = Create(kind, spot, guardsRoot, gang.definition);
                    var facing = Quaternion.LookRotation(normal);
                    npc.transform.rotation = facing;
                    npc.GetComponent<NavMeshAgent>().enabled = false;
                    npc.enabled = false;
                    if (npc.TryGetComponent<NpcCombat>(out var combat)) combat.SetPost(spot, facing);
                    npc.name = "Crew_" + npc.Buyer.NpcName;
                    guards.Add(npc);
                }
            }
        }

        /// <summary>Square gang members work for whoever holds the square, and wear their colour.</summary>
        private void RecolourSquareGangs()
        {
            foreach (var slot in squareSlots)
            {
                if (slot.npc == null || slot.kind.profile.NpcType != NpcType.GangMember) continue;
                var renderers = slot.npc.GetComponentsInChildren<Renderer>();
                var gang = slot.npc.Buyer.Faction;
                if (gang != null) Tint(renderers, gang.definition.Color);
                else foreach (var r in renderers) r.SetPropertyBlock(null);
            }
        }

        // ---------------------------------------------------------------- deaths

        /// <summary>Turns a killed NPC into a body to search, and frees their place for someone new.</summary>
        private void OnDied(NpcWalker npc, NpcKind kind, NpcCombat combat)
        {
            if (kindOf.TryGetValue(npc, out int k)) walking[k].Remove(npc);
            kindOf.Remove(npc);
            gangOf.Remove(npc);
            foreach (var slot in squareSlots)
                if (slot.npc == npc) slot.npc = null;

            var carried = new List<ItemStack>();
            if (kind.loot != null)
                foreach (var drop in kind.loot)
                    if (drop.item != null && Random.value < drop.chance)
                        carried.Add(new ItemStack(drop.item, Random.Range(drop.min, Mathf.Max(drop.min, drop.max) + 1),
                            (ItemQuality)Random.Range((int)ItemQuality.Poor, (int)ItemQuality.Good + 1)));
            if (combat.Armed)
            {
                if (carriedGun != null && Random.value < 0.5f)
                    carried.Add(new ItemStack(carriedGun, 1, (ItemQuality)Random.Range((int)ItemQuality.Poor, (int)ItemQuality.Standard + 1)));
                if (carriedAmmo != null) carried.Add(new ItemStack(carriedAmmo, Random.Range(1, 5), ItemQuality.Standard));
            }

            var buyer = npc.Buyer;
            string name = buyer != null ? buyer.NpcName : "Stranger";
            int cash = buyer != null ? buyer.Cash : 0;
            if (buyer != null) Destroy(buyer);
            if (npc.TryGetComponent<NpcVendor>(out var vendor)) Destroy(vendor);
            npc.gameObject.AddComponent<NpcCorpse>().Setup(name, cash, carried);
            npc.transform.SetParent(bodiesRoot, true);
            npc.name = "Body_" + name;
        }

        // ---------------------------------------------------------------- places

        /// <summary>Somewhere for a walking NPC to head next. Gang members only go places in their home district.</summary>
        public bool TryPickDestination(NpcWalker npc, out Vector3 destination)
        {
            gangOf.TryGetValue(npc, out var gang);
            int district = HomeDistrict(gang);
            var from = npc.transform.position;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                Vector3 candidate;
                float roll = Random.value;
                if (roll < 0.25f && greybox.gangSquares.Count > 0)
                {
                    var sq = greybox.gangSquares[Random.Range(0, greybox.gangSquares.Count)];
                    var c = sq.rect.center + Vector2.Scale(Random.insideUnitCircle, sq.rect.size * 0.35f);
                    candidate = new Vector3(c.x, sq.height, c.y);
                    if (district >= 0 && DistrictOf(candidate) != district) continue;
                }
                else if (!TryRandomStreetPoint(district, out candidate)) continue;

                float distance = Vector3.Distance(from, candidate);
                // Within a district the trips are shorter; only insist on a minimum.
                if (distance < tripLength.x || (district < 0 && distance > tripLength.y)) continue;
                if (!Reachable(npc, candidate, out destination)) continue;
                return true;
            }
            destination = default;
            return false;
        }

        private void FindConnectedDoors()
        {
            doors.Clear();
            foreach (var door in greybox.doorways)
                if (NavMesh.SamplePosition(door.Approach, out var hit, 1.5f, NavMesh.AllAreas) && Connected(hit.position))
                    doors.Add(door);
        }

        /// <summary>On the main street network (not a walled-off courtyard or a ledge).</summary>
        private bool Connected(Vector3 point) =>
            NavMesh.CalculatePath(hub, point, NavMesh.AllAreas, pathBuffer) && pathBuffer.status == NavMeshPathStatus.PathComplete;

        /// <summary>A doorway for a walking NPC to leave by, a reasonable walk away.</summary>
        public bool TryPickExit(NpcWalker npc, out PittGreybox.Doorway exit)
        {
            var from = npc.transform.position;
            gangOf.TryGetValue(npc, out var gang);
            int district = HomeDistrict(gang);
            // Gang members go back indoors in their own district.
            var options = district < 0 ? doors : doors.FindAll(d => DistrictOf(d.position) == district);
            for (int attempt = 0; attempt < 20 && options.Count > 0; attempt++)
            {
                exit = options[Random.Range(0, options.Count)];
                float distance = Vector3.Distance(from, exit.Approach);
                // Late tries take the nearest-ish door rather than wander forever.
                if (attempt < 12 && (distance < tripLength.x || distance > tripLength.y)) continue;
                if (Reachable(npc, exit.Approach, out _)) return true;
            }
            exit = default;
            return false;
        }

        /// <summary>
        /// A doorway in the given district, or (with -1) in whichever district has the fewest NPCs walking
        /// about, so people stay spread out.
        /// </summary>
        private bool TryPickSpawnDoor(int district, out PittGreybox.Doorway door)
        {
            door = default;
            if (doors.Count == 0 || greybox.districts.Count == 0) return false;
            if (district >= 0)
            {
                var home = doors.FindAll(x => DistrictOf(x.position) == district && DoorClear(x));
                if (home.Count == 0) return false;
                door = home[Random.Range(0, home.Count)];
                return true;
            }

            var counts = new int[greybox.districts.Count];
            foreach (var list in walking)
                foreach (var w in list)
                    counts[greybox.DistrictIndexAt(new Vector2(w.transform.position.x, w.transform.position.z))]++;

            var order = new List<int>();
            for (int d = 0; d < counts.Length; d++) order.Add(d);
            order.Sort((a, b) => counts[a].CompareTo(counts[b]));
            foreach (int d in order)
            {
                var options = doors.FindAll(x => greybox.DistrictIndexAt(new Vector2(x.position.x, x.position.z)) == d && DoorClear(x));
                if (options.Count == 0) continue;
                door = options[Random.Range(0, options.Count)];
                return true;
            }
            return false;
        }

        /// <summary>Nobody standing in the doorway already.</summary>
        private bool DoorClear(PittGreybox.Doorway door)
        {
            foreach (var list in walking)
                foreach (var w in list)
                    if ((w.transform.position - door.Approach).sqrMagnitude < 2.5f) return false;
            return true;
        }

        /// <summary>A walkable street point on the main network, optionally inside one district.</summary>
        private bool TryRandomStreetPoint(int district, out Vector3 point)
        {
            float half = greybox.HalfSize - 4f;
            for (int attempt = 0; attempt < 60; attempt++)
            {
                var p2 = new Vector2(Random.Range(-half, half), Random.Range(-half, half));
                if (district >= 0 && greybox.DistrictIndexAt(p2) != district) continue;
                var probe = new Vector3(p2.x, greybox.HeightAt(p2) + 0.5f, p2.y);
                // Close to the ground at that spot: streets, not rooftops.
                if (NavMesh.SamplePosition(probe, out var hit, 2f, NavMesh.AllAreas) && Mathf.Abs(hit.position.y - probe.y) < 1.5f &&
                    !InsideProperty(hit.position) && Connected(hit.position))
                {
                    point = hit.position;
                    return true;
                }
            }
            point = default;
            return false;
        }

        /// <summary>Nobody wanders into a property (the player's or one for sale).</summary>
        private static bool InsideProperty(Vector3 point)
        {
            foreach (var p in Properties.Property.All)
                if (p.Contains(point, -0.5f)) return true;
            return false;
        }

        private bool Reachable(NpcWalker npc, Vector3 target, out Vector3 snapped)
        {
            snapped = target;
            if (!NavMesh.SamplePosition(target, out var hit, 1.5f, NavMesh.AllAreas)) return false;
            snapped = hit.position;
            if (!NavMesh.SamplePosition(npc.transform.position, out var from, 1.5f, NavMesh.AllAreas)) return false;
            return NavMesh.CalculatePath(from.position, snapped, NavMesh.AllAreas, pathBuffer) && pathBuffer.status == NavMeshPathStatus.PathComplete;
        }
    }
}
