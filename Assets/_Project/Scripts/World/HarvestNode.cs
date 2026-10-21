using System;
using ThePitt.Items;
using UnityEngine;
using Random = UnityEngine.Random;

namespace ThePitt.World
{
    /// <summary>
    /// Something in the world that has to be broken apart with the right tool: a heap of rusted scrap in the
    /// pit (pickaxe), or a pile of corpses deep in an alley (shovel). Every strike takes some off, straight into
    /// the inventory: mostly the main yield, now and then one of two extras. A glint sits somewhere on it;
    /// hitting the glint does far more and makes it jump somewhere else, so chasing it breaks the node much
    /// faster. The node shrinks as it's worked and gives a last haul when it falls apart.
    /// </summary>
    public class HarvestNode : MonoBehaviour
    {
        [SerializeField, Min(1f)] private float maxHealth = 100f;
        [Tooltip("Damage and yield multiplier for a strike on the glint.")]
        [SerializeField, Min(1f)] private float sparkMultiplier = 2.5f;
        [Tooltip("How close to the glint the swing has to land.")]
        [SerializeField, Min(0.05f)] private float sparkRadius = 0.3f;
        [Tooltip("Main yield per point of damage dealt.")]
        [SerializeField, Min(0f)] private float yieldPerDamage = 0.22f;
        [Tooltip("Chance per 10 damage of each extra coming loose.")]
        [SerializeField] private Vector2 extrasPer10Damage = new(0.1f, 0.06f);
        [Tooltip("Main yield given when it falls apart.")]
        [SerializeField] private Vector2Int breakHaul = new(4, 7);
        [Tooltip("Chance of each extra when it falls apart.")]
        [SerializeField] private Vector2 breakExtras = new(0.4f, 0.25f);
        [Tooltip("Quality of everything it gives: raw, dug out of a heap or off a body.")]
        [SerializeField] private ItemQuality yieldQuality = ItemQuality.Poor;

        private ToolType tool;
        private ItemDefinition main, extraA, extraB;
        private float health;
        private Transform spark;
        private Light sparkLight;
        private Vector3 baseScale;
        private float flashUntil;
        // Local height of the middle of the mass, and of its top.
        private float centreHeight = 0.6f, topHeight = 1.35f;
        // Only ever approached from in front (down an alley): the glint stays where it can be seen from there.
        private bool viewFromFront;

        public float Health => health;
        public float Fraction => health / maxHealth;
        /// <summary>The tool it has to be worked with.</summary>
        public ToolType Tool => tool;

        /// <summary>Fired once, when it's broken apart.</summary>
        public event Action<HarvestNode> Depleted;

        public void Setup(ToolType requiredTool, ItemDefinition mainYield, ItemDefinition extraYieldA, ItemDefinition extraYieldB, float fraction = 1f)
        {
            tool = requiredTool;
            main = mainYield;
            extraA = extraYieldA;
            extraB = extraYieldB;
            health = maxHealth * Mathf.Clamp01(fraction);
            baseScale = transform.localScale;
            UpdateSize();
            MoveSpark();
        }

        private void Update()
        {
            if (sparkLight == null) return;
            // A restless glint, and a bright flash just after it's struck.
            float flicker = 1.6f + Mathf.PerlinNoise(Time.time * 9f, transform.position.x) * 1.4f;
            sparkLight.intensity = Time.time < flashUntil ? 8f : flicker;
        }

