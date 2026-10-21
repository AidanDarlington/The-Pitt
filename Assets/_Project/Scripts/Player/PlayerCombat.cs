using System;
using System.Collections.Generic;
using ThePitt.Audio;
using ThePitt.Combat;
using ThePitt.Items;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ThePitt.Player
{
    /// <summary>
    /// The player's fighting: fists by default, or a weapon equipped from the inventory. Attack (left
    /// mouse) swings or fires, R reloads, 1 / 2 cycle through the weapons being carried. Firearms load
    /// rounds from the inventory and are loud: every shot is heard by anyone nearby.
    /// </summary>
    public class PlayerCombat : MonoBehaviour
    {
        [SerializeField] private InputActionAsset actions;
        [SerializeField] private Transform viewPoint;
        [SerializeField] private Inventory inventory;

        [Header("Fists")]
        [SerializeField, Min(0f)] private float fistDamage = 8f;
        [SerializeField, Min(0.5f)] private float fistRange = 1.8f;
        [SerializeField, Min(0.05f)] private float fistInterval = 0.55f;

        [Header("Feel")]
        [Tooltip("Head and body shots: hits on a collider named Head do this much more.")]
        [SerializeField, Min(1f)] private float headshotMultiplier = 1.75f;
        [Tooltip("How far a gunshot is heard.")]
        [SerializeField, Min(0f)] private float gunshotRadius = 40f;

        /// <summary>
        /// Rendering layer for the weapon held in front of the camera (Light Layer 1). Lights right at the
        /// camera (the torch) leave it out; the murk fill includes it.
        /// </summary>
        public const uint ViewmodelRenderingLayer = 1u << 1;

        private InputAction attack, previous, next;
        /// <summary>Scroll wheel (or whatever is bound) to switch what's in hand.</summary>
        public InputAction PreviousAction => previous;
        public InputAction NextAction => next;
        public InputAction AttackAction => attack;
        private FirstPersonController controller;
        private PlacementController placement;
        private BuildModeController buildMode;
        private readonly Dictionary<ItemDefinition, int> loaded = new();
        private readonly RaycastHit[] hits = new RaycastHit[16];
        private float nextAttack;
        private float reloadDone = -1f;
        private Transform viewmodel;
        private float swingTime = -10f;
        private bool swingLeft;

        public ItemDefinition Equipped { get; private set; }
        public ItemQuality EquippedQuality { get; private set; }
        /// <summary>Nothing in hand, not even fists up: no viewmodel and no attacking.</summary>
        public bool HandsDown { get; private set; } = true;
        /// <summary>
        /// Food, drink, a drug or a station held up from the hotbar to be used (the hotbar uses it on click).
        /// Nothing to fight with while it's in hand.
        /// </summary>
        public ItemDefinition HeldUsable { get; private set; }
        public ItemQuality HeldUsableQuality { get; private set; }
        public bool IsReloading => reloadDone > Time.time;
        private Hotbar hotbar;
        public int LoadedRounds => Equipped != null && loaded.TryGetValue(Equipped, out int n) ? n : 0;
        public int SpareRounds => Equipped?.Ammo != null ? inventory.Count(Equipped.Ammo) : 0;
        public IReadOnlyDictionary<ItemDefinition, int> Loaded => loaded;

        /// <summary>Fired when the weapon, its ammo or reload state changes.</summary>
        public event Action Changed;
        /// <summary>Fired when an attack lands on something that can be hurt: killed it?</summary>
        public event Action<bool> Hit;

        private void Awake()
        {
            var map = actions.FindActionMap("Player", true);
            attack = map.FindAction("Attack", true);
            previous = map.FindAction("Previous", true);
            next = map.FindAction("Next", true);
            if (viewPoint == null && Camera.main != null) viewPoint = Camera.main.transform;
            if (inventory == null) inventory = GetComponent<Inventory>();
            controller = GetComponent<FirstPersonController>();
            placement = GetComponent<PlacementController>();
            buildMode = GetComponent<BuildModeController>();
            hotbar = GetComponent<Hotbar>();
        }

        private void OnEnable() => inventory.Changed += OnInventoryChanged;
        private void OnDisable() => inventory.Changed -= OnInventoryChanged;

        private void Start() => BuildViewmodel();

        private void OnInventoryChanged()
        {
            // Sold, eaten or stashed: back to fists.
            if (Equipped != null && inventory.Count(Equipped, EquippedQuality) <= 0) Equip(null, ItemQuality.Standard);
            else Changed?.Invoke();
        }

        /// <summary>Takes a weapon in hand, or puts it away if it's already there. Null = fists.</summary>
        public void Equip(ItemDefinition weapon, ItemQuality quality)
        {
            if (weapon != null && (!weapon.IsWeapon || inventory.Count(weapon, quality) <= 0)) return;
            if (weapon != null && weapon == Equipped && quality == EquippedQuality) weapon = null;
            Equipped = weapon;
            EquippedQuality = weapon != null && weapon.HasQuality ? quality : ItemQuality.Standard;
            HandsDown = false;
            HeldUsable = null;
            reloadDone = -1f;
            BuildViewmodel();
            Changed?.Invoke();
        }

        /// <summary>
        /// Takes this in hand without toggling (the hotbar). Anything that can't be held, or null, brings the
        /// fists up instead.
        /// </summary>
        public void Hold(ItemDefinition item, ItemQuality quality)
        {
            if (item != null && (!item.IsWeapon || inventory.Count(item, quality) <= 0)) item = null;
            var q = item != null && item.HasQuality ? quality : ItemQuality.Standard;
            if (!HandsDown && HeldUsable == null && item == Equipped && q == EquippedQuality) return;
            Equipped = item;
            EquippedQuality = q;
            HandsDown = false;
            HeldUsable = null;
            reloadDone = -1f;
            BuildViewmodel();
            Changed?.Invoke();
        }

        /// <summary>Holds up something to use rather than fight with (the hotbar does the using).</summary>
        public void HoldUsable(ItemDefinition item, ItemQuality quality)
        {
            if (item == null)
            {
                Hold(null, ItemQuality.Standard);
                return;
            }
            var q = item.HasQuality ? quality : ItemQuality.Standard;
            if (!HandsDown && HeldUsable == item && HeldUsableQuality == q) return;
            Equipped = null;
            EquippedQuality = ItemQuality.Standard;
            HeldUsable = item;
            HeldUsableQuality = q;
            HandsDown = false;
            reloadDone = -1f;
            BuildViewmodel();
            Changed?.Invoke();
        }

        /// <summary>Lowers whatever is in hand, fists included.</summary>
        public void PutAway()
        {
            if (HandsDown) return;
            Equipped = null;
            EquippedQuality = ItemQuality.Standard;
            HeldUsable = null;
            HandsDown = true;
            reloadDone = -1f;
            BuildViewmodel();
            Changed?.Invoke();
        }

        /// <summary>Whether the player is free to use their hands: in control, not in a menu or placing.</summary>
        public bool CanAct => InControl && !InBuildMode;

        /// <summary>
        /// In control and not placing (nor on the frame a placement ended), build mode or not. Build mode allows the
        /// hotbar, for stations, but no fighting.
        /// </summary>
        public bool InControl =>
            (controller == null || controller.InputEnabled) && Cursor.lockState == CursorLockMode.Locked &&
            (placement == null || (!placement.IsPlacing && placement.LastCancelFrame != Time.frameCount && placement.LastPlacedFrame != Time.frameCount));

        public bool InBuildMode => buildMode != null && buildMode.IsActive;

        /// <summary>
        /// The attack button is still down from a click that wasn't meant for the hands (in a menu, or the click
        /// that took the cursor back): ignored until it's let go.
        /// </summary>
        public bool AttackSwallowed { get; private set; }

        private void Update()
        {
            AnimateViewmodel();
            if (reloadDone > 0f && Time.time >= reloadDone) FinishReload();
            if (!InControl)
            {
                AttackSwallowed = true;
                return;
            }
            if (AttackSwallowed && !attack.IsPressed()) AttackSwallowed = false;
            if (InBuildMode) return;

            // With a hotbar, it does the switching; without one, the scroll wheel cycles weapons.
            if (hotbar == null)
            {
                if (previous.WasPressedThisFrame()) Cycle(-1);
                else if (next.WasPressedThisFrame()) Cycle(1);
            }
            if (Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame) StartReload();

            if (!HandsDown && HeldUsable == null && !AttackSwallowed && attack.IsPressed() && Time.time >= nextAttack && !IsReloading) Attack();
        }

        private void Cycle(int step)
        {
            // Fists plus every distinct weapon being carried.
            var options = new List<(ItemDefinition, ItemQuality)> { (null, ItemQuality.Standard) };
            for (int i = 0; i < inventory.SlotCount; i++)
            {
                var s = inventory.GetSlot(i);
                if (!s.IsEmpty && s.item.IsWeapon && !options.Contains((s.item, s.quality))) options.Add((s.item, s.quality));
            }
            int current = options.IndexOf((Equipped, EquippedQuality));
            var (item, quality) = options[(current + step + options.Count) % options.Count];
            if (item == null) Equip(null, ItemQuality.Standard);
            else if (item != Equipped || quality != EquippedQuality) Equip(item, quality);
        }

        // ---------------------------------------------------------------- attacking

        private void Attack()
        {
            if (Equipped != null && Equipped.WeaponType == WeaponType.Firearm)
            {
                if (LoadedRounds <= 0)
                {
                    // Out of rounds altogether: just the click of an empty chamber.
                    if (!StartReload()) AudioManager.Play2D(AudioManager.Library?.dryFire, 0.6f);
                    nextAttack = Time.time + 0.35f;
                    return;
                }
                Fire();
                return;
            }

            float damage = Equipped != null ? Equipped.DamageAt(EquippedQuality) : fistDamage;
            float range = Equipped != null ? Equipped.Range : fistRange;
            nextAttack = Time.time + (Equipped != null ? Equipped.AttackInterval : fistInterval);
            swingTime = Time.time;
            swingLeft = !swingLeft;
            AudioManager.PlayRandom2D(AudioManager.Library?.swings, Equipped != null ? 0.55f : 0.3f, 0.12f);

            var ray = new Ray(viewPoint.position, viewPoint.forward);
            if (Nearest(Physics.SphereCastNonAlloc(ray, 0.3f, hits, range, ~0, QueryTriggerInteraction.Ignore), out var hit))
            {
                // Scrap heaps and corpse piles are worked with their own tool; anything else just bounces off.
                var node = hit.collider.GetComponentInParent<World.HarvestNode>();
                if (node != null)
                {
                    Mine(node, ray, hit.point);
                    return;
                }
                Strike(hit, damage, ray.direction);
            }
        }

        private void Fire()
        {
            loaded[Equipped]--;
            nextAttack = Time.time + Equipped.AttackInterval;
            swingTime = Time.time;

            var dir = Quaternion.AngleAxis(UnityEngine.Random.Range(0f, Equipped.Spread), UnityEngine.Random.onUnitSphere) * viewPoint.forward;
            var ray = new Ray(viewPoint.position, dir);
            var end = ray.GetPoint(Equipped.Range);
            if (Nearest(Physics.RaycastNonAlloc(ray, hits, Equipped.Range, ~0, QueryTriggerInteraction.Ignore), out var hit))
            {
                end = hit.point;
                Strike(hit, Equipped.DamageAt(EquippedQuality), dir);
            }

            MuzzleFlash(end);
            AudioManager.Play2D(AudioManager.Library?.gunshot, 0.9f, 0.06f);
            Noise.Make(transform.position, gunshotRadius);
            Changed?.Invoke();
        }

        private void Mine(World.HarvestNode node, Ray aim, Vector3 point)
        {
            var lib = AudioManager.Library;
            bool soft = node.Tool == ToolType.Shovel;
            if (Equipped == null || Equipped.ToolType != node.Tool)
            {
                AudioManager.PlayRandom(soft ? lib?.punches : lib?.meleeHits, point, 0.5f, 20f);
                return;
            }
            bool spark = node.Mine(Equipped, EquippedQuality, aim, inventory);
            if (soft)
            {
                // A wet thud, sharper on the glint.
                if (spark) AudioManager.PlayRandom(lib?.meleeHits, point, 0.9f, 25f, 1.5f, 0.1f);
                else AudioManager.PlayRandom(lib?.punches, point, 0.9f, 20f);
            }
            // A bright ring on the spark; a dull crunch otherwise.
            else if (spark) AudioManager.PlayRandom(lib?.craftMetal, point, 0.9f, 25f, 1.5f, 0.15f);
            else AudioManager.PlayRandom(lib?.meleeHits, point, 0.8f, 25f);
            Hit?.Invoke(spark);
        }

        private void Strike(RaycastHit hit, float damage, Vector3 direction)
        {
            var target = hit.collider.GetComponentInParent<IDamageable>();
            if (target == null || target.IsDead) return;
            if (hit.collider.name == "Head") damage *= headshotMultiplier;
            // Fists and clubs make themselves heard; a bullet's report is the gunshot itself.
            if (Equipped == null) AudioManager.PlayRandom(AudioManager.Library?.punches, hit.point, 0.8f, 20f);
            else if (Equipped.WeaponType == WeaponType.Melee) AudioManager.PlayRandom(AudioManager.Library?.meleeHits, hit.point, 0.9f, 25f);
            target.TakeDamage(new DamageInfo
            {
                amount = damage,
                point = hit.point,
                direction = direction,
                source = gameObject,
                fromPlayer = true,
            });
            Hit?.Invoke(target.IsDead);
        }

        /// <summary>The closest hit that isn't the player's own body.</summary>
        private bool Nearest(int count, out RaycastHit nearest)
        {
            nearest = default;
            float best = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                if (hits[i].collider.transform.IsChildOf(transform)) continue;
                // A sphere cast starting inside something reports distance 0 and no point.
                if (hits[i].distance <= 0f && hits[i].point == Vector3.zero) continue;
                if (hits[i].distance < best)
                {
                    best = hits[i].distance;
                    nearest = hits[i];
                }
            }
            return best < float.MaxValue;
        }

        // ---------------------------------------------------------------- reloading

        /// <summary>Starts reloading if there's room in the gun and rounds to load. Returns whether it started.</summary>
        private bool StartReload()
        {
            if (Equipped == null || Equipped.WeaponType != WeaponType.Firearm || IsReloading) return false;
            if (LoadedRounds >= Equipped.MagazineSize || Equipped.Ammo == null || inventory.Count(Equipped.Ammo) <= 0) return false;
            reloadDone = Time.time + Equipped.ReloadSeconds;
            AudioManager.Play2D(AudioManager.Library?.reload, 0.7f);
            Changed?.Invoke();
            return true;
        }

        private void FinishReload()
        {
            reloadDone = -1f;
            if (Equipped?.Ammo == null) return;
            int wanted = Equipped.MagazineSize - LoadedRounds;
            int taken = inventory.Remove(Equipped.Ammo, wanted);
            loaded[Equipped] = LoadedRounds + taken;
            Changed?.Invoke();
        }

        /// <summary>Puts back what was loaded and in hand when the game was saved.</summary>
        public void Restore(ItemDefinition weapon, ItemQuality quality, IEnumerable<(ItemDefinition weapon, int rounds)> rounds)
        {
            loaded.Clear();
            foreach (var (w, n) in rounds)
                if (w != null && n > 0) loaded[w] = n;
            Equipped = null;
            if (weapon != null) Equip(weapon, quality);
            else
            {
                BuildViewmodel();
                Changed?.Invoke();
            }
        }

        // ---------------------------------------------------------------- viewmodel

        private static Material Mat(Color color, float smoothness = 0.2f)
        {
            var m = new Material(Shader.Find("Universal Render Pipeline/Lit")) { color = color };
            m.SetFloat("_Smoothness", smoothness);
            return m;
        }

        private static Material skin, metal, rust, grip;

        private static readonly Dictionary<Sprite, Material> iconMaterials = new();

        /// <summary>A hand holding up the item's icon, low on the right.</summary>
        private void BuildHeldItem(ItemDefinition item)
        {
            Part(PrimitiveType.Cube, new Vector3(0.22f, -0.27f, 0.42f), new Vector3(0.085f, 0.075f, 0.1f), new Vector3(0f, -10f, 0f), skin).name = "Hand";
            var sprite = item.Icon;
            if (sprite == null)
            {
                Part(PrimitiveType.Cube, new Vector3(0.22f, -0.2f, 0.45f), new Vector3(0.09f, 0.09f, 0.09f), new Vector3(10f, 25f, 0f), rust).name = "Item";
                return;
            }
            if (!iconMaterials.TryGetValue(sprite, out var material) || material == null)
            {
                // The icon's own patch of its texture, drawn unlit with its transparency.
                material = new Material(Shader.Find("Sprites/Default")) { mainTexture = sprite.texture };
                var r = sprite.textureRect;
                material.mainTextureScale = new Vector2(r.width / sprite.texture.width, r.height / sprite.texture.height);
                material.mainTextureOffset = new Vector2(r.x / sprite.texture.width, r.y / sprite.texture.height);
                iconMaterials[sprite] = material;
            }
            var icon = Part(PrimitiveType.Quad, new Vector3(0.2f, -0.17f, 0.46f), new Vector3(0.17f, 0.17f, 1f), new Vector3(0f, 12f, 0f), material);
            icon.name = "Item";
        }

        private Transform Part(PrimitiveType type, Vector3 position, Vector3 scale, Vector3 euler, Material material)
        {
            var go = GameObject.CreatePrimitive(type);
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(viewmodel, false);
            go.transform.localPosition = position;
            go.transform.localScale = scale;
            go.transform.localRotation = Quaternion.Euler(euler);
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            // Its own rendering layer, so the head torch inches away doesn't blow it out.
            r.renderingLayerMask = ViewmodelRenderingLayer;
            return go.transform;
        }

        private void BuildViewmodel()
        {
            if (viewPoint == null) return;
            if (viewmodel != null) Destroy(viewmodel.gameObject);
            // Unity's null check, not ??=: these can be destroyed between play sessions.
            if (skin == null) skin = Mat(new Color(0.55f, 0.4f, 0.32f));
            if (metal == null) metal = Mat(new Color(0.42f, 0.43f, 0.45f), 0.5f);
            if (rust == null) rust = Mat(new Color(0.42f, 0.22f, 0.12f));
            if (grip == null) grip = Mat(new Color(0.18f, 0.12f, 0.08f));

            viewmodel = new GameObject("Viewmodel").transform;
            viewmodel.SetParent(viewPoint, false);

            if (HandsDown) return;
            if (HeldUsable != null)
            {
                BuildHeldItem(HeldUsable);
                return;
            }
            if (Equipped == null)
            {
                Part(PrimitiveType.Cube, new Vector3(-0.22f, -0.22f, 0.42f), new Vector3(0.09f, 0.09f, 0.12f), new Vector3(0f, 10f, 0f), skin).name = "FistL";
                Part(PrimitiveType.Cube, new Vector3(0.22f, -0.22f, 0.42f), new Vector3(0.09f, 0.09f, 0.12f), new Vector3(0f, -10f, 0f), skin).name = "FistR";
            }
            else if (Equipped.WeaponType == WeaponType.Firearm)
            {
                Part(PrimitiveType.Cube, new Vector3(0.18f, -0.17f, 0.42f), new Vector3(0.05f, 0.07f, 0.2f), Vector3.zero, rust);
                Part(PrimitiveType.Cylinder, new Vector3(0.18f, -0.14f, 0.58f), new Vector3(0.035f, 0.12f, 0.035f), new Vector3(90f, 0f, 0f), metal).name = "Muzzle";
                Part(PrimitiveType.Cube, new Vector3(0.18f, -0.24f, 0.37f), new Vector3(0.045f, 0.12f, 0.06f), new Vector3(-15f, 0f, 0f), grip);
                Part(PrimitiveType.Cube, new Vector3(0.18f, -0.3f, 0.36f), new Vector3(0.08f, 0.07f, 0.09f), Vector3.zero, skin);
            }
            else if (Equipped.ToolType == ToolType.Pickaxe)
            {
                // A pipe handle with a hammered scrap head across the top.
                var handle = Part(PrimitiveType.Cylinder, new Vector3(0.24f, -0.12f, 0.48f), new Vector3(0.035f, 0.3f, 0.035f), new Vector3(55f, 0f, -10f), metal);
                // At the top of the handle, crossways (placed in world space so the handle's thin scale doesn't squash it).
                var head = Part(PrimitiveType.Cube, Vector3.zero, new Vector3(0.05f, 0.06f, 0.42f), Vector3.zero, rust);
                head.SetPositionAndRotation(handle.TransformPoint(new Vector3(0f, 1f, 0f)), handle.rotation);
                Part(PrimitiveType.Cube, new Vector3(0.24f, -0.3f, 0.36f), new Vector3(0.08f, 0.07f, 0.09f), Vector3.zero, skin);
            }
            else if (Equipped.ToolType == ToolType.Shovel)
            {
                // A pipe handle with a flat scrap blade on the end.
                var handle = Part(PrimitiveType.Cylinder, new Vector3(0.24f, -0.12f, 0.48f), new Vector3(0.035f, 0.3f, 0.035f), new Vector3(55f, 0f, -10f), metal);
                var blade = Part(PrimitiveType.Cube, Vector3.zero, new Vector3(0.2f, 0.22f, 0.015f), Vector3.zero, rust);
                blade.SetPositionAndRotation(handle.TransformPoint(new Vector3(0f, 1.25f, 0f)), handle.rotation);
                Part(PrimitiveType.Cube, new Vector3(0.24f, -0.3f, 0.36f), new Vector3(0.08f, 0.07f, 0.09f), Vector3.zero, skin);
            }
            else
            {
                // Held low on the right by the handle end: a wrapped grip in the hand, the shaft running up and
                // away, and on a club a heavy block head at the far end (like its icon).
                bool club = Equipped.DisplayName.Contains("Club");
                bool heavy = Equipped.DisplayName.Contains("Heavy");
                var hand = Part(PrimitiveType.Cube, new Vector3(0.24f, -0.3f, 0.36f), new Vector3(0.08f, 0.07f, 0.09f), Vector3.zero, skin);
                var shaft = Part(PrimitiveType.Cylinder, Vector3.zero, new Vector3(0.03f, club ? 0.3f : 0.2f, 0.03f), new Vector3(55f, 0f, -10f), heavy ? metal : rust);
                // Slid along itself until the hand is near the bottom end (placed in world space, like the tool heads).
                var halfLength = shaft.TransformPoint(Vector3.up) - shaft.position;
                shaft.position = hand.position + halfLength * 0.8f;
                for (int i = 0; i < 3; i++)
                {
                    var wrap = Part(PrimitiveType.Cylinder, Vector3.zero, new Vector3(0.036f, 0.012f, 0.036f), Vector3.zero, grip);
                    wrap.SetPositionAndRotation(shaft.TransformPoint(new Vector3(0f, -0.95f + i * 0.13f, 0f)), shaft.rotation);
                }
                if (club)
                {
                    float size = heavy ? 0.1f : 0.08f;
                    var head = Part(PrimitiveType.Cube, Vector3.zero, new Vector3(size, size * 1.3f, size), Vector3.zero, metal);
                    head.SetPositionAndRotation(shaft.TransformPoint(new Vector3(0f, 0.92f, 0f)), shaft.rotation);
                }
            }
        }

        private void AnimateViewmodel()
        {
            if (viewmodel == null) return;
            if (HeldUsable != null)
            {
                // Just a gentle sway in the hand.
                viewmodel.localPosition = new Vector3(0f, Mathf.Sin(Time.time * 1.6f) * 0.004f, 0f);
                return;
            }
            float t = Time.time - swingTime;
            if (Equipped == null)
            {
                // Jab with alternate fists.
                float punch = t < 0.25f ? Mathf.Sin(t / 0.25f * Mathf.PI) : 0f;
                foreach (Transform fist in viewmodel)
                {
                    bool left = fist.name == "FistL";
                    float x = left ? -0.22f : 0.22f;
                    float p = left == swingLeft ? punch : 0f;
                    fist.localPosition = new Vector3(Mathf.Lerp(x, x * 0.3f, p), -0.22f + 0.1f * p, 0.42f + 0.25f * p);
                }
                return;
            }
            if (Equipped.WeaponType == WeaponType.Firearm)
            {
                // Kick up and back, then settle; dip out of view while reloading.
                float kick = t < 0.15f ? 1f - t / 0.15f : 0f;
                float dip = IsReloading ? 1f : 0f;
                viewmodel.localPosition = new Vector3(0f, -0.15f * dip, -0.04f * kick);
                viewmodel.localRotation = Quaternion.Euler(-12f * kick + 35f * dip, 0f, 0f);
                return;
            }
            // Overhead-to-low swing.
            float swing = t < 0.35f ? Mathf.Sin(t / 0.35f * Mathf.PI) : 0f;
            viewmodel.localRotation = Quaternion.Euler(50f * swing, -25f * swing, 20f * swing);
            viewmodel.localPosition = new Vector3(-0.08f * swing, 0f, 0.06f * swing);
        }

        private void MuzzleFlash(Vector3 end)
        {
            var muzzle = viewmodel != null ? viewmodel.Find("Muzzle") : null;
            var from = muzzle != null ? muzzle.position : viewPoint.position;

            var flash = new GameObject("MuzzleFlash");
            flash.transform.position = from;
            var light = flash.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.75f, 0.4f);
            light.intensity = 6f;
            light.range = 8f;
            light.shadows = LightShadows.None;

            var tracer = flash.AddComponent<LineRenderer>();
            tracer.positionCount = 2;
            tracer.SetPosition(0, from);
            tracer.SetPosition(1, end);
            tracer.startWidth = 0.02f;
            tracer.endWidth = 0.005f;
            tracer.material = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { color = new Color(1f, 0.85f, 0.55f) };
            tracer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            Destroy(tracer.material, 0.06f);
            Destroy(flash, 0.06f);
        }
    }
}
