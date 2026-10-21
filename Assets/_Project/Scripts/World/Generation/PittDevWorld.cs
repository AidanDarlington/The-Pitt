using System.Collections.Generic;
using ThePitt.Dev;
using ThePitt.Interaction;
using ThePitt.Items;
using UnityEngine;

namespace ThePitt.World.Generation
{
    /// <summary>
    /// The dev world: a small hand-laid map (editor and development builds only, picked on the New Game screen) with
    /// an example of everything in the game, to test things without playing up to them. Nothing saves there.
    /// It's made of the same parts as the real city, and fills in the same layout data (districts, gang squares,
    /// dead-end alleys, the scrap pit, a Watcher outpost), so the game's own systems bring it to life as they would a
    /// real one: gangs claiming turf, corpse piles, scrap heaps. There's no street crowd and no NPC doorways; one
    /// of each kind of NPC (and a dealer) stands still in the showroom instead.
    /// In the middle, Crimson Row is a showroom: the dev console (scrip, rep and time buttons), every item on a
    /// plinth (they come back a few seconds after being picked up), every station, a swatch of every material,
    /// every kind of lamp, the NPC line-up, and stairs. South of it are the player's home and a hidden room; each
    /// outer district has a gang square, a property or business for sale and a dead-end alley. The pit is in Sodium Yards.
    /// <para><b>Keep it current.</b> Whenever the game gets something new to see or use in the world, add an
    /// example of it here as well. Items, stations (placeable items) and the materials in the greybox's library
    /// turn up on their own; anything else (a new light, building part, business, node, world object) needs adding.</para>
    /// </summary>
    public static partial class PittGreyboxGenerator
    {
        private const int DevSeed = 7;
        private const float DevSize = 160f;

        /// <summary>Each district: its palette (by name), its centre (which decides who owns what), and the hub of its streets.</summary>
        private static readonly (string name, Vector2 centre, Vector2 hub)[] DevDistricts =
        {
            ("Crimson Row", Vector2.zero, Vector2.zero),
            // Sodium Yards' streets sit off to the side of the pit.
            ("Sodium Yards", new Vector2(45f, -45f), new Vector2(24f, -58f)),
            ("Mildew", new Vector2(-45f, -45f), new Vector2(-45f, -45f)),
            ("Coldwater", new Vector2(-45f, 45f), new Vector2(-45f, 45f)),
            ("The Velvet", new Vector2(45f, 45f), new Vector2(45f, 45f)),
        };

        /// <summary>Property doors on the district blocks, built once the blocks are up: district index, point, normal.</summary>
        private static List<(int district, Vector2 point, Vector2 normal)> devDoors;

        /// <summary>
        /// Builds the dev world under the greybox, a step at a time like <see cref="BuildSteps"/>, returning progress (0..1).
        /// <see cref="LastBuilt"/> is set when it's done.
        /// </summary>
        public static IEnumerable<float> BuildDevWorldSteps(PittGreybox greybox)
        {
            Begin(greybox, DevSeed, PittGreybox.GeneratorVersion);
            Clear(g);
            var root = new GameObject(GeneratedName).transform;
            root.SetParent(g.transform, false);
            // Switched off while it's built, as the real city is: everything wakes up together.
            root.gameObject.SetActive(false);
            LastRoot = root;

            DevLayout();
            yield return 0.05f;
            BuildTerrain(root);
            yield return 0.3f;
            BuildPillar(root);
            DevBuildDistricts(root);
            BuildWatcherOutposts(root);
            BuildScrapPit(root);
            yield return 0.4f;
            DevBuildShowroom(root);
            DevBuildProperties(root);
            BuildCeiling(root);
            yield return 0.5f;
            foreach (float part in BakeMapSteps()) yield return 0.5f + part * 0.3f;

            root.gameObject.SetActive(true);
            Physics.SyncTransforms();
            // Pickups settle onto what's under them, and stations take room on the NavMesh: both once the rest is standing.
            DevPlaceItems(root);
            DevPlaceStations(root);
            yield return 0.85f;
            BuildNavMesh();
            LastBuilt = root;
            Debug.Log("PittGreybox built the dev world.");
            yield return 1f;
        }

