using System;
using System.Collections.Generic;
using ThePitt.Items;
using UnityEngine;

namespace ThePitt.World
{
    /// <summary>
    /// Settings and shape data for the generated greybox of The Pitt: a dense city on top of a
    /// colossal square pillar, under a ceiling so high it is nearly out of sight.
    /// Built at runtime from a seed by <see cref="Generation.PittGreyboxGenerator"/> (WorldBootstrap); the
    /// inspector's Generate shows a seed in the editor.
    /// </summary>
    public class PittGreybox : MonoBehaviour
    {
        /// <summary>
        /// Bump whenever a change to the generator would build a different city from the same seed. Saves record
        /// it; one made with another version can't be loaded, since its world no longer exists.
        /// </summary>
        public const int GeneratorVersion = 2;

        /// <summary>A district's colour identity: the murk you walk through and the colours its signs use.</summary>
        /// <summary>A flat area at its own height: one of the terraces the city stands on, or a gang square.</summary>
        [Serializable]
        public struct Plateau
        {
            public Rect rect;
            public float height;
        }

        [Serializable]
        public struct DistrictPalette
        {
            public string name;
            [Tooltip("Ambient light. Darkness here is a colour, not black.")]
            public Color ambient;
            public Color fog;
            [Tooltip("Main colour for lamps, billboards and signs.")]
            public Color primary;
            [Tooltip("Contrasting neon accent.")]
            public Color accent;
            [Tooltip("Multiplier on neon, shop sign and billboard chances: how loud the district is.")]
            [Min(0f)] public float signage;
        }

        [Serializable]
        public struct District
        {
            public string name;
            public Vector2 center;
            public int palette;
        }

        /// <summary>
        /// A Watcher outpost hanging off the edge of the city: a marble platform out over the void, reached by
        /// a bridge from the end of a street. The landing where the bridge meets the city is the Watchers' ground.
        /// </summary>
        [Serializable]
        public struct WatcherOutpost
        {
            [Tooltip("The platform, out beyond the edge of the city.")]
            public Rect rect;
            [Tooltip("Back toward the city.")]
            public Vector2 inward;
            [Tooltip("The bridge from the city edge to the platform.")]
            public Rect bridge;
            [Tooltip("Open ground at the city edge where the bridge lands; nobody can claim it.")]
            public Rect landing;
        }

        /// <summary>A street-level doorway NPCs walk in and out of.</summary>
        [Serializable]
        public struct Doorway
        {
            [Tooltip("On the ground, at the wall face, in the middle of the opening.")]
            public Vector3 position;
            [Tooltip("Out of the building, toward the street.")]
            public Vector2 normal;

            /// <summary>The spot just outside the door where an NPC starts or ends its walk.</summary>
            public Vector3 Approach => position + new Vector3(normal.x, 0f, normal.y) * 1.2f;
            /// <summary>Just inside the opening, where an NPC vanishes or appears.</summary>
            public Vector3 Inside => position - new Vector3(normal.x, 0f, normal.y) * 0.4f;
        }

        /// <summary>The closed end of a dead-end alley, deep out of sight of the streets.</summary>
        [Serializable]
        public struct AlleySpot
        {
            [Tooltip("On the ground, in the middle of the alley, a little way in from the closed end.")]
            public Vector3 position;
            [Tooltip("Along the alley, from the closed end toward the way out.")]
            public Vector2 outward;
            public float width;
        }

        [Serializable]
        public struct LootEntry
        {
            public ItemDefinition item;
            public Material material;
            [Min(1)] public int minQuantity;
            [Min(1)] public int maxQuantity;
            [Min(0f)] public float weight;
        }

        [Header("Layout")]
        public int seed = 1337;
        [Tooltip("Side length of the square pillar top the city sits on.")]
        [Min(50f)] public float citySize = 300f;
        [Tooltip("Average distance between walkway lines.")]
        [Min(8f)] public float blockSpacing = 18f;
        [Tooltip("Random offset applied to each walkway line so blocks aren't uniform.")]
        [Min(0f)] public float blockJitter = 3f;
        [Tooltip("Width of the thoroughfares: the few long routes that cross most of the map.")]
        [Min(1.5f)] public float walkwayWidth = 7f;
        [Tooltip("Width of the secondary alleys linking the thoroughfares.")]
        [Min(1.2f)] public float secondaryWidth = 2.5f;
        [Tooltip("Thoroughfares per direction (north-south and east-west).")]
        [Range(1, 6)] public int thoroughfaresPerAxis = 3;
        [Tooltip("Chance a thoroughfare kinks sideways by one block at each junction.")]
        [Range(0f, 1f)] public float thoroughfareJogChance = 0.15f;
        [Tooltip("Chance to add extra secondary alleys so routes loop instead of dead-ending.")]
        [Range(0f, 1f)] public float loopChance = 0.15f;
        [Tooltip("Chance a walkway runs out to the edge of the pillar.")]
        [Range(0f, 1f)] public float edgeWalkwayChance = 0.35f;

