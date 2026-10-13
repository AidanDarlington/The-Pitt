using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using ThePitt.Economy;
using ThePitt.Factions;
using ThePitt.Interaction;
using ThePitt.Items;
using ThePitt.Player;
using ThePitt.World;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace ThePitt.Core
{
    /// <summary>
    /// Saves and loads the game, per character: each has a folder of saves named after them, and each save
    /// carries the seed its world was built from. F5 quicksaves, F9 loads the character's most recent save, and
    /// the game autosaves every midnight and on quit. Loading rebuilds the character's world (reloading the game
    /// scene, see <see cref="WorldRequest"/>), then puts the saved state back once everything has started up.
    /// Files are JSON in the persistent data folder. Nothing saves when the game scene was played without a
    /// character (straight from the editor).
    /// </summary>
    [DefaultExecutionOrder(-90)]
    public class SaveSystem : MonoBehaviour
    {
        public const string QuickSlot = "quicksave";
        public const string AutoSlot = "autosave";

        public static SaveSystem Instance { get; private set; }

        [SerializeField] private SaveRegistry registry;
        [SerializeField] private bool autosaveAtMidnight = true;
        [SerializeField] private bool autosaveOnQuit = true;

        [Header("Scene references (found automatically if empty)")]
        [SerializeField] private Transform player;

        /// <summary>A save waiting to be applied once the reloaded scene has started.</summary>
        private static SaveData pending;

        /// <summary>No saving or loading of any kind (autosaves, F5, F9) while set.</summary>
        public static bool Suspended { get; set; }

        // Statics survive between play sessions when domain reload is off in the editor.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            pending = null;
            Suspended = false;
        }

        private Text toast;
        private float toastUntil;

        /// <summary>
        /// Where saves live. A built game uses "saves", kept through every update; playing in the editor uses its own
        /// "editor-saves", so testing never touches a build's saves (or the other way round).
        /// </summary>
        public static string Root => Path.Combine(Application.persistentDataPath, Application.isEditor ? "editor-saves" : "saves");
        public static string FolderFor(string character) => Path.Combine(Root, character);
        public static string PathFor(string character, string slot) => Path.Combine(FolderFor(character), slot + ".json");
        /// <summary>Whether this game has a character to save for.</summary>
        public static bool CanSave => !string.IsNullOrEmpty(WorldRequest.Character);

        public event Action Saved;
        public event Action Loaded;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;
            SaveRegistry.Instance = registry;
            if (player == null)
            {
                var controller = FindFirstObjectByType<FirstPersonController>();
                if (controller != null) player = controller.transform;
            }
        }

        private IEnumerator Start()
        {
            if (TimeManager.Instance != null) TimeManager.Instance.DayChanged += OnDayChanged;
            BuildToast();
            if (pending == null) yield break;

            // Let every system finish its own Start (and the NPCs populate) before overwriting their state.
            yield return null;
            yield return null;
            var data = pending;
            pending = null;
            Apply(data);
            ShowToast($"Loaded · Day {data.day}");
            Loaded?.Invoke();
        }

        private void OnDestroy()
        {
            if (TimeManager.Instance != null) TimeManager.Instance.DayChanged -= OnDayChanged;
            if (Instance == this) Instance = null;
        }

        private void OnApplicationQuit()
        {
            if (autosaveOnQuit && pending == null && !Suspended) Save(AutoSlot, quiet: true);
        }

        private void OnDayChanged(int _)
        {
            if (autosaveAtMidnight && !Suspended) Save(AutoSlot);
        }

        private void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && !Suspended)
            {
                if (keyboard.f5Key.wasPressedThisFrame) Save(QuickSlot);
                else if (keyboard.f9Key.wasPressedThisFrame) LoadNewest();
            }
            if (toast != null && toast.enabled && Time.unscaledTime > toastUntil) toast.enabled = false;
        }

        // ---------------------------------------------------------------- saving

        public void Save(string slot, bool quiet = false)
        {
            if (!CanSave) return;
            // No clock means the scene is gone or going (quitting mid-reload): there's nothing real to save, and
            // writing one would leave a blank Day 0, 00:00 save over the last good one.
            if (TimeManager.Instance == null)
            {
                Debug.LogWarning($"Save to {slot} skipped: the game clock isn't running.");
                return;
            }
            try
            {
                var data = Capture();
                Directory.CreateDirectory(FolderFor(WorldRequest.Character));
                // Write then swap, so a crash mid-write never leaves a broken save.
                string path = PathFor(WorldRequest.Character, slot), temp = path + ".tmp";
                File.WriteAllText(temp, JsonUtility.ToJson(data, true));
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
                if (!quiet) ShowToast(slot == AutoSlot ? "Autosaved" : "Game saved");
                Debug.Log($"Saved {slot} to {path}");
                Saved?.Invoke();
            }
            catch (Exception e)
            {
                Debug.LogError($"Save failed: {e}");
                if (!quiet) ShowToast("Save failed");
            }
        }

        // ---------------------------------------------------------------- loading

        /// <summary>Loads this character's most recent save, if there is one.</summary>
        public void LoadNewest()
        {
            SaveInfo? newest = null;
            foreach (var save in AllSaves())
                if (save.character == WorldRequest.Character && save.Loadable) { newest = save; break; }
            if (newest == null)
            {
                ShowToast("No save found");
                return;
            }
            StartFromSave(newest.Value.data);
        }

        /// <summary>Builds a save's world and puts the save back in it (loads the game scene).</summary>
        public static void StartFromSave(SaveData data)
        {
            pending = data;
            WorldRequest.Set(data.character, data.seed, newGame: false);
            Time.timeScale = 1f;
            Suspended = false;
            SceneManager.LoadScene(WorldRequest.WorldScene);
        }

        /// <summary>A new character in a new world built from the seed (loads the game scene).</summary>
        public static void StartNewGame(string character, int seed)
        {
            pending = null;
            WorldRequest.Set(character, seed, newGame: true);
            Time.timeScale = 1f;
            Suspended = false;
            SceneManager.LoadScene(WorldRequest.WorldScene);
        }

        /// <summary>One save file: whose, which slot, what's in it, and when it was written.</summary>
        public readonly struct SaveInfo
        {
            public readonly string character;
            public readonly string slot;
            public readonly SaveData data;
            public readonly DateTime written;

            public SaveInfo(string character, string slot, SaveData data, DateTime written)
            {
                this.character = character;
                this.slot = slot;
                this.data = data;
                this.written = written;
            }

            /// <summary>Made by this version of the world generator, so its world can be rebuilt.</summary>
            public bool Loadable => data.generatorVersion == PittGreybox.GeneratorVersion && data.seed != 0;
        }

        /// <summary>Every character's saves, newest first. Files outside a character folder (older saves) are ignored.</summary>
        public static List<SaveInfo> AllSaves()
        {
            var list = new List<SaveInfo>();
            if (!Directory.Exists(Root)) return list;
            foreach (var folder in Directory.GetDirectories(Root))
            {
                string character = Path.GetFileName(folder);
                foreach (var slot in new[] { QuickSlot, AutoSlot })
                {
                    string path = Path.Combine(folder, slot + ".json");
                    var data = Read(path);
                    if (data == null || string.IsNullOrEmpty(data.character)) continue;
                    list.Add(new SaveInfo(character, slot, data, File.GetLastWriteTime(path)));
                }
            }
            list.Sort((a, b) => b.written.CompareTo(a.written));
            return list;
        }

        /// <summary>
        /// Deletes one of a character's saves. With the last one gone the character's folder goes too, so the
        /// name is free again.
        /// </summary>
        public static void DeleteSave(string character, string slot)
        {
            try
            {
                string path = PathFor(character, slot);
                if (File.Exists(path)) File.Delete(path);
                string folder = FolderFor(character);
                if (Directory.Exists(folder) && Directory.GetFiles(folder, "*.json").Length == 0)
                    Directory.Delete(folder, recursive: true);
                Debug.Log($"Deleted {character}'s {slot}.");
            }
            catch (Exception e)
            {
                Debug.LogError($"Couldn't delete {character}'s {slot}: {e.Message}");
            }
        }

        /// <summary>Whether a character of this name already has a save folder (names are case-insensitive on disk).</summary>
        public static bool CharacterExists(string character)
        {
            if (!Directory.Exists(Root)) return false;
            foreach (var folder in Directory.GetDirectories(Root))
                if (string.Equals(Path.GetFileName(folder), character, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static SaveData Read(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                var data = JsonUtility.FromJson<SaveData>(File.ReadAllText(path));
                if (data == null || data.version > SaveData.CurrentVersion) return null;
                return data;
            }
            catch (Exception e)
            {
                Debug.LogError($"Couldn't read save {path}: {e.Message}");
                return null;
            }
        }

        // ---------------------------------------------------------------- capture

        private SaveData Capture()
        {
            var data = new SaveData
            {
                savedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                character = WorldRequest.Character,
                seed = WorldRequest.Seed,
                generatorVersion = PittGreybox.GeneratorVersion,
            };

            var time = TimeManager.Instance;
            if (time != null)
            {
                data.day = Mathf.Max(1, time.Day);
                data.timeOfDay = time.TimeOfDay;
            }

            if (player != null)
            {
                var p = data.player;
                p.position = player.position;
                // Only set when saved inside a property's room.
                var lastProperty = Properties.Property.At(player.position);
                data.lastProperty = lastProperty != null ? lastProperty.Id : null;
                p.yaw = player.eulerAngles.y;
                if (player.TryGetComponent<FirstPersonController>(out var fpc)) p.pitch = fpc.Pitch;
                if (player.TryGetComponent<PlayerVitals>(out var vitals))
                {
                    p.health = vitals.Health;
                    p.hunger = vitals.Hunger;
                    p.thirst = vitals.Thirst;
                }
                if (player.TryGetComponent<PlayerWallet>(out var wallet)) p.money = wallet.Money;
                if (player.TryGetComponent<Inventory>(out var inventory))
                    for (int i = 0; i < inventory.SlotCount; i++)
                    {
                        var s = inventory.GetSlot(i);
                        p.inventory.Add(s.IsEmpty ? new ItemStackSave() : new ItemStackSave
                        {
                            item = SaveRegistry.IdOf(s.item),
                            quantity = s.quantity,
                            quality = (int)s.quality,
                        });
                    }
                if (player.TryGetComponent<PlayerEffects>(out var effects))
                    foreach (var e in effects.Active)
                        p.effects.Add(new EffectSave { item = SaveRegistry.IdOf(e.source), minutesLeft = e.minutesLeft });
                if (player.TryGetComponent<PlayerCombat>(out var combat))
                {
                    p.equipped = SaveRegistry.IdOf(combat.Equipped);
                    p.equippedQuality = (int)combat.EquippedQuality;
                    foreach (var pair in combat.Loaded)
                        if (pair.Value > 0) p.loaded.Add(new ItemStackSave { item = SaveRegistry.IdOf(pair.Key), quantity = pair.Value });
                    p.handsDown = combat.HandsDown;
                }
                if (player.TryGetComponent<Hotbar>(out var hotbar)) p.hotbarSlot = hotbar.Selected;
            }

            if (DebtLedger.Instance != null) data.debt = DebtLedger.Instance.Capture();
            if (FactionManager.Instance != null) data.factions = FactionManager.Instance.Capture();

            foreach (var s in FindObjectsByType<PlacedStructure>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (s.Item == null) continue;
                var save = new StructureSave
                {
                    item = SaveRegistry.IdOf(s.Item),
                    position = s.transform.position,
                    rotation = s.transform.rotation,
                };
                var home = Properties.Property.At(s.transform.position + Vector3.up * 0.2f);
                if (home != null)
                {
                    save.property = home.Id;
                    save.local = s.transform.position - home.Centre;
                }
                foreach (var c in s.GetComponentsInChildren<ISaveState>(true))
                    save.components.Add(new ComponentSave { type = c.GetType().Name, json = c.CaptureState() });
                data.structures.Add(save);
            }

            var pit = FindFirstObjectByType<ScrapPitSpawner>();
            if (pit != null) data.scrapPit = pit.Capture();
            var corpses = FindFirstObjectByType<CorpsePileSpawner>();
            if (corpses != null) data.corpsePiles = corpses.Capture();
            if (Properties.WatcherRaids.Instance != null) data.raids = Properties.WatcherRaids.Instance.Capture();

            data.learnedRecipes = new System.Collections.Generic.List<string>();
            foreach (var recipe in Production.RecipeBook.Learned) data.learnedRecipes.Add(SaveRegistry.IdOf(recipe));
            data.discoveredItems = new System.Collections.Generic.List<string>();
            foreach (var item in Production.RecipeUnlocks.Discovered) data.discoveredItems.Add(SaveRegistry.IdOf(item));
            data.knownStations = new System.Collections.Generic.List<int>();
            foreach (var station in Production.RecipeUnlocks.Stations) data.knownStations.Add((int)station);

            data.ownedProperties = new System.Collections.Generic.List<string>();
            data.propertyUpgrades = new System.Collections.Generic.List<PropertySave>();
            foreach (var property in Properties.Property.All)
            {
                if (property.Owned) data.ownedProperties.Add(property.Id);
                data.propertyUpgrades.Add(new PropertySave { id = property.Id, raises = property.Raises, expansions = property.Expansions });
            }
            data.skills = Progression.Skills.Capture();
            data.businesses = new System.Collections.Generic.List<BusinessSave>();
            foreach (var b in Production.Business.All)
                if (b.Property != null) data.businesses.Add(new BusinessSave { property = b.Property.Id, json = b.CaptureState() });
            return data;
        }

        // ---------------------------------------------------------------- apply

        private void Apply(SaveData data)
        {
            // The clock first, so anything that reads the time sees the saved one.
            if (TimeManager.Instance != null) TimeManager.Instance.SetTime(data.timeOfDay, Mathf.Max(1, data.day));

            // Saves from before gang recipes existed simply have none learned.
            var recipes = new System.Collections.Generic.List<Production.RecipeDefinition>();
            if (data.learnedRecipes != null)
                foreach (var id in data.learnedRecipes) recipes.Add(SaveRegistry.Lookup<Production.RecipeDefinition>(id));
            Production.RecipeBook.Restore(recipes);

            // Saves from before recipe unlocks fall back to whatever the player is carrying (added below).
            var items = new System.Collections.Generic.List<ItemDefinition>();
            if (data.discoveredItems != null)
                foreach (var id in data.discoveredItems) items.Add(SaveRegistry.Lookup<ItemDefinition>(id));
            var stations = new System.Collections.Generic.List<Production.CraftingStation>();
            if (data.knownStations != null)
                foreach (int st in data.knownStations) stations.Add((Production.CraftingStation)st);
            Production.RecipeUnlocks.Restore(items, stations);
            Progression.Skills.Restore(data.skills);

            // Properties before structures: what's owned decides where things may stand.
            if (data.ownedProperties != null)
                foreach (var property in Properties.Property.All)
                    property.SetOwned(data.ownedProperties.Contains(property.Id));
            if (data.propertyUpgrades != null)
                foreach (var up in data.propertyUpgrades)
                    foreach (var property in Properties.Property.All)
                        if (property.Id == up.id) property.RestoreUpgrades(up.raises, up.expansions);
            if (data.businesses != null)
                foreach (var saved in data.businesses)
                    foreach (var b in Production.Business.All)
                        if (b.Property != null && b.Property.Id == saved.property) b.RestoreState(saved.json);

            if (player != null)
            {
                var p = data.player;
                var cc = player.GetComponent<CharacterController>();
                if (cc != null) cc.enabled = false;
                // Exactly where they stood and looked when they saved, in a property's room or out in the city.
                // A save rebuilds its own world from its seed, so the rooms are where they were.
                Properties.Property inside = null;
                if (!string.IsNullOrEmpty(data.lastProperty))
                    foreach (var property in Properties.Property.All)
                        if (property.Id == data.lastProperty) inside = property;
                Properties.Property.LastVisited = inside;
                player.position = p.position;
                if (player.TryGetComponent<FirstPersonController>(out var fpc)) fpc.SetLook(p.yaw, p.pitch);
                else player.rotation = Quaternion.Euler(0f, p.yaw, 0f);
                if (cc != null) cc.enabled = true;

                if (player.TryGetComponent<PlayerVitals>(out var vitals)) vitals.Set(p.health, p.hunger, p.thirst);
                if (player.TryGetComponent<PlayerWallet>(out var wallet)) wallet.Set(p.money);
                if (player.TryGetComponent<Inventory>(out var inventory))
                {
                    var stacks = new System.Collections.Generic.List<ItemStack>();
                    foreach (var s in p.inventory)
                    {
                        var item = SaveRegistry.Lookup<ItemDefinition>(s.item);
                        stacks.Add(item != null && s.quantity > 0 ? new ItemStack(item, s.quantity, (ItemQuality)s.quality) : ItemStack.Empty);
                    }
                    inventory.SetAll(stacks);
                    if (player.TryGetComponent<Production.ItemDiscovery>(out var discovery)) discovery.DiscoverCarried();
                }
                if (player.TryGetComponent<PlayerEffects>(out var effects))
                {
                    var list = new System.Collections.Generic.List<(ItemDefinition, int)>();
                    foreach (var e in p.effects) list.Add((SaveRegistry.Lookup<ItemDefinition>(e.item), e.minutesLeft));
                    effects.Restore(list);
                }
                if (player.TryGetComponent<PlayerCombat>(out var combat))
                {
                    var rounds = new System.Collections.Generic.List<(ItemDefinition, int)>();
                    foreach (var l in p.loaded) rounds.Add((SaveRegistry.Lookup<ItemDefinition>(l.item), l.quantity));
                    combat.Restore(SaveRegistry.Lookup<ItemDefinition>(p.equipped), (ItemQuality)p.equippedQuality, rounds);
                }
                if (player.TryGetComponent<Hotbar>(out var hotbar)) hotbar.Restore(p.hotbarSlot, p.handsDown);
            }

            if (DebtLedger.Instance != null && data.debt != null) DebtLedger.Instance.Restore(data.debt);
            if (FactionManager.Instance != null && data.factions != null) FactionManager.Instance.Restore(data.factions);

            RestoreStructures(data);
            // Saves from before station unlocks: whatever stations are standing count as set up.
            foreach (var bench in FindObjectsByType<Production.CraftingBench>(FindObjectsSortMode.None))
                Production.RecipeUnlocks.AddStation(bench.Station, announce: false);

            var pit = FindFirstObjectByType<ScrapPitSpawner>();
            if (pit != null && data.scrapPit != null) pit.Restore(data.scrapPit);
            var corpses = FindFirstObjectByType<CorpsePileSpawner>();
            if (corpses != null) corpses.Restore(data.corpsePiles);
            if (Properties.WatcherRaids.Instance != null) Properties.WatcherRaids.Instance.Restore(data.raids ?? new WatcherRaidsSave());
        }

        private void RestoreStructures(SaveData data)
        {
            foreach (var existing in FindObjectsByType<PlacedStructure>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                Destroy(existing.gameObject);

            var placement = player != null ? player.GetComponent<PlacementController>() : null;
            var parent = placement != null ? placement.PlacedParent : null;
            foreach (var s in data.structures)
            {
                var item = SaveRegistry.Lookup<ItemDefinition>(s.item);
                if (item == null || item.PlaceablePrefab == null)
                {
                    Debug.LogWarning($"Save: unknown structure '{s.item}' skipped.");
                    continue;
                }
                var go = Instantiate(item.PlaceablePrefab, StructurePosition(s, data), s.rotation, parent);
                go.name = item.PlaceablePrefab.name;
                var marker = go.GetComponent<PlacedStructure>();
                if (marker == null) marker = go.AddComponent<PlacedStructure>();
                marker.Setup(item);

                var parts = go.GetComponentsInChildren<ISaveState>(true);
                for (int i = 0; i < parts.Length && i < s.components.Count; i++)
                    if (parts[i].GetType().Name == s.components[i].type) parts[i].RestoreState(s.components[i].json);
            }
        }

        /// <summary>
        /// Where a saved structure goes: in its property's room, wherever that room is now. Saves from before
        /// structures named their property only have a world position, and regenerating the city can hand that
        /// room to a different property; those go in the same spot of the player's last (or first) owned property.
        /// </summary>
        private static Vector3 StructurePosition(StructureSave s, SaveData data)
        {
            Properties.Property Find(string id)
            {
                foreach (var p in Properties.Property.All)
                    if (p.Id == id) return p;
                return null;
            }

            if (!string.IsNullOrEmpty(s.property))
            {
                var home = Find(s.property);
                return home != null ? home.Centre + s.local : s.position;
            }

            var owner = data.ownedProperties != null && data.ownedProperties.Contains(data.lastProperty) ? Find(data.lastProperty) : null;
            if (owner == null && data.ownedProperties != null)
                foreach (var id in data.ownedProperties)
                    if ((owner = Find(id)) != null) break;
            // The rooms all sit in one row, so the nearest room's middle is where this one's was.
            Properties.Property room = null;
            foreach (var p in Properties.Property.All)
                if (room == null || (p.Centre - s.position).sqrMagnitude < (room.Centre - s.position).sqrMagnitude) room = p;
            if (owner == null || room == null || (room.Centre - s.position).sqrMagnitude > 30f * 30f) return s.position;
            return owner.Centre + (s.position - room.Centre);
        }

        // ---------------------------------------------------------------- feedback

        private void BuildToast()
        {
            var canvasGo = new GameObject("SaveToast", typeof(Canvas), typeof(CanvasScaler));
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            ThePitt.UI.UIScale.Apply(scaler);

            var textGo = new GameObject("Text", typeof(RectTransform), typeof(Text));
            textGo.transform.SetParent(canvasGo.transform, false);
            var rt = (RectTransform)textGo.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.anchoredPosition = new Vector2(0f, 140f);
            rt.sizeDelta = new Vector2(600f, 40f);
            toast = textGo.GetComponent<Text>();
            toast.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            toast.fontSize = 22;
            toast.alignment = TextAnchor.MiddleCenter;
            toast.color = new Color(0.9f, 0.88f, 0.82f);
            toast.raycastTarget = false;
            toast.enabled = false;
        }

        private void ShowToast(string message)
        {
            if (toast == null) return;
            toast.text = message;
            toast.enabled = true;
            toastUntil = Time.unscaledTime + 2.5f;
        }
    }
}