        /// <summary>The map's shape: one flat terrace, the districts and their squares, the pit, a Watcher outpost and the start.</summary>
        private static void DevLayout()
        {
            g.citySize = DevSize;
            float h = g.HalfSize;
            g.terraces.Clear();
            // A little past the edges, so the pillar's skirts close the cliff.
            g.terraces.Add(new PittGreybox.Plateau { rect = Rect.MinMaxRect(-h - 2f, -h - 2f, h + 2f, h + 2f), height = 0f });

            g.pitCenter = new Vector2(55f, -55f);
            g.pitRadius = 13f;
            g.pitWallWidth = 4f;
            g.pitDepth = 8f;
            g.rampLength = 16f;
            g.rampWidth = 5f;
            g.scrapPiles = 5;

            g.districts.Clear();
            g.gangSquares.Clear();
            for (int i = 0; i < DevDistricts.Length; i++)
            {
                var d = DevDistricts[i];
                int palette = System.Array.FindIndex(g.palettes, p => p.name == d.name);
                g.districts.Add(new PittGreybox.District { name = d.name, center = d.centre, palette = palette >= 0 ? palette : i });
                if (i > 0) g.gangSquares.Add(new PittGreybox.Plateau { rect = Square(d.hub, 7f), height = 0f });
            }

            g.sumpSquares.Clear();
            g.sumpShafts.Clear();
            g.doorways.Clear();
            g.alleySpots.Clear();
            g.watcherOutposts.Clear();
            g.watcherOutposts.Add(new PittGreybox.WatcherOutpost
            {
                rect = Rect.MinMaxRect(-g.outpostWidth / 2f, -h - g.outpostBridge - g.outpostDepth, g.outpostWidth / 2f, -h - g.outpostBridge),
                inward = Vector2.up,
                bridge = Rect.MinMaxRect(-2f, -h - g.outpostBridge - 0.5f, 2f, -h + 0.5f),
                landing = Rect.MinMaxRect(-4f, -h, 4f, -h + 6f),
            });

            // In front of the dev console, facing it.
            g.startPoint = new Vector2(0f, -17f);
            g.startPosition = new Vector3(0f, 0.05f, -17f);
            g.startYaw = 0f;

            // No crowd and no NPC doorways: one of each NPC stands still behind the item plinths, facing them.
            g.npcLineup = true;
            g.npcLineupStart = new Vector2(-3f, 8f);
            g.npcLineupFacing = Vector2.down;

            // What the shared build parts read: no streets or alleys of their own, the whole map open ground.
            plans = new List<Plan>();
            segments = new List<Segment>();
            alleys = new List<Rect>();
            catwalkZones = new List<Rect>();
            sumps = new List<Sump>();
            streetIndex = new RectIndex();
            alleyIndex = new RectIndex();
            walkIndex = new RectIndex();
            streetIndex.Add(Rect.MinMaxRect(-h, -h, h, h));
            walkIndex.Add(Rect.MinMaxRect(-h, -h, h, h));
            kerbIndex = null;
            planIndex = null;
            devDoors = new List<(int, Vector2, Vector2)>();
            interiorsBuilt = 0;
        }

        // ---------------------------------------------------------------- districts