        [Header("Districts")]
        [Min(1)] public int districtCount = 5;
        [Min(10f)] public float districtMinSpacing = 85f;
        [Tooltip("The first palette is always used for the district at the centre of the city, where the player lives.")]
        public DistrictPalette[] palettes = DefaultPalettes();

        [Header("Lots & Alleys")]
        [Tooltip("Blocks are split into building lots until each side is under the max.")]
        public Vector2 lotSize = new(4f, 9f);
        [Range(0f, 1f)] public float alleyChance = 0.55f;
        [Tooltip("Chance a small alley that would run through is blocked part way, making a dead end.")]
        [Range(0f, 1f)] public float deadEndChance = 0.75f;
        public Vector2 alleyWidth = new(1.2f, 2f);

        [Header("Buildings")]
        public Vector2 buildingHeight = new(8f, 30f);
        [Range(0f, 1f)] public float towerChance = 0.08f;
        public Vector2 towerHeight = new(45f, 75f);
        [Range(0f, 1f)] public float ledgeChance = 0.3f;
        [Range(0f, 1f)] public float cantileverChance = 0.2f;
        [Range(0f, 1f)] public float windowChance = 0.45f;
        [Range(0, 8)] public int maxWindows = 3;

        [Header("Verticality")]
        [Tooltip("Raised catwalks along walkway walls, with stairs up to them.")]
        [Range(0f, 1f)] public float catwalkChance = 0.22f;

        [Header("Signage")]
        [Range(0f, 1f)] public float neonChance = 0.12f;
        [Range(0f, 1f)] public float shopSignChance = 0.15f;
        [Range(0f, 1f)] public float billboardChance = 0.2f;
        [Min(0)] public int fluorescentStrips = 90;

        [Header("Street Furniture")]
        [Range(0f, 1f)] public float awningChance = 0.15f;
        [Range(0f, 1f)] public float ductChance = 0.25f;
        [Range(0f, 1f)] public float wallPipeChance = 0.2f;
        [Tooltip("Chain-link fences across alley mouths, leaving a gap to squeeze through.")]
        [Range(0f, 1f)] public float alleyFenceChance = 0.25f;

        [Header("Gang Squares")]
        [Tooltip("Flat open squares where gangs operate, centred on walkway junctions. How many each district gets.")]
        public Vector2Int gangSquaresPerDistrict = new(1, 2);
        [Tooltip("Range for each side of a gang square; width and length are rolled separately. Big ones swallow whole blocks.")]
        public Vector2 gangSquareSize = new(10f, 44f);
        [Tooltip("Minimum distance between gang squares.")]
        [Min(10f)] public float gangSquareSpacing = 45f;

        [Header("NPC Doorways")]
        [Tooltip("Street-level doorways NPCs walk in and out of, split evenly between the districts.")]
        [Min(0)] public int npcDoorways = 60;
        [Range(0f, 1f)] public float doorwayLightChance = 0.35f;
        [Tooltip("Minimum distance between NPC doorways.")]
        [Min(2f)] public float doorwaySpacing = 12f;

        [Header("Properties")]
        [Tooltip("Asking price range for properties for sale, in scrip (rounded to 50).")]
        public Vector2Int propertyPrice = new(900, 1200);
        [Tooltip("Inside size of the player's own property, on a thoroughfare in the central district.")]
        public Vector2 homeSize = new(9f, 7f);
        [Tooltip("Smallest lot (each side) that can become a property in the other districts.")]
        [Min(4f)] public float propertyMinLot = 6.5f;
        [Tooltip("Properties (yours, ones for sale and businesses) only go in this middle fraction of the map each way, clear of the edges.")]
        [Range(0.2f, 1f)] public float propertyArea = 0.8f;
        [Min(2.5f)] public float propertyHeight = 3.6f;
        [Tooltip("Price of the morgue, a business that turns the city's dead into better meat.")]
        [Min(0)] public int morguePrice = 2000;
        [Tooltip("Price of the metalworks, a business that refines scrap into better scrap.")]
        [Min(0)] public int metalworksPrice = 2500;
        [Tooltip("How far beyond the edge of the pillar the property rooms are built, out of sight in the dark.")]
        [Min(200f)] public float interiorDistance = 2000f;

