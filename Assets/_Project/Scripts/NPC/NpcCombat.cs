using System;
using System.Collections.Generic;
using ThePitt.Audio;
using ThePitt.Combat;
using ThePitt.Factions;
using ThePitt.Player;
using UnityEngine;
using UnityEngine.AI;
using Random = UnityEngine.Random;

namespace ThePitt.NPC
{
    /// <summary>
    /// An NPC's health and fight-or-flight. Those who fight back (gang members, dealers) chase the player
    /// down and beat or shoot them; everyone else runs. Hurting a gang member turns their whole gang on
    /// the player for a while (see <see cref="FactionManager.ReportAttack"/>): gangmates nearby pile in at
    /// once, and any of them who spot the player later attack on sight. Gunshots send bystanders running.
    /// When it's over, walkers head indoors and NPCs with a post (gang squares) walk back to it.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public class NpcCombat : MonoBehaviour, IDamageable
    {
        public enum Mood { Calm, Fleeing, Fighting, Dead }

        private static readonly List<NpcCombat> All = new();

        [SerializeField, Min(1f)] private float maxHealth = 60f;
        [SerializeField] private bool fightsBack;
        [SerializeField] private bool armed;

        [Header("Fists")]
        [SerializeField, Min(0f)] private float meleeDamage = 9f;
        [SerializeField, Min(0.5f)] private float meleeRange = 1.6f;
        [SerializeField, Min(0.1f)] private float meleeInterval = 1.1f;

        [Header("Gun (if armed)")]
        [SerializeField, Min(0f)] private float gunDamage = 12f;
        [SerializeField, Min(1f)] private float gunRange = 20f;
        [SerializeField, Min(0.1f)] private float gunInterval = 1.7f;
        [Tooltip("Chance to hit at point-blank; falls off with distance.")]
        [SerializeField, Range(0f, 1f)] private float accuracy = 0.7f;

        [Header("Awareness")]
        [Tooltip("How far a gang member out for the player spots them.")]
        [SerializeField, Min(1f)] private float sightRange = 15f;
        [Tooltip("Gangmates this close join in when one is attacked.")]
        [SerializeField, Min(0f)] private float allyRadius = 25f;
        [Tooltip("Bystanders this close run when someone is attacked.")]
        [SerializeField, Min(0f)] private float witnessRadius = 15f;
        [Tooltip("Seconds a frightened NPC keeps running.")]
        [SerializeField, Min(1f)] private float panicSeconds = 20f;
        [Tooltip("Give up the chase after this long without seeing the player.")]
        [SerializeField, Min(1f)] private float loseInterest = 10f;
        [SerializeField, Min(0.5f)] private float runSpeed = 3.6f;

        private NavMeshAgent agent;
        private NpcWalker walker;
        private NpcBuyer buyer;
        private Transform player;
        private PlayerVitals playerVitals;
        private FirstPersonController playerController;
        private Renderer[] renderers;
        private MaterialPropertyBlock block;
        private float walkSpeed;
        private bool hasPost;
        private Vector3 post;
        private Quaternion postRotation;
        private bool returning;
        private float moodUntil, nextThink, nextAction, lastSeen, flinchUntil;
        private bool flinching;
        private Color[] savedColors;
        private float fallStart = -1f;
        private Quaternion fallFrom, fallTo;
        private Vector3 fallFromPos;

        private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");

        public float Health { get; private set; }
        public float MaxHealth => maxHealth;
        public Mood State { get; private set; }
        public bool IsDead => State == Mood.Dead;
        public bool IsCalm => State == Mood.Calm;
        public bool Armed => armed;

        /// <summary>Fired once, when killed.</summary>
        public event Action<NpcCombat> Died;

        private void Awake()
        {
            agent = GetComponent<NavMeshAgent>();
            walker = GetComponent<NpcWalker>();
            buyer = GetComponent<NpcBuyer>();
            Health = maxHealth;
        }

        /// <summary>Sets how tough and how dangerous this NPC is. Call right after spawning.</summary>
        public void Setup(float health, bool fights, bool carriesGun)
        {
            maxHealth = Health = health;
            fightsBack = fights;
            armed = fights && carriesGun;
            if (armed)
            {
                // A pipe pistol in the right hand, so the player can tell.
                var gun = GameObject.CreatePrimitive(PrimitiveType.Cube);
                gun.name = "Gun";
                Destroy(gun.GetComponent<Collider>());
                gun.transform.SetParent(transform, false);
                gun.transform.localPosition = new Vector3(0.32f, 1.05f, 0.22f);
                gun.transform.localScale = new Vector3(0.06f, 0.08f, 0.24f);
                gun.GetComponent<Renderer>().sharedMaterial = GetComponentInChildren<Renderer>().sharedMaterial;
                var r = gun.GetComponent<Renderer>();
                var b = new MaterialPropertyBlock();
                b.SetColor(BaseColor, new Color(0.1f, 0.1f, 0.11f));
                r.SetPropertyBlock(b);
            }
        }

        /// <summary>Marks the spot this NPC stands at (gang squares) and walks back to after trouble.</summary>
        public void SetPost(Vector3 position, Quaternion rotation)
        {
            hasPost = true;
            post = position;
            postRotation = rotation;
        }

        private void OnEnable()
        {
            All.Add(this);
            Noise.Heard += OnNoise;
        }

        private void OnDisable()
        {
            All.Remove(this);
            Noise.Heard -= OnNoise;
            if (playerVitals != null) playerVitals.Died -= OnPlayerDied;
        }

        private void Start()
        {
            walkSpeed = agent.speed;
            renderers = GetComponentsInChildren<Renderer>();
            block = new MaterialPropertyBlock();
            playerController = FindFirstObjectByType<FirstPersonController>();
            if (playerController != null)
            {
                player = playerController.transform;
                playerVitals = player.GetComponent<PlayerVitals>();
                if (playerVitals != null) playerVitals.Died += OnPlayerDied;
            }
        }

        // ---------------------------------------------------------------- taking hits

        public void TakeDamage(DamageInfo info)
        {
            if (IsDead || info.amount <= 0f) return;
            Health -= info.amount;
            StartFlinch();

            var gang = buyer != null ? buyer.Faction : null;
            if (info.fromPlayer && gang != null && FactionManager.Instance != null)
                FactionManager.Instance.ReportAttack(gang, Health <= 0f);

            if (Health <= 0f)
            {
                Die(info);
                if (info.fromPlayer) RaiseAlarm(gang);
                return;
            }
            if (info.fromPlayer)
            {
                React();
                RaiseAlarm(gang);
            }
        }

        private void React()
        {
            if (fightsBack) Engage();
            else Flee();
        }

        /// <summary>Gangmates nearby and anyone working the same square join the fight; bystanders run.</summary>
        private void RaiseAlarm(FactionManager.FactionState gang)
        {
            foreach (var other in All.ToArray())
            {
                if (other == this || other.IsDead) continue;
                float d = Vector3.Distance(other.transform.position, transform.position);
                var otherGang = other.buyer != null ? other.buyer.Faction : null;
                bool gangmate = gang != null && otherGang == gang && d <= allyRadius;
                bool crewmate = hasPost && other.hasPost && Vector3.Distance(other.post, post) <= CrewRadius;
                if (other.fightsBack && (gangmate || crewmate)) other.Engage();
                else if (!other.fightsBack && d <= witnessRadius && other.IsCalm) other.Flee();
            }
        }

        /// <summary>NPCs posted this close together (the same gang square) look out for each other.</summary>
        private const float CrewRadius = 10f;

        private void OnNoise(Vector3 position, float radius)
        {
            if (IsDead || fightsBack || !IsCalm) return;
            if ((transform.position - position).sqrMagnitude <= radius * radius) Flee();
        }

        private void OnPlayerDied()
        {
            if (State == Mood.Fighting) CalmDown();
        }

        // ---------------------------------------------------------------- moods

        private void Engage()
        {
            if (IsDead || player == null) return;
            State = Mood.Fighting;
            lastSeen = Time.time;
            TakeControl();
            agent.speed = runSpeed;
        }

        private void Flee()
        {
            if (IsDead) return;
            State = Mood.Fleeing;
            moodUntil = Time.time + panicSeconds;
            TakeControl();
            agent.speed = runSpeed;
            nextThink = 0f;
        }

        /// <summary>Takes the agent over from the walker (or wakes it up for a standing NPC).</summary>
        private void TakeControl()
        {
            returning = false;
            if (walker != null) walker.enabled = false;
            if (!agent.enabled)
            {
                agent.enabled = true;
                if (NavMesh.SamplePosition(transform.position, out var hit, 2f, NavMesh.AllAreas)) agent.Warp(hit.position);
            }
            if (agent.isOnNavMesh) agent.isStopped = false;
        }

        private void CalmDown()
        {
            State = Mood.Calm;
            agent.speed = walkSpeed;
            if (!agent.isOnNavMesh) return;
            agent.isStopped = false;
            if (hasPost)
            {
                returning = true;
                agent.SetDestination(post);
            }
            else if (walker != null)
            {
                // Had enough for one day: off home.
                walker.enabled = true;
                walker.LeaveNow();
            }
        }

        // ---------------------------------------------------------------- update

        private void Update()
        {
            Flinch();
            if (IsDead)
            {
                Fall();
                return;
            }

            if (State == Mood.Fighting && player != null) FaceTowards(player.position);
            if (Time.time < nextThink) return;
            nextThink = Time.time + 0.3f;

            switch (State)
            {
                case Mood.Calm: ThinkCalm(); break;
                case Mood.Fleeing: ThinkFlee(); break;
                case Mood.Fighting: ThinkFight(); break;
            }
        }

        private void ThinkCalm()
        {
            if (returning && agent.enabled && !agent.pathPending && agent.remainingDistance <= agent.stoppingDistance + 0.2f)
            {
                returning = false;
                agent.enabled = false;
                transform.SetPositionAndRotation(post, postRotation);
            }

            // Members of a gang the player has crossed attack on sight.
            if (!fightsBack || player == null || buyer == null || FactionManager.Instance == null) return;
            if (!FactionManager.Instance.IsHostileToPlayer(buyer.Faction)) return;
            if ((player.position - transform.position).sqrMagnitude > sightRange * sightRange) return;
            if (CanSee(player)) Engage();
        }

        private void ThinkFlee()
        {
            if (player == null || !agent.isOnNavMesh)
            {
                CalmDown();
                return;
            }
            var away = transform.position - player.position;
            away.y = 0f;
            if (Time.time > moodUntil && away.magnitude > 20f)
            {
                CalmDown();
                return;
            }
            var target = transform.position + away.normalized * 12f + Random.insideUnitSphere * 3f;
            if (NavMesh.SamplePosition(target, out var hit, 4f, NavMesh.AllAreas)) agent.SetDestination(hit.position);
        }

        private void ThinkFight()
        {
            if (player == null || !agent.isOnNavMesh)
            {
                CalmDown();
                return;
            }
            float distance = Vector3.Distance(transform.position, player.position);
            bool visible = CanSee(player);
            if (visible) lastSeen = Time.time;
            if (Time.time - lastSeen > loseInterest || distance > 45f)
            {
                CalmDown();
                return;
            }

            if (armed && visible && distance <= gunRange && distance > meleeRange)
            {
                agent.isStopped = true;
                if (Time.time >= nextAction) Shoot(distance);
            }
            else if (distance <= meleeRange)
            {
                agent.isStopped = true;
                if (Time.time >= nextAction) Punch();
            }
            else
            {
                agent.isStopped = false;
                agent.SetDestination(player.position);
            }
        }

        private void Punch()
        {
            nextAction = Time.time + meleeInterval;
            if (playerVitals == null) return;
            AudioManager.PlayRandom(AudioManager.Library?.punches, player.position + Vector3.up * 1.4f, 0.85f, 20f);
            playerVitals.TakeDamage(new DamageInfo
            {
                amount = meleeDamage,
                point = player.position + Vector3.up,
                direction = (player.position - transform.position).normalized,
                source = gameObject,
            });
        }

        private void Shoot(float distance)
        {
            nextAction = Time.time + gunInterval * Random.Range(0.8f, 1.25f);
            float chance = accuracy * Mathf.Lerp(1f, 0.35f, distance / gunRange);
            if (playerController != null && playerController.IsSprinting) chance *= 0.6f;

            var from = transform.position + Vector3.up * 1.1f + transform.right * 0.3f + transform.forward * 0.35f;
            var aim = player.position + Vector3.up * 1.3f;
            bool hit = Random.value < chance;
            if (!hit) aim += Random.onUnitSphere * Random.Range(0.6f, 1.5f);
            if (hit && playerVitals != null)
                playerVitals.TakeDamage(new DamageInfo
                {
                    amount = gunDamage,
                    point = aim,
                    direction = (aim - from).normalized,
                    source = gameObject,
                });
            Tracer(from, aim);
            AudioManager.Play(AudioManager.Library?.gunshot, from, 1f, 90f, 6f, 0.08f);
            Noise.Make(transform.position, 40f);
        }

        // ---------------------------------------------------------------- dying

        private void Die(DamageInfo info)
        {
            State = Mood.Dead;
            Health = 0f;
            if (walker != null) walker.enabled = false;
            if (agent.enabled) agent.enabled = false;

            // Topple away from the blow.
            var away = info.direction;
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f) away = -transform.forward;
            fallFrom = transform.rotation;
            fallFromPos = transform.position;
            fallTo = Quaternion.FromToRotation(Vector3.up, away.normalized) * transform.rotation;
            fallStart = Time.time;

            Died?.Invoke(this);
        }