        /// <summary>
        /// A strike with the right tool. <paramref name="aim"/> is the line of the swing, used to judge whether
        /// it caught the glint. Returns whether it did.
        /// </summary>
        public bool Mine(ItemDefinition with, ItemQuality quality, Ray aim, Inventory inventory)
        {
            if (health <= 0f || with == null) return false;
            bool onSpark = spark != null && DistanceToRay(spark.position, aim) <= sparkRadius;
            // A poor tool barely bites; a good one tears through.
            float qualityMult = with.HasQuality ? quality.GatherMultiplier() : 1f;
            float skill = Progression.Skills.Multiplier(Progression.Skill.Harvesting, Progression.Perk.Speed);
            float damage = Mathf.Min(health, with.GatherPower * qualityMult * skill * (onSpark ? sparkMultiplier : 1f));
            health -= damage;

            int xp = Give(inventory, main, Mathf.Max(1, Mathf.RoundToInt(damage * yieldPerDamage))) * 2;
            if (Random.value < extrasPer10Damage.x * damage / 10f) xp += Give(inventory, extraA, 1) * 3;
            if (Random.value < extrasPer10Damage.y * damage / 10f) xp += Give(inventory, extraB, 1) * 3;

            if (health <= 0f)
            {
                // It comes apart: one last haul.
                xp += Give(inventory, main, Random.Range(breakHaul.x, breakHaul.y + 1)) * 2 + 10;
                if (Random.value < breakExtras.x) xp += Give(inventory, extraA, 1) * 3;
                if (Random.value < breakExtras.y) xp += Give(inventory, extraB, 1) * 3;
                Progression.Skills.Add(Progression.Skill.Harvesting, xp);
                Depleted?.Invoke(this);
                Destroy(gameObject);
                return onSpark;
            }

            Progression.Skills.Add(Progression.Skill.Harvesting, xp);
            UpdateSize();
            if (onSpark)
            {
                flashUntil = Time.time + 0.12f;
                MoveSpark(nearby: true);
            }
            return onSpark;
        }

        /// <summary>Puts the haul in the inventory. Returns how much went in (what harvesting xp counts).</summary>
        private int Give(Inventory inventory, ItemDefinition item, int amount)
        {
            if (inventory == null || item == null || amount <= 0) return 0;
            return inventory.Add(item, Progression.Skills.RollYield(Progression.Skill.Harvesting, amount), yieldQuality);
        }

        private void UpdateSize() => transform.localScale = baseScale * Mathf.Lerp(0.55f, 1f, Fraction);

        private static float DistanceToRay(Vector3 point, Ray ray)
        {
            var toPoint = point - ray.origin;
            float along = Mathf.Max(0f, Vector3.Dot(toPoint, ray.direction));
            return Vector3.Distance(point, ray.origin + ray.direction * along);
        }

        /// <summary>
        /// Puts the glint on a fresh spot of the surface, somewhere on its upper half, where it can be seen: the
        /// first thing a ray from outside hits has to be this node, and someone standing on that side (or in
        /// front, for a node only reached from the front) has to have a clear view of it. After a hit it only
        /// jumps a short way from where it was, so it creeps round the side being worked rather than leaping
        /// to the far side; now and then that walks it round a corner and the player has to follow.
        /// </summary>
        private void MoveSpark(bool nearby = false)
        {
            if (spark == null) return;
            // Freshly built pieces aren't in physics until synced.
            Physics.SyncTransforms();
            var centre = transform.position + Vector3.up * centreHeight * transform.localScale.y;
            var previous = spark.position;
            var previousDir = previous - centre;
            previousDir.y = 0f;
            nearby &= previousDir.sqrMagnitude > 0.0001f;
            // Nearby: the first tries stay close to the last spot; the rest fall back to anywhere.
            int nearTries = nearby ? 24 : 0;
            for (int attempt = 0; attempt < 32 + nearTries; attempt++)
            {
                bool near = attempt < nearTries;
                int tries = near ? attempt : attempt - nearTries;
                var dir = near ? previousDir.normalized + Random.insideUnitSphere * 0.9f : Random.onUnitSphere;
                if (viewFromFront && Vector3.Dot(dir, transform.forward) < 0f) dir = Vector3.Reflect(dir, transform.forward);
                dir.y = Mathf.Abs(dir.y) * 0.8f + 0.25f;
                var from = centre + dir.normalized * 3f;
                if (!Physics.Raycast(from, centre - from, out var hit, 3.2f, ~0, QueryTriggerInteraction.Ignore)) continue;
                if (!hit.collider.transform.IsChildOf(transform)) continue;
                // Facing outward and up a little, and not right where it just was (nor far from it, when nearby).
                if (hit.normal.y < -0.2f) continue;
                float moved = Vector3.Distance(hit.point, previous);
                if ((near || tries < 24) && moved < 0.35f) continue;
                if (near && moved > 0.9f * Mathf.Max(0.5f, transform.localScale.x)) continue;
                var point = hit.point + hit.normal * 0.05f;
                if ((near || tries < 30) && !InView(point, hit.normal, dir)) continue;
                spark.position = point;
                return;
            }
            // Nothing found: the top.
            spark.position = transform.position + Vector3.up * topHeight * transform.localScale.y;
        }