        [Header("Watcher Outposts")]
        [Tooltip("Width of each outpost platform along the edge of the city.")]
        [Min(8f)] public float outpostWidth = 18f;
        [Tooltip("How far each platform reaches out over the void.")]
        [Min(8f)] public float outpostDepth = 16f;
        [Tooltip("Length of the bridge out to each platform.")]
        [Min(2f)] public float outpostBridge = 7f;
        [Tooltip("Outposts sit somewhere within this middle fraction of each edge.")]
        [Range(0.1f, 1f)] public float outpostEdgeSpan = 0.6f;

        [Header("Hidden Spots")]
        [Tooltip("Hollow back rooms reached through a hole in an alley wall. Future black markets and dead drops.")]
        [Min(0)] public int hiddenSpots = 8;

        [Header("Shacks & Clutter")]
        [Tooltip("Shacks hung on walls above walkways.")]
        [Range(0f, 1f)] public float wallShackChance = 0.12f;
        [Range(0f, 1f)] public float rooftopShackChance = 0.25f;
        [Tooltip("Planks, pipes and cables strung across walkways overhead.")]
        [Range(0f, 1f)] public float overheadChance = 0.6f;

        [Header("Terraces (the whole city stands on flat terraces at different heights)")]
        [Tooltip("Size of each terrace in blocks, each way. Edges run through the middle of blocks, so streets cross them square-on.")]
        public Vector2Int terraceBlocks = new(2, 5);
        [Tooltip("Lowest and highest a terrace can stand.")]
        public Vector2 terraceHeights = new(-9f, 9f);
        [Tooltip("Terrace heights are multiples of this.")]
        [Min(0.25f)] public float terraceStep = 1.5f;
        [Tooltip("Most a terrace may stand above or below the one next to it, so the stairs between fit in the street.")]
        [Min(1f)] public float maxTerraceRise = 4.5f;

        [Header("Street Structures")]
        [Tooltip("Steel portal frames spanning alleys, with hanging lights.")]
        [Range(0f, 1f)] public float gantryChance = 0.18f;
        [Tooltip("Building masses bridging over secondary alleys, making underpasses.")]
        [Range(0f, 1f)] public float underpassChance = 0.12f;
        [Tooltip("Wrecked cars and jersey barriers along thoroughfares.")]
        [Range(0f, 1f)] public float streetPropChance = 0.3f;

        [Header("Underground")]
        [Tooltip("Small open squares with a stairwell down to the sump tunnels.")]
        [Min(0)] public int sumpEntrances = 0;
        [Tooltip("Floor of level -1 (the sump tunnels), below street level.")]
        [Min(3f)] public float sumpDepth = 4.5f;
        [Tooltip("Floor of level -2 (the cistern).")]
        [Min(6f)] public float cisternDepth = 9.5f;
        [Min(0)] public int sumpSideRooms = 4;

        [Header("Scrap Pit")]
        public Vector2 pitCenter = new(70f, -70f);
        [Min(5f)] public float pitRadius = 28f;
        [Min(1f)] public float pitWallWidth = 6f;
        [Min(1f)] public float pitDepth = 12f;
        [Min(5f)] public float rampLength = 32f;
        [Min(2f)] public float rampWidth = 6f;
        [Min(0)] public int scrapPiles = 14;
        [Tooltip("What the ScrapPitSpawner drops into the pit, and how often each turns up.")]
        public LootEntry[] pitLoot;

        [Header("Pillar & Ceiling")]
        [Tooltip("How far the pillar drops below the city before it disappears into the dark.")]
        [Min(50f)] public float pillarDepth = 800f;
        public float ceilingHeight = 420f;
        [Min(1f)] public float holeRadius = 45f;
        [Tooltip("Brightness of the light pouring through the hole. Purely visual; it lights nothing.")]
        [Min(1f)] public float holeIntensity = 30f;
        [Min(500f)] public float ceilingExtent = 4000f;

        [Header("Lights")]
        [Tooltip("Wall lamps along walkways. Fewer but brighter; glowing signs and windows do the rest.")]
        [Min(0)] public int wallLamps = 110;
        [Min(0)] public int alleyLights = 50;
        [Range(0, 6)] public int maxTrashPerSegment = 3;
        [Tooltip("Keep trash out of this radius around the player's start so it doesn't bury starting pickups.")]
        [Min(0f)] public float clearStartRadius = 14f;