        /// <summary>
        /// Round each outer district's gang square, a block at each corner (leaving a street through the square each
        /// way): one with a property door, one cut by a dead-end alley, lamps on the rest. In Crimson
        /// Row, south of the showroom, the player's home block and a hidden room.
        /// </summary>
        private static void DevBuildDistricts(Transform root)
        {
            var parent = Group(root, "DevDistricts");
            var lights = Group(root, "Lights");
            var mats = new[] { g.brick, g.concrete, g.concreteDark };

            for (int d = 1; d < DevDistricts.Length; d++)
            {
                var hub = DevDistricts[d].hub;
                var palette = g.palettes[g.districts[d].palette % g.palettes.Length];
                // Corner 0 faces the middle of the map; the rest go round from there.
                var s0 = new Vector2(-Mathf.Sign(hub.x), -Mathf.Sign(hub.y));
                var corners = new[] { s0, new Vector2(s0.x, -s0.y), new Vector2(-s0.x, s0.y), -s0 };
                for (int c = 0; c < corners.Length; c++)
                {
                    var s = corners[c];
                    var a = hub + s * 11f;
                    var b = hub + s * 19f;
                    var fp = Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
                    // Nothing on the pit, its rim or its ramp.
                    if (DistanceToRect(g.pitCenter, fp) < g.pitRadius + 7f || g.InRampCorridor(fp.center, fp.width)) continue;

                    float top = R(9f, 16f);
                    var material = mats[(d + c) % mats.Length];
                    // The faces on the streets through the square: across (x) and along (z).
                    var xFace = (point: new Vector2(hub.x + s.x * 11f, hub.y + s.y * 15f), normal: new Vector2(-s.x, 0f));
                    var zFace = (point: new Vector2(hub.x + s.x * 15f, hub.y + s.y * 11f), normal: new Vector2(0f, -s.y));
                    if (c == 1) DevAlleyBlock(parent, fp, xFace.normal, top, material);
                    else DevBlock(parent, fp, top, material);

                    if (c == 0) devDoors.Add((d, xFace.point, xFace.normal));
                    AddWallLamp(lights, zFace.point - Vector2.Perpendicular(zFace.normal) * 2f, zFace.normal, 3.6f,
                        c % 2 == 0 ? new Color(1f, 0.64f, 0.28f) : palette.primary, 50f, 15f, false);
                }

                // A pair of posts on opposite corners of the square, in the district's colour, as in the city.
                var square = Grow(g.gangSquares[d - 1].rect, -1.5f);
                AddPostLight(lights, square.min, 4.5f, palette.primary, 22f, 16f);
                AddPostLight(lights, square.max, 4.5f, palette.primary, 22f, 16f);
            }

            // Crimson Row: home to the south of the showroom, the hidden room beside it, and an alley for a corpse pile.
            var home = Rect.MinMaxRect(4f, -36f, 14f, -26f);
            DevBlock(parent, home, 11f, g.brick);
            devDoors.Add((0, new Vector2(9f, -26f), Vector2.up));

            var hidden = new Plan
            {
                footprint = Rect.MinMaxRect(-14f, -36f, -5f, -27f),
                groundMin = 0f, ground = 0f, top = 8f, hollow = true, holeNormal = Vector2.up,
            };
            plans.Add(hidden);
            BuildHollow(parent, Group(root, "HiddenSpots"), hidden, g.concrete);
            DevSign(parent, new Vector2(-9.5f, -25.5f), Vector2.up, "HIDDEN ROOM");

            DevAlleyBlock(parent, Rect.MinMaxRect(-26f, -36f, -18f, -28f), Vector2.right, 10f, g.concreteDark);
            DevSign(parent, new Vector2(-15.5f, -32f), Vector2.right, "CORPSE ALLEY");
        }

        /// <summary>A plain building.</summary>
        private static void DevBlock(Transform parent, Rect fp, float top, Material m)
        {
            plans.Add(new Plan { footprint = fp, groundMin = 0f, ground = 0f, top = top });
            Box("Building", parent, fp, -BuildingSink, top, m, ground: 0f);
        }