        private bool InView(Vector3 point, Vector3 normal, Vector3 dir)
        {
            if (viewFromFront)
            {
                // Someone standing in front of it, a little to either side.
                for (int i = -1; i <= 1; i++)
                {
                    var from = transform.position + transform.forward * 1.8f + transform.right * (0.3f * i) + Vector3.up * 1.6f;
                    if (Physics.CheckSphere(from, 0.15f, ~0, QueryTriggerInteraction.Ignore)) continue;
                    if (!Physics.Linecast(from, point, ~0, QueryTriggerInteraction.Ignore)) return true;
                }
                return false;
            }
            var side = new Vector3(normal.x, 0f, normal.z);
            if (side.sqrMagnitude < 0.01f) side = new Vector3(dir.x, 0f, dir.z);
            var eye = point + side.normalized * 1.6f;
            eye.y = transform.position.y + 1.6f;
            if (Physics.CheckSphere(eye, 0.2f, ~0, QueryTriggerInteraction.Ignore)) return false;
            // The eye has to be out in the open, reachable from above the node: a point inside a building would
            // pass the checks above, since rays don't see the inside faces of mesh colliders.
            var above = transform.position + Vector3.up * 1.6f;
            if (Physics.Linecast(above, eye, ~0, QueryTriggerInteraction.Ignore)) return false;
            return !Physics.Linecast(eye, point, ~0, QueryTriggerInteraction.Ignore);
        }

        // ---------------------------------------------------------------- building

        private static Material scrapSpark, corpseGlint, flesh, rags;

        /// <summary>A heap of rusted scrap: a jumble of plates, pipes and blocks around a core, with its spark.</summary>
        public static HarvestNode BuildScrapHeap(Transform parent, Vector3 position, float yaw, Material rust, Material metal)
        {
            var root = Root("ScrapNode", parent, position, yaw);
            Piece(root, PrimitiveType.Cube, new Vector3(0f, 0.45f, 0f), new Vector3(1.4f, 0.9f, 1.1f), new Vector3(8f, 15f, -6f), rust);
            Piece(root, PrimitiveType.Cube, new Vector3(0.35f, 0.95f, 0.1f), new Vector3(0.9f, 0.5f, 0.8f), new Vector3(-12f, 40f, 18f), metal);
            Piece(root, PrimitiveType.Cube, new Vector3(-0.45f, 0.8f, -0.2f), new Vector3(0.7f, 0.06f, 1.1f), new Vector3(30f, -20f, 10f), rust);
            Piece(root, PrimitiveType.Cylinder, new Vector3(0.1f, 0.6f, 0.5f), new Vector3(0.18f, 0.7f, 0.18f), new Vector3(70f, 30f, 0f), metal);
            Piece(root, PrimitiveType.Cylinder, new Vector3(-0.5f, 0.35f, 0.45f), new Vector3(0.14f, 0.5f, 0.14f), new Vector3(85f, -50f, 0f), rust);
            Piece(root, PrimitiveType.Cube, new Vector3(0.6f, 0.3f, -0.45f), new Vector3(0.5f, 0.6f, 0.4f), new Vector3(0f, 25f, 12f), rust);
            Piece(root, PrimitiveType.Cube, new Vector3(-0.1f, 1.25f, -0.1f), new Vector3(0.6f, 0.06f, 0.5f), new Vector3(-25f, 10f, 22f), metal);

            if (scrapSpark == null) scrapSpark = Unlit(new Color(1f, 0.75f, 0.3f));
            var node = root.gameObject.AddComponent<HarvestNode>();
            node.AddSpark(scrapSpark, new Color(1f, 0.7f, 0.3f));
            return node;
        }