        [Header("Assets (created automatically if empty)")]
        public GameObject worldItemPrefab;
        public Material concrete;
        public Material concreteDark;
        public Material brick;
        public Material wood;
        public Material metal;
        public Material rust;
        public Material trash;
        public Material lampGlow;
        public Material windowWarm;
        public Material windowCold;
        public Material ceiling;
        public Material holeGlow;
        public Material chainLink;
        public Material cable;
        public Material terrainMaterial;
        public TerrainLayer groundLayer;
        public TerrainLayer dirtLayer;
        public Material terminalScreen;
        [Tooltip("Named materials the generator uses (marble, robes, door voids…). Filled by the editor's Refresh Assets.")]
        public List<Material> materialLibrary = new();
        [Tooltip("Items the generator puts into businesses, looked up by name.")]
        public Core.SaveRegistry itemRegistry;

        [Header("Generated (do not edit)")]
        [Tooltip("Top-down map of the city, north up, covering the whole pillar top.")]
        public Texture2D mapTexture;
        [Tooltip("The terraces, covering the whole city between them without overlapping.")]
        public List<Plateau> terraces = new();
        [Tooltip("Where the player starts: the street outside their own property.")]
        public Vector2 startPoint;
        [Tooltip("The same, as a position on the ground, and the way to face (toward the property's door).")]
        public Vector3 startPosition;
        public float startYaw;

        /// <summary>
        /// The city in play. It lives in the World scene and the game's systems in another, so they find it through
        /// this rather than a scene reference.
        /// </summary>
        public static PittGreybox Current { get; private set; }

        private void Awake() => Current = this;

        private void OnDestroy()
        {
            if (Current == this) Current = null;
        }
        public List<District> districts = new();
        public List<Doorway> doorways = new();
        public List<WatcherOutpost> watcherOutposts = new();
        [Tooltip("Gang squares: open, flat, each at its own absolute height.")]
        public List<Plateau> gangSquares = new();
        [Tooltip("Open squares around the sump stairwells; kept free of buildings.")]
        public List<Rect> sumpSquares = new();
        [Tooltip("The stairwell openings themselves (for the map and terrain holes).")]
        public List<Rect> sumpShafts = new();
        [Tooltip("The dead ends of the small alleys, for things left there at runtime (corpse piles).")]
        public List<AlleySpot> alleySpots = new();