        private void Fall()
        {
            if (fallStart < 0f) return;
            float t = Mathf.Clamp01((Time.time - fallStart) / 0.45f);
            float eased = t * t;
            transform.rotation = Quaternion.Slerp(fallFrom, fallTo, eased);
            // Lift by the body's radius so it lies on the ground rather than half through it.
            transform.position = fallFromPos + Vector3.up * 0.3f * eased;
            if (t >= 1f) fallStart = -1f;
        }

        // ---------------------------------------------------------------- helpers

        private bool CanSee(Transform target)
        {
            var eye = transform.position + Vector3.up * 1.6f + transform.forward * 0.35f;
            var to = target.position + Vector3.up * 1.5f;
            if (!Physics.Linecast(eye, to, out var hit, ~0, QueryTriggerInteraction.Ignore)) return true;
            return hit.collider.transform.IsChildOf(target) || hit.collider.transform.IsChildOf(transform);
        }

        private void FaceTowards(Vector3 target)
        {
            var d = target - transform.position;
            d.y = 0f;
            if (d.sqrMagnitude < 0.01f) return;
            transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(d), 540f * Time.deltaTime);
        }

        /// <summary>A red flash when hit, then back to whatever colour each part was (gang tints included).</summary>
        private void StartFlinch()
        {
            flinchUntil = Time.time + 0.12f;
            if (renderers == null || flinching) return;
            flinching = true;
            savedColors ??= new Color[renderers.Length];
            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null || r.name == "Facing") continue;
                r.GetPropertyBlock(block);
                savedColors[i] = block.isEmpty ? r.sharedMaterial.GetColor(BaseColor) : block.GetColor(BaseColor);
                block.SetColor(BaseColor, new Color(0.9f, 0.08f, 0.05f));
                r.SetPropertyBlock(block);
            }
        }

        private void Flinch()
        {
            if (!flinching || Time.time < flinchUntil) return;
            flinching = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null || r.name == "Facing") continue;
                r.GetPropertyBlock(block);
                block.SetColor(BaseColor, savedColors[i]);
                r.SetPropertyBlock(block);
            }
        }

        private static void Tracer(Vector3 from, Vector3 to)
        {
            var go = new GameObject("NpcShot");
            go.transform.position = from;
            var light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.7f, 0.35f);
            light.intensity = 5f;
            light.range = 7f;
            var line = go.AddComponent<LineRenderer>();
            line.positionCount = 2;
            line.SetPosition(0, from);
            line.SetPosition(1, to);
            line.startWidth = 0.02f;
            line.endWidth = 0.005f;
            line.material = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { color = new Color(1f, 0.8f, 0.5f) };
            Destroy(line.material, 0.06f);
            Destroy(go, 0.06f);
        }
    }
}