        /// <summary>
        /// A building with a dead-end alley through the middle, open on the <paramref name="open"/> side and walled
        /// at the other, where corpse piles get dumped.
        /// </summary>
        private static void DevAlleyBlock(Transform parent, Rect fp, Vector2 open, float top, Material m)
        {
            const float half = 0.8f, cap = 0.6f;
            bool alongX = open.x != 0f;
            Rect a, b, end, alley;
            Vector2 spot;
            if (alongX)
            {
                float c = fp.center.y;
                a = Rect.MinMaxRect(fp.xMin, fp.yMin, fp.xMax, c - half);
                b = Rect.MinMaxRect(fp.xMin, c + half, fp.xMax, fp.yMax);
                end = open.x > 0f ? Rect.MinMaxRect(fp.xMin, c - half, fp.xMin + cap, c + half) : Rect.MinMaxRect(fp.xMax - cap, c - half, fp.xMax, c + half);
                alley = open.x > 0f ? Rect.MinMaxRect(fp.xMin + cap, c - half, fp.xMax, c + half) : Rect.MinMaxRect(fp.xMin, c - half, fp.xMax - cap, c + half);
                spot = new Vector2(open.x > 0f ? fp.xMin + cap + 1f : fp.xMax - cap - 1f, c);
            }
            else
            {
                float c = fp.center.x;
                a = Rect.MinMaxRect(fp.xMin, fp.yMin, c - half, fp.yMax);
                b = Rect.MinMaxRect(c + half, fp.yMin, fp.xMax, fp.yMax);
                end = open.y > 0f ? Rect.MinMaxRect(c - half, fp.yMin, c + half, fp.yMin + cap) : Rect.MinMaxRect(c - half, fp.yMax - cap, c + half, fp.yMax);
                alley = open.y > 0f ? Rect.MinMaxRect(c - half, fp.yMin + cap, c + half, fp.yMax) : Rect.MinMaxRect(c - half, fp.yMin, c + half, fp.yMax - cap);
                spot = new Vector2(c, open.y > 0f ? fp.yMin + cap + 1f : fp.yMax - cap - 1f);
            }
            DevBlock(parent, a, top, m);
            DevBlock(parent, b, top, m);
            DevBlock(parent, end, top, m);
            alleys.Add(alley);
            g.alleySpots.Add(new PittGreybox.AlleySpot { position = new Vector3(spot.x, 0f, spot.y), outward = open, width = half * 2f });
        }

        // ---------------------------------------------------------------- properties

        /// <summary>The home (owned), a morgue in Mildew, a metalworks in Sodium Yards and lockups for sale elsewhere.</summary>
        private static void DevBuildProperties(Transform root)
        {
            var parent = Group(root, "Properties");
            foreach (var (d, point, normal) in devDoors)
            {
                string district = g.districts[d].name;
                Properties.Property property;
                if (d == 0) property = BuildInterior(parent, "Home", "Home Lockup", district, 0, true, g.homeSize);
                else if (district == "Mildew" || district == "Sodium Yards")
                {
                    var kind = district == "Mildew" ? Production.BusinessKind.Morgue : Production.BusinessKind.Metalworks;
                    bool morgue = kind == Production.BusinessKind.Morgue;
                    property = BuildInterior(parent, kind.ToString(), $"{district} {(morgue ? "Morgue" : "Metalworks")}", district,
                        morgue ? g.morguePrice : g.metalworksPrice, false, new Vector2(10f, 8f));
                    BuildBusiness(property, kind);
                }
                else property = BuildInterior(parent, district.Replace(" ", ""), $"{district} Lockup", district, 1000, false, new Vector2(8.5f, 7.5f));
                BuildPropertyDoor(parent, property, point, normal, 0f);
            }
        }

        // ---------------------------------------------------------------- showroom