        /// <summary>
        /// A pile of bodies dumped at the end of an alley: slumped torsos, heads and limbs under a torn tarp,
        /// long along the alley so it fits between the walls. Its glint is a sickly shimmer of rot.
        /// </summary>
        public static HarvestNode BuildCorpsePile(Transform parent, Vector3 position, float yaw, Material tarp)
        {
            if (flesh == null) flesh = Lit(new Color(0.27f, 0.25f, 0.22f), 0.3f);
            if (rags == null) rags = Lit(new Color(0.16f, 0.14f, 0.13f), 0.1f);
            var root = Root("CorpsePile", parent, position, yaw);
            // Two bodies lying lengthways, one slumped across them, one on top.
            Piece(root, PrimitiveType.Capsule, new Vector3(-0.22f, 0.2f, 0f), new Vector3(0.42f, 0.7f, 0.32f), new Vector3(90f, 0f, 6f), rags);
            Piece(root, PrimitiveType.Capsule, new Vector3(0.24f, 0.2f, 0.1f), new Vector3(0.4f, 0.65f, 0.3f), new Vector3(88f, 0f, -8f), rags);
            Piece(root, PrimitiveType.Capsule, new Vector3(0f, 0.48f, -0.25f), new Vector3(0.38f, 0.55f, 0.3f), new Vector3(80f, 70f, 0f), rags);
            Piece(root, PrimitiveType.Capsule, new Vector3(0.05f, 0.7f, 0.25f), new Vector3(0.34f, 0.5f, 0.28f), new Vector3(75f, -15f, 10f), rags);
            // Heads.
            Piece(root, PrimitiveType.Sphere, new Vector3(-0.2f, 0.22f, 0.8f), Vector3.one * 0.24f, Vector3.zero, flesh);
            Piece(root, PrimitiveType.Sphere, new Vector3(0.28f, 0.2f, -0.7f), Vector3.one * 0.23f, Vector3.zero, flesh);
            Piece(root, PrimitiveType.Sphere, new Vector3(0.3f, 0.82f, 0.55f), Vector3.one * 0.22f, Vector3.zero, flesh);
            // Arms and legs sticking out.
            Piece(root, PrimitiveType.Capsule, new Vector3(0.45f, 0.35f, -0.3f), new Vector3(0.1f, 0.35f, 0.1f), new Vector3(20f, 0f, 60f), flesh);
            Piece(root, PrimitiveType.Capsule, new Vector3(-0.42f, 0.55f, 0.15f), new Vector3(0.1f, 0.32f, 0.1f), new Vector3(-30f, 0f, -70f), flesh);
            Piece(root, PrimitiveType.Capsule, new Vector3(-0.15f, 0.12f, -0.85f), new Vector3(0.12f, 0.3f, 0.12f), new Vector3(90f, 15f, 0f), rags);
            // A torn tarp thrown half over the top.
            Piece(root, PrimitiveType.Cube, new Vector3(0.05f, 0.88f, -0.05f), new Vector3(0.8f, 0.03f, 0.9f), new Vector3(14f, 8f, -18f), tarp);

            if (corpseGlint == null) corpseGlint = Unlit(new Color(0.75f, 1f, 0.45f));
            var node = root.gameObject.AddComponent<HarvestNode>();
            node.centreHeight = 0.45f;
            node.topHeight = 1f;
            node.viewFromFront = true;
            node.yieldPerDamage = 0.12f;
            node.breakHaul = new Vector2Int(2, 4);
            node.extrasPer10Damage = new Vector2(0.08f, 0.05f);
            node.breakExtras = new Vector2(0.35f, 0.2f);
            node.AddSpark(corpseGlint, new Color(0.6f, 1f, 0.35f));
            return node;
        }

        private void AddSpark(Material material, Color lightColor)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "Spark";
            UnityEngine.Object.Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(transform, false);
            go.transform.localScale = Vector3.one * 0.14f;
            go.GetComponent<Renderer>().sharedMaterial = material;
            var light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = lightColor;
            light.range = 1.8f;
            light.shadows = LightShadows.None;
            spark = go.transform;
            sparkLight = light;
        }

        private static Transform Root(string name, Transform parent, Vector3 position, float yaw)
        {
            var root = new GameObject(name).transform;
            root.SetParent(parent, false);
            root.SetPositionAndRotation(position, Quaternion.Euler(0f, yaw, 0f));
            return root;
        }

        private static Material Unlit(Color color) =>
            new(Shader.Find("Universal Render Pipeline/Unlit")) { color = color };

        private static Material Lit(Color color, float smoothness)
        {
            var m = new Material(Shader.Find("Universal Render Pipeline/Lit")) { color = color };
            m.SetFloat("_Smoothness", smoothness);
            return m;
        }

        private static void Piece(Transform root, PrimitiveType type, Vector3 position, Vector3 scale, Vector3 euler, Material material)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = "Piece";
            go.transform.SetParent(root, false);
            go.transform.localPosition = position;
            go.transform.localScale = scale;
            go.transform.localRotation = Quaternion.Euler(euler);
            go.GetComponent<Renderer>().sharedMaterial = material;
        }
    }
}
