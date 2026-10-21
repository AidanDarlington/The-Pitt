using System.Collections.Generic;
using ThePitt.Interaction;
using ThePitt.Items;
using System.Diagnostics;
using Unity.AI.Navigation;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace ThePitt.World.Generation
{
    /// <summary>
    /// Builds the greybox: a dense city of tightly packed blocks on top of a sheer-sided pillar,
    /// split into colour-coded districts. Narrow walkways form a connected, looping network; blocks
    /// are split into lots by tighter alleys. Streets get catwalks with stairs, railings and chain-link,
    /// heavy overhead clutter, and are lit mostly by neon, billboards and windows with a smaller number
    /// of real lamps. The city stands on wide flat terraces at different heights, high and low, with
    /// retaining walls and stairs between them. Also builds the scrap pit, hidden back rooms,
    /// and the distant ceiling with its blinding hole.
    /// Runs in the game (a new world for each character, rebuilt from its seed on load) and in the editor for a
    /// look at a seed. Everything it makes lives in memory; the materials it uses are assets set up in the editor
    /// (PittWorldAssets) and listed on the <see cref="PittGreybox"/>. The same seed always builds the same city.
    /// </summary>
    public static partial class PittGreyboxGenerator
    {
        private const string GeneratedName = "Generated";
        private const float StepHeight = 0.25f;
        /// <summary>The shallowest tread a terrace stair is steepened to.</summary>
        private const float MinTread = 0.26f;

        private struct Segment
        {
            public Rect rect;
            public bool alongX;
            public bool thoroughfare;
        }

        private class Plan
        {
            public Rect footprint;
            public float groundMin;
            public float ground;
            public float top;
            public bool hollow;
            /// <summary>Built over a stretch of alley (a dead-end plug or a filled-in piece) rather than a lot of its own.</summary>
            public bool filler;
            public bool property;
            /// <summary>The business run from this lot's property, if it's one.</summary>
            public Production.BusinessKind? business;
            public Vector2 doorNormal;
            public Vector2 doorPoint;
            public float doorGround;
            public Vector2 holeNormal;
        }

        /// <summary>Spatial hash of rects for fast "is this point in a walkway/alley" checks.</summary>
        private class RectIndex
        {
            private const float CellSize = 10f;
            private readonly Dictionary<Vector2Int, List<Rect>> cells = new();

            public void Add(Rect r)
            {
                for (int x = Mathf.FloorToInt(r.xMin / CellSize); x <= Mathf.FloorToInt(r.xMax / CellSize); x++)
                for (int z = Mathf.FloorToInt(r.yMin / CellSize); z <= Mathf.FloorToInt(r.yMax / CellSize); z++)
                {
                    var key = new Vector2Int(x, z);
                    if (!cells.TryGetValue(key, out var list)) cells[key] = list = new List<Rect>();
                    list.Add(r);
                }
            }

            public bool Contains(Vector2 p)
            {
                var key = new Vector2Int(Mathf.FloorToInt(p.x / CellSize), Mathf.FloorToInt(p.y / CellSize));
                if (!cells.TryGetValue(key, out var list)) return false;
                foreach (var r in list)
                    if (r.Contains(p)) return true;
                return false;
            }
        }

        private static System.Random rng;
        private static float R(float min, float max) => min + (float)rng.NextDouble() * (max - min);
        private static float R(Vector2 range) => R(range.x, range.y);
        private static bool Chance(float p) => rng.NextDouble() < p;

        // Per-run state.
        private static PittGreybox g;
        private static float[] xs, zs;
        private static bool[,] hEdge, vEdge;
        private static float[,] hWidth, vWidth;
        private static List<Segment> segments;
        private static List<Rect> alleys;
        private static List<Plan> plans;
        private static List<Rect> catwalkZones;
        private static List<Rect> stairZones = new();
        /// <summary>
        /// The terrace stairs: each flight's rect (without its top landing), the way up, its run, and its flights as
        /// distances up from the foot with the heights they climb between. The kerbs beside them climb them (version 10).
        /// </summary>
        private static List<(Rect stair, Vector2 up, float run, List<(float from, float to, float bottom, float top)> flights)> stairFlights = new();
        private static RectIndex streetIndex, alleyIndex, walkIndex;
        /// <summary>The kerbs along building faces, once built (version 6).</summary>
        private static RectIndex kerbIndex;
        private static Dictionary<string, Material> glowCache;

        public static void Clear(PittGreybox greybox)
        {
            var existing = greybox.transform.Find(GeneratedName);
            if (existing == null) return;
            if (Application.isPlaying) Object.Destroy(existing.gameObject);
            else Object.DestroyImmediate(existing.gameObject);
        }

        /// <summary>
        /// Lays the city out (districts, streets, terraces, squares, lots, properties) and draws its map into
        /// <see cref="PittGreybox.mapTexture"/>, without building anything: the world preview on the New Game screen.
        /// The full <see cref="Build"/> for the same seed lays out exactly the same city.
        /// </summary>
        public static void PlanLayout(PittGreybox greybox, int seed)
        {
            Begin(greybox, seed, PittGreybox.GeneratorVersion);
            Layout();
            // Catwalks only exist once built; the preview map goes without them.
            catwalkZones = new List<Rect>();
            BakeMap();
        }

        /// <summary>
        /// Builds the whole city under the greybox from a seed: layout, geometry, lights, map and the NPC
        /// NavMesh, all at once (the editor's preview). The game uses <see cref="BuildSteps"/> to show progress.
        /// Returns the root of everything built.
        /// </summary>
        public static Transform Build(PittGreybox greybox, int seed, int version = PittGreybox.GeneratorVersion)
        {
            foreach (var _ in BuildSteps(greybox, seed, version)) { }
            return LastBuilt;
        }

        /// <summary>The root of the city the last <see cref="BuildSteps"/> finished.</summary>
        public static Transform LastBuilt { get; private set; }

        /// <summary>
        /// The same build as <see cref="Build"/>, a piece at a time: each step returns how far through it is (0..1),
        /// weighted by how long each part takes, so a loading bar can follow it. The city is finished (and
        /// <see cref="LastBuilt"/> set) when the steps run out. <paramref name="version"/> is the generator version
        /// to build at: an older save's world is rebuilt as that version made it.
        /// </summary>
        public static IEnumerable<float> BuildSteps(PittGreybox greybox, int seed, int version = PittGreybox.GeneratorVersion)
        {
            var clock = Stopwatch.StartNew();
            var timings = new System.Text.StringBuilder();
            void Mark(string stage)
            {
                timings.Append($"{stage} {clock.ElapsedMilliseconds} ms, ");
                clock.Restart();
            }

            // Each part's typical time in milliseconds (from timing builds); the bar moves by these, so it keeps
            // pace with the real work. Re-time them if a part gets much slower or faster.
            const float layoutCost = 20f, mapCost = 1280f, wakeCost = 28f, batchCost = 95f, navCost = 155f;
            var stages = new (string name, System.Action<Transform> build, float cost)[]
            {
                ("terrain", BuildTerrain, 300f), ("pillar", BuildPillar, 6f), ("buildings", BuildBuildings, 57f),
                ("properties", BuildProperties, 20f), ("outposts", BuildWatcherOutposts, 5f), ("heights", BuildHeights, 35f),
                ("kerbs", BuildKerbs, 15f), ("underground", BuildUnderground, 5f), ("catwalks", BuildCatwalks, 20f), ("underpasses", BuildUnderpasses, 3f),
                ("details", BuildDetails, 105f), ("doorways", BuildDoorways, 45f), ("signage", BuildSignage, 19f),
                ("overhead", BuildOverhead, 11f), ("fences", BuildFences, 14f), ("gantries", BuildGantries, 17f),
                ("props", BuildStreetProps, 5f), ("trash", BuildTrash, 24f), ("pit", BuildScrapPit, 8f),
                ("lights", BuildLights, 19f), ("ceiling", BuildCeiling, 1f),
            };
            float batch = Application.isPlaying ? batchCost : 0f;
            float total = layoutCost + mapCost + wakeCost + batch + navCost;
            foreach (var stage in stages) total += stage.cost;
            float done = 0f;

            Begin(greybox, seed, version);
            Clear(g);
            var root = new GameObject(GeneratedName).transform;
            root.SetParent(g.transform, false);
            // A look in the editor isn't saved with the scene: the game builds its own from the seed.
            if (!Application.isPlaying) root.gameObject.hideFlags = HideFlags.DontSave;
            // Built switched off: every component wakes up once the whole city is standing and set up, as it
            // would loading a saved one (Property reads whether it starts owned in Awake, for one).
            root.gameObject.SetActive(false);
            LastRoot = root;

            Layout();
            Mark("layout");
            done += layoutCost;
            yield return done / total;

            foreach (var stage in stages)
            {
                // From version 5 the properties come last of all (see PlaceProperties).
                if (stage.name == "properties" && Since(5)) continue;
                stage.build(root);
                Mark(stage.name);
                done += stage.cost;
                yield return done / total;
            }

            foreach (float part in BakeMapSteps()) yield return (done + part * mapCost) / total;
            Mark("map");
            done += mapCost;

            root.gameObject.SetActive(true);
            Mark("wake");
            done += wakeCost;
            yield return done / total;

            if (Since(5))
            {
                // Clutter clear of the alleys first, then the NPC NavMesh, which the property doors are checked against.
                ClearAlleys(root);
                Mark("alley clearance");
                BuildNavMesh();
                Mark("navmesh");
                PlaceProperties(root);
                Mark("properties");
            }

            // Everything that never moves is batched together; lights, pickups, cables and flickering fittings stay apart.
            if (Application.isPlaying)
            {
                var statics = new List<GameObject>();
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (t.GetComponent<MeshRenderer>() == null || t.GetComponent<WorldItem>() != null ||
                        t.GetComponentInParent<LightFlicker>() != null) continue;
                    statics.Add(t.gameObject);
                }
                StaticBatchingUtility.Combine(statics.ToArray(), root.gameObject);
                Mark("batching");
            }
            done += batch;
            yield return done / total;

            if (!Since(5))
            {
                BuildNavMesh();
                Mark("navmesh");
            }
            LastBuilt = root;
            Debug.Log($"PittGreybox built seed {g.seed}: {plans.Count} buildings, {segments.Count} walkway segments, " +
                      $"{alleys.Count} alleys, {catwalkZones.Count} catwalks, {g.districts.Count} districts. {timings}");
            yield return 1f;
        }

        private static void Begin(PittGreybox greybox, int seed, int version)
        {
            g = greybox;
            g.seed = seed;
            g.buildVersion = version;
            glowCache = new Dictionary<string, Material>();
            planIndex = null;
            kerbIndex = null;
            rng = new System.Random(seed);
        }

        /// <summary>
        /// Whether the city being built is at least this generator version. Every change that alters what a seed
        /// builds goes behind one, with the old behaviour kept for older saves (see <see cref="PittGreybox.GeneratorVersion"/>).
        /// </summary>
        private static bool Since(int version) => g.buildVersion >= version;

        /// <summary>The plan of the city: everything the build and the map follow. Same seed, same plan.</summary>
        private static void Layout()
        {
            stairZones = new List<Rect>();
            PlaceDistricts();
            g.sumpSquares.Clear();
            g.sumpShafts.Clear();
            LayOutWalkways();
            PlaceGangSquares();
            PlaceTerraces();
            PlaceSumps();
            PlanLots();
            RecordAlleySpots();
            ChooseHiddenSpots();
            // From version 5 they're chosen once the city stands, from doors the player can get to (PlaceProperties).
            if (!Since(5)) ChooseProperties();
        }

        // ---------------------------------------------------------------- layout

        private static void PlaceDistricts()
        {
            g.districts.Clear();
            if (g.palettes == null || g.palettes.Length == 0) g.palettes = PittGreybox.DefaultPalettes();

            // The central district always uses the first palette.
            var centers = new List<Vector2> { Vector2.zero };
            float limit = g.HalfSize * 0.8f;
            for (int attempt = 0; attempt < 500 && centers.Count < g.districtCount; attempt++)
            {
                var c = new Vector2(R(-limit, limit), R(-limit, limit));
                bool clear = true;
                foreach (var other in centers) clear &= Vector2.Distance(c, other) > g.districtMinSpacing;
                if (clear) centers.Add(c);
            }

            var uses = new int[g.palettes.Length];
            for (int i = 0; i < centers.Count; i++)
            {
                int palette = i == 0 ? 0 : 1 + (i - 1) % Mathf.Max(1, g.palettes.Length - 1);
                palette %= g.palettes.Length;
                uses[palette]++;
                string name = g.palettes[palette].name + (uses[palette] > 1 ? " " + Roman(uses[palette]) : "");
                g.districts.Add(new PittGreybox.District { name = name, center = centers[i], palette = palette });
            }
        }

        private static string Roman(int n) => n switch { 2 => "II", 3 => "III", 4 => "IV", 5 => "V", _ => n.ToString() };

        private static PittGreybox.DistrictPalette PaletteAt(Vector2 p) => g.PaletteAt(p);

        private static float[] Lines(int n)
        {
            var lines = new float[n + 1];
            float step = g.citySize / n;
            for (int i = 0; i <= n; i++)
            {
                lines[i] = -g.HalfSize + i * step;
                if (i > 0 && i < n) lines[i] += R(-g.blockJitter, g.blockJitter);
            }
            return lines;
        }

        private static Rect HRect(int i, int j)
        {
            float w = hWidth[i, j] / 2f;
            return Clip(Rect.MinMaxRect(xs[i] - w, zs[j] - w, xs[i + 1] + w, zs[j] + w));
        }

        private static Rect VRect(int i, int j)
        {
            float w = vWidth[i, j] / 2f;
            return Clip(Rect.MinMaxRect(xs[i] - w, zs[j] - w, xs[i] + w, zs[j + 1] + w));
        }

        private static Rect Clip(Rect r) => Rect.MinMaxRect(
            Mathf.Max(r.xMin, -g.HalfSize), Mathf.Max(r.yMin, -g.HalfSize),
            Mathf.Min(r.xMax, g.HalfSize), Mathf.Min(r.yMax, g.HalfSize));

        private static void MarkH(int i, int j, float width)
        {
            hEdge[i, j] = true;
            hWidth[i, j] = Mathf.Max(hWidth[i, j], width);
        }

        private static void MarkV(int i, int j, float width)
        {
            vEdge[i, j] = true;
            vWidth[i, j] = Mathf.Max(vWidth[i, j], width);
        }

        /// <summary>
        /// Three tiers of route: a few long thoroughfares crossing most of the map, a looping network of
        /// secondary alleys linking them, and (later, inside blocks) small alleys that mostly dead-end.
        /// </summary>
        private static void LayOutWalkways()
        {
            int n = Mathf.Max(3, Mathf.RoundToInt(g.citySize / g.blockSpacing));
            xs = Lines(n);
            zs = Lines(n);
            hEdge = new bool[n, n + 1];
            vEdge = new bool[n + 1, n];
            hWidth = new float[n, n + 1];
            vWidth = new float[n + 1, n];
            float main = g.walkwayWidth, minor = g.secondaryWidth;
            bool Interior(int i, int j) => i > 0 && j > 0 && i < n && j < n;

            // Thoroughfares: long routes edge to edge, kinking sideways now and then.
            int k = Mathf.Max(1, g.thoroughfaresPerAxis);
            for (int t = 0; t < k; t++)
            {
                int c = Mathf.Clamp(Mathf.RoundToInt(n * (t + 1f) / (k + 1f)) + rng.Next(-1, 2), 1, n - 1);
                for (int j = 0; j < n; j++)
                {
                    MarkV(c, j, main);
                    if (j < n - 1 && Chance(g.thoroughfareJogChance))
                    {
                        int c2 = Mathf.Clamp(c + (Chance(0.5f) ? 1 : -1), 1, n - 1);
                        if (c2 != c) MarkH(Mathf.Min(c, c2), j + 1, main);
                        c = c2;
                    }
                }
                int r = Mathf.Clamp(Mathf.RoundToInt(n * (t + 1f) / (k + 1f)) + rng.Next(-1, 2), 1, n - 1);
                for (int i = 0; i < n; i++)
                {
                    MarkH(i, r, main);
                    if (i < n - 1 && Chance(g.thoroughfareJogChance))
                    {
                        int r2 = Mathf.Clamp(r + (Chance(0.5f) ? 1 : -1), 1, n - 1);
                        if (r2 != r) MarkV(i + 1, Mathf.Min(r, r2), main);
                        r = r2;
                    }
                }
            }

            // Secondary alleys: a random spanning tree over interior intersections, so everything connects.
            var visited = new bool[n + 1, n + 1];
            var stack = new Stack<Vector2Int>();
            var start = new Vector2Int(n / 2, n / 2);
            visited[start.x, start.y] = true;
            stack.Push(start);
            var dirs = new[] { Vector2Int.up, Vector2Int.down, Vector2Int.left, Vector2Int.right };
            while (stack.Count > 0)
            {
                var cur = stack.Peek();
                var options = new List<Vector2Int>();
                foreach (var d in dirs)
                {
                    var next = cur + d;
                    if (Interior(next.x, next.y) && !visited[next.x, next.y]) options.Add(d);
                }
                if (options.Count == 0)
                {
                    stack.Pop();
                    continue;
                }
                var step = options[rng.Next(options.Count)];
                var target = cur + step;
                SetEdge(cur, target, minor);
                visited[target.x, target.y] = true;
                stack.Push(target);
            }

            for (int i = 0; i < n; i++)
            for (int j = 0; j <= n; j++)
            {
                bool a = Interior(i, j), b = Interior(i + 1, j);
                if (hEdge[i, j]) continue;
                if ((a && b && Chance(g.loopChance)) || ((a || b) && !(a && b) && Chance(g.edgeWalkwayChance))) MarkH(i, j, minor);
            }
            for (int i = 0; i <= n; i++)
            for (int j = 0; j < n; j++)
            {
                bool a = Interior(i, j), b = Interior(i, j + 1);
                if (vEdge[i, j]) continue;
                if ((a && b && Chance(g.loopChance)) || ((a || b) && !(a && b) && Chance(g.edgeWalkwayChance))) MarkV(i, j, minor);
            }

            // Ledges along the drop, only where a walkway already reaches the edge.
            for (int i = 0; i < n; i++)
            foreach (int j in new[] { 0, n })
                if (!hEdge[i, j] && Chance(g.edgeWalkwayChance * 0.5f) && (ReachesEdge(i, j) || ReachesEdge(i + 1, j)))
                    MarkH(i, j, minor);
            for (int j = 0; j < n; j++)
            foreach (int i in new[] { 0, n })
                if (!vEdge[i, j] && Chance(g.edgeWalkwayChance * 0.5f) && (ReachesEdge(i, j) || ReachesEdge(i, j + 1)))
                    MarkV(i, j, minor);

            PlaceOutposts(n);

            // Make sure the top of the pit ramp and the Watcher outposts join the network.
            var rampTop = Rect.MinMaxRect(g.pitCenter.x - g.rampWidth, g.pitCenter.y + g.pitRadius,
                g.pitCenter.x + g.rampWidth, g.pitCenter.y + g.RampEnd + 2f);
            for (int i = 0; i < n; i++)
            for (int j = 1; j < n; j++)
            {
                float w = hWidth[i, j] > 0f ? hWidth[i, j] : minor;
                var r = Clip(Rect.MinMaxRect(xs[i] - w / 2f, zs[j] - w / 2f, xs[i + 1] + w / 2f, zs[j] + w / 2f));
                if (r.Overlaps(rampTop) || OverlapsOutpost(r)) MarkH(i, j, minor);
            }
            for (int i = 1; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                float w = vWidth[i, j] > 0f ? vWidth[i, j] : minor;
                var r = Clip(Rect.MinMaxRect(xs[i] - w / 2f, zs[j] - w / 2f, xs[i] + w / 2f, zs[j + 1] + w / 2f));
                if (OverlapsOutpost(r)) MarkV(i, j, minor);
            }

            segments = new List<Segment>();
            streetIndex = new RectIndex();
            alleyIndex = new RectIndex();
            walkIndex = new RectIndex();
            for (int i = 0; i < n; i++)
            for (int j = 0; j <= n; j++)
                if (hEdge[i, j]) AddSegment(HRect(i, j), true, hWidth[i, j] >= main - 0.01f);
            for (int i = 0; i <= n; i++)
            for (int j = 0; j < n; j++)
                if (vEdge[i, j]) AddSegment(VRect(i, j), false, vWidth[i, j] >= main - 0.01f);
        }

        // ---------------------------------------------------------------- watcher outposts

        /// <summary>
        /// One Watcher outpost off each edge of the city, somewhere in the middle stretch of it: a platform out
        /// over the void at the end of a street, with a bridge back to a small landing at the city's edge.
        /// </summary>
        private static void PlaceOutposts(int n)
        {
            g.watcherOutposts.Clear();
            float half = g.HalfSize, span = half * g.outpostEdgeSpan, w = g.outpostWidth;
            float depth = g.outpostDepth, bridge = g.outpostBridge;
            const float landingDepth = 6f, landingHalf = 4f, bridgeHalf = 2f;
            var sides = new (Vector2 inward, float[] lines)[]
            {
                (Vector2.down, xs), (Vector2.up, xs), (Vector2.left, zs), (Vector2.right, zs),
            };
            foreach (var (inward, lines) in sides)
            {
                var options = new List<float>();
                for (int i = 1; i < n; i++)
                    if (Mathf.Abs(lines[i]) <= span - w / 2f) options.Add(lines[i]);
                for (int i = options.Count - 1; i > 0; i--)
                {
                    int j = rng.Next(i + 1);
                    (options[i], options[j]) = (options[j], options[i]);
                }
                foreach (float along in options)
                {
                    // Distances out from the edge (negative = into the city), mapped onto this side.
                    Rect Span(float a0, float a1, float out0, float out1)
                    {
                        if (inward.y < 0f) return Rect.MinMaxRect(a0, half + out0, a1, half + out1);
                        if (inward.y > 0f) return Rect.MinMaxRect(a0, -half - out1, a1, -half - out0);
                        if (inward.x < 0f) return Rect.MinMaxRect(half + out0, a0, half + out1, a1);
                        return Rect.MinMaxRect(-half - out1, a0, -half - out0, a1);
                    }
                    var platform = Span(along - w / 2f, along + w / 2f, bridge, bridge + depth);
                    var bridgeRect = Span(along - bridgeHalf, along + bridgeHalf, -0.5f, bridge + 0.5f);
                    var landing = Span(along - landingHalf, along + landingHalf, -landingDepth, 0f);
                    if (DistanceToRect(g.pitCenter, landing) < g.pitRadius + 8f || g.InRampCorridor(landing.center, 8f)) continue;
                    g.watcherOutposts.Add(new PittGreybox.WatcherOutpost { rect = platform, inward = inward, bridge = bridgeRect, landing = landing });
                    break;
                }
            }
        }

        /// <summary>
        /// The outposts themselves: nothing like the rest of the Pitt. A polished white marble platform hanging
        /// off the edge of the city on steel beams, void on three sides, reached by a railed bridge. On it, a
        /// columned pavilion edged with cool light, a counter with a Watcher behind it, and a tall beacon.
        /// The Watcher is robed dark under a white mask so they stand out against all that marble.
        /// </summary>
        private static void BuildWatcherOutposts(Transform root)
        {
            if (g.watcherOutposts.Count == 0) return;
            var parent = Group(root, "WatcherOutposts");
            var marble = Mat("World_Marble", new Color(0.82f, 0.82f, 0.85f));
            var robe = Mat("World_WatcherRobe", new Color(0.09f, 0.1f, 0.12f));
            robe.SetFloat("_Smoothness", 0.35f);
            var mask = Mat("World_WatcherMask", new Color(0.95f, 0.95f, 0.97f));
            mask.SetFloat("_Smoothness", 0.85f);
            var strip = Glow(new Color(0.82f, 0.92f, 1f), 2.5f);
            var visor = Glow(new Color(0.4f, 0.85f, 1f), 6f);
            var cool = new Color(0.8f, 0.9f, 1f);
            var terminalScreen = g.terminalScreen != null ? g.terminalScreen : Glow(new Color(1f, 0.15f, 0.1f), 1.5f);

            int index = 0;
            foreach (var o in g.watcherOutposts)
            {
                var post = Group(parent, $"Outpost_{index++}");
                var r = o.rect;
                var n = o.inward;
                var along = new Vector2(n.y, -n.x);
                float depth = Mathf.Abs(n.x) > 0f ? r.width : r.height;
                float width = Mathf.Abs(n.x) > 0f ? r.height : r.width;
                var back = r.center - n * (depth / 2f);   // the far side, out over the void
                var front = r.center + n * (depth / 2f);  // the side facing the city
                float ground = g.HeightAt(o.landing.center);
                float floor = ground + 0.08f;

                Rect Oriented(Vector2 c, float alongSize, float inwardSize) => Mathf.Abs(n.x) > 0f
                    ? new Rect(c.x - inwardSize / 2f, c.y - alongSize / 2f, inwardSize, alongSize)
                    : new Rect(c.x - alongSize / 2f, c.y - inwardSize / 2f, alongSize, inwardSize);

                // The platform, and what holds it up: two beams running back into the pillar, with struts.
                Box("Platform", post, r, ground - 0.6f, floor, marble);
                foreach (float a in new[] { -width / 2f + 2f, width / 2f - 2f })
                {
                    var beamCentre = (back + n * 0.5f + (front + n * (g.outpostBridge + 6f))) / 2f + along * a;
                    float beamLength = depth + g.outpostBridge + 5.5f;
                    Box("Beam", post, Oriented(beamCentre, 0.6f, beamLength), ground - 1.6f, ground - 0.6f, g.metal);
                    // A diagonal brace from under the far end of the platform back down into the cliff face.
                    var under = back + n * 1.5f + along * a;
                    var cliff = front + n * g.outpostBridge + along * a;
                    Brace(post, ToV3(under, ground - 1.4f), ToV3(cliff, ground - 11f), 0.45f, g.metal);
                    Brace(post, ToV3(r.center + along * a, ground - 1.4f), ToV3(cliff, ground - 6f), 0.4f, g.metal);
                }
                Box("Underside", post, Oriented(r.center, width - 1f, depth - 1f), ground - 1.2f, ground - 0.6f, g.concreteDark);

                // The bridge: marble deck, low marble walls each side, lights along it.
                var b = o.bridge;
                Box("Bridge", post, b, ground - 0.5f, floor, marble);
                float bridgeLength = Mathf.Abs(n.x) > 0f ? b.width : b.height;
                foreach (float side in new[] { -1f, 1f })
                {
                    var wallCentre = b.center + along * side * 1.85f;
                    Box("BridgeWall", post, Oriented(wallCentre, 0.3f, bridgeLength), floor, floor + 1f, marble);
                    Box("BridgeLight", post, Oriented(wallCentre, 0.32f, bridgeLength - 1f), floor + 1f, floor + 1.06f, strip);
                }

                // A balustrade round the three open sides, and either side of the bridge on the fourth.
                Box("Balustrade", post, Oriented(back + n * 0.15f, width, 0.3f), floor, floor + 1.1f, marble);
                foreach (float side in new[] { -1f, 1f })
                {
                    Box("Balustrade", post, Oriented(r.center + along * side * (width / 2f - 0.15f), 0.3f, depth), floor, floor + 1.1f, marble);
                    float frontLength = width / 2f - 2.3f;
                    Box("Balustrade", post, Oriented(front - n * 0.15f + along * side * (2.3f + frontLength / 2f), frontLength, 0.3f), floor, floor + 1.1f, marble);
                }

                // The pavilion: back wall, columns, roof edged with light rather than glowing all over.
                Box("BackWall", post, Oriented(back + n * 0.6f, 11f, 0.4f), floor, floor + 4.4f, marble);
                foreach (float a in new[] { -3.8f, 3.8f })
                    Box("WallLight", post, Oriented(back + n * 0.82f + along * a, 0.12f, 0.05f), floor + 0.8f, floor + 3.6f, strip);
                foreach (float a in new[] { -4.6f, -1.6f, 1.6f, 4.6f })
                    Box("Column", post, Oriented(back + n * 5.4f + along * a, 0.55f, 0.55f), floor, floor + 4f, marble);
                Box("Roof", post, Oriented(back + n * 3f, 11f, 5.6f), floor + 4f, floor + 4.45f, marble);
                Box("RoofEdge", post, Oriented(back + n * 5.75f, 11f, 0.08f), floor + 3.9f, floor + 4f, strip);
                Box("RoofEdge", post, Oriented(back + n * 1f, 10f, 0.08f), floor + 3.9f, floor + 4f, strip);
                AddLight(post, ToV3(back + n * 4.5f, floor + 3.4f), cool, 2.2f, 9f, fixture: false);

                // The counter, and the Watcher behind it: a dark robe, a white mantle and mask, a glowing visor.
                var desk = Group(post, "Watcher");
                Box("Counter", desk, Oriented(back + n * 3.4f, 3.4f, 0.8f), floor, floor + 1.1f, marble);
                Box("CounterGlow", desk, Oriented(back + n * 3.82f, 3.2f, 0.04f), floor + 0.95f, floor + 1.0f, strip);
                var watcher = Group(desk, "Figure");
                watcher.position = ToV3(back + n * 2.4f, floor);
                watcher.rotation = Quaternion.LookRotation(new Vector3(n.x, 0f, n.y));
                Part(watcher, PrimitiveType.Capsule, new Vector3(0f, 1.0f, 0f), new Vector3(0.66f, 1.0f, 0.66f), robe, "Body", collider: true);
                Part(watcher, PrimitiveType.Cylinder, new Vector3(0f, 1.62f, 0f), new Vector3(0.78f, 0.08f, 0.6f), mask, "Mantle", collider: false);
                Part(watcher, PrimitiveType.Sphere, new Vector3(0f, 1.86f, 0f), new Vector3(0.36f, 0.4f, 0.36f), mask, "Head", collider: true);
                Part(watcher, PrimitiveType.Cube, new Vector3(0f, 1.88f, 0.16f), new Vector3(0.3f, 0.06f, 0.08f), visor, "Visor", collider: false);
                // A soft key light in front of them so they read against the bright marble behind.
                AddLight(desk, ToV3(back + n * 3.3f, floor + 3.3f), new Color(1f, 0.97f, 0.92f), 1.4f, 3.5f, fixture: false);
                desk.gameObject.AddComponent<Properties.WatcherDesk>();

                // The debt terminal beside the counter, screen toward the city, where the player pays the Watchers.
                var terminal = Group(post, "WatcherTerminal");
                terminal.position = ToV3(back + n * 3.4f + along * 3f, floor);
                terminal.rotation = Quaternion.LookRotation(new Vector3(n.x, 0f, n.y));
                Part(terminal, PrimitiveType.Cube, new Vector3(0f, 1f, 0f), new Vector3(0.9f, 2f, 0.6f), g.metal, "Cabinet", collider: true);
                Part(terminal, PrimitiveType.Cube, new Vector3(0f, 1.4f, 0.31f), new Vector3(0.7f, 0.5f, 0.02f), terminalScreen, "Screen", collider: false);
                var glow = new GameObject("Glow").AddComponent<Light>();
                glow.transform.SetParent(terminal, false);
                glow.transform.localPosition = new Vector3(0f, 1.4f, 0.8f);
                glow.type = LightType.Point;
                glow.color = new Color(1f, 0.2f, 0.15f);
                glow.intensity = 6f;
                glow.range = 3f;
                glow.shadows = LightShadows.None;
                terminal.gameObject.AddComponent<Economy.DebtTerminal>();

                // The beacon, at a back corner.
                var beacon = back + n * 1.2f + along * (width / 2f - 1.2f);
                Box("Beacon", post, Oriented(beacon, 0.6f, 0.6f), floor, floor + 12f, marble);
                Box("BeaconRing", post, Oriented(beacon, 0.75f, 0.75f), floor + 11f, floor + 11.4f, strip);
                Box("BeaconTop", post, Oriented(beacon, 0.4f, 0.4f), floor + 12f, floor + 12.4f, Glow(cool, 5f));
                AddLight(post, ToV3(beacon, floor + 12.8f), cool, 5f, 20f, fixture: false);

                // The landing at the city's edge: a marble threshold, so the way out is plain from the street.
                Box("Landing", post, o.landing, ground - 0.3f, ground + 0.04f, marble);
            }
        }

        /// <summary>A square-section beam between two points.</summary>
        private static void Brace(Transform parent, Vector3 from, Vector3 to, float thickness, Material material)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "Brace";
            go.transform.SetParent(parent, false);
            go.transform.position = (from + to) / 2f;
            go.transform.rotation = Quaternion.LookRotation(to - from);
            go.transform.localScale = new Vector3(thickness, thickness, Vector3.Distance(from, to));
            go.GetComponent<Renderer>().sharedMaterial = material;
        }

        /// <summary>A primitive part of a figure, in the figure's local space.</summary>
        private static void Part(Transform parent, PrimitiveType type, Vector3 position, Vector3 scale, Material material, string name, bool collider)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            if (!collider) Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.transform.localScale = scale;
            go.GetComponent<Renderer>().sharedMaterial = material;
        }

        private static bool OverlapsOutpost(Rect r)
        {
            foreach (var o in g.watcherOutposts)
                if (o.landing.Overlaps(r)) return true;
            return false;
        }

        private static bool ReachesEdge(int i, int j)
        {
            int n = xs.Length - 1;
            if (i == 0 && j > 0 && j < n) return hEdge[0, j];
            if (i == n && j > 0 && j < n) return hEdge[n - 1, j];
            if (j == 0 && i > 0 && i < n) return vEdge[i, 0];
            if (j == n && i > 0 && i < n) return vEdge[i, n - 1];
            return false;
        }

        private static void SetEdge(Vector2Int a, Vector2Int b, float width)
        {
            if (a.y == b.y) MarkH(Mathf.Min(a.x, b.x), a.y, width);
            else MarkV(a.x, Mathf.Min(a.y, b.y), width);
        }

        private static void AddSegment(Rect r, bool alongX, bool thoroughfare)
        {
            if (r.width <= 0f || r.height <= 0f) return;
            segments.Add(new Segment { rect = r, alongX = alongX, thoroughfare = thoroughfare });
            streetIndex.Add(r);
            walkIndex.Add(r);
        }

        private static bool OffMap(Vector2 p) => Mathf.Abs(p.x) > g.HalfSize || Mathf.Abs(p.y) > g.HalfSize;

        private static bool Walkable(Vector2 p) => OffMap(p) || walkIndex.Contains(p) || g.IsOpen(p);

        private static float CellMidX(int i) => (xs[i] + xs[i + 1]) / 2f;
        private static float CellMidZ(int j) => (zs[j] + zs[j + 1]) / 2f;

        private static float DistanceToRect(Vector2 p, Rect r)
        {
            float dx = Mathf.Max(r.xMin - p.x, 0f, p.x - r.xMax);
            float dz = Mathf.Max(r.yMin - p.y, 0f, p.y - r.yMax);
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>
        /// Splits the whole city into wide flat terraces, each at its own height.
        /// Edges run along the middle of blocks, so streets cross them square-on and get stairs there (see
        /// <see cref="BuildHeights"/>). The pit with its ramp, each Watcher landing and each gang square stand
        /// wholly on one level, and no terrace is more than <see cref="PittGreybox.maxTerraceRise"/>
        /// above or below a neighbour.
        /// </summary>
        private static void PlaceTerraces()
        {
            g.terraces.Clear();
            int n = xs.Length - 1;
            // Cell boundaries: the middle of every block, and well past the edges of the map.
            var bx = new float[n + 2];
            var bz = new float[n + 2];
            bx[0] = bz[0] = -g.HalfSize - 50f;
            bx[n + 1] = bz[n + 1] = g.HalfSize + 50f;
            for (int i = 1; i <= n; i++)
            {
                bx[i] = CellMidX(i - 1);
                bz[i] = CellMidZ(i - 1);
            }
            // The pit with its ramp, each Watcher landing and each gang square have to be level all over: no cut runs through them.
            // Tight: the rim round the pit can take a terrace edge, but not the pit itself.
            float pr = g.pitRadius;
            var pc = g.pitCenter;
            var level = new List<Rect> { Rect.MinMaxRect(pc.x - pr, pc.y - pr, pc.x + pr, pc.y + Mathf.Max(pr, g.RampEnd)) };
            foreach (var o in g.watcherOutposts) level.Add(Grow(o.landing, 3f));
            foreach (var sq in g.gangSquares) level.Add(Grow(sq.rect, 3f));
            int CellOf(float[] lines, float v)
            {
                for (int c = 0; c < lines.Length - 1; c++)
                    if (v < lines[c + 1]) return c;
                return lines.Length - 2;
            }
            keepWhole = level.ConvertAll(a =>
            {
                int x0 = CellOf(bx, a.xMin), x1 = CellOf(bx, a.xMax), z0 = CellOf(bz, a.yMin), z1 = CellOf(bz, a.yMax);
                return new RectInt(x0, z0, x1 - x0 + 1, z1 - z0 + 1);
            });
            var cells = new List<RectInt>();
            SplitTerrace(new RectInt(0, 0, n + 1, n + 1), cells);
            var rects = cells.ConvertAll(c => Rect.MinMaxRect(bx[c.xMin], bz[c.yMin], bx[c.xMax], bz[c.yMax]));
            int count = rects.Count;

            // Areas that have to be level all over: each one's terraces share a height.
            var group = new int[count];
            for (int i = 0; i < count; i++) group[i] = -1;
            int groups = 0;
            void Together(Rect area)
            {
                int id = groups++;
                for (int i = 0; i < count; i++)
                {
                    if (!rects[i].Overlaps(area)) continue;
                    // Already in another group: merge that one into this.
                    if (group[i] >= 0)
                        for (int k = 0; k < count; k++)
                            if (k != i && group[k] == group[i]) group[k] = id;
                    group[i] = id;
                }
            }
            // Each of those is on one terrace now, unless two share one.
            foreach (var area in level) Together(area);

            float step = g.terraceStep;
            float Level(float h) => Mathf.Clamp(Mathf.Round(h / step) * step, g.terraceHeights.x, g.terraceHeights.y);
            float maxRise = Mathf.Max(step, Mathf.Floor(g.maxTerraceRise / step) * step);
            var heights = new float[count];
            for (int i = 0; i < count; i++) heights[i] = Level(R(g.terraceHeights));
            void Set(int i, float h)
            {
                for (int k = 0; k < count; k++)
                    if (k == i || (group[i] >= 0 && group[k] == group[i])) heights[k] = h;
            }
            for (int i = 0; i < count; i++)
                if (group[i] >= 0) Set(i, heights[i]);

            // Neighbours: sharing a stretch of edge.
            var pairs = new List<(int a, int b)>();
            for (int a = 0; a < count; a++)
            for (int b = a + 1; b < count; b++)
            {
                Rect ra = rects[a], rb = rects[b];
                bool sideX = (Mathf.Abs(ra.xMax - rb.xMin) < 0.01f || Mathf.Abs(rb.xMax - ra.xMin) < 0.01f) &&
                             Mathf.Min(ra.yMax, rb.yMax) - Mathf.Max(ra.yMin, rb.yMin) > 0.5f;
                bool sideZ = (Mathf.Abs(ra.yMax - rb.yMin) < 0.01f || Mathf.Abs(rb.yMax - ra.yMin) < 0.01f) &&
                             Mathf.Min(ra.xMax, rb.xMax) - Mathf.Max(ra.xMin, rb.xMin) > 0.5f;
                if (sideX || sideZ) pairs.Add((a, b));
            }
            // Pull too-steep neighbours together, moving either one.
            for (int pass = 0; pass < 300; pass++)
            {
                bool changed = false;
                foreach (var (a, b) in pairs)
                {
                    float d = heights[b] - heights[a];
                    if (Mathf.Abs(d) <= maxRise + 0.01f) continue;
                    int move = Chance(0.5f) ? a : b;
                    int other = move == a ? b : a;
                    Set(move, heights[other] + Mathf.Sign(heights[move] - heights[other]) * maxRise);
                    changed = true;
                }
                if (!changed) break;
            }

            float worst = 0f;
            foreach (var (a, b) in pairs) worst = Mathf.Max(worst, Mathf.Abs(heights[a] - heights[b]));
            if (worst > maxRise + 0.01f) Debug.LogWarning($"PittGreybox: terraces up to {worst} m apart (limit {maxRise}).");
            for (int i = 0; i < count; i++) g.terraces.Add(new PittGreybox.Plateau { rect = rects[i], height = heights[i] });
            // The gang squares were laid out first; now they know what height they stand at.
            for (int i = 0; i < g.gangSquares.Count; i++)
                g.gangSquares[i] = new PittGreybox.Plateau { rect = g.gangSquares[i].rect, height = g.GroundAt(g.gangSquares[i].rect.center) };
        }

        /// <summary>Areas (in terrace cells) no terrace edge may cut through.</summary>
        private static List<RectInt> keepWhole = new();

        /// <summary>Cuts a block of terrace cells into terraces terraceBlocks across, at random, never through <see cref="keepWhole"/>.</summary>
        private static void SplitTerrace(RectInt r, List<RectInt> into)
        {
            int min = Mathf.Max(1, g.terraceBlocks.x), max = Mathf.Max(min, g.terraceBlocks.y);
            int Cut(bool alongX)
            {
                int size = alongX ? r.width : r.height;
                var options = new List<int>();
                for (int k = min; k <= size - min; k++)
                {
                    int line = (alongX ? r.x : r.y) + k;
                    bool through = false;
                    foreach (var w in keepWhole)
                    {
                        bool overlaps = alongX ? w.yMin < r.yMax && w.yMax > r.yMin : w.xMin < r.xMax && w.xMax > r.xMin;
                        through |= overlaps && (alongX ? w.xMin < line && line < w.xMax : w.yMin < line && line < w.yMax);
                    }
                    if (!through) options.Add(k);
                }
                return options.Count > 0 ? options[rng.Next(options.Count)] : -1;
            }
            bool wantX = r.width > max || (r.width >= min * 2 && Chance(0.4f));
            bool wantZ = r.height > max || (r.height >= min * 2 && Chance(0.4f));
            int kx = wantX ? Cut(true) : -1, kz = wantZ ? Cut(false) : -1;
            if (kx > 0 && (kz < 0 || r.width >= r.height))
            {
                SplitTerrace(new RectInt(r.x, r.y, kx, r.height), into);
                SplitTerrace(new RectInt(r.x + kx, r.y, r.width - kx, r.height), into);
            }
            else if (kz > 0)
            {
                SplitTerrace(new RectInt(r.x, r.y, r.width, kz), into);
                SplitTerrace(new RectInt(r.x, r.y + kz, r.width, r.height - kz), into);
            }
            else into.Add(r);
        }

        /// <summary>
        /// Gang squares: a sparse few flat, open areas per district where gangs operate, centred on walkway
        /// junctions so streets lead in. Width and length are rolled separately, so they come long and narrow
        /// or broad, and the big ones swallow whole blocks. Laid out before the terraces, which keep each level.
        /// </summary>
        private static void PlaceGangSquares()
        {
            g.gangSquares.Clear();
            int n = xs.Length - 1;
            // Edges fall inside blocks, clear of any street, so streets cross them square-on (that's where
            // the stairs go if the square ends up on a different level from the street).
            float clearance = g.walkwayWidth / 2f + 2.5f;
            var candidates = new List<Rect>();
            for (int i = 1; i < n; i++)
            for (int j = 1; j < n; j++)
            {
                int ways = (hEdge[i - 1, j] ? 1 : 0) + (hEdge[i, j] ? 1 : 0) + (vEdge[i, j - 1] ? 1 : 0) + (vEdge[i, j] ? 1 : 0);
                if (ways < 2) continue;
                var p = new Vector2(xs[i], zs[j]);
                float hx = FitHalf(xs, i, R(g.gangSquareSize) / 2f, clearance);
                float hz = FitHalf(zs, j, R(g.gangSquareSize) / 2f, clearance);
                if (hx < 4.5f || hz < 4.5f) continue;
                var rect = new Rect(p.x - hx, p.y - hz, hx * 2f, hz * 2f);
                if (DistanceToRect(g.pitCenter, rect) < g.pitRadius + 12f || g.InRampCorridor(p, 8f + Mathf.Max(hx, hz))) continue;
                if (rect.xMin < -g.HalfSize + 12f || rect.xMax > g.HalfSize - 12f || rect.yMin < -g.HalfSize + 12f || rect.yMax > g.HalfSize - 12f) continue;
                if (OverlapsOutpost(Grow(rect, 12f))) continue;
                candidates.Add(rect);
            }

            for (int d = 0; d < g.districts.Count; d++)
            {
                int want = rng.Next(g.gangSquaresPerDistrict.x, g.gangSquaresPerDistrict.y + 1);
                var pool = candidates.FindAll(c => g.DistrictIndexAt(c.center) == d);
                for (int got = 0; got < want && pool.Count > 0;)
                {
                    int pick = rng.Next(pool.Count);
                    var rect = pool[pick];
                    pool.RemoveAt(pick);

                    bool clear = true;
                    foreach (var other in g.gangSquares)
                        clear &= Vector2.Distance(other.rect.center, rect.center) > g.gangSquareSpacing && !Grow(other.rect, 12f).Overlaps(rect);
                    if (!clear) continue;

                    // Height is filled in once the terraces are placed.
                    g.gangSquares.Add(new PittGreybox.Plateau { rect = rect });
                    got++;
                }
            }
        }

        /// <summary>
        /// Half-size of a square centred on grid line <paramref name="at"/>, as near to <paramref name="want"/> as
        /// it can be with both edges at least <paramref name="clearance"/> from every other grid line; 0 if none fits.
        /// </summary>
        private static float FitHalf(float[] lines, int at, float want, float clearance)
        {
            float c = lines[at], half = want;
            for (int pass = 0; pass < 4; pass++)
            {
                bool ok = true;
                for (int k = 0; k < lines.Length; k++)
                {
                    if (k == at) continue;
                    float d = Mathf.Abs(lines[k] - c);
                    if (Mathf.Abs(d - half) >= clearance) continue;
                    // Move the edge off that line, to whichever side is nearer: big squares take the street in.
                    half = half > d ? d + clearance : d - clearance;
                    ok = false;
                }
                if (ok) return half;
            }
            return 0f;
        }

        private class Sump
        {
            public Vector2 node;
            public Rect shaft;
            public Vector2 up; // direction the entrance stairs climb toward
            public Vector2Int quadrant;
        }

        private static List<Sump> sumps;
        private static float BuildingSink => sumps != null && sumps.Count > 0 ? 0.4f : 2f;
        private const float TunnelCell = 2f;

        private static float SnapUp(float v) => Mathf.Ceil((v + g.HalfSize) / TunnelCell) * TunnelCell - g.HalfSize;
        private static float SnapDown(float v) => Mathf.Floor((v + g.HalfSize) / TunnelCell) * TunnelCell - g.HalfSize;

        /// <summary>Small open squares at thoroughfare junctions, each with a stairwell down to the sumps.</summary>
        private static void PlaceSumps()
        {
            sumps = new List<Sump>();
            g.sumpSquares.Clear();
            g.sumpShafts.Clear();
            int n = xs.Length - 1;
            float main = g.walkwayWidth - 0.01f;
            var candidates = new List<Vector2>();
            for (int i = 1; i < n; i++)
            for (int j = 1; j < n; j++)
            {
                bool onMain = hWidth[i - 1, j] >= main || hWidth[i, j] >= main || vWidth[i, j - 1] >= main || vWidth[i, j] >= main;
                if (!onMain) continue;
                var p = new Vector2(xs[i], zs[j]);
                if (Vector2.Distance(p, g.pitCenter) < g.pitRadius + 24f) continue;
                if (g.InRampCorridor(p, 14f) || Mathf.Abs(p.x) > g.HalfSize - 24f || Mathf.Abs(p.y) > g.HalfSize - 24f) continue;
                candidates.Add(p);
            }

            while (candidates.Count > 0 && sumps.Count < g.sumpEntrances)
            {
                int pick = rng.Next(candidates.Count);
                var p = candidates[pick];
                candidates.RemoveAt(pick);
                bool spaced = true;
                foreach (var other in sumps) spaced &= Vector2.Distance(other.node, p) > 50f;
                if (!spaced) continue;

                // Stairwell in one quadrant of the junction, snapped to the tunnel grid: 4 m wide, 6 m long.
                var q = new Vector2Int(Chance(0.5f) ? 1 : -1, Chance(0.5f) ? 1 : -1);
                float x0 = q.x > 0 ? SnapUp(p.x + 2.2f) : SnapDown(p.x - 2.2f) - 4f;
                float z0 = q.y > 0 ? SnapUp(p.y + 2.2f) : SnapDown(p.y - 2.2f) - 6f;
                var shaft = new Rect(x0, z0, 4f, 6f);
                sumps.Add(new Sump { node = p, shaft = shaft, up = new Vector2(0f, -q.y), quadrant = q });
                g.sumpSquares.Add(Grow(new Rect(p.x - 1f, p.y - 1f, 2f, 2f), 10f));
                g.sumpShafts.Add(shaft);
            }
        }

        private static void PlanLots()
        {
            alleys = new List<Rect>();
            alleyRuns = new List<(Rect rect, bool alongZ)>();
            plans = new List<Plan>();
            var lots = new List<Rect>();
            int n = xs.Length - 1;

            for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                var cell = Rect.MinMaxRect(
                    xs[i] + vWidth[i, j] / 2f, zs[j] + hWidth[i, j] / 2f,
                    xs[i + 1] - vWidth[i + 1, j] / 2f, zs[j + 1] - hWidth[i, j + 1] / 2f);
                Split(cell, lots);
            }

            // Every lot from here on is built over a stretch of alley.
            int gridLots = lots.Count;

            // Small alleys mostly dead-end: block a through-alley part way along with an extra building.
            var kept = new List<Rect>();
            foreach (var a in alleys)
            {
                bool alongX = a.width > a.height;
                var endA = alongX ? new Vector2(a.xMin - 0.3f, a.center.y) : new Vector2(a.center.x, a.yMin - 0.3f);
                var endB = alongX ? new Vector2(a.xMax + 0.3f, a.center.y) : new Vector2(a.center.x, a.yMax + 0.3f);
                bool openA = streetIndex.Contains(endA), openB = streetIndex.Contains(endB);
                float length = alongX ? a.width : a.height;
                if (openA && openB && length > 4f && Chance(g.deadEndChance))
                {
                    float keep = length * R(0.35f, 0.75f);
                    bool keepA = Chance(0.5f);
                    Rect open, plug;
                    if (alongX)
                    {
                        open = keepA ? Rect.MinMaxRect(a.xMin, a.yMin, a.xMin + keep, a.yMax) : Rect.MinMaxRect(a.xMax - keep, a.yMin, a.xMax, a.yMax);
                        plug = keepA ? Rect.MinMaxRect(a.xMin + keep, a.yMin, a.xMax, a.yMax) : Rect.MinMaxRect(a.xMin, a.yMin, a.xMax - keep, a.yMax);
                    }
                    else
                    {
                        open = keepA ? Rect.MinMaxRect(a.xMin, a.yMin, a.xMax, a.yMin + keep) : Rect.MinMaxRect(a.xMin, a.yMax - keep, a.xMax, a.yMax);
                        plug = keepA ? Rect.MinMaxRect(a.xMin, a.yMin + keep, a.xMax, a.yMax) : Rect.MinMaxRect(a.xMin, a.yMin, a.xMax, a.yMax - keep);
                    }
                    kept.Add(open);
                    lots.Add(plug);
                }
                else kept.Add(a);
            }
            alleys = kept;
            if (Since(3))
            {
                FillAlleysAlongsideWalkways(lots);
                FillAlleysBesideStairs(lots);
            }
            if (Since(5))
            {
                // From version 7 cut back to the open ground's edge rather than dropped.
                if (Since(7)) CutAlleysAtOpenGround();
                else DropAlleysIntoOpenGround();
                FillAlleysAlongTerraceEdges(lots);
            }
            var pits = FillAlleyPits();
            if (Since(8)) pits.AddRange(FillNarrowedAlleys());
            if (Since(4)) pits.AddRange(FillUnreachableAlleys());
            foreach (var a in alleys)
            {
                alleyIndex.Add(a);
                walkIndex.Add(a);
            }

            // Up to version 6 a lot touching open ground was left out whole, leaving open ground beyond the square (or
            // rim, or landing) with no wall along its edge. From version 7 it's cut back to the edge and built.
            var openGround = Since(7) ? OpenRects() : null;
            var pieces = new List<Rect>();
            var slivers = new List<Rect>();
            for (int i = 0; i < lots.Count; i++)
            {
                pieces.Clear();
                if (Since(7)) CutOpen(lots[i], openGround, pieces, 0.05f);
                else if (lots[i].width >= 1.2f && lots[i].height >= 1.2f && !TouchesOpen(lots[i])) pieces.Add(lots[i]);
                foreach (var lot in pieces)
                {
                    if (lot.width < 1.2f || lot.height < 1.2f)
                    {
                        // Too thin to stand alone: built onto the building beside it below.
                        slivers.Add(lot);
                        continue;
                    }
                    GroundRange(lot, out float min, out float max);
                    float height = Chance(g.towerChance) ? R(g.towerHeight) : R(g.buildingHeight);
                    plans.Add(new Plan { footprint = lot, groundMin = min, ground = max, top = max + height, filler = i >= gridLots });
                }
            }
            // From version 7, a strip of cut lot too thin for a building of its own (between the edge of the open
            // ground and the next lot) takes on the building it shares the longest wall with, so the wall along the
            // open ground runs unbroken. Uses no randomness.
            int built = plans.Count;
            foreach (var sliver in slivers)
            {
                var touch = Grow(sliver, 0.05f);
                Plan host = null;
                float best = 0f;
                for (int j = 0; j < built; j++)
                {
                    var o = plans[j].footprint;
                    if (!touch.Overlaps(o)) continue;
                    float shared = Mathf.Max(Mathf.Min(touch.xMax, o.xMax) - Mathf.Max(touch.xMin, o.xMin), Mathf.Min(touch.yMax, o.yMax) - Mathf.Max(touch.yMin, o.yMin));
                    if (shared <= best) continue;
                    best = shared;
                    host = plans[j];
                }
                if (host == null) continue;
                GroundRange(sliver, out float min, out float max);
                plans.Add(new Plan { footprint = sliver, groundMin = min, ground = max, top = host.top, filler = true });
            }

            // Filled-in stretches of alley, however small: a building's worth of solid where the hole was.
            foreach (var pit in pits)
            {
                GroundRange(pit, out float min, out float max);
                plans.Add(new Plan { footprint = pit, groundMin = min, ground = max, top = max + R(g.buildingHeight), filler = true });
            }
        }

        /// <summary>How far a face is set back from (or proud of) another it would otherwise sit flush with and fight.</summary>
        private const float FlushOffset = 0.02f;

        /// <summary>Narrowest gap the player can stand in.</summary>
        private const float PlayerGap = 0.9f;

        /// <summary>
        /// Builds over every piece of small alley the player can't walk into: ones whose way out was plugged or
        /// built over (an alley branching off another alley that's gone), or that only meet the streets across a
        /// terrace wall. Floods out from the streets, squares and other open ground through alley pieces at the
        /// same level; whatever it doesn't reach is returned (and taken out of <see cref="alleys"/>) to be built on.
        /// Run after <see cref="FillAlleyPits"/>, which leaves each piece level. Uses no randomness.
        /// </summary>
        private static List<Rect> FillUnreachableAlleys()
        {
            const float touch = 0.3f, level = 0.3f;
            var open = new List<Rect>();
            foreach (var s in segments) open.Add(s.rect);
            foreach (var sq in g.gangSquares) open.Add(sq.rect);
            open.AddRange(g.sumpSquares);
            foreach (var o in g.watcherOutposts) open.Add(o.landing);

            int n = alleys.Count;
            var heights = new float[n];
            for (int i = 0; i < n; i++) heights[i] = g.HeightAt(alleys[i].center);
            var reached = new bool[n];
            var queue = new Queue<int>();
            for (int i = 0; i < n; i++)
            {
                var grown = Grow(alleys[i], touch);
                bool root = false;
                foreach (var r in open)
                {
                    if (!grown.Overlaps(r)) continue;
                    var contact = Rect.MinMaxRect(Mathf.Max(grown.xMin, r.xMin), Mathf.Max(grown.yMin, r.yMin),
                        Mathf.Min(grown.xMax, r.xMax), Mathf.Min(grown.yMax, r.yMax)).center;
                    if (Mathf.Abs(g.HeightAt(contact) - heights[i]) < level) { root = true; break; }
                }
                // The pit's rim and anything else open that isn't a rect: just past the middle of each side.
                if (!root)
                {
                    var a = alleys[i];
                    foreach (var p in new[] { new Vector2(a.xMin - touch, a.center.y), new Vector2(a.xMax + touch, a.center.y),
                                 new Vector2(a.center.x, a.yMin - touch), new Vector2(a.center.x, a.yMax + touch) })
                        if (g.IsOpen(p) && Mathf.Abs(g.HeightAt(p) - heights[i]) < level) root = true;
                }
                if (!root) continue;
                reached[i] = true;
                queue.Enqueue(i);
            }
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                var grown = Grow(alleys[i], touch);
                for (int j = 0; j < n; j++)
                {
                    if (reached[j] || !grown.Overlaps(alleys[j]) || Mathf.Abs(heights[i] - heights[j]) >= level) continue;
                    reached[j] = true;
                    queue.Enqueue(j);
                }
            }

            var kept = new List<Rect>();
            var sealedOff = new List<Rect>();
            for (int i = 0; i < n; i++) (reached[i] ? kept : sealedOff).Add(alleys[i]);
            alleys = kept;
            return sealedOff;
        }

        /// <summary>
        /// Every small alley that would open onto a terrace stairway is built over instead, so nothing opens off
        /// the side of a flight (a gap beside the steps is a hole to fall into). The stairs aren't built until
        /// later and roll their own tread, so this takes the most room each could use: the longest flight
        /// with a mid landing, plus the landing over the edge. Uses no randomness.
        /// </summary>
        private static void FillAlleysBesideStairs(List<Rect> lots)
        {
            var reach = new List<Rect>();
            float h = g.HalfSize;
            foreach (var pl in g.terraces)
            {
                var r = pl.rect;
                var edges = new (float line, bool alongX, Vector2 normal, float from, float to)[]
                {
                    (r.yMin, true, Vector2.down, Mathf.Max(r.xMin, -h), Mathf.Min(r.xMax, h)),
                    (r.yMax, true, Vector2.up, Mathf.Max(r.xMin, -h), Mathf.Min(r.xMax, h)),
                    (r.xMin, false, Vector2.left, Mathf.Max(r.yMin, -h), Mathf.Min(r.yMax, h)),
                    (r.xMax, false, Vector2.right, Mathf.Max(r.yMin, -h), Mathf.Min(r.yMax, h)),
                };
                foreach (var e in edges)
                {
                    if (Mathf.Abs(e.line) >= h || e.to <= e.from) continue;
                    // The same crossings BuildHeights puts stairs at: streets crossing the edge square-on, built
                    // from the higher side.
                    foreach (var sg in segments)
                    {
                        if (sg.alongX == e.alongX) continue;
                        var sr = sg.rect;
                        float cross0 = e.alongX ? sr.xMin : sr.yMin, cross1 = e.alongX ? sr.xMax : sr.yMax;
                        float span0 = e.alongX ? sr.yMin : sr.xMin, span1 = e.alongX ? sr.yMax : sr.xMax;
                        if (cross1 < e.from || cross0 > e.to || e.line < span0 || e.line > span1) continue;
                        var crossMid = e.alongX ? new Vector2((cross0 + cross1) / 2f, e.line) : new Vector2(e.line, (cross0 + cross1) / 2f);
                        float rise = pl.height - g.HeightAt(crossMid + e.normal * 1.5f);
                        if (rise < 0.3f) continue;
                        bool lowIsPositive = e.normal.x + e.normal.y > 0f;
                        float room = lowIsPositive ? (e.alongX ? sr.yMax : sr.xMax) - e.line : e.line - (e.alongX ? sr.yMin : sr.xMin);
                        // Longest it could roll: the widest tread and a mid landing, but never past the street's room.
                        float run = Mathf.Min(Mathf.CeilToInt(rise / StepHeight) * 0.45f + (rise > 3.2f ? 2.2f : 0f), Mathf.Max(0f, room - 0.5f));
                        float near = -0.9f, far = run + 0.5f;
                        reach.Add(e.alongX
                            ? (lowIsPositive ? Rect.MinMaxRect(cross0, e.line + near, cross1, e.line + far) : Rect.MinMaxRect(cross0, e.line - far, cross1, e.line - near))
                            : (lowIsPositive ? Rect.MinMaxRect(e.line + near, cross0, e.line + far, cross1) : Rect.MinMaxRect(e.line - far, cross0, e.line - near, cross1)));
                    }
                }
            }

            var kept = new List<Rect>();
            foreach (var a in alleys)
            {
                bool opensOnto = false;
                // Touching a stairway's side counts: that's where its mouth would be.
                foreach (var z in reach) opensOnto |= Grow(z, 0.4f).Overlaps(a);
                if (!opensOnto) kept.Add(a);
                // Over the street itself (where the stairs go): dropped, never built on.
                else if (!(Since(5) && OverStreet(a))) lots.Add(a);
            }
            alleys = kept;
        }

        /// <summary>Less than this between a small alley and a bigger walkway running alongside it and they read as one.</summary>
        private const float AlleyClearance = 3.5f;

        /// <summary>
        /// A small alley shouldn't run right alongside a street, thoroughfare or gang square, with no more than a
        /// sliver of building between (or none). Any that does, along at least a quarter of its length, is built
        /// over instead. Uses no randomness.
        /// </summary>
        private static void FillAlleysAlongsideWalkways(List<Rect> lots)
        {
            var bigger = new List<Rect>();
            foreach (var s in segments) bigger.Add(s.rect);
            foreach (var sq in g.gangSquares) bigger.Add(sq.rect);
            var kept = new List<Rect>();
            foreach (var a in alleys)
            {
                bool alongX = a.width > a.height;
                float length = alongX ? a.width : a.height;
                bool alongside = false;
                foreach (var r in bigger)
                {
                    float overlap = alongX ? Mathf.Min(a.xMax, r.xMax) - Mathf.Max(a.xMin, r.xMin) : Mathf.Min(a.yMax, r.yMax) - Mathf.Max(a.yMin, r.yMin);
                    if (overlap < length * 0.25f) continue;
                    // The strip between them, across the alley's run (negative if they overlap).
                    float gap = alongX ? Mathf.Max(r.yMin - a.yMax, a.yMin - r.yMax) : Mathf.Max(r.xMin - a.xMax, a.xMin - r.xMax);
                    if (gap < AlleyClearance)
                    {
                        alongside = true;
                        break;
                    }
                }
                if (!alongside) kept.Add(a);
                // An alley lying over the street is just more street: building on it would block the street.
                else if (!(Since(5) && OverStreet(a))) lots.Add(a);
            }
            alleys = kept;
        }

        /// <summary>
        /// Small alleys running into open ground (the scrap pit and its rim, a gang square) are dropped: the lots
        /// there are left open already, and an alley sloping down into the pit bowl can't be walked. What's left of
        /// the ground is simply open. Uses no randomness.
        /// </summary>
        private static void DropAlleysIntoOpenGround()
        {
            alleys.RemoveAll(a =>
            {
                var inner = Grow(a, -0.3f);
                bool alongX = a.width > a.height;
                float length = alongX ? inner.width : inner.height;
                for (float d = 0f; d <= length + 0.01f; d += 0.5f)
                {
                    var p = alongX ? new Vector2(inner.xMin + d, inner.center.y) : new Vector2(inner.center.x, inner.yMin + d);
                    if (g.IsOpen(p)) return true;
                }
                return false;
            });
        }

        /// <summary>
        /// From version 7, small alleys running into open ground are cut back to its edge, so the stretch between the
        /// buildings either side stays an alley opening onto the square (or rim, or landing). Uses no randomness.
        /// </summary>
        private static void CutAlleysAtOpenGround()
        {
            var open = OpenRects();
            var kept = new List<Rect>();
            var pieces = new List<Rect>();
            foreach (var a in alleys)
            {
                pieces.Clear();
                CutOpen(a, open, pieces, PlayerGap);
                // Shortened only: a piece narrowed by open ground alongside would jog against the rest of the alley
                // (version 8). That strip is left as open ground.
                bool alongX = a.width > a.height;
                foreach (var piece in pieces)
                    if (!Since(8) || Mathf.Abs((alongX ? piece.height : piece.width) - (alongX ? a.height : a.width)) < 0.01f)
                        kept.Add(piece);
            }
            alleys = kept;
        }

        /// <summary>The open ground (see <see cref="PittGreybox.IsOpen"/>) as rects: everything but the pit's round rim.</summary>
        private static List<Rect> OpenRects()
        {
            var list = new List<Rect>();
            foreach (var sq in g.gangSquares) list.Add(sq.rect);
            foreach (var o in g.watcherOutposts) list.Add(o.landing);
            list.AddRange(g.sumpSquares);
            // The ramp corridor, as InRampCorridor with its 1.5 m margin: the ramp climbs along +Z.
            float half = g.rampWidth * 0.5f + 1.5f;
            list.Add(Rect.MinMaxRect(g.pitCenter.x - half, g.pitCenter.y, g.pitCenter.x + half, g.pitCenter.y + g.RampEnd + 1.5f));
            return list;
        }

        /// <summary>The scrap pit and the rim kept open around it, as <see cref="PittGreybox.IsOpen"/>.</summary>
        private static float PitRimRadius => g.pitRadius + 4f;

        private static bool OverlapsPitRim(Rect r) => DistanceToRect(g.pitCenter, r) < PitRimRadius;

        /// <summary>
        /// What's left of a rect once the open ground is cut out of it: the parts beside each open rect, then any piece
        /// reaching into the pit rim trimmed back from whichever side keeps the most. Pieces narrower than
        /// <paramref name="minSide"/> are dropped.
        /// </summary>
        private static void CutOpen(Rect r, List<Rect> open, List<Rect> into, float minSide)
        {
            var pieces = new List<Rect> { r };
            foreach (var o in open)
            {
                if (!r.Overlaps(o)) continue;
                var next = new List<Rect>();
                foreach (var p in pieces) Subtract(p, o, next);
                pieces = next;
            }
            foreach (var p in pieces)
            {
                var piece = p;
                if (OverlapsPitRim(piece))
                {
                    var cut = CutFromPitRim(piece);
                    if (cut == null) continue;
                    piece = cut.Value;
                }
                if (piece.width >= minSide && piece.height >= minSide) into.Add(piece);
            }
        }

        /// <summary>The parts of <paramref name="a"/> outside <paramref name="b"/>: whole-height strips either side, then the rest above and below.</summary>
        private static void Subtract(Rect a, Rect b, List<Rect> into)
        {
            if (!a.Overlaps(b))
            {
                into.Add(a);
                return;
            }
            if (b.xMin > a.xMin) into.Add(Rect.MinMaxRect(a.xMin, a.yMin, b.xMin, a.yMax));
            if (b.xMax < a.xMax) into.Add(Rect.MinMaxRect(b.xMax, a.yMin, a.xMax, a.yMax));
            float x0 = Mathf.Max(a.xMin, b.xMin), x1 = Mathf.Min(a.xMax, b.xMax);
            if (b.yMin > a.yMin) into.Add(Rect.MinMaxRect(x0, a.yMin, x1, b.yMin));
            if (b.yMax < a.yMax) into.Add(Rect.MinMaxRect(x0, b.yMax, x1, a.yMax));
        }

        /// <summary>A rect cut back from one side until it clears the pit rim, keeping as much as it can; null if nothing's left.</summary>
        private static Rect? CutFromPitRim(Rect r)
        {
            Rect? best = null;
            float bestArea = 0f;
            for (int side = 0; side < 4; side++)
            {
                Rect Cut(float d) => side switch
                {
                    0 => Rect.MinMaxRect(r.xMin + d, r.yMin, r.xMax, r.yMax),
                    1 => Rect.MinMaxRect(r.xMin, r.yMin, r.xMax - d, r.yMax),
                    2 => Rect.MinMaxRect(r.xMin, r.yMin + d, r.xMax, r.yMax),
                    _ => Rect.MinMaxRect(r.xMin, r.yMin, r.xMax, r.yMax - d),
                };
                float lo = 0f, hi = side < 2 ? r.width : r.height;
                if (OverlapsPitRim(Cut(hi - 0.05f))) continue;
                for (int k = 0; k < 16; k++)
                {
                    float mid = (lo + hi) / 2f;
                    if (OverlapsPitRim(Cut(mid))) lo = mid;
                    else hi = mid;
                }
                var c = Cut(hi);
                if (c.width * c.height > bestArea)
                {
                    bestArea = c.width * c.height;
                    best = c;
                }
            }
            return best;
        }

        /// <summary>
        /// A small alley with a terrace edge running down its length gets the retaining wall built along it, which
        /// leaves strips either side too narrow to walk: it's built over instead (unless it's over a street).
        /// Alleys crossing an edge are dealt with by <see cref="FillAlleyPits"/>. Uses no randomness.
        /// </summary>
        private static void FillAlleysAlongTerraceEdges(List<Rect> lots)
        {
            const float wallHalf = 0.5f;
            var kept = new List<Rect>();
            foreach (var a in alleys)
            {
                bool alongX = a.width > a.height;
                float length = alongX ? a.width : a.height;
                bool walled = false;
                foreach (var t in g.terraces)
                {
                    var r = t.rect;
                    // The terrace's edges running the same way as the alley.
                    foreach (float line in alongX ? new[] { r.yMin, r.yMax } : new[] { r.xMin, r.xMax })
                    {
                        float side0 = alongX ? a.yMin : a.xMin, side1 = alongX ? a.yMax : a.xMax;
                        if (line < side0 - wallHalf || line > side1 + wallHalf) continue;
                        float overlap = alongX ? Mathf.Min(a.xMax, r.xMax) - Mathf.Max(a.xMin, r.xMin) : Mathf.Min(a.yMax, r.yMax) - Mathf.Max(a.yMin, r.yMin);
                        if (overlap < length * 0.25f) continue;
                        // Only where the ground changes height across it: that's where a wall goes.
                        var c = alongX ? new Vector2(Mathf.Clamp(a.center.x, r.xMin, r.xMax), line) : new Vector2(line, Mathf.Clamp(a.center.y, r.yMin, r.yMax));
                        var across = alongX ? Vector2.up : Vector2.right;
                        if (Mathf.Abs(g.HeightAt(c + across * 1.2f) - g.HeightAt(c - across * 1.2f)) < 0.3f) continue;
                        walled = true;
                        break;
                    }
                    if (walled) break;
                }
                if (!walled) kept.Add(a);
                else if (!OverStreet(a)) lots.Add(a);
            }
            alleys = kept;
        }

        /// <summary>Whether a rect lies partly over a street (not just touching its side).</summary>
        private static bool OverStreet(Rect r)
        {
            var inner = Grow(r, -0.05f);
            foreach (var s in segments)
                if (inner.Overlaps(s.rect)) return true;
            return false;
        }

        /// <summary>
        /// Alleys cross terrace edges with no stairs (only streets get those), so the stretch below a retaining
        /// wall can be a hole: a player hops the parapet and lands somewhere walled in on every side. Cuts every
        /// alley into level pieces at the terrace edges, works out where a player can walk (level), drop (down)
        /// or not go at all, and fills in each piece they can get into but not back out of, along with strips
        /// beside a retaining wall too narrow to stand in. Leaves the rest in <see cref="alleys"/>; returns the
        /// filled pieces, to be built on.
        /// </summary>
        private static List<Rect> FillAlleyPits()
        {
            var pieces = new List<Rect>();
            var heights = new List<float>();
            foreach (var a in alleys)
                foreach (var t in g.terraces)
                {
                    var r = Rect.MinMaxRect(Mathf.Max(a.xMin, t.rect.xMin), Mathf.Max(a.yMin, t.rect.yMin),
                        Mathf.Min(a.xMax, t.rect.xMax), Mathf.Min(a.yMax, t.rect.yMax));
                    if (r.width < 0.01f || r.height < 0.01f) continue;
                    pieces.Add(r);
                    heights.Add(t.height);
                }
            int n = pieces.Count;
            var filled = new bool[n];

            // A retaining wall takes half a metre off each side of a terrace edge with a step in it.
            for (int i = 0; i < n; i++)
            {
                var r = pieces[i];
                float h = heights[i];
                bool Wall(Vector2 mid, Vector2 normal) => Mathf.Abs(g.GroundAt(mid + normal * 0.3f) - h) >= 0.3f;
                float xMin = r.xMin + (Wall(new Vector2(r.xMin, r.center.y), Vector2.left) ? 0.5f : 0f);
                float xMax = r.xMax - (Wall(new Vector2(r.xMax, r.center.y), Vector2.right) ? 0.5f : 0f);
                float zMin = r.yMin + (Wall(new Vector2(r.center.x, r.yMin), Vector2.down) ? 0.5f : 0f);
                float zMax = r.yMax - (Wall(new Vector2(r.center.x, r.yMax), Vector2.up) ? 0.5f : 0f);
                if (xMax - xMin < PlayerGap || zMax - zMin < PlayerGap) filled[i] = true;
            }

            // Ways between pieces and the streets: level both ways, otherwise only down.
            const int Main = -1;
            var down = new List<int>[n];   // where each piece leads
            var up = new List<int>[n];     // what leads into each piece
            for (int i = 0; i < n; i++)
            {
                down[i] = new List<int>();
                up[i] = new List<int>();
            }
            var fromMain = new List<int>();
            var toMain = new List<int>();
            void Link(int from, int to)
            {
                if (from == Main) fromMain.Add(to);
                else if (to == Main) toMain.Add(from);
                else
                {
                    down[from].Add(to);
                    up[to].Add(from);
                }
            }
            void Join(int a, float ha, int b, float hb)
            {
                if (Mathf.Abs(ha - hb) < 0.3f)
                {
                    Link(a, b);
                    Link(b, a);
                }
                else if (ha > hb) Link(a, b);
                else Link(b, a);
            }
            for (int i = 0; i < n; i++)
            {
                if (filled[i]) continue;
                var r = pieces[i];
                for (int k = i + 1; k < n; k++)
                {
                    if (filled[k]) continue;
                    var o = Grow(r, 0.05f);
                    var other = Grow(pieces[k], 0.05f);
                    if (!o.Overlaps(other)) continue;
                    float shared = Mathf.Max(Mathf.Min(o.xMax, other.xMax) - Mathf.Max(o.xMin, other.xMin),
                        Mathf.Min(o.yMax, other.yMax) - Mathf.Max(o.yMin, other.yMin)) - 0.1f;
                    if (shared >= PlayerGap) Join(i, heights[i], k, heights[k]);
                }
                // Streets and open squares just past each side.
                bool walk = false, dropOut = false, fallIn = false;
                void Probe(Vector2 p)
                {
                    if (OffMap(p) || !(streetIndex.Contains(p) || g.IsOpen(p))) return;
                    float hs = g.GroundAt(p);
                    if (Mathf.Abs(hs - heights[i]) < 0.3f) walk = true;
                    else if (hs < heights[i]) dropOut = true;
                    else fallIn = true;
                }
                for (float x = r.xMin + 0.25f; x < r.xMax; x += 0.5f)
                {
                    Probe(new Vector2(x, r.yMin - 0.3f));
                    Probe(new Vector2(x, r.yMax + 0.3f));
                }
                for (float z = r.yMin + 0.25f; z < r.yMax; z += 0.5f)
                {
                    Probe(new Vector2(r.xMin - 0.3f, z));
                    Probe(new Vector2(r.xMax + 0.3f, z));
                }
                if (walk || dropOut) Link(i, Main);
                if (walk || fallIn) Link(Main, i);
            }

            // Pieces a player can get into from the streets, and pieces they can get back out of.
            var reached = new bool[n];
            var escapes = new bool[n];
            void Spread(List<int> start, List<int>[] next, bool[] seen)
            {
                var queue = new Queue<int>();
                foreach (int s in start)
                    if (!seen[s])
                    {
                        seen[s] = true;
                        queue.Enqueue(s);
                    }
                while (queue.Count > 0)
                    foreach (int k in next[queue.Dequeue()])
                        if (!seen[k])
                        {
                            seen[k] = true;
                            queue.Enqueue(k);
                        }
            }
            Spread(fromMain, down, reached);
            Spread(toMain, up, escapes);

            var kept = new List<Rect>();
            var pits = new List<Rect>();
            for (int i = 0; i < n; i++)
            {
                if (filled[i] || (reached[i] && !escapes[i])) pits.Add(pieces[i]);
                else kept.Add(pieces[i]);
            }
            alleys = kept;
            return pits;
        }

        /// <summary>
        /// Remembers the closed ends of dead-end alleys (a way in from any street), for things left there at
        /// runtime. Uses no randomness, so the rest of the layout is unchanged.
        /// </summary>
        private static void RecordAlleySpots()
        {
            g.alleySpots.Clear();
            foreach (var a in alleys)
            {
                bool alongX = a.width > a.height;
                float length = alongX ? a.width : a.height;
                if (length < 5f) continue;
                var axis = alongX ? Vector2.right : Vector2.up;
                var endA = a.center - axis * (length / 2f + 0.3f);
                var endB = a.center + axis * (length / 2f + 0.3f);
                bool openA = walkIndex.Contains(endA) || g.IsOpen(endA);
                bool openB = walkIndex.Contains(endB) || g.IsOpen(endB);
                // Only true dead ends: open at one end, walled at the other.
                if (openA == openB) continue;
                var outward = openA ? -axis : axis;
                var p = a.center - outward * (length / 2f - 1f);
                if (g.IsOpen(p)) continue;
                g.alleySpots.Add(new PittGreybox.AlleySpot
                {
                    position = new Vector3(p.x, FloorAt(p), p.y),
                    outward = outward,
                    width = alongX ? a.height : a.width,
                });
            }
        }

        /// <summary>Re-runs just the street and alley layout (same seed) to fill in alley spots, without rebuilding the city.</summary>
        public static void RecordAlleySpotsOnly(PittGreybox greybox)
        {
            g = greybox;
            rng = new System.Random(g.seed);
            stairZones = new List<Rect>();
            PlaceDistricts();
            g.sumpSquares.Clear();
            g.sumpShafts.Clear();
            LayOutWalkways();
            PlaceGangSquares();
            PlaceTerraces();
            PlaceSumps();
            PlanLots();
            RecordAlleySpots();
        }

        /// <summary>
        /// From version 8, a piece of small alley left narrower than the alley it was cut from (a terrace edge running
        /// part way down it) is built over, like one with an edge all down its length: it would jog against the rest.
        /// Returns the pieces (taken out of <see cref="alleys"/>). Uses no randomness.
        /// </summary>
        private static List<Rect> FillNarrowedAlleys()
        {
            var kept = new List<Rect>();
            var narrowed = new List<Rect>();
            foreach (var a in alleys)
            {
                bool narrower = false;
                foreach (var (made, alongZ) in alleyRuns)
                {
                    if (!made.Overlaps(a)) continue;
                    narrower = Mathf.Abs((alongZ ? a.width : a.height) - (alongZ ? made.width : made.height)) > 0.01f;
                    break;
                }
                (narrower ? narrowed : kept).Add(a);
            }
            alleys = kept;
            return narrowed;
        }

        /// <summary>The small alleys as <see cref="Split"/> made them, with the way each runs, for lining up the next (version 8).</summary>
        private static List<(Rect rect, bool alongZ)> alleyRuns;

        /// <summary>
        /// From version 8, a new small alley across <paramref name="r"/> that would carry on from one already made (its
        /// end on r's edge, or just across an alley crossing between them) takes that alley's exact line and width. If
        /// that leaves too little lot either side, no alley is made here and the other dead-ends instead: two alleys
        /// meeting slightly out of line leave a jog too tight to pass. Uses no randomness.
        /// </summary>
        private static void LineUpAlley(Rect r, bool alongX, float min, ref float gap, ref float cut)
        {
            // Splitting along X makes an alley running along Z, across r from its bottom edge to its top.
            float lo = alongX ? r.xMin : r.yMin, hi = alongX ? r.xMax : r.yMax;
            float start = alongX ? r.yMin : r.xMin, end = alongX ? r.yMax : r.xMax;
            float reach = g.alleyWidth.y + 0.1f;
            bool found = false;
            float lineLo = 0f, lineHi = 0f;
            foreach (var (a, alongZ) in alleyRuns)
            {
                if (alongZ != alongX) continue;
                float aLo = alongX ? a.xMin : a.yMin, aHi = alongX ? a.xMax : a.yMax;
                float aStart = alongX ? a.yMin : a.xMin, aEnd = alongX ? a.yMax : a.xMax;
                bool meets = (aEnd <= start + 0.01f && aEnd >= start - reach) || (aStart >= end - 0.01f && aStart <= end + reach);
                if (!meets || aHi <= lo || aLo >= hi) continue;
                // Alleys at both ends out of line with each other: whichever this followed, the other would jog.
                if (found && (Mathf.Abs(aLo - lineLo) > 0.01f || Mathf.Abs(aHi - lineHi) > 0.01f))
                {
                    gap = 0f;
                    return;
                }
                found = true;
                lineLo = aLo;
                lineHi = aHi;
            }
            if (!found) return;
            if (lineLo - lo >= min && hi - lineHi >= min)
            {
                gap = lineHi - lineLo;
                cut = lineLo - lo;
            }
            else gap = 0f;
        }

        private static void Split(Rect r, List<Rect> lots)
        {
            float max = g.lotSize.y, min = g.lotSize.x;
            bool alongX = r.width >= r.height;
            float length = alongX ? r.width : r.height;
            if (length <= max || length < min * 2f)
            {
                lots.Add(r);
                return;
            }

            float gap = Chance(g.alleyChance) ? R(g.alleyWidth) : 0f;
            if (length - gap < min * 2f) gap = 0f;
            float cut = R(0.35f, 0.65f) * (length - gap);
            if (gap > 0f && Since(8)) LineUpAlley(r, alongX, min, ref gap, ref cut);

            if (alongX)
            {
                float x = r.xMin + cut;
                Split(Rect.MinMaxRect(r.xMin, r.yMin, x, r.yMax), lots);
                Split(Rect.MinMaxRect(x + gap, r.yMin, r.xMax, r.yMax), lots);
                if (gap > 0f) AddAlley(Rect.MinMaxRect(x, r.yMin, x + gap, r.yMax), true);
            }
            else
            {
                float z = r.yMin + cut;
                Split(Rect.MinMaxRect(r.xMin, r.yMin, r.xMax, z), lots);
                Split(Rect.MinMaxRect(r.xMin, z + gap, r.xMax, r.yMax), lots);
                if (gap > 0f) AddAlley(Rect.MinMaxRect(r.xMin, z, r.xMax, z + gap), false);
            }
        }

        private static void AddAlley(Rect a, bool alongZ)
        {
            alleys.Add(a);
            alleyRuns?.Add((a, alongZ));
        }

        private static bool TouchesOpen(Rect r)
        {
            for (int i = 0; i <= 2; i++)
            for (int j = 0; j <= 2; j++)
                if (g.IsOpen(new Vector2(Mathf.Lerp(r.xMin, r.xMax, i / 2f), Mathf.Lerp(r.yMin, r.yMax, j / 2f)))) return true;
            return false;
        }

        private static void GroundRange(Rect r, out float min, out float max)
        {
            min = float.MaxValue;
            max = float.MinValue;
            for (int i = 0; i <= 2; i++)
            for (int j = 0; j <= 2; j++)
            {
                float h = g.HeightAt(new Vector2(Mathf.Lerp(r.xMin, r.xMax, i / 2f), Mathf.Lerp(r.yMin, r.yMax, j / 2f)));
                min = Mathf.Min(min, h);
                max = Mathf.Max(max, h);
            }
        }

        /// <summary>Hollows out a few lots that back onto alleys and knocks a hole through the alley wall.</summary>
        private static void ChooseHiddenSpots()
        {
            var candidates = new List<Plan>();
            foreach (var p in plans)
            {
                if (p.footprint.width < 5f || p.footprint.height < 5f) continue;
                if (p.ground - p.groundMin > 0.5f) continue; // keep the floor level
                foreach (var normal in Faces)
                {
                    FaceInfo(p.footprint, normal, out var center, out _, out _);
                    var probe = center + normal * 0.4f;
                    if (alleyIndex.Contains(probe) && !streetIndex.Contains(probe))
                    {
                        p.holeNormal = normal;
                        candidates.Add(p);
                        break;
                    }
                }
            }
            for (int i = 0; i < g.hiddenSpots && candidates.Count > 0; i++)
            {
                int pick = rng.Next(candidates.Count);
                candidates[pick].hollow = true;
                candidates.RemoveAt(pick);
            }
        }

        // ---------------------------------------------------------------- terrain & pillar

        /// <summary>Below the lowest ground and the pit floor.</summary>
        private static float TerrainBottom => -g.pitDepth + Mathf.Min(0f, g.terraceHeights.x) - 4f;

        private static void BuildTerrain(Transform root)
        {
            float size = g.citySize;
            var data = new TerrainData { heightmapResolution = 513, alphamapResolution = 256 };
            int res = data.heightmapResolution;
            var heights = new float[res, res];
            float maxY = 1f;
            for (int zi = 0; zi < res; zi++)
            for (int xi = 0; xi < res; xi++)
            {
                var p = new Vector2(-size / 2f + xi / (res - 1f) * size, -size / 2f + zi / (res - 1f) * size);
                heights[zi, xi] = g.HeightAt(p);
                maxY = Mathf.Max(maxY, heights[zi, xi]);
            }
            float minY = TerrainBottom;
            float range = maxY + 3f - minY;
            for (int zi = 0; zi < res; zi++)
            for (int xi = 0; xi < res; xi++)
                heights[zi, xi] = (heights[zi, xi] - minY) / range;
            data.size = new Vector3(size, range, size);
            data.SetHeights(0, 0, heights);

            // Holes over the sump stairwells (slightly oversized; the rims hide the edges).
            if (g.sumpShafts.Count > 0)
            {
                int hres = data.holesResolution;
                var solid = new bool[hres, hres];
                float cell = size / hres;
                for (int zi = 0; zi < hres; zi++)
                for (int xi = 0; xi < hres; xi++)
                {
                    var p = new Vector2(-size / 2f + (xi + 0.5f) * cell, -size / 2f + (zi + 0.5f) * cell);
                    bool hole = false;
                    foreach (var sh in g.sumpShafts) hole |= Grow(sh, 0.25f).Contains(p);
                    solid[zi, xi] = !hole;
                }
                data.SetHoles(0, 0, solid);
            }

            data.terrainLayers = new[] { g.groundLayer, g.dirtLayer };
            int ares = data.alphamapResolution;
            var alpha = new float[ares, ares, 2];
            for (int zi = 0; zi < ares; zi++)
            for (int xi = 0; xi < ares; xi++)
            {
                var p = new Vector2(-size / 2f + (xi + 0.5f) / ares * size, -size / 2f + (zi + 0.5f) / ares * size);
                // Dirt down in the pit; the streets above stay paved however high or low they run.
                float dirt = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-1f, -3f, g.HeightAt(p) - g.GroundAt(p)));
                alpha[zi, xi, 0] = 1f - dirt;
                alpha[zi, xi, 1] = dirt;
            }
            data.SetAlphamaps(0, 0, alpha);

            var go = Terrain.CreateTerrainGameObject(data);
            go.name = "Terrain";
            go.transform.SetParent(root, false);
            go.transform.position = new Vector3(-size / 2f, minY, -size / 2f);
            var terrain = go.GetComponent<Terrain>();
            terrain.materialTemplate = g.terrainMaterial;
            // Instanced terrain ignores its baked lightmap in this URP setup and renders black.
            terrain.drawInstanced = false;
        }

        private static void BuildPillar(Transform root)
        {
            var parent = Group(root, "Pillar");
            float h = g.HalfSize;
            float top = TerrainBottom;

            // The pillar body sits under the terrain; thin skirts close the gap at the cliff edge. Each terrace that
            // reaches the edge gets its own, up to its own ground and no higher, so every street runs straight out to
            // the drop. The faces are sheer all the way down: nothing to catch a fall.
            Box("PillarBody", parent, Rect.MinMaxRect(-h - 1f, -h - 1f, h + 1f, h + 1f), -g.pillarDepth, top, g.concreteDark);
            foreach (var t in g.terraces)
            {
                var r = t.rect;
                float z0 = Mathf.Max(r.yMin, -h - 1f), z1 = Mathf.Min(r.yMax, h + 1f);
                float x0 = Mathf.Max(r.xMin, -h), x1 = Mathf.Min(r.xMax, h);
                if (r.xMin < -h && z1 > z0) Box("Skirt", parent, Rect.MinMaxRect(-h - 1f, z0, -h, z1), top, t.height, g.concreteDark);
                if (r.xMax > h && z1 > z0) Box("Skirt", parent, Rect.MinMaxRect(h, z0, h + 1f, z1), top, t.height, g.concreteDark);
                if (r.yMin < -h && x1 > x0) Box("Skirt", parent, Rect.MinMaxRect(x0, -h - 1f, x1, -h), top, t.height, g.concreteDark);
                if (r.yMax > h && x1 > x0) Box("Skirt", parent, Rect.MinMaxRect(x0, h, x1, h + 1f), top, t.height, g.concreteDark);
            }
        }

        // ---------------------------------------------------------------- buildings

        private static readonly Vector2[] Faces = { Vector2.left, Vector2.right, Vector2.down, Vector2.up };

        private static void FaceInfo(Rect fp, Vector2 normal, out Vector2 center, out Vector2 along, out float length)
        {
            along = new Vector2(-normal.y, normal.x);
            if (normal.x != 0f)
            {
                center = new Vector2(normal.x < 0f ? fp.xMin : fp.xMax, fp.center.y);
                length = fp.height;
            }
            else
            {
                center = new Vector2(fp.center.x, normal.y < 0f ? fp.yMin : fp.yMax);
                length = fp.width;
            }
        }

        /// <summary>A random point on a face that opens onto a walkway or alley, if any.</summary>
        private static bool ExposedPoint(Rect fp, Vector2 normal, float margin, out Vector2 point, RectIndex index = null)
        {
            FaceInfo(fp, normal, out var center, out var along, out float length);
            point = default;
            float slack = length / 2f - margin;
            if (slack < 0f) return false;
            point = center + along * R(-slack, slack);
            var probe = point + normal * 0.4f;
            if (OffMap(probe)) return false;
            return index != null ? index.Contains(probe) : Walkable(probe);
        }

        private static bool NearStairs(Vector2 p, float margin)
        {
            foreach (var z in stairZones)
                if (Grow(z, margin).Contains(p)) return true;
            return false;
        }

        private static bool NearCatwalk(Vector2 p, float margin)
        {
            foreach (var z in catwalkZones)
                if (Grow(z, margin).Contains(p)) return true;
            return false;
        }

        private static void BuildBuildings(Transform root)
        {
            var parent = Group(root, "Buildings");
            var hidden = Group(root, "HiddenSpots");
            var mats = new Material[plans.Count];
            for (int i = 0; i < plans.Count; i++)
            {
                float roll = R(0f, 1f);
                mats[i] = roll < 0.4f ? g.brick : roll < 0.8f ? g.concrete : g.concreteDark;
            }
            // From version 5 a filled-in alley takes the walls of the building it sits against, so it reads as
            // part of that building rather than a patch of different material wedged between two.
            if (Since(5)) MatchFillerMaterials(mats);
            for (int i = 0; i < plans.Count; i++)
            {
                var p = plans[i];
                if (p.hollow) BuildHollow(parent, hidden, p, mats[i]);
                // With sump tunnels, bottoms stop just under the street so the tunnels can run beneath.
                else Box("Building", parent, p.footprint, p.groundMin - BuildingSink, p.top, mats[i], ground: p.groundMin);
            }
        }

        /// <summary>Gives each filler plan the material of the non-filler building it shares the longest wall with.</summary>
        private static void MatchFillerMaterials(Material[] mats)
        {
            var index = new Dictionary<Vector2Int, List<int>>();
            const float cell = 10f;
            for (int i = 0; i < plans.Count; i++)
            {
                if (plans[i].filler) continue;
                var r = plans[i].footprint;
                for (int x = Mathf.FloorToInt(r.xMin / cell); x <= Mathf.FloorToInt(r.xMax / cell); x++)
                for (int z = Mathf.FloorToInt(r.yMin / cell); z <= Mathf.FloorToInt(r.yMax / cell); z++)
                {
                    var key = new Vector2Int(x, z);
                    if (!index.TryGetValue(key, out var list)) index[key] = list = new List<int>();
                    list.Add(i);
                }
            }
            for (int i = 0; i < plans.Count; i++)
            {
                if (!plans[i].filler) continue;
                var f = Grow(plans[i].footprint, 0.1f);
                float best = 0f;
                for (int x = Mathf.FloorToInt(f.xMin / cell); x <= Mathf.FloorToInt(f.xMax / cell); x++)
                for (int z = Mathf.FloorToInt(f.yMin / cell); z <= Mathf.FloorToInt(f.yMax / cell); z++)
                {
                    if (!index.TryGetValue(new Vector2Int(x, z), out var list)) continue;
                    foreach (int j in list)
                    {
                        var o = plans[j].footprint;
                        if (!f.Overlaps(o)) continue;
                        // Length of the shared wall: the overlap along the longer side of the touching strip.
                        float w = Mathf.Min(f.xMax, o.xMax) - Mathf.Max(f.xMin, o.xMin);
                        float h = Mathf.Min(f.yMax, o.yMax) - Mathf.Max(f.yMin, o.yMin);
                        float shared = Mathf.Max(w, h);
                        if (shared <= best) continue;
                        best = shared;
                        mats[i] = mats[j];
                    }
                }
            }
        }

        // ---------------------------------------------------------------- properties

        /// <summary>
        /// One property per district outside the central one (where the player's own is): a door on the street face of one of the roomier level
        /// lots. The building itself stays solid; the door leads to the property's own room elsewhere.
        /// </summary>
        private static void ChooseProperties()
        {
            for (int d = 1; d < g.districts.Count; d++)
            {
                var candidates = new List<Plan>();
                foreach (var p in plans)
                {
                    if (p.hollow) continue;
                    var fp = p.footprint;
                    if (fp.width < g.propertyMinLot || fp.height < g.propertyMinLot || g.DistrictIndexAt(fp.center) != d) continue;
                    if (Vector2.Distance(fp.center, g.pitCenter) < g.pitRadius + 10f) continue;
                    if (TryStreetDoor(p, false) && InPropertyArea(p) && Accessible(p)) candidates.Add(p);
                }
                if (candidates.Count == 0)
                {
                    Debug.LogWarning($"PittGreybox: no lot for a property door in {g.districts[d].name}.");
                    continue;
                }
                candidates.Sort((a, b) => (b.footprint.width * b.footprint.height).CompareTo(a.footprint.width * a.footprint.height));
                candidates[rng.Next(Mathf.Min(3, candidates.Count))].property = true;
            }
            ChooseBusinesses();
        }

        /// <summary>
        /// The businesses for sale: a morgue in Mildew and a metalworks in the Sodium Yards (or anywhere outside the
        /// central district if those districts aren't there), each behind its own street door on a roomy lot.
        /// </summary>
        private static void ChooseBusinesses()
        {
            foreach (var (kind, home) in new[] { (Production.BusinessKind.Morgue, "Mildew"), (Production.BusinessKind.Metalworks, "Sodium Yards") })
            {
                int district = g.districts.FindIndex(d => d.name == home);
                var candidates = new List<Plan>();
                foreach (var p in plans)
                {
                    if (p.hollow || p.property) continue;
                    var fp = p.footprint;
                    int d = g.DistrictIndexAt(fp.center);
                    if (fp.width < g.propertyMinLot || fp.height < g.propertyMinLot || d == 0 || (district >= 0 && d != district)) continue;
                    if (Vector2.Distance(fp.center, g.pitCenter) < g.pitRadius + 10f) continue;
                    if (TryStreetDoor(p, false) && InPropertyArea(p) && Accessible(p)) candidates.Add(p);
                }
                if (candidates.Count == 0)
                {
                    Debug.LogWarning($"PittGreybox: no lot for the {kind}.");
                    continue;
                }
                candidates.Sort((a, b) => (b.footprint.width * b.footprint.height).CompareTo(a.footprint.width * a.footprint.height));
                var pick = candidates[rng.Next(Mathf.Min(3, candidates.Count))];
                pick.property = true;
                pick.business = kind;
            }
        }

        /// <summary>
        /// The lot, and the street in front of its door, lie in the middle of the map (<see cref="PittGreybox.propertyArea"/>),
        /// away from the edge ledges. Call after <see cref="TryStreetDoor"/> has set the door.
        /// </summary>
        private static bool InPropertyArea(Plan p)
        {
            float limit = g.HalfSize * g.propertyArea;
            var fp = p.footprint;
            var step = p.doorPoint + p.doorNormal * 3f;
            return fp.xMin >= -limit && fp.xMax <= limit && fp.yMin >= -limit && fp.yMax <= limit &&
                   Mathf.Abs(step.x) <= limit && Mathf.Abs(step.y) <= limit;
        }

        /// <summary>Finds a face of the lot opening onto a street (a thoroughfare if asked) with level ground outside, for a door.</summary>
        private static bool TryStreetDoor(Plan p, bool thoroughfareOnly, float minLength = 3f)
        {
            foreach (var normal in Faces)
            {
                FaceInfo(p.footprint, normal, out var center, out _, out float length);
                if (length < minLength) continue;
                var outside = center + normal * 0.4f;
                bool opens = thoroughfareOnly ? OnThoroughfare(outside) && OnThoroughfare(center + normal * 3f) : streetIndex.Contains(outside);
                if (!opens || !LevelOutside(center, normal, out float ground)) continue;
                p.doorNormal = normal;
                p.doorPoint = center;
                p.doorGround = ground;
                return true;
            }
            return false;
        }

        /// <summary>
        /// The street at the foot of a wall, if it's level enough there for a door: across the doorway and a
        /// step out, and a few metres around. Buildings stand on sloping ground, so each door takes the height of the street in front of it.
        /// </summary>
        private static bool LevelOutside(Vector2 wallPoint, Vector2 normal, out float ground)
        {
            var along = new Vector2(normal.y, -normal.x);
            ground = g.HeightAt(wallPoint + normal * 0.3f);
            foreach (var probe in new[] { wallPoint + normal * 1.5f, wallPoint + normal * 0.3f + along * 0.6f, wallPoint + normal * 0.3f - along * 0.6f })
                if (Mathf.Abs(g.HeightAt(probe) - ground) > 0.25f) return false;
            // Clear of terrace edges: a door close to a retaining wall can open onto a pocket with no way out.
            GroundRange(Square(wallPoint + normal * 2.5f, 3.5f), out float low, out float high);
            if (high - low >= 0.3f) return false;
            if (Since(6))
            {
                // From version 6 the door stands on the kerb (or alley floor) in front of it: kerb across the whole
                // doorway, or none, so a frame never hangs off the end of one.
                var foot = wallPoint + normal * 0.1f;
                bool kerb = OnKerb(foot);
                if (OnKerb(foot + along * 0.75f) != kerb || OnKerb(foot - along * 0.75f) != kerb) return false;
                ground = FloorAt(foot);
            }
            return true;
        }

        /// <summary>Height of the street at the foot of a wall, for things fixed to it at street level.</summary>
        private static float FootAt(Vector2 wallPoint, Vector2 normal) => g.HeightAt(wallPoint + normal * 0.6f);

        private static bool OnThoroughfare(Vector2 p)
        {
            foreach (var seg in segments)
                if (seg.thoroughfare && seg.rect.Contains(p)) return true;
            return false;
        }

        /// <summary>
        /// The player's own property: a door onto a thoroughfare in the central district, as near its centre as
        /// there is one, on a wide enough face. The player starts in the street outside.
        /// </summary>
        private static Plan ChooseHome()
        {
            var centre = g.districts.Count > 0 ? g.districts[0].center : Vector2.zero;
            Plan best = null;
            float bestDistance = float.MaxValue;
            foreach (var p in plans)
            {
                if (p.hollow || p.property || g.DistrictIndexAt(p.footprint.center) != 0) continue;
                if (!TryStreetDoor(p, true, 5f) || !InPropertyArea(p) || !Accessible(p)) continue;
                float distance = Vector2.Distance(p.doorPoint, centre);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = p;
                }
            }
            return best;
        }

        /// <summary>Puts the player and their respawn point in the street outside their own door, facing it.</summary>
        private static void PlaceStart(Plan home)
        {
            var spot = home.doorPoint + home.doorNormal * 3f;
            g.startPoint = spot;
            var position = new Vector3(spot.x, (Since(6) ? FloorAt(spot) : home.doorGround) + 0.05f, spot.y);
            var facing = Quaternion.LookRotation(new Vector3(-home.doorNormal.x, 0f, -home.doorNormal.y));
            g.startPosition = position;
            g.startYaw = facing.eulerAngles.y;
            var scene = g.gameObject.scene;
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.name != "PlayerSpawn" && !root.CompareTag("Player")) continue;
                root.transform.SetPositionAndRotation(position, facing);
            }
        }

        /// <summary>Doors on the city's walls, and each property's own room under its district.</summary>
        private static void BuildProperties(Transform root)
        {
            var parent = Group(root, "Properties");
            interiorsBuilt = 0;
            var home = ChooseHome();
            if (home != null)
            {
                home.property = true;
                PlaceStart(home);
            }
            else Debug.LogWarning("PittGreybox: no wall on a central thoroughfare for the player's property door.");

            foreach (var p in plans)
            {
                if (!p.property) continue;
                bool isHome = p == home;
                int d = g.DistrictIndexAt(p.footprint.center);
                string district = g.districts[d].name;
                string id = isHome ? "Home" : district.Replace(" ", "");
                string name = isHome ? "Home Lockup" : $"{district} Lockup";
                int price = isHome ? 0 : Mathf.RoundToInt(rng.Next(g.propertyPrice.x, g.propertyPrice.y + 1) / 50f) * 50;
                var size = isHome ? g.homeSize : new Vector2(R(7.5f, 9.5f), R(6.5f, 8.5f));
                if (p.business is { } kind)
                {
                    id = kind.ToString();
                    name = $"{district} {(kind == Production.BusinessKind.Morgue ? "Morgue" : "Metalworks")}";
                    price = kind == Production.BusinessKind.Morgue ? g.morguePrice : g.metalworksPrice;
                    size = new Vector2(10f, 8f);
                }

                var property = BuildInterior(parent, id, name, district, price, isHome, size);
                if (p.business is { } works) BuildBusiness(property, works);
                BuildPropertyDoor(parent, property, p.doorPoint, p.doorNormal, p.doorGround);
            }
        }

        private static int interiorsBuilt;

        /// <summary>
        /// A closed room far out in the dark beyond the pillar, where fog hides it: walls, a concrete floor, a
        /// ceiling light and an exit door. The rooms stand in a row, well apart. The district atmosphere uses
        /// the property's own district while the player is inside.
        /// </summary>
        private static Properties.Property BuildInterior(Transform parent, string id, string displayName, string district, int price,
            bool owned, Vector2 insideSize)
        {
            const float wall = 0.35f;
            float floor = 0f, height = g.propertyHeight;
            var center = new Vector2(g.HalfSize + g.interiorDistance, interiorsBuilt++ * 40f);
            var inner = new Rect(center - insideSize / 2f, insideSize);
            var outer = Grow(inner, wall);

            var room = new GameObject("Interior_" + id).transform;
            room.SetParent(parent, false);
            Box("Floor", room, outer, floor - 0.4f, floor, g.concrete);
            Box("Ceiling", room, outer, floor + height, floor + height + 0.3f, g.concreteDark);
            Box("Wall", room, Rect.MinMaxRect(inner.xMin, outer.yMin, inner.xMax, inner.yMin), floor, floor + height, g.brick);
            Box("Wall", room, Rect.MinMaxRect(inner.xMin, inner.yMax, inner.xMax, outer.yMax), floor, floor + height, g.brick);
            // Out here there's no baked light to fill in, so the lamp does the work.
            HangFrom(AddLight(room, new Vector3(inner.center.x, floor + height - 0.35f, inner.center.y), new Color(1f, 0.85f, 0.65f), 9f, 13f, fixture: true), floor + height);

            // Side rooms east and west, each behind a bricked-up doorway in the side wall until the
            // Watchers allow it to be opened up.
            var annexRects = new List<Rect>();
            var annexRoots = new List<GameObject>();
            var seals = new List<GameObject>();
            foreach (float side in new[] { 1f, -1f })
            {
                float wallIn = side > 0f ? inner.xMax : inner.xMin;
                float wallOut = side > 0f ? outer.xMax : outer.xMin;
                float lo = Mathf.Min(wallIn, wallOut), hi = Mathf.Max(wallIn, wallOut);
                const float doorHalf = 0.9f;
                float mid = inner.center.y;
                Box("Wall", room, Rect.MinMaxRect(lo, outer.yMin, hi, mid - doorHalf), floor, floor + height, g.brick);
                Box("Wall", room, Rect.MinMaxRect(lo, mid + doorHalf, hi, outer.yMax), floor, floor + height, g.brick);
                Box("Lintel", room, Rect.MinMaxRect(lo, mid - doorHalf, hi, mid + doorHalf), floor + 2.4f, floor + height, g.brick, ground: floor);
                var seal = Box("Seal", room, Rect.MinMaxRect(lo, mid - doorHalf, hi, mid + doorHalf), floor, floor + 2.4f, g.concreteDark);
                seals.Add(seal);

                float annexWidth = R(6f, 7.5f);
                var annex = side > 0f
                    ? Rect.MinMaxRect(outer.xMax, inner.yMin, outer.xMax + annexWidth, inner.yMax)
                    : Rect.MinMaxRect(outer.xMin - annexWidth, inner.yMin, outer.xMin, inner.yMax);
                var annexOuter = Grow(annex, wall);
                var annexRoot = new GameObject(side > 0f ? "AnnexEast" : "AnnexWest").transform;
                annexRoot.SetParent(room, false);
                Box("Floor", annexRoot, annexOuter, floor - 0.4f, floor, g.concrete);
                Box("Ceiling", annexRoot, annexOuter, floor + height, floor + height + 0.3f, g.concreteDark);
                Box("Wall", annexRoot, side > 0f ? Rect.MinMaxRect(annex.xMax, annexOuter.yMin, annexOuter.xMax, annexOuter.yMax)
                    : Rect.MinMaxRect(annexOuter.xMin, annexOuter.yMin, annex.xMin, annexOuter.yMax), floor, floor + height, g.brick);
                Box("Wall", annexRoot, Rect.MinMaxRect(annex.xMin, annexOuter.yMin, annex.xMax, annex.yMin), floor, floor + height, g.brick);
                Box("Wall", annexRoot, Rect.MinMaxRect(annex.xMin, annex.yMax, annex.xMax, annexOuter.yMax), floor, floor + height, g.brick);
                HangFrom(AddLight(annexRoot, new Vector3(annex.center.x, floor + height - 0.35f, annex.center.y), new Color(1f, 0.85f, 0.65f), 7f, 11f, fixture: true), floor + height);
                annexRects.Add(annex);
                annexRoots.Add(annexRoot.gameObject);
            }

            var property = room.gameObject.AddComponent<Properties.Property>();
            property.Setup(id, displayName, district, price, owned, inner, floor, height, null);
            property.SetAnnexes(annexRects.ToArray(), annexRoots.ToArray(), seals.ToArray());

            // The way back out: a door in the middle of the south wall, facing in.
            var exitPoint = new Vector2(inner.center.x, inner.yMin);
            var exit = BuildDoorLeaf(room, exitPoint, Vector2.up, floor, "ExitDoor");
            exit.AddComponent<Properties.PropertyDoor>().SetupExit(property);
            return property;
        }

        /// <summary>
        /// The works in a business's room, against the back wall: a morgue's steel slab in front of a bank of cold
        /// lockers under a blue-white light, or a metalworks' furnace with a glowing mouth and a hopper beside it.
        /// </summary>
        private static void BuildBusiness(Properties.Property property, Production.BusinessKind kind)
        {
            var inner = property.Interior;
            float floor = property.Centre.y;
            var works = new GameObject(kind.ToString()).transform;
            works.SetParent(property.transform, false);
            float back = inner.yMax;
            float cx = inner.center.x;
            ItemDefinition Item(string key) => (Core.SaveRegistry.Instance != null ? Core.SaveRegistry.Instance : g.itemRegistry)?.Find<ItemDefinition>($"Item_{key}");

            var business = works.gameObject.AddComponent<Production.Business>();
            if (kind == Production.BusinessKind.Morgue)
            {
                // Lockers along the back wall, the slab out in front of them.
                Box("Lockers", works, Rect.MinMaxRect(cx - 3.2f, back - 0.9f, cx + 3.2f, back), floor, floor + 2.4f, g.metal);
                for (int i = 0; i < 6; i++)
                for (int j = 0; j < 2; j++)
                {
                    float x = cx - 2.65f + i * 1.06f;
                    Box("LockerDoor", works, Rect.MinMaxRect(x - 0.45f, back - 0.95f, x + 0.45f, back - 0.9f), floor + 0.2f + j * 1.1f, floor + 1.1f + j * 1.1f, g.concreteDark);
                }
                Box("SlabLegs", works, Rect.MinMaxRect(cx - 0.9f, back - 2.7f, cx + 0.9f, back - 2.1f), floor, floor + 0.85f, g.metal);
                Box("Slab", works, Rect.MinMaxRect(cx - 1.15f, back - 2.85f, cx + 1.15f, back - 1.95f), floor + 0.85f, floor + 0.95f, g.metal);
                Box("Sheet", works, Rect.MinMaxRect(cx - 0.95f, back - 2.75f, cx + 0.75f, back - 2.05f), floor + 0.95f, floor + 1.15f, g.trash);
                HangFrom(AddLight(works, new Vector3(cx, floor + 2.9f, back - 2.4f), new Color(0.75f, 0.9f, 1f), 6f, 8f, fixture: true, flicker: true), floor + g.propertyHeight);
                business.Setup(kind, property, Item("MysteryMeat"), null, new[] { Item("Grey"), Item("PipeRounds") }, 50, new[] { 1500, 3000 });
            }
            else
            {
                // A squat furnace against the back wall, its mouth glowing, with the hopper to one side.
                Box("Furnace", works, Rect.MinMaxRect(cx - 1.4f, back - 1.6f, cx + 1.4f, back), floor, floor + 2.2f, g.concreteDark);
                Box("Flue", works, Rect.MinMaxRect(cx - 0.4f, back - 0.9f, cx + 0.4f, back - 0.1f), floor + 2.2f, floor + g.propertyHeight, g.rust);
                var mouth = Box("Mouth", works, Rect.MinMaxRect(cx - 0.6f, back - 1.65f, cx + 0.6f, back - 1.58f), floor + 0.6f, floor + 1.3f, Glow(new Color(1f, 0.45f, 0.12f), 6f));
                NoShadows(mouth);
                Box("Hopper", works, Rect.MinMaxRect(cx + 1.6f, back - 1.4f, cx + 2.8f, back - 0.2f), floor + 0.9f, floor + 1.9f, g.rust);
                Box("HopperLegs", works, Rect.MinMaxRect(cx + 1.8f, back - 1.2f, cx + 2.6f, back - 0.4f), floor, floor + 0.9f, g.metal);
                Box("Bench", works, Rect.MinMaxRect(cx - 2.9f, back - 1.1f, cx - 1.7f, back - 0.2f), floor, floor + 0.9f, g.metal);
                AddLight(works, new Vector3(cx, floor + 1f, back - 2.3f), new Color(1f, 0.55f, 0.2f), 5f, 7f, fixture: false, flicker: true);
                business.Setup(kind, property, Item("ScrapMetal"), Item("ScrapMetal"), null, 60, new[] { 1800, 3600 });
            }
        }

        /// <summary>A framed door on a building's face that leads into a property, with a sign over it and a lamp.</summary>
        private static void BuildPropertyDoor(Transform parent, Properties.Property property, Vector2 point, Vector2 normal, float ground)
        {
            var door = new GameObject("PropertyDoor_" + property.Id).transform;
            door.SetParent(parent, false);
            var leaf = BuildDoorLeaf(door, point, normal, ground, "Door");

            var signCentre = point + normal * 0.1f;
            var signRect = normal.x != 0f
                ? Rect.MinMaxRect(signCentre.x - 0.06f, signCentre.y - 0.8f, signCentre.x + 0.06f, signCentre.y + 0.8f)
                : Rect.MinMaxRect(signCentre.x - 0.8f, signCentre.y - 0.06f, signCentre.x + 0.8f, signCentre.y + 0.06f);
            var sign = Box("Sign", door, signRect, ground + 2.55f, ground + 2.8f, Glow(Color.white, 3f));
            sign.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var lamp = point + normal * 0.5f;
            BracketFrom(AddLight(door, new Vector3(lamp.x, ground + 3.1f, lamp.y), new Color(1f, 0.78f, 0.5f), 4f, 7f, fixture: true), point, normal);

            property.SetSign(sign.GetComponent<Renderer>());
            leaf.AddComponent<Properties.PropertyDoor>().SetupEntrance(property, new Vector3(point.x, ground, point.y), normal);
        }

        /// <summary>A steel door in a concrete frame, flat on a wall face. Returns the door leaf (with a collider to use it by).</summary>
        private static GameObject BuildDoorLeaf(Transform parent, Vector2 point, Vector2 normal, float ground, string name)
        {
            var along = new Vector2(normal.y, -normal.x);
            foreach (float side in new[] { -1f, 1f })
                Box("Jamb", parent, Square(point + along * side * 0.68f + normal * 0.08f, 0.1f), ground, ground + 2.35f, g.concreteDark);
            var lc = point + normal * 0.08f;
            Box("Lintel", parent, normal.x != 0f
                ? Rect.MinMaxRect(lc.x - 0.1f, lc.y - 0.78f, lc.x + 0.1f, lc.y + 0.78f)
                : Rect.MinMaxRect(lc.x - 0.78f, lc.y - 0.1f, lc.x + 0.78f, lc.y + 0.1f), ground + 2.2f, ground + 2.45f, g.concreteDark);
            var leaf = FaceBox(name, parent, point, normal, ground + 1.1f, new Vector3(1.16f, 2.2f, 0.06f), g.metal);
            leaf.AddComponent<BoxCollider>();
            // A handle, so it reads as a door.
            FaceBox("Handle", parent, point + along * 0.4f + normal * 0.06f, normal, ground + 1.05f, new Vector3(0.05f, 0.2f, 0.06f), g.rust);
            return leaf;
        }

        private static void BuildHollow(Transform parent, Transform hiddenParent, Plan p, Material mat)
        {
            const float wall = 0.35f;
            float roomHeight = R(3.2f, 3.8f);
            var fp = p.footprint;
            float bottom = p.groundMin - BuildingSink;
            float ceiling = p.ground + roomHeight;
            var room = new GameObject("HiddenRoomBuilding").transform;
            room.SetParent(parent, false);

            var walls = new Dictionary<Vector2, Rect>
            {
                [Vector2.left] = Rect.MinMaxRect(fp.xMin, fp.yMin, fp.xMin + wall, fp.yMax),
                [Vector2.right] = Rect.MinMaxRect(fp.xMax - wall, fp.yMin, fp.xMax, fp.yMax),
                [Vector2.down] = Rect.MinMaxRect(fp.xMin + wall, fp.yMin, fp.xMax - wall, fp.yMin + wall),
                [Vector2.up] = Rect.MinMaxRect(fp.xMin + wall, fp.yMax - wall, fp.xMax - wall, fp.yMax),
            };

            foreach (var kv in walls)
            {
                if (kv.Key != p.holeNormal)
                {
                    Box("Wall", room, kv.Value, bottom, ceiling, mat, ground: p.groundMin);
                    continue;
                }
                // Split the alley-facing wall around a squeeze-through hole.
                var r = kv.Value;
                const float holeWidth = 1.2f, holeHeight = 2.1f;
                bool alongX = kv.Key.y != 0f;
                float length = alongX ? r.width : r.height;
                float start = (alongX ? r.xMin : r.yMin) + R(0.6f, length - holeWidth - 0.6f);
                float end = start + holeWidth;
                Rect a, b, mid;
                if (alongX)
                {
                    a = Rect.MinMaxRect(r.xMin, r.yMin, start, r.yMax);
                    b = Rect.MinMaxRect(end, r.yMin, r.xMax, r.yMax);
                    mid = Rect.MinMaxRect(start, r.yMin, end, r.yMax);
                }
                else
                {
                    a = Rect.MinMaxRect(r.xMin, r.yMin, r.xMax, start);
                    b = Rect.MinMaxRect(r.xMin, end, r.xMax, r.yMax);
                    mid = Rect.MinMaxRect(r.xMin, start, r.xMax, end);
                }
                Box("Wall", room, a, bottom, ceiling, mat, ground: p.groundMin);
                Box("Wall", room, b, bottom, ceiling, mat, ground: p.groundMin);
                Box("Lintel", room, mid, p.ground + holeHeight, ceiling, mat, ground: p.groundMin);
                Box("Sill", room, mid, bottom, p.groundMin + 0.02f, mat, ground: p.groundMin);
            }

            Box("Roof", room, fp, ceiling, ceiling + 0.3f, mat, ground: p.groundMin);
            if (p.top > ceiling + 1f) Box("Upper", room, fp, ceiling + 0.3f, p.top, mat, ground: p.groundMin);

            // Inside: one weak, failing light and a marker for future black markets / dead drops.
            var palette = PaletteAt(fp.center);
            var light = HangFrom(AddLight(room, new Vector3(fp.center.x, p.ground + roomHeight - 0.4f, fp.center.y),
                palette.accent, 3f, 6f, fixture: true, flicker: true), ceiling);
            light.name = "HiddenRoomLight";

            var spot = new GameObject("HiddenSpot").AddComponent<HiddenSpot>();
            spot.transform.SetParent(hiddenParent, false);
            spot.transform.position = new Vector3(fp.center.x, p.ground, fp.center.y);
            spot.Setup(g.districts.Count > 0 ? g.districts[g.DistrictIndexAt(fp.center)].name : "",
                new Vector3(fp.width - wall * 2f, roomHeight, fp.height - wall * 2f));
        }

        // ---------------------------------------------------------------- catwalks

        /// <summary>Raised decks along walkway walls with stairs up from street level, giving streets a second level.</summary>
        private static void BuildCatwalks(Transform root)
        {
            var parent = Group(root, "Catwalks");
            catwalkZones = new List<Rect>();
            const float deck = 1.5f;
            const float stairWidth = 1.2f;

            foreach (var s in segments)
            {
                if (!Chance(g.catwalkChance)) continue;
                var r = s.rect;
                float segLength = s.alongX ? r.width : r.height;
                float width = s.alongX ? r.height : r.width;
                if (width < g.walkwayWidth - 0.1f) continue; // only thoroughfares are wide enough
                // Not on streets that climb between terraces: the stairs are there.
                GroundRange(r, out float lowest, out float highest);
                if (highest - lowest > 0.3f) continue;
                float rise = R(3.4f, 4f);
                int steps = Mathf.CeilToInt(rise / StepHeight);
                float stepH = rise / steps;
                float tread = R(0.28f, 0.4f);
                float run = steps * tread;
                float deckLength = R(8f, 14f);
                float total = run + deckLength;
                if (segLength < total + 2f) continue;

                Vector2 P(float a, float b) => s.alongX ? new Vector2(a, b) : new Vector2(b, a);
                Rect Span(float a0, float a1, float b0, float b1) => s.alongX
                    ? Rect.MinMaxRect(Mathf.Min(a0, a1), Mathf.Min(b0, b1), Mathf.Max(a0, a1), Mathf.Max(b0, b1))
                    : Rect.MinMaxRect(Mathf.Min(b0, b1), Mathf.Min(a0, a1), Mathf.Max(b0, b1), Mathf.Max(a0, a1));

                // A few tries along the street to find a stretch with building wall behind the whole length.
                float centre = s.alongX ? r.center.y : r.center.x;
                float side = 0f, wallLine = 0f, start = 0f, gMin = 0f, gMax = 0f, deckY = 0f, stairStart = 0f, dir = 0f;
                bool flip = false, found = false;
                for (int attempt = 0; attempt < 8 && !found; attempt++)
                {
                    side = Chance(0.5f) ? -1f : 1f;
                    wallLine = centre + side * width / 2f;
                    start = (s.alongX ? r.xMin : r.yMin) + R(1f, segLength - total - 1f);
                    flip = Chance(0.5f); // which end the stairs are at

                    // On level ground, away from open areas.
                    bool ok = true;
                    gMin = float.MaxValue;
                    gMax = float.MinValue;
                    for (float a = start; a <= start + total && ok; a += 1f)
                    {
                        var onDeck = P(a, wallLine - side * deck / 2f);
                        if (g.IsOpen(onDeck) || OffMap(P(a, wallLine + side * 0.4f))) ok = false;
                        float h = g.HeightAt(onDeck);
                        gMin = Mathf.Min(gMin, h);
                        gMax = Mathf.Max(gMax, h);
                    }
                    if (!ok || gMax - gMin > 0.6f) continue;

                    deckY = gMax + rise;
                    // Stairs and deck alike back onto building wall all the way along, tall enough to stand above the
                    // railing: nothing bridges an alley mouth or runs on past the corner of a building.
                    for (float a = start - 0.2f; a <= start + total + 0.2f && ok; a += 0.5f)
                        ok = WallBehind(P(a, wallLine + side * 0.3f), deckY + 1.2f);
                    if (!ok || !WallBehind(P(start + total + 0.2f, wallLine + side * 0.3f), deckY + 1.2f)) continue;
                    stairStart = flip ? start + total : start;
                    dir = flip ? -1f : 1f;

                    // The foot of the stairs needs solid, level ground to walk up from.
                    var approach = P(stairStart - dir * 1.5f, wallLine - side * stairWidth / 2f);
                    if (g.IsOpen(approach) || OffMap(approach) || Mathf.Abs(g.HeightAt(approach) - gMin) > 0.4f) continue;

                    // Don't collide with another catwalk where walkways meet.
                    var zone = Span(stairStart - dir * 1.5f, stairStart + dir * total, wallLine, wallLine - side * deck);
                    bool overlaps = false;
                    foreach (var other in catwalkZones) overlaps |= Grow(other, 1f).Overlaps(zone);
                    if (overlaps) continue;
                    found = true;
                }
                if (!found) continue;
                float deckStart = stairStart + dir * run;
                float deckEnd = deckStart + dir * deckLength;
                var walk = Group(parent, "Catwalk");

                for (int k = 0; k < steps; k++)
                {
                    float a0 = stairStart + dir * k * tread;
                    float a1 = a0 + dir * tread;
                    Box("Step", walk, Span(a0, a1, wallLine, wallLine - side * stairWidth), gMin - 0.3f, gMax + (k + 1) * stepH, g.concreteDark);
                }

                Box("Deck", walk, Span(deckStart, deckEnd, wallLine, wallLine - side * deck), deckY - 0.15f, deckY, g.metal);

                // Chain-link railing on the open side and at the far end; an invisible wall stops falls.
                float railLine = wallLine - side * deck;
                for (float a = Mathf.Min(deckStart, deckEnd); a < Mathf.Max(deckStart, deckEnd) - 0.1f; a += 2f)
                {
                    float a1 = Mathf.Min(a + 2f, Mathf.Max(deckStart, deckEnd));
                    FencePanel(walk, P(a, railLine), P(a1, railLine), deckY, 1.1f, g.chainLink, collider: false);
                }
                FencePanel(walk, P(deckEnd, wallLine), P(deckEnd, railLine), deckY, 1.1f, g.chainLink, collider: false);
                Blocker(walk, Span(deckStart, deckEnd, railLine - side * 0.05f, railLine + side * 0.05f), deckY, deckY + 1.1f);
                Blocker(walk, Span(deckEnd - dir * 0.05f, deckEnd + dir * 0.05f, wallLine, railLine), deckY, deckY + 1.1f);

                // Brackets under the deck.
                for (float a = Mathf.Min(deckStart, deckEnd) + 1f; a < Mathf.Max(deckStart, deckEnd); a += 3f)
                {
                    var c = P(a, wallLine - side * deck / 2f);
                    var bracket = LocalBox("Bracket", walk, Vector3.zero, new Vector3(0.1f, 1.9f, 0.1f), g.metal, collider: false);
                    bracket.transform.position = new Vector3(c.x, deckY - 0.75f, c.y);
                    var outward = s.alongX ? new Vector3(0f, 0f, -side) : new Vector3(-side, 0f, 0f);
                    bracket.transform.rotation = Quaternion.AngleAxis(40f, Vector3.Cross(Vector3.up, outward));
                }

                // A door at deck level, so the catwalk goes somewhere.
                var door = P((deckStart + deckEnd) / 2f + R(-2f, 2f), wallLine);
                var doorNormal = s.alongX ? new Vector2(0f, -side) : new Vector2(-side, 0f);
                FaceBox("Door", walk, door, doorNormal, deckY + 1f, new Vector3(0.9f, 2f, 0.06f), g.trash);

                catwalkZones.Add(Span(stairStart, deckEnd, wallLine, wallLine - side * deck));
            }
        }

        /// <summary>Whether a building stands at this point, reaching at least this high.</summary>
        private static bool WallBehind(Vector2 p, float top)
        {
            foreach (var plan in plans)
                if (plan.footprint.Contains(p) && plan.top >= top) return true;
            return false;
        }

        // ---------------------------------------------------------------- dressing

        // ---------------------------------------------------------------- elevation & underground

        /// <summary>Part of a rect between two distances measured from its low end along <paramref name="up"/>.</summary>
        private static Rect Slice(Rect r, Vector2 up, float from, float to)
        {
            if (up.x > 0f) return Rect.MinMaxRect(r.xMin + from, r.yMin, r.xMin + to, r.yMax);
            if (up.x < 0f) return Rect.MinMaxRect(r.xMax - to, r.yMin, r.xMax - from, r.yMax);
            if (up.y > 0f) return Rect.MinMaxRect(r.xMin, r.yMin + from, r.xMax, r.yMin + to);
            return Rect.MinMaxRect(r.xMin, r.yMax - to, r.xMax, r.yMax - from);
        }

        /// <summary>Solid steps climbing along <paramref name="up"/> across a footprint, from one height to another.</summary>
        private static void BuildStairs(Transform parent, Rect footprint, Vector2 up, float bottomY, float topY, Material m)
        {
            bool alongX = up.x != 0f;
            float length = alongX ? footprint.width : footprint.height;
            int steps = Mathf.Max(1, Mathf.CeilToInt((topY - bottomY) / StepHeight));
            float depth = length / steps;
            float rise = (topY - bottomY) / steps;
            for (int k = 0; k < steps; k++)
            {
                // Step k counts from the low end.
                float a0 = k * depth, a1 = (k + 1) * depth;
                Rect r;
                if (alongX)
                    r = up.x > 0f ? Rect.MinMaxRect(footprint.xMin + a0, footprint.yMin, footprint.xMin + a1, footprint.yMax)
                                  : Rect.MinMaxRect(footprint.xMax - a1, footprint.yMin, footprint.xMax - a0, footprint.yMax);
                else
                    r = up.y > 0f ? Rect.MinMaxRect(footprint.xMin, footprint.yMin + a0, footprint.xMax, footprint.yMin + a1)
                                  : Rect.MinMaxRect(footprint.xMin, footprint.yMax - a1, footprint.xMax, footprint.yMax - a0);
                Box("Step", parent, r, bottomY - 0.05f, bottomY + (k + 1) * rise, m);
            }
        }

        /// <summary>
        /// Retaining walls along the terrace edges, with stairs where streets cross them. Each edge is built from
        /// its higher side: a wall from below the lower ground up to a parapet a metre above the higher, and
        /// stairs on the street below, climbing to the edge. From version 3 no small alley opens off the side of a
        /// flight (see <see cref="FillAlleysBesideStairs"/>).
        /// </summary>
        private static void BuildHeights(Transform root)
        {
            stairZones = new List<Rect>();
            stairFlights = new();
            if (g.terraces.Count == 0) return;
            var parent = Group(root, "Heights");
            // The flights and landings are surfaced like the street they climb from.
            var stairSurface = Mat("World_Asphalt", new Color(0.27f, 0.26f, 0.25f));
            float h = g.HalfSize;
            foreach (var pl in g.terraces)
            {
                var terrace = Group(parent, "Terrace");
                var r = pl.rect;
                float hIn = pl.height;

                // Each edge: (fixed coordinate, along X?, outward normal), clipped to the map.
                var edges = new (float line, bool alongX, Vector2 normal, float from, float to)[]
                {
                    (r.yMin, true, Vector2.down, Mathf.Max(r.xMin, -h), Mathf.Min(r.xMax, h)),
                    (r.yMax, true, Vector2.up, Mathf.Max(r.xMin, -h), Mathf.Min(r.xMax, h)),
                    (r.xMin, false, Vector2.left, Mathf.Max(r.yMin, -h), Mathf.Min(r.yMax, h)),
                    (r.xMax, false, Vector2.right, Mathf.Max(r.yMin, -h), Mathf.Min(r.yMax, h)),
                };
                // A street crossing an edge square-on, with the ground just beyond lower (stairs are only built from the
                // higher side): where its flight would go, and from version 11 how far buildings line both sides of
                // the street below, down it from the edge.
                bool Crossing((float line, bool alongX, Vector2 normal, float from, float to) e, Segment sg,
                    out float cross0, out float cross1, out float hOut, out float room, out float flank)
                {
                    var sr = sg.rect;
                    cross0 = e.alongX ? sr.xMin : sr.yMin;
                    cross1 = e.alongX ? sr.xMax : sr.yMax;
                    hOut = room = flank = 0f;
                    if (sg.alongX == e.alongX) return false;
                    float span0 = e.alongX ? sr.yMin : sr.xMin, span1 = e.alongX ? sr.yMax : sr.xMax;
                    if (cross1 < e.from || cross0 > e.to || e.line < span0 || e.line > span1) return false;
                    Vector2 Point(float a) => e.alongX ? new Vector2(a, e.line) : new Vector2(e.line, a);
                    hOut = g.HeightAt(Point((cross0 + cross1) / 2f) + e.normal * 1.5f);
                    if (hIn - hOut < 0.3f) return false;
                    bool positive = e.normal.x + e.normal.y > 0f;
                    room = positive ? (e.alongX ? sr.yMax : sr.xMax) - e.line : e.line - (e.alongX ? sr.yMin : sr.xMin);
                    if (Since(11))
                        for (float d = 0.6f; d < room; d += 0.1f)
                        {
                            if (!BuildingAt(Point(cross0 - 0.3f) + e.normal * d) || !BuildingAt(Point(cross1 + 0.3f) + e.normal * d)) break;
                            flank = d;
                        }
                    return true;
                }
                bool Flanked(float hOut, float flank) => flank >= Mathf.CeilToInt((hIn - hOut) / StepHeight) * MinTread;

                // From version 12 a flight that can't fit between buildings isn't built (the street ends at the wall)
                // unless the terrace has no other way up: one that can.
                bool anyFlanked = false;
                if (Since(12))
                    foreach (var e in edges)
                    {
                        if (Mathf.Abs(e.line) >= h || e.to <= e.from) continue;
                        foreach (var sg in segments)
                            if (Crossing(e, sg, out _, out _, out float ho, out _, out float fl) && Flanked(ho, fl)) anyFlanked = true;
                    }

                foreach (var e in edges)
                {
                    if (Mathf.Abs(e.line) >= h || e.to <= e.from) continue;
                    Vector2 At(float a) => e.alongX ? new Vector2(a, e.line) : new Vector2(e.line, a);
                    var gaps = new List<(float a, float b)>();
                    foreach (var sg in segments)
                    {
                        if (!Crossing(e, sg, out float cross0, out float cross1, out float hOut, out float room, out float flank)) continue;
                        var sr = sg.rect;
                        var crossMid = At((cross0 + cross1) / 2f);
                        float rise = hIn - hOut;
                        int steps = Mathf.CeilToInt(rise / StepHeight);
                        float tread = R(0.28f, 0.45f);
                        bool midLanding = rise > 3.2f && Chance(0.7f);
                        float landing = midLanding ? R(1.2f, 2.2f) : 0f;
                        float run = steps * tread + landing;

                        // Down the street on the low side, outside this terrace.
                        var lowDir = e.normal;
                        bool lowIsPositive = lowDir.x + lowDir.y > 0f;
                        // From version 11 the flight stays between the buildings either side of it rather than running
                        // on past them, out beside an alley or across a street, wherever it can fit steepened.
                        if (Since(11) && Flanked(hOut, flank)) room = Mathf.Min(room, flank + 0.5f);
                        else if (Since(12) && anyFlanked)
                        {
                            R(6f, 10f); // the lamp roll every built flight makes, so the rest come out the same
                            continue;
                        }
                        if (room < run + 0.5f)
                        {
                            // Short street: one flight, steeper.
                            midLanding = false;
                            landing = 0f;
                            tread = Mathf.Max(MinTread, (room - 0.5f) / steps);
                            run = steps * tread;
                        }
                        if (room < run + 0.5f)
                        {
                            Debug.LogWarning($"PittGreybox: no room for stairs at {crossMid} ({rise} m up, {room:F1} m of street).");
                            continue;
                        }
                        gaps.Add((cross0, cross1));

                        Rect stair = e.alongX
                            ? (lowIsPositive ? Rect.MinMaxRect(cross0, e.line, cross1, e.line + run) : Rect.MinMaxRect(cross0, e.line - run, cross1, e.line))
                            : (lowIsPositive ? Rect.MinMaxRect(e.line, cross0, e.line + run, cross1) : Rect.MinMaxRect(e.line - run, cross0, e.line, cross1));
                        float lowY = hOut, highY = hIn;
                        var up = -lowDir;
                        var flights = new List<(float from, float to, float bottom, float top)>();
                        if (midLanding)
                        {
                            int lower = steps / 2;
                            float midY = lowY + rise * lower / steps;
                            BuildStairs(terrace, Slice(stair, up, 0f, lower * tread), up, lowY, midY, stairSurface);
                            Box("Landing", terrace, Slice(stair, up, lower * tread, lower * tread + landing), lowY - 0.05f, midY, stairSurface);
                            BuildStairs(terrace, Slice(stair, up, lower * tread + landing, run), up, midY, highY, stairSurface);
                            flights.Add((0f, lower * tread, lowY, midY));
                            flights.Add((lower * tread + landing, run, midY, highY));
                        }
                        else
                        {
                            BuildStairs(terrace, stair, up, lowY, highY, stairSurface);
                            flights.Add((0f, run, lowY, highY));
                        }
                        stairFlights.Add((stair, up, run, flights));
                        // Top landing reaching over the edge, where the ground dips between the levels.
                        Box("TopLanding", terrace, Slice(stair, up, run - 0.05f, run + 0.9f), lowY - 0.05f, highY, stairSurface);
                        stairZones.Add(Slice(stair, up, 0f, run + 0.9f));
                        // No lamp here (a post at the stair foot stood out in the open street); its draw is kept
                        // so the rest of the city comes out the same.
                        R(6f, 10f);
                    }

                    // Wall along the edge wherever the ground beyond is lower, skipping the stairs. A neighbour can
                    // change part way along, so it goes in runs of the same ground beyond.
                    gaps.Sort((x, y) => x.a.CompareTo(y.a));
                    void Wall(float a, float b)
                    {
                        const float piece = 1f;
                        float runStart = a, runLow = float.NaN;
                        void Flush(float end)
                        {
                            if (!float.IsNaN(runLow) && end - runStart > 0.05f)
                            {
                                var wr = e.alongX ? Rect.MinMaxRect(runStart, e.line - 0.5f, end, e.line + 0.5f) : Rect.MinMaxRect(e.line - 0.5f, runStart, e.line + 0.5f, end);
                                // From version 5 tucked just inside the buildings it meets: flush, their faces fought
                                // (brick and concrete flickering on one plane beside the stairs).
                                if (Since(5)) wr = Grow(wr, -FlushOffset);
                                Box("RetainingWall", terrace, wr, runLow - 0.4f, hIn + 1f, g.concreteDark);
                            }
                        }
                        for (float x = a; x < b; x += piece)
                        {
                            float outside = g.HeightAt(At(Mathf.Min(x + piece / 2f, b)) + e.normal * 1.5f);
                            float low = hIn - outside >= 0.3f ? outside : float.NaN;
                            bool same = float.IsNaN(low) ? float.IsNaN(runLow) : !float.IsNaN(runLow) && Mathf.Abs(low - runLow) < 0.05f;
                            if (same) continue;
                            Flush(x);
                            runStart = x;
                            runLow = low;
                        }
                        Flush(b);
                    }
                    float cursor = e.from;
                    foreach (var gap in gaps)
                    {
                        Wall(cursor, Mathf.Max(cursor, gap.a));
                        cursor = Mathf.Max(cursor, gap.b);
                    }
                    Wall(cursor, e.to);
                }
            }
        }

        // ---------------------------------------------------------------- kerbs (version 6)

        /// <summary>Whether a point is on a kerb or a small alley's floor, both raised <see cref="PittGreybox.kerbHeight"/> (version 6).</summary>
        private static bool OnKerb(Vector2 p) =>
            Since(6) && (alleyIndex.Contains(p) || (kerbIndex != null && kerbIndex.Contains(p)));

        /// <summary>Height of what's underfoot: the ground, or from version 6 the top of a kerb or alley floor.</summary>
        private static float FloorAt(Vector2 p) => g.HeightAt(p) + (OnKerb(p) ? g.kerbHeight : 0f);

        /// <summary>
        /// From version 6, concrete kerbs along the foot of every building wall facing a street or square, stopping
        /// where the building stops (round its corners, never on out into open ground), and wherever a small alley
        /// opens onto a street the kerb runs across its mouth and the alley is floored at kerb height all through.
        /// Kept off stairs and sump stairwells. Run after <see cref="BuildHeights"/> (the stairs) and before anything
        /// that stands on the ground (see <see cref="FloorAt"/>). Uses no randomness.
        /// </summary>
        private static void BuildKerbs(Transform root)
        {
            kerbIndex = new RectIndex();
            if (!Since(6)) return;
            var parent = Group(root, "Kerbs");
            var mat = g.kerb != null ? g.kerb : g.concreteDark;
            float rise = g.kerbHeight;
            const float cell = 0.5f;

            // From version 9 a wall facing a thoroughfare gets a sidewalk instead of a kerb.
            var thoroughfares = new RectIndex();
            foreach (var seg in segments) if (seg.thoroughfare) thoroughfares.Add(seg.rect);
            float WidthAt(Vector2 wallPoint, Vector2 normal) =>
                Since(9) && thoroughfares.Contains(wallPoint + normal * 0.2f) ? g.sidewalkWidth : g.kerbWidth;

            void Lay(string name, Rect r, float ground)
            {
                Box(name, parent, r, ground - 0.1f, ground + rise, mat, ground: ground);
                kerbIndex.Add(r);
            }

            // Whether a piece of kerb can go here: on a street or square, level, clear of stairs and stairwells.
            bool Fits(Rect r, out float h)
            {
                var c = r.center;
                h = g.HeightAt(c);
                if (OffMap(c) || alleyIndex.Contains(c) || !(streetIndex.Contains(c) || g.IsOpen(c))) return false;
                var inner = Grow(r, -0.02f);
                foreach (var q in new[] { new Vector2(inner.xMin, inner.yMin), new Vector2(inner.xMax, inner.yMin),
                             new Vector2(inner.xMin, inner.yMax), new Vector2(inner.xMax, inner.yMax) })
                    if (Mathf.Abs(g.HeightAt(q) - h) > 0.05f) return false;
                foreach (var z in stairZones) if (Grow(z, 0.05f).Overlaps(r)) return false;
                foreach (var sh in g.sumpShafts) if (Grow(sh, 0.3f).Overlaps(r)) return false;
                return BuildingIn(r) == null;
            }

            // A building the rect runs into, where two stand close at a corner.
            Rect? BuildingIn(Rect r)
            {
                foreach (var q in new[] { r.center, r.min, r.max, new Vector2(r.xMin, r.yMax), new Vector2(r.xMax, r.yMin) })
                {
                    var near = PlansNear(q);
                    if (near == null) continue;
                    foreach (var plan in near)
                        if (Grow(plan.footprint, -0.01f).Overlaps(r)) return plan.footprint;
                }
                return null;
            }

            Rect Between(Vector2 a, Vector2 b) =>
                Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));

            // Along each building face, in runs of level kerb. Faces go left, right, down, up (as Faces), each from
            // its low corner to its high one; whether each end got kerb decides the corner pieces.
            var endKerb = new bool[4, 2];
            var endHeight = new float[4, 2];
            var endWidth = new float[4, 2];
            foreach (var plan in plans)
            {
                var fp = plan.footprint;
                for (int f = 0; f < 4; f++)
                {
                    var normal = Faces[f];
                    Vector2 a, b;
                    if (normal.x != 0f)
                    {
                        float x = normal.x < 0f ? fp.xMin : fp.xMax;
                        a = new Vector2(x, fp.yMin);
                        b = new Vector2(x, fp.yMax);
                    }
                    else
                    {
                        float z = normal.y < 0f ? fp.yMin : fp.yMax;
                        a = new Vector2(fp.xMin, z);
                        b = new Vector2(fp.xMax, z);
                    }
                    float length = Vector2.Distance(a, b);
                    var dir = (b - a) / length;
                    int n = Mathf.Max(1, Mathf.CeilToInt(length / cell));
                    float step = length / n;
                    endKerb[f, 0] = endKerb[f, 1] = false;

                    int runStart = -1;
                    float runH = 0f, runW = 0f;
                    void Flush(int end)
                    {
                        if (runStart >= 0) Lay("Kerb", Between(a + dir * runStart * step, a + dir * end * step + normal * runW), runH);
                        runStart = -1;
                    }
                    for (int i = 0; i < n; i++)
                    {
                        float w = WidthAt(a + dir * (i + 0.5f) * step, normal);
                        var piece = Between(a + dir * i * step, a + dir * (i + 1) * step + normal * w);
                        bool fits = Fits(piece, out float h);
                        // Running into a neighbour: what's left short of it stands on its own, so the kerb meets its wall.
                        Rect? stub = null;
                        var other = fits ? null : BuildingIn(piece);
                        if (other != null)
                        {
                            var o = other.Value;
                            var cut = dir.x != 0f
                                ? (o.xMin <= piece.xMin ? Rect.MinMaxRect(o.xMax, piece.yMin, piece.xMax, piece.yMax) : Rect.MinMaxRect(piece.xMin, piece.yMin, o.xMin, piece.yMax))
                                : (o.yMin <= piece.yMin ? Rect.MinMaxRect(piece.xMin, o.yMax, piece.xMax, piece.yMax) : Rect.MinMaxRect(piece.xMin, piece.yMin, piece.xMax, o.yMin));
                            if (cut.width > 0.05f && cut.height > 0.05f && Fits(cut, out float ch) && Mathf.Abs(ch - h) < 0.01f) stub = cut;
                        }
                        if (i == 0) { endKerb[f, 0] = fits; endHeight[f, 0] = h; endWidth[f, 0] = w; }
                        if (i == n - 1) { endKerb[f, 1] = fits; endHeight[f, 1] = h; endWidth[f, 1] = w; }
                        if (runStart >= 0 && (!fits || Mathf.Abs(h - runH) > 0.01f || w != runW)) Flush(i);
                        if (stub != null) Lay("Kerb", stub.Value, h);
                        if (fits && runStart < 0)
                        {
                            runStart = i;
                            runH = h;
                            runW = w;
                        }
                    }
                    Flush(n);
                }

                // Round each outside corner where both faces' kerbs reach it.
                void Corner(int fx, int ex, int fz, int ez)
                {
                    if (!endKerb[fx, ex] || !endKerb[fz, ez] || Mathf.Abs(endHeight[fx, ex] - endHeight[fz, ez]) > 0.01f) return;
                    var nx = Faces[fx];
                    var nz = Faces[fz];
                    var c = new Vector2(nx.x < 0f ? fp.xMin : fp.xMax, nz.y < 0f ? fp.yMin : fp.yMax);
                    // Each face's kerb reaches out its own width: a sidewalk one way, a kerb the other.
                    var square = Between(c, c + nx * endWidth[fx, ex] + nz * endWidth[fz, ez]);
                    if (Fits(square, out float h)) Lay("KerbCorner", square, h);
                }
                Corner(0, 0, 2, 0);
                Corner(0, 1, 3, 0);
                Corner(1, 0, 2, 1);
                Corner(1, 1, 3, 1);
            }

            // Small alleys: floored at kerb height, with the kerb carried across the mouth onto the street.
            foreach (var alley in alleys)
            {
                float h = g.HeightAt(alley.center);
                Box("AlleyFloor", parent, alley, h - 0.1f, h + rise, mat, ground: h);
                var c = alley.center;
                float left = WidthAt(new Vector2(alley.xMin, c.y), Vector2.left), right = WidthAt(new Vector2(alley.xMax, c.y), Vector2.right);
                float down = WidthAt(new Vector2(c.x, alley.yMin), Vector2.down), up = WidthAt(new Vector2(c.x, alley.yMax), Vector2.up);
                foreach (var mouth in new[]
                         {
                             Rect.MinMaxRect(alley.xMin - left, alley.yMin, alley.xMin, alley.yMax),
                             Rect.MinMaxRect(alley.xMax, alley.yMin, alley.xMax + right, alley.yMax),
                             Rect.MinMaxRect(alley.xMin, alley.yMin - down, alley.xMax, alley.yMin),
                             Rect.MinMaxRect(alley.xMin, alley.yMax, alley.xMax, alley.yMax + up),
                         })
                    if (Fits(mouth, out float mh)) Lay("KerbMouth", mouth, mh);
            }

            if (Since(10)) BuildStairKerbs(parent, mat, WidthAt);
        }

        /// <summary>
        /// From version 10 the kerbs and sidewalks along a building beside a terrace stair climb it instead of stopping
        /// at its foot: a ramp <see cref="PittGreybox.kerbHeight"/> above the nose of every step, rising straight off the
        /// street's kerb a tread short of the foot, levelling out on a mid landing, and meeting the kerb on the terrace
        /// where the top step ends. The steps under it are buried in it. Each ramp runs on level past both ends of the
        /// stair, over the ends of the level kerbs (they stop short of stairs), so the two join without a gap.
        /// </summary>
        private static void BuildStairKerbs(Transform parent, Material mat, System.Func<Vector2, Vector2, float> widthAt)
        {
            const float overlap = 0.55f; // a kerb cell and its margin: as far short of a stair as the level kerb can stop
            const float topLanding = 0.9f;
            foreach (var s in stairFlights)
            {
                // Positions as distance t up the flight from its foot (the stair rect's low edge), and across it.
                bool climbsX = s.up.x != 0f;
                float sign = climbsX ? s.up.x : s.up.y;
                float foot = climbsX ? (sign > 0f ? s.stair.xMin : s.stair.xMax) : (sign > 0f ? s.stair.yMin : s.stair.yMax);
                Vector2 At(float t, float across) => climbsX ? new Vector2(foot + sign * t, across) : new Vector2(across, foot + sign * t);
                float Along(Vector2 p) => ((climbsX ? p.x : p.y) - foot) * sign;

                // Each flight's steps (as BuildStairs cuts them) and the line a step's depth ahead of their noses: the
                // kerb's top runs that far above it, so it clears every nose and starts level with the street's kerb.
                var breaks = new List<float>();
                float Ground(float t)
                {
                    float y = s.flights[0].bottom;
                    foreach (var f in s.flights)
                    {
                        float depth = (f.to - f.from) / Mathf.Max(1, Mathf.CeilToInt((f.top - f.bottom) / StepHeight));
                        y += Mathf.Clamp((t - f.from + depth) * (f.top - f.bottom) / (f.to - f.from), 0f, f.top - f.bottom);
                    }
                    return y;
                }
                foreach (var f in s.flights)
                {
                    float depth = (f.to - f.from) / Mathf.Max(1, Mathf.CeilToInt((f.top - f.bottom) / StepHeight));
                    breaks.Add(f.from - depth);
                    breaks.Add(f.to - depth);
                }
                float t0 = breaks[0] - overlap, t1 = s.run + topLanding + overlap;
                float bottom = s.flights[0].bottom - 0.1f;

                // Both sides: whichever buildings line them, each with its own kerb or sidewalk.
                var lined = new List<(float from, float to)>();
                for (int side = 0; side < 2; side++)
                {
                    float line = climbsX ? (side == 0 ? s.stair.yMin : s.stair.yMax) : (side == 0 ? s.stair.xMin : s.stair.xMax);
                    float outward = side == 0 ? -1f : 1f; // towards the building, across the flight
                    var lining = new List<Plan>();
                    lined.Clear();
                    for (float t = t0; t <= t1; t += 0.25f)
                    {
                        var probe = At(t, line + outward * 0.3f);
                        var near = PlansNear(probe);
                        if (near == null) continue;
                        foreach (var plan in near)
                            if (plan.footprint.Contains(probe) && !lining.Contains(plan)) lining.Add(plan);
                    }
                    foreach (var plan in lining)
                    {
                        var fp = plan.footprint;
                        float wall = climbsX ? (outward < 0f ? fp.yMax : fp.yMin) : (outward < 0f ? fp.xMax : fp.xMin);
                        if (Mathf.Abs(wall - line) > 0.6f) continue;
                        float a = Along(fp.min), b = Along(fp.max);
                        float from = Mathf.Max(t0, Mathf.Min(a, b)), to = Mathf.Min(t1, Mathf.Max(a, b));
                        if (to - from < 0.1f) continue;

                        var normal = climbsX ? new Vector2(0f, -outward) : new Vector2(-outward, 0f);
                        float w = widthAt(At((from + to) / 2f, wall), normal);
                        // From version 13, where the building ends beside the flight (its corner by the foot), the
                        // ramp carries on round the corner as far out as the kerb along its front, filling the corner.
                        float start = from;
                        if (Since(13) && Mathf.Min(a, b) > t0)
                        {
                            float front = widthAt(At(from, wall - outward * 0.05f), -s.up);
                            // Only at a corner: no other building carrying the wall on past it.
                            var beside = At(from - front / 2f, wall + outward * 0.3f);
                            var beyond = At(from - front / 2f, wall - outward * w / 2f);
                            if (!BuildingAt(beside) && !BuildingAt(beyond) && streetIndex.Contains(beyond)) start = from - front;
                        }
                        Climb("StairKerb", start, to, wall, wall - outward * w);
                        lined.Add((from, to));
                    }

                    // From version 11, where no building lines the flight (one too long to fit between them even
                    // steepened), a kerb runs up its edge anyway, closing off the ends of the steps.
                    if (!Since(11)) continue;
                    lined.Sort((x, y) => x.from.CompareTo(y.from));
                    // A touch proud of the stair's edge, so its face doesn't fight the ends of the steps (from version 12).
                    float cheekOuter = line + (Since(12) ? outward * 0.02f : 0f);
                    float cursor = 0f;
                    foreach (var l in lined)
                    {
                        if (l.from - cursor > 0.05f) Climb("StairCheek", cursor, l.from, cheekOuter, line - outward * g.kerbWidth);
                        cursor = Mathf.Max(cursor, l.to);
                    }
                    if (s.run - cursor > 0.05f) Climb("StairCheek", cursor, s.run, cheekOuter, line - outward * g.kerbWidth);
                }

                // A ramp between two distances up the flight, from one line across it to another.
                void Climb(string name, float from, float to, float outer, float inner)
                {
                    var ts = new List<float> { from };
                    foreach (var k in breaks) if (k > from && k < to) ts.Add(k);
                    ts.Add(to);
                    ts.Sort();
                    var outerSide = new Vector3[ts.Count];
                    var innerSide = new Vector3[ts.Count];
                    for (int i = 0; i < ts.Count; i++)
                    {
                        float y = Ground(ts[i]) + g.kerbHeight;
                        outerSide[i] = ToV3(At(ts[i], outer), y);
                        innerSide[i] = ToV3(At(ts[i], inner), y);
                    }
                    Ramp(name, parent, outerSide, innerSide, bottom, mat);
                }
            }
        }

        /// <summary>
        /// A solid strip with a sloping top, down to a level bottom: the top edges on its two long sides given column
        /// by column (in world space), with faces and UVs as <see cref="WorldCube"/>'s so it matches the boxes it meets.
        /// Open underneath. Has a mesh collider.
        /// </summary>
        private static GameObject Ramp(string name, Transform parent, Vector3[] left, Vector3[] right, float bottom, Material m)
        {
            var verts = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var heights = new List<Vector2>();
            var tris = new List<int>();
            Vector3 Drop(Vector3 v) => new(v.x, bottom, v.z);
            Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z).normalized;
            void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 outward)
            {
                var n = Vector3.Cross(b - a, c - a);
                if (n.sqrMagnitude < 1e-8f) n = Vector3.Cross(c - a, d - a);
                if (Vector3.Dot(n, outward) < 0f) { (b, d) = (d, b); n = -n; }
                n.Normalize();
                // As WorldCube: tops take the ground's UVs, walls run U along themselves and V up.
                var across = Mathf.Abs(n.y) > 0.5f ? Vector3.zero : Vector3.Cross(Flat(n), Vector3.up);
                int i = verts.Count;
                foreach (var v in new[] { a, b, c, d })
                {
                    verts.Add(v);
                    normals.Add(n);
                    uvs.Add(across == Vector3.zero ? new Vector2(v.x, v.z) : new Vector2(Vector3.Dot(v, across), v.y));
                    heights.Add(new Vector2(v.y - bottom, 0f));
                }
                tris.AddRange(new[] { i, i + 1, i + 2, i, i + 2, i + 3 });
            }
            var toLeft = Flat(left[0] - right[0]);
            for (int k = 0; k + 1 < left.Length; k++)
            {
                Quad(left[k], left[k + 1], right[k + 1], right[k], Vector3.up);
                Quad(left[k], left[k + 1], Drop(left[k + 1]), Drop(left[k]), toLeft);
                Quad(right[k], right[k + 1], Drop(right[k + 1]), Drop(right[k]), -toLeft);
            }
            int last = left.Length - 1;
            var forward = Flat(left[last] - left[0]);
            Quad(left[0], right[0], Drop(right[0]), Drop(left[0]), -forward);
            Quad(left[last], right[last], Drop(right[last]), Drop(left[last]), forward);

            var mesh = new Mesh { name = "Ramp" };
            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetUVs(3, heights);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();

            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = m;
            go.AddComponent<MeshCollider>().sharedMesh = mesh;
            return go;
        }

        /// <summary>
        /// Level -1 (the sumps): tunnels and chambers under the streets, laid out on a 2 m grid so floors,
        /// ceilings and walls merge into long boxes. Level -2 (the cistern): one big pillared room further
        /// down, reached from a sump chamber.
        /// </summary>
        private static void BuildUnderground(Transform root)
        {
            if (sumps == null || sumps.Count == 0) return;
            var parent = Group(root, "Underground");
            int n = Mathf.RoundToInt(g.citySize / TunnelCell);
            var tunnel = new bool[n, n];
            var openAbove = new bool[n, n];   // stairwell up to the street
            var openBelow = new bool[n, n];   // stairwell down to the cistern
            int Cell(float v) => Mathf.Clamp(Mathf.FloorToInt((v + g.HalfSize) / TunnelCell), 0, n - 1);
            Vector2 Center(int i, int j) => new(-g.HalfSize + (i + 0.5f) * TunnelCell, -g.HalfSize + (j + 0.5f) * TunnelCell);
            bool Allowed(int i, int j)
            {
                if (i < 2 || j < 2 || i >= n - 2 || j >= n - 2) return false;
                var c = Center(i, j);
                return Vector2.Distance(c, g.pitCenter) > g.pitRadius + 6f && !g.InRampCorridor(c, 4f);
            }
            bool MarkRect(int i0, int j0, int i1, int j1, bool test)
            {
                for (int i = Mathf.Min(i0, i1); i <= Mathf.Max(i0, i1); i++)
                for (int j = Mathf.Min(j0, j1); j <= Mathf.Max(j0, j1); j++)
                {
                    if (!Allowed(i, j)) return false;
                    if (!test) tunnel[i, j] = true;
                }
                return true;
            }

            // Chambers under each sump square.
            var nodes = new List<Vector2Int>();
            foreach (var sp in sumps)
            {
                int ci = Cell(sp.node.x), cj = Cell(sp.node.y);
                nodes.Add(new Vector2Int(ci, cj));
                MarkRect(ci - 5, cj - 5, ci + 5, cj + 5, false);
                for (int i = Cell(sp.shaft.xMin + 0.1f); i <= Cell(sp.shaft.xMax - 0.1f); i++)
                for (int j = Cell(sp.shaft.yMin + 0.1f); j <= Cell(sp.shaft.yMax - 0.1f); j++)
                {
                    tunnel[i, j] = true;
                    openAbove[i, j] = true;
                }
            }

            // Tunnels: a minimum spanning tree between chambers, each link an L-shaped 4 m corridor.
            var inTree = new List<int> { 0 };
            var remaining = new List<int>();
            for (int i = 1; i < nodes.Count; i++) remaining.Add(i);
            var corridors = new List<(Vector2Int a, Vector2Int b)>();
            while (remaining.Count > 0)
            {
                int bestA = -1, bestB = -1;
                float best = float.MaxValue;
                foreach (int a in inTree)
                foreach (int b in remaining)
                {
                    float d = Vector2Int.Distance(nodes[a], nodes[b]);
                    if (d < best) { best = d; bestA = a; bestB = b; }
                }
                remaining.Remove(bestB);
                inTree.Add(bestB);
                var na = nodes[bestA];
                var nb = nodes[bestB];
                // Try x-then-z, else z-then-x; give up on this link if both cross the pit.
                bool viaX = MarkRect(na.x, na.y, nb.x, na.y + 1, true) && MarkRect(nb.x, na.y, nb.x + 1, nb.y, true);
                bool viaZ = MarkRect(na.x, na.y, na.x + 1, nb.y, true) && MarkRect(na.x, nb.y, nb.x, nb.y + 1, true);
                if (viaX) { MarkRect(na.x, na.y, nb.x, na.y + 1, false); MarkRect(nb.x, na.y, nb.x + 1, nb.y, false); corridors.Add((na, new Vector2Int(nb.x, na.y))); corridors.Add((new Vector2Int(nb.x, na.y), nb)); }
                else if (viaZ) { MarkRect(na.x, na.y, na.x + 1, nb.y, false); MarkRect(na.x, nb.y, nb.x, nb.y + 1, false); corridors.Add((na, new Vector2Int(na.x, nb.y))); corridors.Add((new Vector2Int(na.x, nb.y), nb)); }
            }

            // Dead-end side rooms off the chambers.
            var sideRooms = new List<Vector2Int>();
            for (int k = 0, tries = 0; k < g.sumpSideRooms && tries < 60; tries++)
            {
                var from = nodes[rng.Next(nodes.Count)];
                var dir = new[] { Vector2Int.up, Vector2Int.down, Vector2Int.left, Vector2Int.right }[rng.Next(4)];
                int len = rng.Next(8, 14);
                var end = from + dir * (6 + len);
                var side = new Vector2Int(dir.y, dir.x);
                if (!MarkRect(from.x + dir.x * 6, from.y + dir.y * 6, end.x + side.x, end.y + side.y, true)) continue;
                if (!MarkRect(end.x - 2, end.y - 2, end.x + 2, end.y + 2, true)) continue;
                MarkRect(from.x + dir.x * 6, from.y + dir.y * 6, end.x + side.x, end.y + side.y, false);
                MarkRect(end.x - 2, end.y - 2, end.x + 2, end.y + 2, false);
                sideRooms.Add(end);
                k++;
            }

            // Level -2: the cistern, under the chamber with the most room, reached by a stairwell in the
            // chamber quadrant opposite the street stairs.
            Rect cistern = default;
            Rect cisternStair = default;
            bool hasCistern = false;
            for (int s = 0; s < sumps.Count && !hasCistern; s++)
            {
                var sp = sumps[s];
                var q = -sp.quadrant;
                float sx0 = q.x > 0 ? SnapUp(sp.node.x + 2.2f) : SnapDown(sp.node.x - 2.2f) - 4f;
                float sz0 = q.y > 0 ? SnapUp(sp.node.y + 2.2f) : SnapDown(sp.node.y - 2.2f) - 6f;
                var stair = new Rect(sx0, sz0, 4f, 6f);
                var room = Grow(new Rect(stair.center.x - 1f, stair.center.y - 1f, 2f, 2f), 13f);
                if (DistanceToRect(g.pitCenter, room) < g.pitRadius + 6f || Mathf.Abs(room.xMin) > g.HalfSize - 4f ||
                    Mathf.Abs(room.xMax) > g.HalfSize - 4f || Mathf.Abs(room.yMin) > g.HalfSize - 4f || Mathf.Abs(room.yMax) > g.HalfSize - 4f) continue;
                cistern = room;
                cisternStair = stair;
                hasCistern = true;
                for (int i = Cell(stair.xMin + 0.1f); i <= Cell(stair.xMax - 0.1f); i++)
                for (int j = Cell(stair.yMin + 0.1f); j <= Cell(stair.yMax - 0.1f); j++)
                    openBelow[i, j] = true;
            }

            float floorTop = -g.sumpDepth, ceilingBottom = -0.8f;
            var floor = Group(parent, "Level-1_Floor");
            var ceiling = Group(parent, "Level-1_Ceiling");
            var walls = Group(parent, "Level-1_Walls");

            // Merge rows of cells into boxes.
            void Runs(System.Func<int, int, bool> include, System.Action<int, int, int> emit)
            {
                for (int j = 0; j < n; j++)
                {
                    int startI = -1;
                    for (int i = 0; i <= n; i++)
                    {
                        bool on = i < n && include(i, j);
                        if (on && startI < 0) startI = i;
                        if (!on && startI >= 0) { emit(startI, i - 1, j); startI = -1; }
                    }
                }
            }
            Rect CellsRect(int i0, int i1, int j) => Rect.MinMaxRect(-g.HalfSize + i0 * TunnelCell, -g.HalfSize + j * TunnelCell,
                -g.HalfSize + (i1 + 1) * TunnelCell, -g.HalfSize + (j + 1) * TunnelCell);

            Runs((i, j) => tunnel[i, j] && !openBelow[i, j], (i0, i1, j) => Box("Floor", floor, CellsRect(i0, i1, j), floorTop - 0.5f, floorTop, g.concrete));
            Runs((i, j) => tunnel[i, j] && !openAbove[i, j], (i0, i1, j) => Box("Ceiling", ceiling, CellsRect(i0, i1, j), ceilingBottom, -0.3f, g.concreteDark));

            // Walls on every tunnel edge that faces solid ground.
            bool T(int i, int j) => i >= 0 && j >= 0 && i < n && j < n && tunnel[i, j];
            float wallLow = floorTop - 0.5f, wallHigh = -0.3f;
            for (int j = 0; j < n; j++)
            {
                foreach (int side in new[] { -1, 1 })
                {
                    int startI = -1;
                    for (int i = 0; i <= n; i++)
                    {
                        bool on = i < n && T(i, j) && !T(i, j + side);
                        if (on && startI < 0) startI = i;
                        if (!on && startI >= 0)
                        {
                            float z = -g.HalfSize + (side > 0 ? j + 1 : j) * TunnelCell;
                            Box("Wall", walls, Rect.MinMaxRect(-g.HalfSize + startI * TunnelCell, z - 0.15f, -g.HalfSize + i * TunnelCell, z + 0.15f),
                                wallLow, wallHigh, g.brick, ground: floorTop);
                            startI = -1;
                        }
                    }
                }
            }
            for (int i = 0; i < n; i++)
            {
                foreach (int side in new[] { -1, 1 })
                {
                    int startJ = -1;
                    for (int j = 0; j <= n; j++)
                    {
                        bool on = j < n && T(i, j) && !T(i + side, j);
                        if (on && startJ < 0) startJ = j;
                        if (!on && startJ >= 0)
                        {
                            float x = -g.HalfSize + (side > 0 ? i + 1 : i) * TunnelCell;
                            Box("Wall", walls, Rect.MinMaxRect(x - 0.15f, -g.HalfSize + startJ * TunnelCell, x + 0.15f, -g.HalfSize + j * TunnelCell),
                                wallLow, wallHigh, g.brick, ground: floorTop);
                            startJ = -1;
                        }
                    }
                }
            }

            // Street stairwells: steps down, a rim round the hole under the street, a railing above.
            var entrances = Group(parent, "SumpEntrances");
            foreach (var sp in sumps)
            {
                BuildStairs(entrances, sp.shaft, sp.up, floorTop, 0f, g.concrete);
                var sh = sp.shaft;
                // The three sides that aren't the stairs' top end.
                var rims = new List<(Vector2 a, Vector2 b)>();
                if (sp.up.y < 0f) // top at yMin
                {
                    rims.Add((new Vector2(sh.xMin, sh.yMin), new Vector2(sh.xMin, sh.yMax)));
                    rims.Add((new Vector2(sh.xMax, sh.yMin), new Vector2(sh.xMax, sh.yMax)));
                    rims.Add((new Vector2(sh.xMin, sh.yMax), new Vector2(sh.xMax, sh.yMax)));
                }
                else
                {
                    rims.Add((new Vector2(sh.xMin, sh.yMin), new Vector2(sh.xMin, sh.yMax)));
                    rims.Add((new Vector2(sh.xMax, sh.yMin), new Vector2(sh.xMax, sh.yMax)));
                    rims.Add((new Vector2(sh.xMin, sh.yMin), new Vector2(sh.xMax, sh.yMin)));
                }
                // All four sides get a rim below ground so nothing shows through the terrain hole.
                Box("Rim", entrances, Rect.MinMaxRect(sh.xMin - 0.2f, sh.yMin, sh.xMin, sh.yMax), ceilingBottom, 0.05f, g.concreteDark);
                Box("Rim", entrances, Rect.MinMaxRect(sh.xMax, sh.yMin, sh.xMax + 0.2f, sh.yMax), ceilingBottom, 0.05f, g.concreteDark);
                Box("Rim", entrances, Rect.MinMaxRect(sh.xMin - 0.2f, sh.yMin - 0.2f, sh.xMax + 0.2f, sh.yMin), ceilingBottom, 0.05f, g.concreteDark);
                Box("Rim", entrances, Rect.MinMaxRect(sh.xMin - 0.2f, sh.yMax, sh.xMax + 0.2f, sh.yMax + 0.2f), ceilingBottom, 0.05f, g.concreteDark);
                foreach (var (a, b) in rims) FencePanel(entrances, a, b, 0f, 1.1f, g.chainLink, collider: true);
                var pal = PaletteAt(sp.node);
                AddPostLight(entrances, sp.shaft.center + new Vector2(sp.quadrant.x * 3.2f, 0f), 4f, pal.accent, 18f, 14f);
            }

            // Lights: one per chamber and side room, and along the tunnels every ~14 m.
            var lights = Group(parent, "Level-1_Lights");
            var sumpColor = new Color(0.7f, 0.9f, 0.75f);
            foreach (var node in nodes)
            {
                var c = Center(node.x, node.y);
                HangFrom(AddLight(lights, new Vector3(c.x, ceilingBottom - 0.3f, c.y), sumpColor, 8f, 14f, fixture: true, flicker: Chance(0.3f)), ceilingBottom);
            }
            foreach (var room in sideRooms)
            {
                var c = Center(room.x, room.y);
                HangFrom(AddLight(lights, new Vector3(c.x, ceilingBottom - 0.3f, c.y), new Color(0.9f, 0.4f, 0.25f), 5f, 10f, fixture: true, flicker: true), ceilingBottom);
            }
            foreach (var (a, b) in corridors)
            {
                var pa = Center(a.x, a.y) + Vector2.one;
                var pb = Center(b.x, b.y) + Vector2.one;
                float length = Vector2.Distance(pa, pb);
                for (float d = 14f; d < length - 6f; d += 14f)
                {
                    var p = Vector2.Lerp(pa, pb, d / length);
                    HangFrom(AddLight(lights, new Vector3(p.x, ceilingBottom - 0.25f, p.y), sumpColor, 3.5f, 10f, fixture: true, flicker: Chance(0.15f)), ceilingBottom);
                }
            }

            if (!hasCistern) return;

            // Level -2: the cistern.
            var cis = Group(parent, "Level-2_Cistern");
            float cFloor = -g.cisternDepth, cCeiling = floorTop - 0.5f;
            Box("Floor", cis, cistern, cFloor - 0.5f, cFloor, g.concreteDark);
            // Ceiling with a hole for the stairs.
            var cs = cisternStair;
            Box("Ceiling", cis, Rect.MinMaxRect(cistern.xMin, cistern.yMin, cistern.xMax, cs.yMin), cCeiling - 0.3f, cCeiling, g.concreteDark);
            Box("Ceiling", cis, Rect.MinMaxRect(cistern.xMin, cs.yMax, cistern.xMax, cistern.yMax), cCeiling - 0.3f, cCeiling, g.concreteDark);
            Box("Ceiling", cis, Rect.MinMaxRect(cistern.xMin, cs.yMin, cs.xMin, cs.yMax), cCeiling - 0.3f, cCeiling, g.concreteDark);
            Box("Ceiling", cis, Rect.MinMaxRect(cs.xMax, cs.yMin, cistern.xMax, cs.yMax), cCeiling - 0.3f, cCeiling, g.concreteDark);
            Box("Wall", cis, Rect.MinMaxRect(cistern.xMin - 0.3f, cistern.yMin - 0.3f, cistern.xMax + 0.3f, cistern.yMin), cFloor - 0.5f, cCeiling, g.brick, ground: cFloor);
            Box("Wall", cis, Rect.MinMaxRect(cistern.xMin - 0.3f, cistern.yMax, cistern.xMax + 0.3f, cistern.yMax + 0.3f), cFloor - 0.5f, cCeiling, g.brick, ground: cFloor);
            Box("Wall", cis, Rect.MinMaxRect(cistern.xMin - 0.3f, cistern.yMin, cistern.xMin, cistern.yMax), cFloor - 0.5f, cCeiling, g.brick, ground: cFloor);
            Box("Wall", cis, Rect.MinMaxRect(cistern.xMax, cistern.yMin, cistern.xMax + 0.3f, cistern.yMax), cFloor - 0.5f, cCeiling, g.brick, ground: cFloor);
            // The stairs face away from the street stairs' chamber quadrant.
            var cisUp = new Vector2(0f, cs.center.y > sumps[0].node.y ? -1f : 1f);
            foreach (var sp in sumps)
                if (Grow(cistern, 2f).Contains(cs.center)) { cisUp = new Vector2(0f, cs.center.y > sp.node.y ? -1f : 1f); break; }
            BuildStairs(cis, cs, cisUp, cFloor, floorTop, g.concrete);
            // Railing round the hole on level -1, except where the stairs arrive.
            if (cisUp.y < 0f)
            {
                FencePanel(cis, new Vector2(cs.xMin, cs.yMin), new Vector2(cs.xMin, cs.yMax), floorTop, 1.1f, g.chainLink, collider: true);
                FencePanel(cis, new Vector2(cs.xMax, cs.yMin), new Vector2(cs.xMax, cs.yMax), floorTop, 1.1f, g.chainLink, collider: true);
                FencePanel(cis, new Vector2(cs.xMin, cs.yMax), new Vector2(cs.xMax, cs.yMax), floorTop, 1.1f, g.chainLink, collider: true);
            }
            else
            {
                FencePanel(cis, new Vector2(cs.xMin, cs.yMin), new Vector2(cs.xMin, cs.yMax), floorTop, 1.1f, g.chainLink, collider: true);
                FencePanel(cis, new Vector2(cs.xMax, cs.yMin), new Vector2(cs.xMax, cs.yMax), floorTop, 1.1f, g.chainLink, collider: true);
                FencePanel(cis, new Vector2(cs.xMin, cs.yMin), new Vector2(cs.xMax, cs.yMin), floorTop, 1.1f, g.chainLink, collider: true);
            }
            // Pillars on a grid, kept clear of the stairs.
            for (float x = cistern.xMin + 4f; x < cistern.xMax - 2f; x += 6f)
            for (float z = cistern.yMin + 4f; z < cistern.yMax - 2f; z += 6f)
            {
                var pp = new Vector2(x, z);
                if (Grow(cs, 1.5f).Contains(pp)) continue;
                var pillar = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                pillar.name = "Pillar";
                pillar.transform.SetParent(cis, false);
                pillar.transform.position = new Vector3(x, (cFloor + cCeiling) / 2f, z);
                pillar.transform.localScale = new Vector3(1.1f, (cCeiling - cFloor) / 2f, 1.1f);
                pillar.GetComponent<Renderer>().sharedMaterial = g.concrete;
            }
            // Standing water.
            var water = LocalBox("Water", cis, Vector3.zero, new Vector3(cistern.width - 1f, 0.05f, cistern.height - 1f), Glow(new Color(0.15f, 0.3f, 0.3f), 0.15f), collider: false);
            water.transform.position = new Vector3(cistern.center.x, cFloor + 0.03f, cistern.center.y);
            for (int i = 0; i < 4; i++)
            {
                var lp = new Vector2(Mathf.Lerp(cistern.xMin + 5f, cistern.xMax - 5f, i % 2), Mathf.Lerp(cistern.yMin + 5f, cistern.yMax - 5f, i / 2));
                HangFrom(AddLight(cis, new Vector3(lp.x, cCeiling - 0.4f, lp.y), new Color(0.45f, 0.75f, 0.8f), 7f, 16f, fixture: true, flicker: i == 2), cCeiling);
            }
        }

        // ---------------------------------------------------------------- street structures

        private static bool Blocked(Rect r)
        {
            foreach (var z in stairZones) if (Grow(z, 1f).Overlaps(r)) return true;
            if (catwalkZones != null) foreach (var z in catwalkZones) if (Grow(z, 1f).Overlaps(r)) return true;
            return false;
        }

        /// <summary>Level along a rect: false if the ground under it changes height (terrace edges).</summary>
        private static bool Flat(Rect r, out float y)
        {
            GroundRange(r, out float min, out float max);
            y = max;
            return max - min < 0.2f;
        }

        /// <summary>Building masses spanning secondary alleys: the street ducks under the building.</summary>
        private static void BuildUnderpasses(Transform root)
        {
            var parent = Group(root, "Underpasses");
            foreach (var s in segments)
            {
                if (s.thoroughfare || !Chance(g.underpassChance)) continue;
                var r = s.rect;
                float segLength = s.alongX ? r.width : r.height;
                float length = R(6f, 11f);
                if (segLength < length + 4f) continue;
                float start = (s.alongX ? r.xMin : r.yMin) + R(2f, segLength - length - 2f);
                var span = s.alongX ? Rect.MinMaxRect(start, r.yMin, start + length, r.yMax) : Rect.MinMaxRect(r.xMin, start, r.xMax, start + length);
                if (Blocked(span) || TouchesOpen(span) || !Flat(span, out float ground)) continue;

                // Both sides must be buildings for the mass to rest on.
                var side = s.alongX ? Vector2.up : Vector2.right;
                float half = (s.alongX ? r.height : r.width) / 2f + 0.4f;
                if (Walkable(span.center + side * half) || Walkable(span.center - side * half)) continue;

                float clearance = R(3.6f, 4.4f);
                var mass = Box("Underpass", parent, Grow(span, 0.05f), ground + clearance, ground + clearance + R(5f, 12f),
                    Chance(0.5f) ? g.brick : g.concrete, ground: ground);
                // Soffit lights.
                foreach (float t in new[] { 0.3f, 0.7f })
                {
                    var c = s.alongX ? new Vector2(Mathf.Lerp(span.xMin, span.xMax, t), span.center.y) : new Vector2(span.center.x, Mathf.Lerp(span.yMin, span.yMax, t));
                    var strip = LocalBox("Strip", parent, Vector3.zero, s.alongX ? new Vector3(1.2f, 0.06f, 0.12f) : new Vector3(0.12f, 0.06f, 1.2f),
                        Glow(new Color(1f, 0.85f, 0.45f), 4f), collider: false);
                    strip.transform.position = new Vector3(c.x, ground + clearance - 0.05f, c.y);
                    NoShadows(strip);
                    if (t < 0.5f) AddLight(parent, new Vector3(c.x, ground + clearance - 0.3f, c.y), new Color(1f, 0.8f, 0.45f), 9f, 9f, fixture: false);
                }
                stairZones.Add(span); // keeps catwalk-style clutter and shacks out
            }
        }

        /// <summary>Steel portal frames down the length of some alleys, with hanging lights and sometimes a grating roof.</summary>
        private static void BuildGantries(Transform root)
        {
            var parent = Group(root, "Gantries");
            var lamp = Glow(new Color(1f, 0.85f, 0.35f), 5f);
            foreach (var s in segments)
            {
                if (!Chance(g.gantryChance)) continue;
                var r = s.rect;
                if (Blocked(r) || TouchesOpen(r) || !Flat(r, out float ground)) continue;
                float width = s.alongX ? r.height : r.width;
                float segLength = s.alongX ? r.width : r.height;
                if (segLength < 10f) continue;

                float beamY = ground + R(5.2f, 6.5f);
                float a0 = (s.alongX ? r.xMin : r.yMin) + 2f;
                float a1 = (s.alongX ? r.xMax : r.yMax) - 2f;
                float lateral = s.alongX ? r.center.y : r.center.x;
                Vector2 P(float a, float b) => s.alongX ? new Vector2(a, b) : new Vector2(b, a);
                int frames = 0;
                for (float a = a0; a <= a1; a += 6.5f, frames++)
                {
                    foreach (float side in new[] { -1f, 1f })
                    {
                        var post = P(a, lateral + side * (width / 2f - 0.15f));
                        Box("Post", parent, Rect.MinMaxRect(post.x - 0.1f, post.y - 0.1f, post.x + 0.1f, post.y + 0.1f), ground - 0.1f, beamY + 0.25f, g.metal);
                    }
                    var b0 = P(a, lateral - width / 2f + 0.05f);
                    var b1 = P(a, lateral + width / 2f - 0.05f);
                    Box("Beam", parent, Rect.MinMaxRect(Mathf.Min(b0.x, b1.x) - (s.alongX ? 0.12f : 0f), Mathf.Min(b0.y, b1.y) - (s.alongX ? 0f : 0.12f),
                        Mathf.Max(b0.x, b1.x) + (s.alongX ? 0.12f : 0f), Mathf.Max(b0.y, b1.y) + (s.alongX ? 0f : 0.12f)), beamY, beamY + 0.25f, g.metal);
                    // A pair of lamps hanging under the beam; every other frame casts real light.
                    foreach (float side in new[] { -0.25f, 0.25f })
                    {
                        var c = P(a, lateral + side * width);
                        var head = LocalBox("Lamp", parent, Vector3.zero, s.alongX ? new Vector3(0.18f, 0.06f, 0.7f) : new Vector3(0.7f, 0.06f, 0.18f), lamp, collider: false);
                        head.transform.position = new Vector3(c.x, beamY - 0.55f, c.y);
                        NoShadows(head);
                        LocalBox("Hanger", parent, Vector3.zero, new Vector3(0.03f, 0.5f, 0.03f), g.metal, collider: false).transform.position =
                            new Vector3(c.x, beamY - 0.27f, c.y);
                    }
                    // Every frame lights the ground beneath it, so a covered alley is a run of warm pools.
                    {
                        var c = P(a, lateral);
                        AddLight(parent, new Vector3(c.x, beamY - 0.8f, c.y), new Color(1f, 0.82f, 0.4f), R(9f, 13f), 9f, fixture: false, flicker: Chance(0.1f));
                    }
                }
                // Longitudinal rails tie the frames together; half get a grating roof.
                foreach (float side in new[] { -1f, 1f })
                {
                    var r0 = P(a0, lateral + side * (width / 2f - 0.15f));
                    var r1 = P(a1, lateral + side * (width / 2f - 0.15f));
                    Box("Rail", parent, Rect.MinMaxRect(Mathf.Min(r0.x, r1.x) - 0.08f, Mathf.Min(r0.y, r1.y) - 0.08f, Mathf.Max(r0.x, r1.x) + 0.08f, Mathf.Max(r0.y, r1.y) + 0.08f),
                        beamY + 0.05f, beamY + 0.2f, g.rust);
                }
                if (Chance(0.5f))
                {
                    var roof = GameObject.CreatePrimitive(PrimitiveType.Quad);
                    roof.name = "Grating";
                    roof.transform.SetParent(parent, false);
                    var c = P((a0 + a1) / 2f, lateral);
                    roof.transform.position = new Vector3(c.x, beamY + 0.28f, c.y);
                    roof.transform.rotation = Quaternion.Euler(90f, s.alongX ? 0f : 90f, 0f);
                    roof.transform.localScale = new Vector3(a1 - a0, width - 0.1f, 1f);
                    roof.GetComponent<Renderer>().sharedMaterial = g.chainLink;
                    Object.DestroyImmediate(roof.GetComponent<Collider>());
                }
                stairZones.Add(r); // no props or curbs under the frames
            }
        }

        /// <summary>Kerbs, wrecked cars and jersey barriers along the thoroughfares.</summary>
        private static void BuildStreetProps(Transform root)
        {
            var parent = Group(root, "StreetProps");
            foreach (var s in segments)
            {
                if (!s.thoroughfare) continue;
                var r = s.rect;
                float width = s.alongX ? r.height : r.width;
                if (width < g.walkwayWidth - 0.1f || TouchesOpen(r) || !Flat(r, out float ground)) continue;
                float lateral = s.alongX ? r.center.y : r.center.x;
                float a0 = (s.alongX ? r.xMin : r.yMin) + g.walkwayWidth / 2f, a1 = (s.alongX ? r.xMax : r.yMax) - g.walkwayWidth / 2f;
                Rect Span(float from, float to, float l0, float l1) => s.alongX ? Rect.MinMaxRect(from, l0, to, l1) : Rect.MinMaxRect(l0, from, l1, to);

                // A raised kerb down one side, unless something else is in the way.
                // From version 6 kerbs line the buildings instead (BuildKerbs); the rolls are kept so the rest comes out the same.
                if (!Blocked(r) && Chance(0.6f))
                {
                    float side = Chance(0.5f) ? -1f : 1f;
                    float l0 = lateral + side * (width / 2f), l1 = lateral + side * (width / 2f - 0.75f);
                    if (!Since(6))
                        Box("Kerb", parent, Span(a0, a1, Mathf.Min(l0, l1), Mathf.Max(l0, l1)), ground - 0.1f, ground + 0.15f, g.concreteDark);
                }

                if (!Chance(g.streetPropChance)) continue;
                float at = R(a0 + 2f, Mathf.Max(a0 + 2.1f, a1 - 4f));
                if (Chance(0.6f))
                {
                    // Wrecked car parked hard against one wall.
                    float side = Chance(0.5f) ? -1f : 1f;
                    // From version 6 against the kerb rather than the wall.
                    float l = lateral + side * (width / 2f - 0.95f - (Since(9) ? g.sidewalkWidth : Since(6) ? g.kerbWidth : 0f));
                    var spot = Span(at, at + 4f, l - 0.85f, l + 0.85f);
                    if (Blocked(spot)) continue;
                    var car = new GameObject("WreckedCar").transform;
                    car.SetParent(parent, false);
                    car.position = new Vector3(spot.center.x, ground, spot.center.y);
                    car.rotation = Quaternion.Euler(R(-3f, 3f), (s.alongX ? 90f : 0f) + R(-8f, 8f), R(-4f, 4f));
                    LocalBox("Body", car, new Vector3(0f, 0.55f, 0f), new Vector3(1.7f, 0.65f, 4f), Chance(0.5f) ? g.rust : g.metal);
                    LocalBox("Cabin", car, new Vector3(0f, 1.1f, -0.2f), new Vector3(1.5f, 0.5f, 2f), g.rust);
                    foreach (var wx in new[] { -0.8f, 0.8f }) foreach (var wz in new[] { -1.3f, 1.3f })
                        LocalBox("Wheel", car, new Vector3(wx, 0.25f, wz), new Vector3(0.2f, 0.5f, 0.5f), g.trash, collider: false);
                }
                else
                {
                    // Jersey barrier across part of the road.
                    float side = Chance(0.5f) ? -1f : 1f;
                    // From version 9 from the sidewalk's edge.
                    float walk = Since(9) ? g.sidewalkWidth : 0f;
                    float l0 = lateral + side * (width / 2f - walk), l1 = lateral + side * (width / 2f - walk - R(1.4f, 2f));
                    var spot = Span(at, at + 0.7f, Mathf.Min(l0, l1), Mathf.Max(l0, l1));
                    if (Blocked(spot)) continue;
                    Box("Barrier", parent, spot, ground - 0.05f, ground + 0.85f, g.concrete);
                    Box("BarrierFoot", parent, Grow(spot, 0.12f), ground - 0.05f, ground + 0.25f, g.concrete);
                }
            }
        }

        /// <summary>
        /// Street-level doorways that NPCs walk out of and back into: a dark opening in a concrete frame on
        /// a building face that opens onto a walkway, on level ground. Split evenly between the districts
        /// and spaced apart so NPCs come and go all over the map.
        /// </summary>
        private static void BuildDoorways(Transform root)
        {
            g.doorways.Clear();
            if (g.npcDoorways <= 0 || g.districts.Count == 0) return;
            var parent = Group(root, "Doorways");
            // Near-black but lit, so the opening still takes the district's murk and fog at a distance.
            var opening = Mat("World_DoorVoid", new Color(0.008f, 0.008f, 0.008f));

            var byDistrict = new List<(Vector2 p, Vector2 normal, float ground)>[g.districts.Count];
            for (int d = 0; d < byDistrict.Length; d++) byDistrict[d] = new List<(Vector2, Vector2, float)>();
            foreach (var b in plans)
            {
                if (b.hollow || b.property) continue;
                foreach (var normal in Faces)
                {
                    if (!ExposedPoint(b.footprint, normal, 1.2f, out var p, walkIndex)) continue;
                    // Room to step out onto level ground, clear of stairs, catwalks and the pit.
                    var outside = p + normal * 1.4f;
                    if (!Walkable(outside) || OffMap(outside) || !LevelOutside(p, normal, out float ground)) continue;
                    if (NearCatwalk(p, 1.5f) || NearStairs(p, 2f) || Vector2.Distance(p, g.pitCenter) < g.pitRadius + 4f) continue;
                    byDistrict[g.DistrictIndexAt(p)].Add((p, normal, ground));
                }
            }

            int quota = Mathf.CeilToInt((float)g.npcDoorways / g.districts.Count);
            foreach (var pool in byDistrict)
            {
                for (int placed = 0; placed < quota && pool.Count > 0 && g.doorways.Count < g.npcDoorways;)
                {
                    int pick = rng.Next(pool.Count);
                    var (p, normal, ground) = pool[pick];
                    pool.RemoveAt(pick);
                    bool spaced = true;
                    foreach (var other in g.doorways)
                        spaced &= Vector2.Distance(new Vector2(other.position.x, other.position.z), p) > g.doorwaySpacing;
                    if (!spaced) continue;

                    var door = Group(parent, "Doorway");
                    var along = new Vector2(normal.y, -normal.x);
                    // Concrete frame standing proud of the wall, and the dark opening inside it.
                    foreach (float side in new[] { -1f, 1f })
                    {
                        var c = p + along * side * 0.62f + normal * 0.09f;
                        Box("Jamb", door, Square(c, 0.12f), ground, ground + 2.3f, g.concreteDark);
                    }
                    var lc = p + normal * 0.09f;
                    Box("Lintel", door, normal.x != 0f
                        ? Rect.MinMaxRect(lc.x - 0.09f, lc.y - 0.74f, lc.x + 0.09f, lc.y + 0.74f)
                        : Rect.MinMaxRect(lc.x - 0.74f, lc.y - 0.09f, lc.x + 0.74f, lc.y + 0.09f), ground + 2.15f, ground + 2.45f, g.concreteDark);
                    FaceBox("Opening", door, p, normal, ground + 1.05f, new Vector3(1.12f, 2.1f, 0.04f), opening);
                    if (Chance(g.doorwayLightChance))
                    {
                        var lp = p + normal * 0.45f;
                        BracketFrom(AddLight(door, new Vector3(lp.x, ground + 2.75f, lp.y), new Color(1f, 0.7f, 0.38f), R(3f, 5f), 6f, fixture: true, flicker: Chance(0.15f)), p, normal);
                    }

                    g.doorways.Add(new PittGreybox.Doorway { position = new Vector3(p.x, ground, p.y), normal = normal });
                    placed++;
                }
            }
        }

        private static Rect Square(Vector2 c, float half) => Rect.MinMaxRect(c.x - half, c.y - half, c.x + half, c.y + half);

        // ---------------------------------------------------------------- clearance (version 5)

        /// <summary>The gap alley fences leave, from version 5: the player (0.35 m radius and skin) fits through.</summary>
        private const float FenceGap = 1f;

        /// <summary>The player's capsule for clearance checks: radius with skin, height, and the step they can climb.</summary>
        private const float PlayerRadius = 0.44f, PlayerHeight = 2f, PlayerStep = 0.3f;

        /// <summary>Groups whose pieces are dressing, and can go where they'd block the way. Buildings, walls and stairs never are.</summary>
        private static readonly HashSet<string> ClutterGroups = new()
            { "Fences", "Shacks", "BuildingDetails", "Gantries", "Signage", "StreetProps", "Overhead", "Trash" };

        private static readonly Collider[] overlapBuffer = new Collider[64];

        /// <summary>
        /// Walks every small alley in player-sized steps: wherever the player's capsule can't fit across the alley,
        /// the dressing in the way (fences, shacks, pipes and boxes on walls, signs, gantry legs) is taken out. Runs
        /// with the city switched on, before batching and the NavMesh. Uses no randomness.
        /// </summary>
        private static void ClearAlleys(Transform root)
        {
            Physics.SyncTransforms();
            // Stuck only counts away from an alley's ends, where a dead end's closing wall is meant to be.
            int sections = 0, cleared = 0, stuck = 0;
            foreach (var a in alleys)
            {
                bool alongX = a.width > a.height;
                float length = alongX ? a.width : a.height, width = alongX ? a.height : a.width;
                float start = alongX ? a.xMin : a.yMin, mid = alongX ? a.center.y : a.center.x;
                for (float d = 0.2f; d <= length - 0.2f; d += 0.25f)
                {
                    sections++;
                    if (Passable(root, alongX, start + d, mid, width)) continue;
                    var at = alongX ? new Vector2(start + d, mid) : new Vector2(mid, start + d);
                    float y = FloorAt(at);
                    var half = alongX ? new Vector3(0.3f, (PlayerHeight - PlayerStep) / 2f, width / 2f) : new Vector3(width / 2f, (PlayerHeight - PlayerStep) / 2f, 0.3f);
                    int n = Physics.OverlapBoxNonAlloc(new Vector3(at.x, y + PlayerStep + half.y, at.y), half, overlapBuffer, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore);
                    for (int i = 0; i < n; i++)
                    {
                        var c = overlapBuffer[i];
                        if (c == null || !IsClutter(c.transform, root)) continue;
                        RemoveClutter(c.transform, root);
                        cleared++;
                    }
                    if (d > 1f && d < length - 1f && !Passable(root, alongX, start + d, mid, width)) stuck++;
                }
            }
            Debug.Log($"PittGreybox: alley clearance checked {sections} sections, cleared {cleared} pieces of clutter, {stuck} still too tight.");
        }

        /// <summary>Whether the player's capsule fits somewhere across an alley at this point along it.</summary>
        private static bool Passable(Transform root, bool alongX, float along, float mid, float width)
        {
            float reach = width / 2f - PlayerRadius;
            if (reach < 0f) return false;
            for (float off = -reach; off <= reach + 0.001f; off += 0.05f)
            {
                var p = alongX ? new Vector2(along, mid + off) : new Vector2(mid + off, along);
                float y = FloorAt(p);
                var bottom = new Vector3(p.x, y + PlayerStep + PlayerRadius, p.y);
                var top = new Vector3(p.x, y + PlayerHeight - PlayerRadius, p.y);
                int n = Physics.OverlapCapsuleNonAlloc(bottom, top, PlayerRadius, overlapBuffer, ~0, QueryTriggerInteraction.Ignore);
                bool blocked = false;
                for (int i = 0; i < n && !blocked; i++)
                    blocked = overlapBuffer[i] != null && overlapBuffer[i].transform.IsChildOf(root);
                if (!blocked) return true;
            }
            return false;
        }

        /// <summary>The group a piece of the city sits in, directly under the root.</summary>
        private static Transform GroupOf(Transform t, Transform root)
        {
            while (t != null && t.parent != root) t = t.parent;
            return t;
        }

        private static bool IsClutter(Transform t, Transform root)
        {
            var group = GroupOf(t, root);
            return group != null && ClutterGroups.Contains(group.name);
        }

        /// <summary>Takes out a piece of dressing: the whole object directly under its group (a fence panel, a shack, a sign).</summary>
        private static void RemoveClutter(Transform t, Transform root)
        {
            var group = GroupOf(t, root);
            while (t.parent != null && t.parent != group) t = t.parent;
            t.gameObject.SetActive(false);
            if (Application.isPlaying) Object.Destroy(t.gameObject);
            else Object.DestroyImmediate(t.gameObject);
        }

        // ---------------------------------------------------------------- properties (version 5)

        /// <summary>Where the player can get to from: on the NavMesh, on the thoroughfare nearest the centre of the central district.</summary>
        private static Vector3? navOrigin;

        /// <summary>
        /// The properties, last of all: the player's home, one for sale per district and the businesses, each
        /// only at a door the player can reach (see <see cref="Accessible"/>), with any dressing in front of the
        /// door cleared away. Built switched off, then woken, as the rest of the city was.
        /// </summary>
        private static void PlaceProperties(Transform root)
        {
            navOrigin = FindNavOrigin();
            if (navOrigin == null) Debug.LogWarning("PittGreybox: nowhere on the NavMesh to check property doors from; they won't be checked.");

            ChooseProperties();
            var holder = new GameObject("PropertyBuild").transform;
            holder.SetParent(root, false);
            holder.gameObject.SetActive(false);
            BuildProperties(holder);
            // The home is chosen in there: clear in front of every door now they're all known.
            foreach (var p in plans)
                if (p.property) ClearDoor(p, root);
            var built = holder.Find("Properties");
            if (built != null) built.SetParent(root, true);
            Object.DestroyImmediate(holder.gameObject);
        }

        /// <summary>
        /// The thoroughfare nearest the middle of the central district whose NavMesh joins up with most of the others.
        /// The very nearest can sit in a sunken sump square cut off from the streets around it, and every door
        /// checked from there would fail.
        /// </summary>
        private static Vector3? FindNavOrigin()
        {
            var centre = g.districts.Count > 0 ? g.districts[0].center : Vector2.zero;
            var points = new List<(Vector3 point, float distance)>();
            foreach (var s in segments)
            {
                if (!s.thoroughfare) continue;
                var c = s.rect.center;
                if (!UnityEngine.AI.NavMesh.SamplePosition(new Vector3(c.x, g.HeightAt(c), c.y), out var hit, 2f, UnityEngine.AI.NavMesh.AllAreas)) continue;
                points.Add((hit.position, Vector2.Distance(c, centre)));
            }
            if (points.Count == 0) return null;

            // Thoroughfares spread across the city to test against.
            var targets = new List<Vector3>();
            int stride = Mathf.Max(1, points.Count / 24);
            for (int i = 0; i < points.Count; i += stride) targets.Add(points[i].point);

            points.Sort((a, b) => a.distance.CompareTo(b.distance));
            var path = new UnityEngine.AI.NavMeshPath();
            Vector3? best = null;
            int bestReached = -1;
            for (int i = 0; i < Mathf.Min(10, points.Count); i++)
            {
                int reached = 0;
                foreach (var t in targets)
                    if (UnityEngine.AI.NavMesh.CalculatePath(points[i].point, t, UnityEngine.AI.NavMesh.AllAreas, path) &&
                        path.status == UnityEngine.AI.NavMeshPathStatus.PathComplete) reached++;
                if (reached * 2 > targets.Count) return points[i].point;
                if (reached > bestReached)
                {
                    bestReached = reached;
                    best = points[i].point;
                }
            }
            return best;
        }

        /// <summary>The space just outside a door, a player's width across and a step and a half out.</summary>
        private static void DoorFront(Plan p, out Vector3 centre, out Vector3 half)
        {
            var c = p.doorPoint + p.doorNormal * 0.8f;
            half = p.doorNormal.x != 0f ? new Vector3(0.7f, 0.9f, 0.65f) : new Vector3(0.65f, 0.9f, 0.7f);
            centre = new Vector3(c.x, p.doorGround + PlayerStep + 0.9f, c.y);
        }

        /// <summary>
        /// From version 5, whether the player can use a door: nothing but dressing just outside it, and a path on
        /// the NavMesh from the middle of the city to the street in front.
        /// </summary>
        private static bool Accessible(Plan p)
        {
            if (!Since(5)) return true;
            var root = LastRoot;
            if (root == null) return true;
            DoorFront(p, out var centre, out var half);
            int n = Physics.OverlapBoxNonAlloc(centre, half, overlapBuffer, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var c = overlapBuffer[i];
                if (c != null && c.transform.IsChildOf(root) && !IsClutter(c.transform, root)) return false;
            }
            if (navOrigin == null) return true;
            var front = p.doorPoint + p.doorNormal * 1.5f;
            if (!UnityEngine.AI.NavMesh.SamplePosition(new Vector3(front.x, p.doorGround, front.y), out var hit, 1.2f, UnityEngine.AI.NavMesh.AllAreas)) return false;
            var path = new UnityEngine.AI.NavMeshPath();
            return UnityEngine.AI.NavMesh.CalculatePath(navOrigin.Value, hit.position, UnityEngine.AI.NavMesh.AllAreas, path) &&
                   path.status == UnityEngine.AI.NavMeshPathStatus.PathComplete;
        }

        /// <summary>Takes any dressing out of the space in front of a property door.</summary>
        private static void ClearDoor(Plan p, Transform root)
        {
            DoorFront(p, out var centre, out var half);
            int n = Physics.OverlapBoxNonAlloc(centre, half, overlapBuffer, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var c = overlapBuffer[i];
                if (c != null && c.transform.IsChildOf(root) && IsClutter(c.transform, root)) RemoveClutter(c.transform, root);
            }
        }

        /// <summary>The root of the city being built (set while <see cref="BuildSteps"/> runs).</summary>
        private static Transform LastRoot;

        // ---------------------------------------------------------------- navigation

        /// <summary>
        /// Builds walkable areas for NPCs over the city: streets, alleys, squares, stairs and catwalks, but not
        /// rooftops or the distant ceiling. Built from colliders so it matches what the player can walk on.
        /// The agent's size is set in the project (PittWorldAssets keeps it right).
        /// </summary>
        private static void BuildNavMesh()
        {
            var surface = g.GetComponent<NavMeshSurface>();
            if (surface == null) surface = g.gameObject.AddComponent<NavMeshSurface>();
            surface.agentTypeID = 0;
            surface.collectObjects = CollectObjects.Volume;
            // From the pit floor and the lowest ground up past catwalks over the highest.
            float bottom = TerrainBottom + 2f, top = 9f;
            for (float x = -g.HalfSize; x <= g.HalfSize; x += 4f)
            for (float z = -g.HalfSize; z <= g.HalfSize; z += 4f)
                top = Mathf.Max(top, g.HeightAt(new Vector2(x, z)) + 9f);
            surface.center = new Vector3(0f, (top + bottom) / 2f, 0f);
            surface.size = new Vector3(g.citySize + 2f, top - bottom, g.citySize + 2f);
            surface.useGeometry = UnityEngine.AI.NavMeshCollectGeometry.PhysicsColliders;
            surface.layerMask = ~0;
            surface.RemoveData();
            surface.navMeshData = null;
            // A terrain made moments ago can be missing from physics, and the bake would leave out every street.
            foreach (var tc in g.GetComponentsInChildren<TerrainCollider>())
            {
                tc.enabled = false;
                tc.enabled = true;
            }
            KeepNpcsOutOfPit();
            Physics.SyncTransforms();
            surface.BuildNavMesh();
        }

        /// <summary>
        /// Marks the scrap pit and its ramp not walkable, so NPCs walk round the pit rather than through it. Strips
        /// across the bowl follow its round edge; all of it stays below street level, so the rim and anything
        /// crossing over the ramp are untouched.
        /// </summary>
        private static void KeepNpcsOutOfPit()
        {
            var root = g.transform.Find(GeneratedName);
            if (root == null) return;
            var parent = Group(root, "PitNavBlock");
            int notWalkable = UnityEngine.AI.NavMesh.GetAreaFromName("Not Walkable");
            float street = g.GroundAt(g.pitCenter);
            float bottom = street - g.pitDepth - 2f, top = street - 0.5f;
            void Block(Rect area)
            {
                var volume = new GameObject("NoWalk").AddComponent<NavMeshModifierVolume>();
                volume.transform.SetParent(parent, false);
                volume.area = notWalkable;
                volume.transform.position = new Vector3(area.center.x, (top + bottom) / 2f, area.center.y);
                volume.center = Vector3.zero;
                volume.size = new Vector3(area.width, top - bottom, area.height);
            }

            const int strips = 12;
            float r = g.pitRadius, width = 2f * r / strips;
            for (int i = 0; i < strips; i++)
            {
                // Out to the circle at the strip's edge nearest the middle, so the whole bowl is covered.
                float x0 = -r + i * width, inner = Mathf.Min(Mathf.Abs(x0), Mathf.Abs(x0 + width));
                if (x0 < 0f && x0 + width > 0f) inner = 0f;
                float half = Mathf.Sqrt(Mathf.Max(0f, r * r - inner * inner));
                Block(Rect.MinMaxRect(g.pitCenter.x + x0, g.pitCenter.y - half, g.pitCenter.x + x0 + width, g.pitCenter.y + half));
            }
            // The ramp's trench (it climbs out along +Z), edges included.
            float rampHalf = g.rampWidth * 0.5f + 1.5f;
            Block(Rect.MinMaxRect(g.pitCenter.x - rampHalf, g.pitCenter.y, g.pitCenter.x + rampHalf, g.pitCenter.y + g.RampEnd));
        }

        private static void BuildDetails(Transform root)
        {
            var details = Group(root, "BuildingDetails");
            var shacks = Group(root, "Shacks");

            foreach (var b in plans)
            {
                var fp = b.footprint;
                float height = b.top - b.ground;

                if (Chance(g.windowChance))
                {
                    int count = rng.Next(1, g.maxWindows + 1);
                    for (int k = 0; k < count; k++)
                    {
                        var normal = Faces[rng.Next(4)];
                        if (!ExposedPoint(fp, normal, 0.8f, out var p)) continue;
                        float y = R(b.ground + 2.5f, Mathf.Max(b.ground + 2.6f, b.top - 1.2f));
                        var win = FaceBox("Window", details, p, normal, y, new Vector3(R(0.7f, 1.5f), R(0.7f, 1.3f), 0.08f),
                            Chance(0.75f) ? g.windowWarm : g.windowCold);
                        NoShadows(win);
                    }
                }

                if (height > 9f && Chance(g.ledgeChance))
                {
                    // Decorative only: no collider, so they never snag heads on catwalks or shacks.
                    for (float y = b.ground + 5f; y < b.top - 1.5f; y += R(4.5f, 6f))
                        Object.DestroyImmediate(Box("Ledge", details, Grow(fp, 0.3f), y, y + 0.3f, g.concreteDark).GetComponent<Collider>());
                }

                if (height > 14f && Chance(g.cantileverChance))
                {
                    // From version 5 it caps the roof a touch above it: flush, the two tops fought.
                    var overhang = Grow(fp, R(0.8f, 1.6f));
                    Box("Cantilever", details, overhang, b.top - R(4f, 7f), b.top + (Since(5) ? FlushOffset : 0f), g.concrete);
                }

                if (height > 8f)
                {
                    foreach (var normal in Faces)
                    {
                        if (!Chance(g.wallShackChance)) continue;
                        if (!ExposedPoint(fp, normal, 2f, out var p, streetIndex) || NearCatwalk(p, 2.5f)) continue;
                        // Keep headroom over stairs.
                        bool overStairs = false;
                        foreach (var z in stairZones) overStairs |= Grow(z, 3f).Contains(p);
                        if (overStairs) continue;
                        float baseY = FootAt(p, normal) + R(4.5f, Mathf.Min(10f, height - 3.5f));
                        BuildWallShack(shacks, p, normal, baseY);
                    }
                }

                if (fp.width > 4f && fp.height > 4f && Chance(g.rooftopShackChance))
                {
                    float w = R(2.5f, Mathf.Min(4.5f, fp.width - 0.6f));
                    float d = R(2.5f, Mathf.Min(4.5f, fp.height - 0.6f));
                    var c = new Vector2(R(fp.xMin + w / 2f, fp.xMax - w / 2f), R(fp.yMin + d / 2f, fp.yMax - d / 2f));
                    var shack = new GameObject("RooftopShack").transform;
                    shack.SetParent(shacks, false);
                    shack.position = new Vector3(c.x, b.top, c.y);
                    shack.rotation = Quaternion.Euler(0f, rng.Next(4) * 90f, 0f);
                    float sh = R(2.4f, 3f);
                    LocalBox("Walls", shack, new Vector3(0f, sh / 2f, 0f), new Vector3(w, sh, d), g.wood);
                    var roof = LocalBox("Roof", shack, new Vector3(0f, sh + 0.05f, 0f), new Vector3(w + 0.4f, 0.08f, d + 0.4f), g.metal);
                    roof.transform.localRotation = Quaternion.Euler(R(-6f, 6f), 0f, R(-4f, 4f));
                }

                // Awnings over street-level openings.
                if (Chance(g.awningChance))
                {
                    var normal = Faces[rng.Next(4)];
                    if (ExposedPoint(fp, normal, 1.5f, out var p, streetIndex) && !NearCatwalk(p, 1f) && !NearStairs(p, 2f))
                    {
                        float foot = FootAt(p, normal);
                        // FaceBox already sets it out by half its depth, so its back edge meets the wall.
                        var awning = FaceBox("Awning", details, p, normal, foot + R(2.6f, 3f),
                            new Vector3(R(2f, 3.2f), 0.06f, 1f), g.metal);
                        awning.transform.rotation *= Quaternion.Euler(12f, 0f, 0f);
                        // Raised doorway with a stoop of three steps.
                        bool stoop = Chance(0.5f);
                        float sill = stoop ? 0.6f : 0f;
                        // From version 6 one without a stoop stands on the kerb.
                        float sole = stoop ? foot + sill : Since(6) ? FloorAt(p + normal * 0.1f) : foot;
                        FaceBox("Doorway", details, p, normal, sole + 1f, new Vector3(1f, 2f, 0.05f), g.trash);
                        if (stoop)
                            for (int k = 0; k < 3; k++)
                            {
                                var c = p + normal * (0.175f + k * 0.35f);
                                var step = Box("Stoop", details, normal.x != 0f
                                    ? Rect.MinMaxRect(c.x - 0.175f, c.y - 0.75f, c.x + 0.175f, c.y + 0.75f)
                                    : Rect.MinMaxRect(c.x - 0.75f, c.y - 0.175f, c.x + 0.75f, c.y + 0.175f), foot - 0.3f, foot + sill - k * 0.2f, g.concreteDark);
                            }
                    }
                }

                // Pipes running up the walls.
                if (Chance(g.wallPipeChance))
                {
                    var normal = Faces[rng.Next(4)];
                    if (ExposedPoint(fp, normal, 0.5f, out var p))
                    {
                        // From version 9 thinner, and sunk half into the wall so it stays within the kerb.
                        float radius = Since(9) ? R(0.04f, 0.08f) : R(0.1f, 0.2f);
                        float pipeTop = Mathf.Min(b.top, b.ground + R(6f, 20f));
                        var pipe = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                        pipe.name = "WallPipe";
                        pipe.transform.SetParent(details, false);
                        var c = p + normal * (Since(9) ? radius * 0.5f : radius + 0.08f);
                        pipe.transform.position = new Vector3(c.x, (b.groundMin + pipeTop) / 2f, c.y);
                        pipe.transform.localScale = new Vector3(radius * 2f, (pipeTop - b.groundMin) / 2f, radius * 2f);
                        pipe.GetComponent<Renderer>().sharedMaterial = Chance(0.5f) ? g.rust : g.metal;
                        Object.DestroyImmediate(pipe.GetComponent<Collider>());
                    }
                }
            }
        }

        private static void BuildWallShack(Transform parent, Vector2 wallPoint, Vector2 normal, float baseY)
        {
            float width = R(2.5f, 4f);
            float depth = R(1.6f, 2.4f);
            float height = R(2.4f, 3f);

            var shack = new GameObject("WallShack").transform;
            shack.SetParent(parent, false);
            var c = wallPoint + normal * (depth / 2f - 0.05f);
            shack.position = new Vector3(c.x, baseY, c.y);
            shack.rotation = Quaternion.LookRotation(new Vector3(normal.x, 0f, normal.y));

            LocalBox("Walls", shack, new Vector3(0f, height / 2f, 0f), new Vector3(width, height, depth), g.wood);
            var roof = LocalBox("Roof", shack, new Vector3(0f, height + 0.05f, 0.15f), new Vector3(width + 0.4f, 0.08f, depth + 0.5f), g.metal);
            roof.transform.localRotation = Quaternion.Euler(-6f, 0f, 0f);
            LocalBox("Platform", shack, new Vector3(0f, -0.05f, 0.2f), new Vector3(width + 0.4f, 0.1f, depth + 0.4f), g.wood);
            for (int i = -1; i <= 1; i += 2)
            {
                var strut = LocalBox("Strut", shack, new Vector3(i * (width / 2f - 0.3f), -0.9f, -depth / 2f + 0.55f),
                    new Vector3(0.15f, 2.2f, 0.15f), g.wood, collider: false);
                // Foot against the wall, leaning out to hold the platform's front.
                strut.transform.localRotation = Quaternion.Euler(40f, 0f, 0f);
            }
            if (Chance(0.5f))
            {
                var win = LocalBox("Window", shack, new Vector3(R(-width / 4f, width / 4f), height * 0.55f, depth / 2f + 0.03f),
                    new Vector3(0.6f, 0.5f, 0.04f), g.windowWarm, collider: false);
                NoShadows(win);
            }
        }

        // ---------------------------------------------------------------- signage

        /// <summary>Neon, shop signs, billboards and fluorescent strips: the glowing surfaces that carry the light.</summary>
        private static void BuildSignage(Transform root)
        {
            var parent = Group(root, "Signage");
            var lights = Group(root, "SignLights");

            foreach (var b in plans)
            {
                var fp = b.footprint;
                var palette = PaletteAt(fp.center);
                float height = b.top - b.ground;
                // Older saved palettes have no signage value; treat that as normal.
                float loud = palette.signage > 0f ? palette.signage : 1f;

                if (Chance(g.neonChance * loud))
                {
                    var normal = Faces[rng.Next(4)];
                    if (ExposedPoint(fp, normal, 0.6f, out var p, streetIndex) && !NearCatwalk(p, 1f))
                    {
                        Color c = Chance(0.65f) ? palette.accent : palette.primary;
                        float h = R(1.6f, 3.2f);
                        float y = FootAt(p, normal) + R(3.5f, Mathf.Clamp(height - h, 3.6f, 7f)) + h / 2f;
                        var sign = FaceBox("Neon", parent, p + normal * 0.15f, normal, y, new Vector3(0.3f, h, 0.12f), Glow(c, R(4f, 7f)));
                        NoShadows(sign);
                        // Frame bracket back to the wall.
                        FaceBox("NeonBracket", parent, p, normal, y, new Vector3(0.06f, h * 0.9f, 0.15f), g.metal);
                        if (Chance(0.25f))
                            AddLight(lights, ToV3(p + normal * 0.7f, y), c, R(3f, 5f), 6f, fixture: false);
                    }
                }

                if (Chance(g.shopSignChance * loud))
                {
                    var normal = Faces[rng.Next(4)];
                    if (ExposedPoint(fp, normal, 1.5f, out var p, streetIndex) && !NearCatwalk(p, 1f))
                    {
                        Color c = Chance(0.5f) ? palette.primary : palette.accent;
                        float w = R(1.6f, 3f), h = R(0.5f, 0.8f);
                        float y = FootAt(p, normal) + R(2.8f, 3.4f);
                        FaceBox("SignBacking", parent, p, normal, y, new Vector3(w, h, 0.12f), g.metal);
                        NoShadows(FaceBox("SignFace", parent, p + normal * 0.12f, normal, y, new Vector3(w - 0.15f, h - 0.15f, 0.03f),
                            Glow(c, R(1.5f, 3f))));
                    }
                }

                if (height > 18f && Chance(g.billboardChance * loud))
                {
                    var normal = Faces[rng.Next(4)];
                    FaceInfo(fp, normal, out _, out _, out float faceLength);
                    float w = Mathf.Min(R(5f, 8f), faceLength - 0.5f);
                    if (w > 3f && ExposedPoint(fp, normal, w / 2f, out var p))
                    {
                        float h = Mathf.Min(R(3f, 4.5f), w * 0.7f);
                        float y = b.top - R(3f, Mathf.Min(8f, height - h - 4f));
                        FaceBox("BillboardFrame", parent, p, normal, y, new Vector3(w + 0.3f, h + 0.3f, 0.2f), g.metal);
                        NoShadows(FaceBox("Billboard", parent, p + normal * 0.2f, normal, y, new Vector3(w, h, 0.03f),
                            Glow(Chance(0.7f) ? palette.primary : palette.accent, R(1.2f, 2.2f))));
                    }
                }
            }

            // Fluorescent tubes over doors and along walls, some failing.
            for (int i = 0; i < g.fluorescentStrips && plans.Count > 0; i++)
            {
                var b = plans[rng.Next(plans.Count)];
                var normal = Faces[rng.Next(4)];
                if (!ExposedPoint(b.footprint, normal, 0.8f, out var p)) continue;
                float y = FootAt(p, normal) + R(2.4f, 3f);
                var cold = new Color(0.8f, 0.92f, 1f);
                var tube = FaceBox("Fluorescent", parent, p + normal * 0.06f, normal, y, new Vector3(R(0.9f, 1.4f), 0.06f, 0.06f), Glow(cold, 5f));
                NoShadows(tube);
                if (Chance(0.6f))
                {
                    var light = AddLight(lights, ToV3(p + normal * 0.5f, y - 0.2f), cold, R(2.5f, 4f), 6f, fixture: false,
                        flicker: Chance(0.3f));
                    var flicker = light.GetComponentInChildren<LightFlicker>(true);
                    if (flicker != null)
                    {
                        // Move the tube under the flickering light so it dims with it.
                        tube.transform.SetParent(flicker.transform, true);
                        flicker.SetFixture(tube.GetComponent<Renderer>());
                    }
                }
            }
        }

        // ---------------------------------------------------------------- overhead

        private static void BuildOverhead(Transform root)
        {
            var parent = Group(root, "Overhead");
            foreach (var s in segments)
            {
                var r = s.rect;
                float width = s.alongX ? r.height : r.width;

                // Ducts running the length of the walkway against one wall.
                if (Chance(g.ductChance) && width >= g.walkwayWidth - 0.1f)
                {
                    float side = Chance(0.5f) ? -1f : 1f;
                    float sizeA = R(0.9f, 1.5f), sizeB = R(0.7f, 1.2f);
                    float lat = (s.alongX ? r.center.y : r.center.x) + side * (width / 2f - sizeA / 2f);
                    var mid = s.alongX ? new Vector2(r.center.x, lat) : new Vector2(lat, r.center.y);
                    if (!g.IsOpen(mid))
                    {
                        float y = g.HeightAt(mid) + R(6.5f, 9.5f);
                        float len = (s.alongX ? r.width : r.height) * R(0.6f, 1f);
                        var mat = Chance(0.5f) ? g.metal : g.rust;
                        // Hung on the wall it runs along, so that wall has to stand the whole length, above the duct.
                        var alongDir = s.alongX ? Vector2.right : Vector2.up;
                        var outward = (s.alongX ? Vector2.up : Vector2.right) * side;
                        var wallLine = mid + outward * (sizeA / 2f + 0.3f);
                        if (WallAlong(wallLine - alongDir * len / 2f, wallLine + alongDir * len / 2f, y + sizeB / 2f + 0.2f, y - sizeB / 2f))
                        {
                            var duct = LocalBox("Duct", parent, Vector3.zero,
                                s.alongX ? new Vector3(len, sizeB, sizeA) : new Vector3(sizeA, sizeB, len), mat, collider: false);
                            duct.transform.position = new Vector3(mid.x, y, mid.y);
                        }
                    }
                }

                if (!Chance(g.overheadChance)) continue;
                int count = rng.Next(1, 5);
                for (int k = 0; k < count; k++)
                {
                    var p = s.alongX ? new Vector2(R(r.xMin, r.xMax), r.center.y) : new Vector2(r.center.x, R(r.yMin, r.yMax));
                    if (g.IsOpen(p)) continue;
                    float y = g.HeightAt(p) + R(5f, 11f);
                    float span = width + 1.2f;
                    var across = s.alongX ? Vector3.forward : Vector3.right;
                    float kind = R(0f, 1f);
                    // Each end goes into a wall: drop it under the lower one if need be, or leave it out if that's
                    // too low to walk under (an end over an open square, a gap, or a short building).
                    var across2 = new Vector2(across.x, across.z);
                    float reach = width / 2f + 0.3f;
                    float lowerTop = Mathf.Min(WallTop(p - across2 * reach), WallTop(p + across2 * reach));
                    float floor = g.HeightAt(p);
                    if (lowerTop < y + 0.3f) y = lowerTop - 0.6f;
                    bool anchored = y >= floor + 4.5f && WallBottom(p - across2 * reach) < y && WallBottom(p + across2 * reach) < y;

                    if (kind < 0.25f)
                    {
                        float plankWidth = R(0.4f, 1f);
                        if (!anchored) continue;
                        var plank = LocalBox("Plank", parent, Vector3.zero, new Vector3(plankWidth, 0.1f, span), g.wood, collider: false);
                        plank.transform.position = new Vector3(p.x, y, p.y);
                        plank.transform.rotation = Quaternion.LookRotation(across);
                    }
                    else if (kind < 0.5f)
                    {
                        float radius = R(0.12f, 0.45f);
                        var pipeMaterial = Chance(0.5f) ? g.metal : g.rust;
                        if (!anchored) continue;
                        var pipe = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                        pipe.name = "Pipe";
                        pipe.transform.SetParent(parent, false);
                        pipe.transform.localScale = new Vector3(radius * 2f, span / 2f, radius * 2f);
                        pipe.transform.position = new Vector3(p.x, y, p.y);
                        pipe.transform.rotation = Quaternion.LookRotation(across) * Quaternion.Euler(90f, 0f, 0f);
                        pipe.GetComponent<Renderer>().sharedMaterial = pipeMaterial;
                        Object.DestroyImmediate(pipe.GetComponent<Collider>());
                    }
                    else
                    {
                        // Sagging cables, sometimes several strung together.
                        int strands = rng.Next(1, 4);
                        var a = new Vector3(p.x, y, p.y) - across * span / 2f;
                        var b = new Vector3(p.x, y + R(-1f, 1f), p.y) + across * span / 2f;
                        // A cable's far end can't be lower than the wall it's tied to either.
                        b.y = Mathf.Min(b.y, y + 0.3f);
                        for (int c = 0; c < strands; c++)
                        {
                            var offset = (s.alongX ? Vector3.right : Vector3.forward) * R(-0.6f, 0.6f);
                            Cable(parent, a + offset, b + offset, span * R(0.05f, 0.14f), anchored);
                        }
                    }
                }

                // Occasionally a long cable runs down the walkway itself.
                if (Chance(0.2f))
                {
                    float offset = R(-width / 3f, width / 3f);
                    // Run along the nearer wall, tied into it at both ends.
                    float wallSide = offset < 0f ? -1f : 1f;
                    float lat = (s.alongX ? r.center.y : r.center.x) + wallSide * (width / 2f - 0.15f);
                    var p0 = s.alongX ? new Vector2(r.xMin, lat) : new Vector2(lat, r.yMin);
                    var p1 = s.alongX ? new Vector2(r.xMax, lat) : new Vector2(lat, r.yMax);
                    float y = g.HeightAt(p0) + R(7f, 10f);
                    float y1 = y + R(-1f, 1f);
                    var outward = (s.alongX ? Vector2.up : Vector2.right) * wallSide * 0.45f;
                    var inset = (s.alongX ? Vector2.right : Vector2.up) * 1f;
                    bool tied = WallTop(p0 + inset + outward) > y + 0.3f && WallTop(p1 - inset + outward) > y1 + 0.3f &&
                                WallBottom(p0 + inset + outward) < y && WallBottom(p1 - inset + outward) < y1;
                    Cable(parent, ToV3(p0 + inset, y), ToV3(p1 - inset, y1), Vector2.Distance(p0, p1) * R(0.03f, 0.07f), tied);
                }
            }
        }

        /// <summary>A sagging cable between two points. With build false it only uses up its random numbers (so the
        /// rest of the city comes out the same whether or not it's there).</summary>
        private static void Cable(Transform parent, Vector3 a, Vector3 b, float sag, bool build = true)
        {
            const int points = 12;
            if (!build)
            {
                R(0.03f, 0.06f);
                return;
            }
            var go = new GameObject("Cable");
            go.transform.SetParent(parent, false);
            var line = go.AddComponent<LineRenderer>();
            line.sharedMaterial = g.cable;
            line.widthMultiplier = R(0.03f, 0.06f);
            line.positionCount = points;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            for (int i = 0; i < points; i++)
            {
                float t = i / (points - 1f);
                line.SetPosition(i, Vector3.Lerp(a, b, t) + Vector3.down * sag * 4f * t * (1f - t));
            }
        }

        private static Dictionary<Vector2Int, List<Plan>> planIndex;
        private const float PlanCell = 10f;

        private static List<Plan> PlansNear(Vector2 p)
        {
            if (planIndex == null)
            {
                planIndex = new Dictionary<Vector2Int, List<Plan>>();
                foreach (var plan in plans)
                {
                    var fp = plan.footprint;
                    for (int x = Mathf.FloorToInt(fp.xMin / PlanCell); x <= Mathf.FloorToInt(fp.xMax / PlanCell); x++)
                    for (int z = Mathf.FloorToInt(fp.yMin / PlanCell); z <= Mathf.FloorToInt(fp.yMax / PlanCell); z++)
                    {
                        var key = new Vector2Int(x, z);
                        if (!planIndex.TryGetValue(key, out var list)) planIndex[key] = list = new List<Plan>();
                        list.Add(plan);
                    }
                }
            }
            planIndex.TryGetValue(new Vector2Int(Mathf.FloorToInt(p.x / PlanCell), Mathf.FloorToInt(p.y / PlanCell)), out var near);
            return near;
        }

        /// <summary>Whether one of the city's buildings stands at a point.</summary>
        private static bool BuildingAt(Vector2 p)
        {
            var near = PlansNear(p);
            if (near != null)
                foreach (var plan in near)
                    if (plan.footprint.Contains(p)) return true;
            return false;
        }

        /// <summary>Top of the building standing at a point, or minus infinity in the open.</summary>
        private static float WallTop(Vector2 p)
        {
            float top = float.NegativeInfinity;
            var near = PlansNear(p);
            if (near != null)
                foreach (var plan in near)
                    if (plan.footprint.Contains(p)) top = Mathf.Max(top, plan.top);
            return top;
        }

        /// <summary>Foot of the building standing at a point, or plus infinity in the open.</summary>
        private static float WallBottom(Vector2 p)
        {
            float bottom = float.PositiveInfinity;
            var near = PlansNear(p);
            if (near != null)
                foreach (var plan in near)
                    if (plan.footprint.Contains(p)) bottom = Mathf.Min(bottom, plan.groundMin);
            return bottom;
        }

        /// <summary>Building wall all the way between two points (sampled every couple of metres), from below <paramref name="bottom"/> to above <paramref name="top"/>.</summary>
        private static bool WallAlong(Vector2 a, Vector2 b, float top, float bottom)
        {
            int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) / 2f));
            for (int i = 0; i <= steps; i++)
            {
                var p = Vector2.Lerp(a, b, i / (float)steps);
                if (WallTop(p) < top || WallBottom(p) > bottom) return false;
            }
            return true;
        }

        // ---------------------------------------------------------------- fences

        private static void BuildFences(Transform root)
        {
            var parent = Group(root, "Fences");
            // Nothing along the edge of the city: streets run straight out to the drop.
            // Chain-link across alley mouths, leaving a gap to squeeze through.
            foreach (var a in alleys)
            {
                if (!Chance(g.alleyFenceChance)) continue;
                bool alongX = a.width > a.height;
                float gap = alongX ? a.height : a.width;
                // The gap left to squeeze through: from version 5, wide enough for the player.
                float squeeze = Since(5) ? FenceGap : 0.85f;
                if (gap - squeeze < 0.3f) continue;
                foreach (float end in new[] { -1f, 1f })
                {
                    var tip = alongX ? new Vector2(end < 0 ? a.xMin : a.xMax, a.center.y) : new Vector2(a.center.x, end < 0 ? a.yMin : a.yMax);
                    var probe = tip + (alongX ? new Vector2(end * 0.5f, 0f) : new Vector2(0f, end * 0.5f));
                    if (!streetIndex.Contains(probe)) continue;
                    var inset = tip - (alongX ? new Vector2(end * 0.6f, 0f) : new Vector2(0f, end * 0.6f));
                    float fenceLength = gap - squeeze;
                    Vector2 p0, p1;
                    if (alongX)
                    {
                        p0 = new Vector2(inset.x, a.yMin);
                        p1 = new Vector2(inset.x, a.yMin + fenceLength);
                    }
                    else
                    {
                        p0 = new Vector2(a.xMin, inset.y);
                        p1 = new Vector2(a.xMin + fenceLength, inset.y);
                    }
                    FencePanel(parent, p0, p1, FloorAt(inset), 2.2f, g.chainLink, collider: true);
                    break;
                }
            }

            // A broken fence around the scrap pit rim.
            float rim = g.pitRadius + 0.6f;
            int panels = Mathf.CeilToInt(2f * Mathf.PI * rim / 2f);
            for (int i = 0; i < panels; i++)
            {
                float a0 = i / (float)panels * Mathf.PI * 2f, a1 = (i + 1) / (float)panels * Mathf.PI * 2f;
                var p0 = g.pitCenter + new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * rim;
                var p1 = g.pitCenter + new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * rim;
                if (g.InRampCorridor((p0 + p1) / 2f, 2f) || Chance(0.3f)) continue;
                FencePanel(parent, p0, p1, Mathf.Max(g.HeightAt(p0), g.HeightAt(p1)), 1.4f, g.chainLink, collider: true);
            }
        }

        /// <summary>A chain-link panel with posts and a top rail between two points.</summary>
        private static void FencePanel(Transform parent, Vector2 from, Vector2 to, float baseY, float height, Material mesh, bool collider)
        {
            float length = Vector2.Distance(from, to);
            if (length < 0.2f) return;
            var mid = (from + to) / 2f;
            var dir = (to - from) / length;
            var rot = Quaternion.LookRotation(new Vector3(-dir.y, 0f, dir.x));

            var panel = new GameObject("Fence").transform;
            panel.SetParent(parent, false);
            panel.position = new Vector3(mid.x, baseY, mid.y);
            panel.rotation = rot;

            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "Mesh";
            quad.transform.SetParent(panel, false);
            quad.transform.localPosition = new Vector3(0f, height / 2f, 0f);
            quad.transform.localScale = new Vector3(length, height, 1f);
            quad.GetComponent<Renderer>().sharedMaterial = mesh;
            NoShadows(quad);
            Object.DestroyImmediate(quad.GetComponent<Collider>());

            LocalBox("Rail", panel, new Vector3(0f, height, 0f), new Vector3(length, 0.05f, 0.05f), g.metal, collider: false);
            LocalBox("Post", panel, new Vector3(-length / 2f, height / 2f, 0f), new Vector3(0.07f, height, 0.07f), g.metal, collider: false);
            LocalBox("Post", panel, new Vector3(length / 2f, height / 2f, 0f), new Vector3(0.07f, height, 0.07f), g.metal, collider: false);
            if (collider)
            {
                var box = panel.gameObject.AddComponent<BoxCollider>();
                box.center = new Vector3(0f, height / 2f, 0f);
                box.size = new Vector3(length, height, 0.1f);
            }
        }

        /// <summary>An invisible wall.</summary>
        private static void Blocker(Transform parent, Rect xz, float bottom, float top)
        {
            var go = new GameObject("Blocker");
            go.transform.SetParent(parent, false);
            go.transform.position = new Vector3(xz.center.x, (bottom + top) / 2f, xz.center.y);
            var box = go.AddComponent<BoxCollider>();
            box.size = new Vector3(xz.width, top - bottom, xz.height);
        }

        // ---------------------------------------------------------------- trash

        private static void BuildTrash(Transform root)
        {
            var parent = Group(root, "Trash");
            void Scatter(Rect r, bool alongX, int count, float inset)
            {
                for (int i = 0; i < count; i++)
                {
                    // Pushed up against one wall so the walkway stays passable.
                    float side = Chance(0.5f) ? -1f : 1f;
                    var p = alongX
                        ? new Vector2(R(r.xMin, r.xMax), r.center.y + side * (r.height / 2f - inset))
                        : new Vector2(r.center.x + side * (r.width / 2f - inset), R(r.yMin, r.yMax));
                    if (Vector2.Distance(p, g.startPoint) < g.clearStartRadius || g.InRampCorridor(p, 0.5f) || NearCatwalk(p, 0.3f)) continue;
                    SpawnTrash(parent, p, small: Mathf.Min(r.width, r.height) < 2.5f);
                }
            }

            // From version 6 street trash lies against the kerb, not on its edge.
            float streetInset = 0.35f + (Since(6) ? g.kerbWidth : 0f);
            float sidewalkInset = 0.35f + (Since(9) ? g.sidewalkWidth : Since(6) ? g.kerbWidth : 0f);
            foreach (var s in segments) Scatter(s.rect, s.alongX, rng.Next(0, g.maxTrashPerSegment + 1), s.thoroughfare ? sidewalkInset : streetInset);
            foreach (var a in alleys) Scatter(a, a.width > a.height, rng.Next(0, 3), 0.35f);
        }

        private static void SpawnTrash(Transform parent, Vector2 p, bool small)
        {
            float y = FloorAt(p);
            bool bag = Chance(0.5f);
            var t = GameObject.CreatePrimitive(bag ? PrimitiveType.Sphere : PrimitiveType.Cube);
            t.name = bag ? "TrashBag" : "Debris";
            t.transform.SetParent(parent, false);
            float s = small ? R(0.25f, 0.5f) : bag ? R(0.4f, 0.8f) : R(0.3f, 0.8f);
            t.transform.localScale = bag ? new Vector3(s, s * 0.65f, s * 0.9f) : new Vector3(s, s * R(0.3f, 1f), s * R(0.5f, 1.2f));
            t.transform.position = new Vector3(p.x, y + t.transform.localScale.y * 0.4f, p.y);
            t.transform.rotation = Quaternion.Euler(R(-10f, 10f), R(0f, 360f), R(-10f, 10f));
            t.GetComponent<Renderer>().sharedMaterial = bag ? g.trash : Chance(0.5f) ? g.rust : g.concreteDark;
            Object.DestroyImmediate(t.GetComponent<Collider>());
        }

        // ---------------------------------------------------------------- scrap pit

        private static void BuildScrapPit(Transform root)
        {
            var parent = Group(root, "ScrapPit");
            float floor = g.PitFloorRadius - 3f;

            for (int i = 0; i < g.scrapPiles; i++)
            {
                Vector2 c = RandomPitPoint(floor, 2f);
                var pile = new GameObject("ScrapPile").transform;
                pile.SetParent(parent, false);
                pile.position = new Vector3(c.x, g.HeightAt(c), c.y);

                int pieces = rng.Next(5, 11);
                float level = 0f;
                for (int p = 0; p < pieces; p++)
                {
                    float s = R(0.6f, 2.5f) * (1f - p / (float)pieces * 0.6f);
                    var piece = LocalBox("Scrap", pile, new Vector3(R(-1.5f, 1.5f), level + s * 0.3f, R(-1.5f, 1.5f)),
                        new Vector3(s, s * R(0.3f, 0.8f), s * R(0.6f, 1.4f)), Chance(0.5f) ? g.rust : g.metal);
                    piece.transform.localRotation = Quaternion.Euler(R(-25f, 25f), R(0f, 360f), R(-25f, 25f));
                    level += s * 0.25f;
                }
            }

            for (int i = 0; i < 4; i++)
            {
                Vector2 c = RandomPitPoint(floor, 3f);
                var wreck = LocalBox("Wreck", parent, new Vector3(c.x, g.HeightAt(c) + 0.6f, c.y), new Vector3(2f, 1.4f, 4.2f), g.rust);
                wreck.transform.rotation = Quaternion.Euler(R(-8f, 8f), R(0f, 360f), R(-12f, 12f));
            }

            for (int i = 0; i < 4; i++)
            {
                Vector2 c = RandomPitPoint(floor, 0f);
                AddPostLight(parent, c, 4f, new Color(0.75f, 0.85f, 1f), 35f, 24f);
            }

            // Loot is spawned at runtime by ScrapPitSpawner, so the pit restocks over time.
        }

        private static Vector2 RandomPitPoint(float radius, float rampClearance)
        {
            for (int attempt = 0; attempt < 50; attempt++)
            {
                float a = R(0f, Mathf.PI * 2f);
                float d = Mathf.Sqrt((float)rng.NextDouble()) * radius;
                var p = g.pitCenter + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * d;
                if (!g.InRampCorridor(p, rampClearance)) return p;
            }
            return g.pitCenter;
        }

        // ---------------------------------------------------------------- lights

        private static void BuildLights(Transform root)
        {
            var parent = Group(root, "Lights");

            // Wall lamps on faces that open onto walkways: fewer, brighter, in district colours.
            var candidates = new List<(Vector2 point, Vector2 normal)>();
            foreach (var b in plans)
            foreach (var normal in Faces)
                if (ExposedPoint(b.footprint, normal, 0.6f, out var p, streetIndex) && !NearCatwalk(p, 1f))
                    candidates.Add((p, normal));

            for (int i = 0; i < g.wallLamps && candidates.Count > 0; i++)
            {
                int pick = rng.Next(candidates.Count);
                var (p, normal) = candidates[pick];
                candidates.RemoveAt(pick);
                var palette = PaletteAt(p);
                float roll = R(0f, 1f);
                // Street lamps are sodium and fluorescent, like real fixtures; the district colour comes from
                // the murk and the neon, so light pools read warm against it.
                Color color = roll < 0.55f ? new Color(1f, 0.64f, 0.28f)
                    : roll < 0.8f ? new Color(1f, 0.9f, 0.62f)
                    : palette.primary;
                AddWallLamp(parent, p, normal, g.HeightAt(p) + R(3f, 4.2f), color, R(40f, 70f), R(14f, 19f), Chance(0.08f));
            }

            // Dim lights deep in the alleys.
            var alleyPool = new List<Rect>(alleys);
            for (int i = 0; i < g.alleyLights && alleyPool.Count > 0; i++)
            {
                int pick = rng.Next(alleyPool.Count);
                var a = alleyPool[pick];
                alleyPool.RemoveAt(pick);
                var p = new Vector2(R(a.xMin, a.xMax), R(a.yMin, a.yMax));
                if (g.IsOpen(p)) continue;
                var palette = PaletteAt(p);
                var color = Chance(0.5f) ? palette.accent : new Color(0.75f, 0.95f, 0.6f);
                float intensity = R(7f, 12f);
                bool flickers = Chance(0.2f);
                // On a bracket off one of the alley's side walls (the nearer one that stands tall enough), not in mid-air.
                bool alongX = a.width >= a.height;
                float lateral = alongX ? p.y : p.x, low = alongX ? a.yMin : a.xMin, high = alongX ? a.yMax : a.xMax;
                float y = g.HeightAt(p) + 2.8f;
                bool lowFirst = lateral - low <= high - lateral;
                bool placed = false;
                foreach (bool useLow in new[] { lowFirst, !lowFirst })
                {
                    float wallLine = useLow ? low : high;
                    var normal = alongX ? new Vector2(0f, useLow ? 1f : -1f) : new Vector2(useLow ? 1f : -1f, 0f);
                    var wallPoint = alongX ? new Vector2(p.x, wallLine) : new Vector2(wallLine, p.y);
                    var behind = wallPoint - normal * 0.3f;
                    if (WallTop(behind) < y + 0.5f || WallBottom(behind) > y - 1f) continue;
                    // Out from the wall, but never across more than half the alley.
                    float reach = Mathf.Min(0.35f, (high - low) / 2f);
                    var bulb = wallPoint + normal * reach;
                    BracketFrom(AddLight(parent, new Vector3(bulb.x, y, bulb.y), color, intensity, 8f, fixture: true, flicker: flickers), wallPoint, normal);
                    placed = true;
                    break;
                }
                if (!placed) continue;
            }

            // Gang squares: a pair of posts on opposite corners, in the district's colour.
            foreach (var sq in g.gangSquares)
            {
                var r = Grow(sq.rect, -1.5f);
                var color = PaletteAt(sq.rect.center).primary;
                bool flip = Chance(0.5f);
                AddPostLight(parent, flip ? r.min : new Vector2(r.xMin, r.yMax), 4.5f, color, 22f, 16f);
                AddPostLight(parent, flip ? r.max : new Vector2(r.xMax, r.yMin), 4.5f, color, 22f, 16f);
            }
        }

        private static void AddWallLamp(Transform parent, Vector2 wallPoint, Vector2 normal, float y, Color color,
            float intensity, float range, bool flicker)
        {
            var lamp = new GameObject("WallLamp").transform;
            lamp.SetParent(parent, false);
            lamp.position = new Vector3(wallPoint.x, y, wallPoint.y);
            lamp.rotation = Quaternion.LookRotation(new Vector3(normal.x, 0f, normal.y));
            LocalBox("Arm", lamp, new Vector3(0f, 0f, 0.25f), new Vector3(0.08f, 0.08f, 0.5f), g.metal, collider: false);
            var head = LocalBox("Head", lamp, new Vector3(0f, -0.08f, 0.5f), new Vector3(0.3f, 0.12f, 0.3f), Glow(color, 4f), collider: false);
            NoShadows(head);
            var light = AddLightComponent(lamp, new Vector3(0f, -0.3f, 0.6f), color, intensity, range);
            if (flicker) MakeFlicker(light, head);
        }

        private static void AddPostLight(Transform parent, Vector2 p, float height, Color color, float intensity, float range)
        {
            float ground = g.HeightAt(p);
            var lamp = new GameObject("PostLight").transform;
            lamp.SetParent(parent, false);
            lamp.position = new Vector3(p.x, ground + height, p.y);
            var pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            pole.name = "Pole";
            pole.transform.SetParent(lamp, false);
            // Unity's cylinder is 2 units tall, so half the height is the Y scale.
            pole.transform.localScale = new Vector3(0.15f, height / 2f, 0.15f);
            pole.transform.localPosition = new Vector3(0f, -height / 2f, 0f);
            pole.GetComponent<Renderer>().sharedMaterial = g.metal;
            NoShadows(LocalBox("Head", lamp, new Vector3(0f, 0.1f, 0f), new Vector3(0.5f, 0.15f, 0.5f), Glow(color, 4f), collider: false));
            AddLightComponent(lamp, new Vector3(0f, -0.2f, 0f), color, intensity, range);
        }

        private static GameObject AddLight(Transform parent, Vector3 position, Color color, float intensity, float range,
            bool fixture, bool flicker = false)
        {
            var lamp = new GameObject("Light").transform;
            lamp.SetParent(parent, false);
            lamp.position = position;
            GameObject bulb = null;
            if (fixture)
            {
                bulb = LocalBox("Bulb", lamp, Vector3.zero, new Vector3(0.15f, 0.15f, 0.15f), Glow(color, 4f), collider: false);
                NoShadows(bulb);
            }
            var light = AddLightComponent(lamp, new Vector3(0f, -0.15f, 0f), color, intensity, range);
            if (flicker) MakeFlicker(light, bulb);
            return lamp.gameObject;
        }

        private const float BulbHalf = 0.075f;

        /// <summary>A thin rod from the ceiling down to a lamp's bulb, so it hangs rather than floats.</summary>
        private static GameObject HangFrom(GameObject lamp, float ceilingY)
        {
            var p = lamp.transform.position;
            float top = ceilingY, bottom = p.y + BulbHalf;
            if (top - bottom > 0.01f)
            {
                var rod = LocalBox("Hanger", lamp.transform, Vector3.zero, new Vector3(0.03f, top - bottom, 0.03f), g.metal, collider: false);
                rod.transform.position = new Vector3(p.x, (top + bottom) / 2f, p.z);
                NoShadows(rod);
            }
            return lamp;
        }

        /// <summary>A short arm from the wall out to a lamp's bulb.</summary>
        private static GameObject BracketFrom(GameObject lamp, Vector2 wallPoint, Vector2 normal)
        {
            var p = lamp.transform.position;
            float reach = Vector2.Dot(new Vector2(p.x, p.z) - wallPoint, normal) - BulbHalf;
            if (reach > 0.01f)
            {
                var mid = wallPoint + normal * (reach / 2f);
                var arm = LocalBox("Bracket", lamp.transform, Vector3.zero, new Vector3(0.04f, 0.04f, reach), g.metal, collider: false);
                arm.transform.position = new Vector3(mid.x, p.y, mid.y);
                arm.transform.rotation = Quaternion.LookRotation(new Vector3(normal.x, 0f, normal.y));
                NoShadows(arm);
            }
            return lamp;
        }

        private static Light AddLightComponent(Transform parent, Vector3 localPos, Color color, float intensity, float range)
        {
            var go = new GameObject("PointLight");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            var l = go.AddComponent<Light>();
            l.type = LightType.Point;
            l.color = color;
            l.intensity = intensity;
            l.range = range;
            l.shadows = LightShadows.None;
            var urpData = System.Type.GetType("UnityEngine.Rendering.Universal.UniversalAdditionalLightData, Unity.RenderPipelines.Universal.Runtime");
            if (urpData != null) go.AddComponent(urpData);
            return l;
        }

        /// <summary>Adds a flicker and moves the glowing fixture under the light so both dim together.</summary>
        private static void MakeFlicker(Light light, GameObject fixture)
        {
            var flicker = light.gameObject.AddComponent<LightFlicker>();
            if (fixture == null) return;
            fixture.transform.SetParent(light.transform, true);
            flicker.SetFixture(fixture.GetComponent<Renderer>());
        }

        // ---------------------------------------------------------------- map

        /// <summary>Rasterises the layout into a top-down map: districts, walkways, alleys, the pit.</summary>
        private static void BakeMap()
        {
            foreach (var _ in BakeMapSteps()) { }
        }

        /// <summary>Draws the map a slice of rows at a time, reporting how far it's got (0..1).</summary>
        private static IEnumerable<float> BakeMapSteps()
        {
            const int res = 1024;
            float size = g.citySize, half = g.HalfSize;
            var district = new int[res * res];
            var px = new Color[res * res];

            for (int y = 0; y < res; y++)
            {
            for (int x = 0; x < res; x++)
            {
                var p = new Vector2(-half + (x + 0.5f) / res * size, -half + (y + 0.5f) / res * size);
                int d = g.DistrictIndexAt(p);
                district[y * res + x] = d;
                var pal = g.palettes[g.districts[d].palette % g.palettes.Length];

                var building = Color.Lerp(new Color(0.03f, 0.03f, 0.035f), pal.primary, 0.16f);
                var street = Color.Lerp(new Color(0.42f, 0.41f, 0.4f), pal.primary, 0.35f);
                Color c;
                float pitDistance = Vector2.Distance(p, g.pitCenter);
                if (pitDistance < g.pitRadius || g.InRampCorridor(p, 0f))
                {
                    float depth = Mathf.InverseLerp(0f, -g.pitDepth, g.HeightAt(p) - g.GroundAt(p));
                    c = Color.Lerp(new Color(0.4f, 0.32f, 0.22f), new Color(0.18f, 0.13f, 0.09f), depth);
                }
                else if (streetIndex.Contains(p) || g.IsOpen(p)) c = street;
                else if (alleyIndex.Contains(p)) c = street * 0.65f;
                // Only where a building really stands; any ground left between them reads as alley.
                else c = BuildingAt(p) ? building : street * 0.65f;

                // High ground reads brighter, low ground darker.
                c *= 1f + Mathf.Clamp(g.GroundAt(p), -10f, 10f) * 0.035f;

                // Sump stairwells: bright, so they read as way-points.
                foreach (var sh in g.sumpShafts)
                    if (Grow(sh, 0.8f).Contains(p)) c = sh.Contains(p) ? new Color(0.05f, 0.05f, 0.05f) : new Color(0.95f, 0.85f, 0.4f);
                foreach (var z in catwalkZones)
                    if (z.Contains(p)) c = Color.Lerp(c, pal.accent, 0.45f);

                c.a = 1f;
                px[y * res + x] = c;
            }
                if ((y & 31) == 31) yield return 0.95f * (y + 1f) / res;
            }

            // District borders.
            for (int y = 0; y < res - 1; y++)
            for (int x = 0; x < res - 1; x++)
            {
                int d = district[y * res + x];
                if (d == district[y * res + x + 1] && d == district[(y + 1) * res + x]) continue;
                var pal = g.palettes[g.districts[d].palette % g.palettes.Length];
                px[y * res + x] = Color.Lerp(px[y * res + x], pal.accent, 0.8f);
            }

            // In memory: each world draws its own. The last one drawn is replaced.
            if (g.mapTexture != null && !IsAsset(g.mapTexture))
            {
                if (Application.isPlaying) Object.Destroy(g.mapTexture);
                else Object.DestroyImmediate(g.mapTexture);
            }
            var tex = new Texture2D(res, res, TextureFormat.RGBA32, false)
            {
                name = "PittMap",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            tex.SetPixels(px);
            tex.Apply(false);
            g.mapTexture = tex;
            yield return 1f;
        }

        // ---------------------------------------------------------------- ceiling

        private static void BuildCeiling(Transform root)
        {
            var parent = Group(root, "Ceiling");

            var ceiling = new GameObject("Ceiling");
            ceiling.transform.SetParent(parent, false);
            ceiling.transform.position = new Vector3(0f, g.ceilingHeight, 0f);
            ceiling.AddComponent<MeshFilter>().sharedMesh = CeilingMesh();
            var mr = ceiling.AddComponent<MeshRenderer>();
            mr.sharedMaterial = g.ceiling;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            // The light beyond the hole: blinding to look at, but it lights nothing down here.
            var glow = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            glow.name = "HoleLight";
            glow.transform.SetParent(parent, false);
            glow.transform.position = new Vector3(0f, g.ceilingHeight + 25f, 0f);
            glow.transform.localScale = new Vector3(g.holeRadius * 2.6f, 0.5f, g.holeRadius * 2.6f);
            Object.DestroyImmediate(glow.GetComponent<Collider>());
            var glowRenderer = glow.GetComponent<Renderer>();
            glowRenderer.sharedMaterial = g.holeGlow;
            glowRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            glowRenderer.receiveShadows = false;
        }

        /// <summary>A flat square with a circular hole in the middle, facing down.</summary>
        private static Mesh CeilingMesh()
        {
            const int count = 128;
            float extent = g.ceilingExtent / 2f;
            var verts = new List<Vector3>();
            var tris = new List<int>();
            for (int i = 0; i <= count; i++)
            {
                float a = i / (float)count * Mathf.PI * 2f;
                var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                float toSquare = extent / Mathf.Max(Mathf.Abs(dir.x), Mathf.Abs(dir.y));
                verts.Add(new Vector3(dir.x * g.holeRadius, 0f, dir.y * g.holeRadius));
                verts.Add(new Vector3(dir.x * toSquare, 0f, dir.y * toSquare));
            }
            for (int i = 0; i < count; i++)
            {
                int a = i * 2, b = a + 1, c = a + 2, d = a + 3;
                tris.AddRange(new[] { a, b, c, c, b, d });
            }

            var mesh = new Mesh { name = "PittCeiling" };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            return mesh;
        }

        // ---------------------------------------------------------------- helpers

        private static Transform Group(Transform parent, string name)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            return t;
        }

        private static Vector3 ToV3(Vector2 p, float y) => new(p.x, y, p.y);

        private static Rect Grow(Rect r, float amount) =>
            Rect.MinMaxRect(r.xMin - amount, r.yMin - amount, r.xMax + amount, r.yMax + amount);

        private static void NoShadows(GameObject go)
        {
            var r = go.GetComponent<Renderer>();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        /// <summary>Axis-aligned box spanning an XZ rect between two heights, in world space, with UVs in metres.</summary>
        /// <param name="ground">Height of the ground the box stands on, for ground grime (see <see cref="WorldCube"/>).
        /// Defaults to <paramref name="bottom"/>; pass it when the box is sunk into the ground or starts above it.</param>
        private static GameObject Box(string name, Transform parent, Rect xz, float bottom, float top, Material m, float? ground = null)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.position = new Vector3(xz.center.x, (bottom + top) / 2f, xz.center.y);
            var size = new Vector3(xz.width, top - bottom, xz.height);
            go.transform.localScale = size;
            go.GetComponent<MeshFilter>().sharedMesh = WorldCube(go.transform.position, size, ground ?? bottom);
            go.GetComponent<Renderer>().sharedMaterial = m;
            return go;
        }

        /// <summary>
        /// A unit cube (scaled up by its transform) whose UVs are world position in metres: the texture lies on the
        /// city, not on each box, so it runs on unbroken across neighbouring boxes, and where two faces of the same
        /// material overlap they show the same texels instead of fighting. Faces run U along the wall and V up it
        /// with grime streaks hanging downward. One per box; static batching merges them anyway.
        /// UV channel 3 holds each vertex's height above <paramref name="ground"/> in metres, so the Grime Wall shader
        /// can lay its ground grime along the foot of the wall wherever the box stands.
        /// </summary>
        private static Mesh WorldCube(Vector3 centre, Vector3 size, float ground)
        {
            var verts = new Vector3[24];
            var normals = new Vector3[24];
            var uvs = new Vector2[24];
            var heights = new Vector2[24];
            var tris = new int[36];
            int f = 0;
            void Face(Vector3 n, Vector3 a, Vector3 b)
            {
                int i = f * 4;
                var c = n * 0.5f;
                verts[i] = c - a * 0.5f - b * 0.5f;
                verts[i + 1] = c + a * 0.5f - b * 0.5f;
                verts[i + 2] = c + a * 0.5f + b * 0.5f;
                verts[i + 3] = c - a * 0.5f + b * 0.5f;
                for (int k = 0; k < 4; k++)
                {
                    normals[i + k] = n;
                    var world = centre + Vector3.Scale(verts[i + k], size);
                    uvs[i + k] = new Vector2(Vector3.Dot(world, a), Vector3.Dot(world, b));
                    heights[i + k] = new Vector2(world.y - ground, 0f);
                }
                int t = f * 6;
                tris[t] = i; tris[t + 1] = i + 2; tris[t + 2] = i + 1;
                tris[t + 3] = i; tris[t + 4] = i + 3; tris[t + 5] = i + 2;
                f++;
            }
            Face(Vector3.forward, Vector3.left, Vector3.up);
            Face(Vector3.back, Vector3.right, Vector3.up);
            Face(Vector3.right, Vector3.forward, Vector3.up);
            Face(Vector3.left, Vector3.back, Vector3.up);
            Face(Vector3.up, Vector3.right, Vector3.forward);
            Face(Vector3.down, Vector3.right, Vector3.back);

            var mesh = new Mesh { name = "WorldCube" };
            mesh.vertices = verts;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.SetUVs(3, heights);
            mesh.triangles = tris;
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>A thin box flat against a building face.</summary>
        private static GameObject FaceBox(string name, Transform parent, Vector2 facePoint, Vector2 normal, float y, Vector3 size, Material m)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            var p = facePoint + normal * (size.z / 2f);
            go.transform.position = new Vector3(p.x, y, p.y);
            go.transform.rotation = Quaternion.LookRotation(new Vector3(normal.x, 0f, normal.y));
            go.transform.localScale = size;
            go.GetComponent<Renderer>().sharedMaterial = m;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            return go;
        }

        private static GameObject LocalBox(string name, Transform parent, Vector3 localPos, Vector3 size, Material m, bool collider = true)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = size;
            go.GetComponent<Renderer>().sharedMaterial = m;
            if (!collider) Object.DestroyImmediate(go.GetComponent<Collider>());
            return go;
        }

        // ---------------------------------------------------------------- assets

        /// <summary>Shared emissive material for a colour/brightness, so signs batch instead of each owning a material.</summary>
        private static Material Glow(Color color, float intensity)
        {
            string key = $"{Mathf.RoundToInt(color.r * 20)}_{Mathf.RoundToInt(color.g * 20)}_{Mathf.RoundToInt(color.b * 20)}_{Mathf.RoundToInt(intensity * 2)}";
            if (glowCache.TryGetValue(key, out var m)) return m;
            m = Emissive($"Glow/World_Glow_{key}", color * 0.3f, color * intensity);
            glowCache[key] = m;
            return m;
        }

        /// <summary>
        /// A named material from the greybox's library (assets made in the editor by PittWorldAssets), or, for one
        /// that isn't there, a new one made now and kept in memory.
        /// </summary>
        private static Material Mat(string name, Color color, float metallic = 0f)
        {
            string shortName = name.Substring(name.LastIndexOf('/') + 1);
            foreach (var m in g.materialLibrary)
                if (m != null && m.name == shortName) return m;
            var made = new Material(g.concrete.shader)
            {
                name = shortName,
                color = color,
                enableInstancing = true,
            };
            made.SetFloat("_Metallic", metallic);
            made.SetFloat("_Smoothness", 0f);
            return made;
        }

        private static Material Emissive(string name, Color baseColor, Color emission)
        {
            var m = Mat(name, baseColor);
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", emission);
            return m;
        }

        /// <summary>Whether something is a saved asset rather than made at runtime (always false in a build).</summary>
        private static bool IsAsset(Object o)
        {
#if UNITY_EDITOR
            return UnityEditor.AssetDatabase.Contains(o);
#else
            return false;
#endif
        }
    }
}