        /// <summary>Crimson Row's showroom: the console, item plinths, the station and material rows, the lamp wall and stairs.</summary>
        private static void DevBuildShowroom(Transform root)
        {
            var parent = Group(root, "DevShowroom");
            var lights = Group(root, "Lights");
            var warm = new Color(1f, 0.9f, 0.75f);
            var white = new Color(0.95f, 0.95f, 1f);

            // The console: scrip and time on the left, rep on the right, facing the start.
            Box("Console", parent, Rect.MinMaxRect(-9.5f, -10.2f, 9.5f, -9.8f), 0f, 2.8f, g.concreteDark);
            var console = Group(parent, "ConsoleFace");
            console.SetPositionAndRotation(new Vector3(0f, 0f, -10.2f), Quaternion.Euler(0f, 180f, 0f));
            var plus = Glow(new Color(0.3f, 1f, 0.4f), 2f);
            var minus = Glow(new Color(1f, 0.3f, 0.25f), 2f);
            var amber = Glow(new Color(1f, 0.7f, 0.25f), 2f);
            // Local x runs the opposite way to world x here, so positions are given in world x.
            Vector3 At(float worldX, float y) => new(-worldX, y, 0.04f);
            DevLabel.Create(console, At(0f, 3.4f), "DEV WORLD", 0.07f, new Color(1f, 0.66f, 0.3f));
            DevLabel.Create(console, At(-4.9f, 2.45f), "SCRIP / TIME", 0.035f);
            DevButton.Create(console, At(-7.6f, 1.6f), "SCRIP +100", plus, DevButton.Kind.Scrip, 100f);
            DevButton.Create(console, At(-5.8f, 1.6f), "SCRIP +1000", plus, DevButton.Kind.Scrip, 1000f);
            DevButton.Create(console, At(-4f, 1.6f), "SCRIP -100", minus, DevButton.Kind.Scrip, -100f);
            DevButton.Create(console, At(-2.2f, 1.6f), "SCRIP -1000", minus, DevButton.Kind.Scrip, -1000f);
            DevButton.Create(console, At(-7.6f, 0.95f), "+1 HOUR", amber, DevButton.Kind.Hours, 1f);
            DevButton.Create(console, At(-5.8f, 0.95f), "+12 HOURS", amber, DevButton.Kind.Hours, 12f);
            DevLabel.Create(console, At(4.75f, 2.75f), "REP", 0.035f);
            var rep = Group(console, "RepBoard");
            rep.localPosition = At(7.6f, 0f);
            rep.gameObject.AddComponent<DevRepBoard>().Setup(plus, minus);
            BracketFrom(AddLight(lights, new Vector3(0f, 3.2f, -10.8f), warm, 10f, 9f, fixture: true), new Vector2(0f, -10.2f), Vector2.down);

            // Lights over the plinths, the stations and the swatches; white over the swatches so their colours read true.
            foreach (var p in new[] { new Vector2(-11f, -6f), new Vector2(11f, -6f), new Vector2(-11f, 11f), new Vector2(11f, 11f), new Vector2(-19.5f, 1f), new Vector2(-19.5f, 13f) })
                AddPostLight(lights, p, 4.5f, warm, 30f, 18f);
            foreach (var p in new[] { new Vector2(19f, 1f), new Vector2(19f, 13f) })
                AddPostLight(lights, p, 4.5f, white, 30f, 18f);

            DevTitle(parent, new Vector2(0f, -6.5f), Vector2.down, "ITEMS");
            DevTitle(parent, new Vector2(-19.5f, -8.5f), Vector2.down, "STATIONS");
            DevTitle(parent, new Vector2(19f, -8.5f), Vector2.down, "MATERIALS");
            DevTitle(parent, new Vector2(0f, 10f), Vector2.down, "NPCS");

            DevBuildSwatches(parent);
            DevBuildLampWall(parent, lights);

            // Stairs up onto a raised deck.
            Box("Deck", parent, Rect.MinMaxRect(-26f, 16f, -19f, 23f), 0f, 1.5f, g.concrete);
            BuildStairs(parent, Rect.MinMaxRect(-23.5f, 10f, -21.5f, 16f), Vector2.up, 0f, 1.5f, g.concreteDark);
            DevSign(parent, new Vector2(-22.5f, 9.2f), Vector2.down, "STAIRS");
        }