        /// <summary>Index into <see cref="districts"/> of the district containing a point (nearest centre).</summary>
        public int DistrictIndexAt(Vector2 p)
        {
            int best = 0;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < districts.Count; i++)
            {
                float d = (p - districts[i].center).sqrMagnitude;
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = i;
                }
            }
            return best;
        }

        /// <summary>
        /// Where a district's name goes: the middle of the part of the map it covers (its seed point can sit
        /// right by an edge), moved off the scrap pit if it lands there.
        /// </summary>
        public Vector2 DistrictLabelPoint(int district)
        {
            const float step = 4f;
            float half = HalfSize;
            var cells = new List<Vector2>();
            var sum = Vector2.zero;
            for (float x = -half + step / 2f; x < half; x += step)
            for (float z = -half + step / 2f; z < half; z += step)
            {
                var p = new Vector2(x, z);
                if (DistrictIndexAt(p) != district) continue;
                cells.Add(p);
                sum += p;
            }
            if (cells.Count == 0) return districts[district].center;
            var centre = sum / cells.Count;

            float pitClear = pitRadius + 14f;
            if (Vector2.Distance(centre, pitCenter) >= pitClear) return centre;
            var best = centre;
            float bestDistance = float.MaxValue;
            foreach (var p in cells)
            {
                if (Vector2.Distance(p, pitCenter) < pitClear) continue;
                float d = (p - centre).sqrMagnitude;
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = p;
                }
            }
            return best;
        }

        public DistrictPalette PaletteAt(Vector2 p) =>
            districts.Count == 0 ? palettes[0] : palettes[districts[DistrictIndexAt(p)].palette % palettes.Length];

        public static DistrictPalette[] DefaultPalettes() => new[]
        {
            new DistrictPalette
            {
                name = "Crimson Row",
                ambient = new Color(0.11f, 0.018f, 0.016f),
                fog = new Color(0.13f, 0.02f, 0.018f),
                primary = new Color(1f, 0.32f, 0.12f),
                accent = new Color(1f, 0.85f, 0.2f),
                signage = 1f,
            },
            new DistrictPalette
            {
                name = "Sodium Yards",
                ambient = new Color(0.09f, 0.066f, 0.015f),
                fog = new Color(0.1f, 0.074f, 0.02f),
                primary = new Color(1f, 0.6f, 0.2f),
                accent = new Color(0.3f, 0.8f, 1f),
                signage = 0.7f,
            },
            new DistrictPalette
            {
                name = "Mildew",
                ambient = new Color(0.03f, 0.07f, 0.035f),
                fog = new Color(0.035f, 0.08f, 0.04f),
                primary = new Color(0.5f, 1f, 0.3f),
                accent = new Color(1f, 0.2f, 0.6f),
                signage = 0.6f,
            },
            new DistrictPalette
            {
                name = "Coldwater",
                ambient = new Color(0.02f, 0.035f, 0.1f),
                fog = new Color(0.02f, 0.04f, 0.11f),
                primary = new Color(0.4f, 0.6f, 1f),
                accent = new Color(1f, 0.25f, 0.3f),
                signage = 0.8f,
            },
            new DistrictPalette
            {
                // The nightlife district: ultraviolet haze, warm-white accents, signs everywhere.
                name = "The Velvet",
                ambient = new Color(0.07f, 0.02f, 0.11f),
                fog = new Color(0.08f, 0.025f, 0.12f),
                primary = new Color(0.65f, 0.25f, 1f),
                accent = new Color(1f, 0.88f, 0.72f),
                signage = 2.5f,
            },
        };

        public float HalfSize => citySize * 0.5f;
        public float PitFloorRadius => pitRadius - pitWallWidth;
        /// <summary>The ramp climbs out of the pit along +Z.</summary>
        public Vector2 RampDirection => Vector2.up;
        public float RampEnd => PitFloorRadius + rampLength;

        /// <summary>Distance along the ramp from the pit centre and sideways from its centre line.</summary>
        public void RampCoords(Vector2 p, out float along, out float lateral)
        {
            Vector2 d = p - pitCenter;
            Vector2 dir = RampDirection;
            along = Vector2.Dot(d, dir);
            lateral = Mathf.Abs(d.x * dir.y - d.y * dir.x);
        }

        public bool InRampCorridor(Vector2 p, float extraWidth)
        {
            RampCoords(p, out float along, out float lateral);
            return lateral < rampWidth * 0.5f + extraWidth && along > 0f && along < RampEnd + extraWidth;
        }

        /// <summary>Areas kept free of buildings: the pit and its rim, the ramp, the Watcher landings, and the gang and sump squares.</summary>
        public bool IsOpen(Vector2 p)
        {
            if (Vector2.Distance(p, pitCenter) < pitRadius + 4f || InRampCorridor(p, 1.5f)) return true;
            foreach (var sq in gangSquares)
                if (sq.rect.Contains(p)) return true;
            foreach (var o in watcherOutposts)
                if (o.landing.Contains(p)) return true;
            foreach (var r in sumpSquares)
                if (r.Contains(p)) return true;
            return false;
        }

        /// <summary>The ground the city stands on: the height of the terrace at a point (the nearest one off the edge). No pit.</summary>
        public float GroundAt(Vector2 p)
        {
            float best = float.MaxValue, height = 0f;
            foreach (var t in terraces)
            {
                var r = t.rect;
                if (r.Contains(p)) return t.height;
                float dx = Mathf.Max(0f, Mathf.Max(r.xMin - p.x, p.x - r.xMax));
                float dz = Mathf.Max(0f, Mathf.Max(r.yMin - p.y, p.y - r.yMax));
                if (dx * dx + dz * dz < best)
                {
                    best = dx * dx + dz * dz;
                    height = t.height;
                }
            }
            return height;
        }

        /// <summary>Ground height at a world XZ position, the scrap pit and its ramp included.</summary>
        public float HeightAt(Vector2 p)
        {
            float h = GroundAt(p);

            float dp = Vector2.Distance(p, pitCenter);
            float floor = PitFloorRadius;
            float pit = 0f;
            if (dp < pitRadius)
                pit = dp <= floor ? -pitDepth : -pitDepth * (1f - Mathf.SmoothStep(0f, 1f, (dp - floor) / pitWallWidth));

            RampCoords(p, out float along, out float lateral);
            const float edgeBlend = 1.5f;
            float half = rampWidth * 0.5f;
            if (lateral < half + edgeBlend && along > floor - 2f && along < RampEnd)
            {
                float t = Mathf.Clamp01((along - floor) / rampLength);
                float ramp = -pitDepth * (1f - t);
                float k = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(half, half + edgeBlend, lateral));
                // The ramp cuts a gentle slot through the steep pit wall and a trench below street level beyond it.
                pit = Mathf.Min(pit, Mathf.Lerp(ramp, pit, k));
            }

            return h + pit;
        }
    }
}