        /// <summary>Every material the city uses, each on a pillar with its name: the greybox's own and its library.</summary>
        private static void DevBuildSwatches(Transform parent)
        {
            var group = Group(parent, "Swatches");
            var all = new List<Material>
            {
                g.concrete, g.concreteDark, g.brick, g.wood, g.metal, g.rust, g.trash, g.kerb, g.chainLink, g.cable,
                g.lampGlow, g.windowWarm, g.windowCold, g.terminalScreen, g.ceiling, g.holeGlow,
            };
            all.AddRange(g.materialLibrary);
            var seen = new HashSet<Material>();
            int i = 0;
            foreach (var m in all)
            {
                if (m == null || !seen.Add(m)) continue;
                float x = i % 2 == 0 ? 17f : 21f;
                float z = -6f + i / 2 * 1.8f;
                Box("Swatch_" + m.name, group, Square(new Vector2(x, z), 0.6f), 0f, 2.4f, m);
                var sign = Group(group, "Sign");
                sign.SetPositionAndRotation(new Vector3(x - 0.62f, 0f, z), Quaternion.Euler(0f, -90f, 0f));
                DevLabel.Create(sign, new Vector3(0f, 1.2f, 0f), m.name, 0.009f);
                i++;
            }
        }

        /// <summary>A wall with one of every kind of lamp the city uses along it, each named.</summary>
        private static void DevBuildLampWall(Transform parent, Transform lights)
        {
            const float face = 23.8f;
            Box("LampWall", parent, Rect.MinMaxRect(-15f, face, 15f, face + 0.4f), 0f, 5f, g.brick);
            DevTitle(parent, new Vector2(0f, face - 0.05f), Vector2.down, "LIGHTS", 4.4f);
            var crimson = g.palettes[g.districts[0].palette % g.palettes.Length];
            var down = Vector2.down;
            var lamps = new (string name, System.Action<Vector2> build)[]
            {
                ("SODIUM WALL LAMP", p => AddWallLamp(lights, p, down, 3.6f, new Color(1f, 0.64f, 0.28f), 55f, 16f, false)),
                ("FLUORESCENT WALL LAMP", p => AddWallLamp(lights, p, down, 3.6f, new Color(1f, 0.9f, 0.62f), 55f, 16f, false)),
                ("DISTRICT WALL LAMP", p => AddWallLamp(lights, p, down, 3.6f, crimson.primary, 55f, 16f, false)),
                ("FLICKERING WALL LAMP", p => AddWallLamp(lights, p, down, 3.6f, new Color(1f, 0.64f, 0.28f), 55f, 16f, true)),
                ("ALLEY LAMP", p => BracketFrom(AddLight(lights, new Vector3(p.x, 2.8f, p.y - 0.35f), crimson.accent, 9f, 8f, fixture: true), p, down)),
                ("DOORWAY LAMP", p => BracketFrom(AddLight(lights, new Vector3(p.x, 2.75f, p.y - 0.45f), new Color(1f, 0.7f, 0.38f), 4f, 6f, fixture: true), p, down)),
                ("HANGING LAMP", p => DevHanging(parent, lights, p, new Color(1f, 0.85f, 0.65f), false)),
                ("MORGUE LAMP", p => DevHanging(parent, lights, p, new Color(0.75f, 0.9f, 1f), true)),
                ("WATCHER LIGHT", p => AddLight(lights, new Vector3(p.x, 3.4f, p.y - 1f), new Color(0.8f, 0.9f, 1f), 2.2f, 9f, fixture: false)),
            };
            for (int i = 0; i < lamps.Length; i++)
            {
                var p = new Vector2(-13.6f + i * 3.4f, face);
                lamps[i].build(p);
                DevSign(parent, p - Vector2.up * 0.02f, down, lamps[i].name, 1.5f);
            }
        }

        /// <summary>A lamp hanging on a rod from a beam out of the lamp wall.</summary>
        private static void DevHanging(Transform parent, Transform lights, Vector2 wallPoint, Color color, bool flicker)
        {
            Box("Beam", parent, Rect.MinMaxRect(wallPoint.x - 0.08f, wallPoint.y - 1.2f, wallPoint.x + 0.08f, wallPoint.y), 4.4f, 4.6f, g.metal);
            HangFrom(AddLight(lights, new Vector3(wallPoint.x, 3.4f, wallPoint.y - 1f), color, 7f, 9f, fixture: true, flicker: flicker), 4.4f);
        }

        /// <summary>A section's name, big, at head height.</summary>
        private static TextMesh DevTitle(Transform parent, Vector2 p, Vector2 facing, string text, float y = 3f) =>
            DevLabel.Create(DevFacing(parent, p, facing), new Vector3(0f, y, 0f), text, 0.06f, new Color(1f, 0.66f, 0.3f));

        /// <summary>A small name at chest height.</summary>
        private static TextMesh DevSign(Transform parent, Vector2 p, Vector2 facing, string text, float y = 2.2f) =>
            DevLabel.Create(DevFacing(parent, p, facing), new Vector3(0f, y, 0f), text, 0.028f);

        private static Transform DevFacing(Transform parent, Vector2 p, Vector2 facing)
        {
            var t = Group(parent, "Sign");
            t.SetPositionAndRotation(new Vector3(p.x, 0f, p.y), Quaternion.LookRotation(new Vector3(facing.x, 0f, facing.y)));
            return t;
        }

        // ---------------------------------------------------------------- items and stations

        /// <summary>
        /// Every item in the registry on a plinth (a stack where it stacks), by category, each named. Picked up, it's
        /// back a few seconds later.
        /// </summary>
        private static void DevPlaceItems(Transform root)
        {
            var registry = Core.SaveRegistry.Instance != null ? Core.SaveRegistry.Instance : g.itemRegistry;
            if (registry == null || g.worldItemPrefab == null) return;
            var items = new List<ItemDefinition>();
            foreach (var asset in registry.Assets)
                if (asset is ItemDefinition item) items.Add(item);
            items.Sort((a, b) => a.Category != b.Category ? a.Category.CompareTo(b.Category) : string.CompareOrdinal(a.DisplayName, b.DisplayName));

            var group = Group(root, "DevItems");
            const int perRow = 12;
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var p = new Vector2(-9.35f + i % perRow * 1.7f, -4f + i / perRow * 2.5f);
                Box("Plinth", group, Square(p, 0.3f), 0f, 0.9f, g.concreteDark);
                DevSign(group, p + Vector2.down * 0.32f, Vector2.down, item.DisplayName, 0.6f).characterSize = 0.01f;
                // Settles onto the plinth itself, so it needs to be in physics first.
                Physics.SyncTransforms();
                var go = Object.Instantiate(g.worldItemPrefab, new Vector3(p.x, 1.05f, p.y), Quaternion.Euler(0f, 180f, 0f), group);
                go.name = "Dev_" + item.name.Replace("Item_", "");
                var pickup = go.GetComponent<WorldItem>();
                pickup.SetRespawn(0.1f);
                pickup.Setup(item, item.MaxStack > 1 ? Mathf.Min(item.MaxStack, 20) : 1, ItemQuality.Standard);
            }
        }

        /// <summary>Every station (anything placeable), in two rows facing the plinths, each named. They can be picked up.</summary>
        private static void DevPlaceStations(Transform root)
        {
            var registry = Core.SaveRegistry.Instance != null ? Core.SaveRegistry.Instance : g.itemRegistry;
            if (registry == null) return;
            var group = Group(root, "DevStations");
            int i = 0;
            foreach (var asset in registry.Assets)
            {
                if (asset is not ItemDefinition item || !item.IsPlaceable) continue;
                var p = new Vector2(i % 2 == 0 ? -17f : -22f, -6f + i / 2 * 3.5f);
                var placed = Object.Instantiate(item.PlaceablePrefab, new Vector3(p.x, 0f, p.y), Quaternion.Euler(0f, 90f, 0f), group);
                var marker = placed.GetComponent<PlacedStructure>();
                if (marker == null) marker = placed.AddComponent<PlacedStructure>();
                marker.Setup(item);
                DevSign(group, p, Vector2.right, item.DisplayName, 2.6f);
                i++;
            }
        }
    }
}
