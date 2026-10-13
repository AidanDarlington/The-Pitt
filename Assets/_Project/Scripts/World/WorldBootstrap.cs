using System.Collections;
using System.Diagnostics;
using ThePitt.Core;
using ThePitt.Player;
using ThePitt.World.Generation;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace ThePitt.World
{
    /// <summary>
    /// The World scene's loader. Shows "Loading" and a bar, builds the city a piece at a time from the seed the
    /// menu asked for (<see cref="WorldRequest"/>), then loads the game scene (player, HUD, the game's systems)
    /// on top, so everything there starts with the city already standing. The bar follows the real work: the
    /// build's own progress, then the game scene's loading. A new character is put inside their own property,
    /// facing its door, and given a first save.
    /// Opened with nothing requested it goes to the menu instead, unless <see cref="devQuickStart"/> is set, in
    /// which case it builds the greybox's own seed with no character (nothing saves).
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public class WorldBootstrap : MonoBehaviour
    {
        [SerializeField] private PittGreybox greybox;
        [Tooltip("Playing this scene directly: build the greybox's seed and play without a character, instead of opening the menu.")]
        [SerializeField] private bool devQuickStart;
        [Tooltip("Shown while loading; switched off once the game scene's own camera takes over.")]
        [SerializeField] private Camera loadingCamera;

        /// <summary>Share of the bar for building the city; the rest is loading the game scene and starting it up.</summary>
        private const float BuildShare = 0.85f;

        private bool redirected;
        private Image bar;
        private GameObject screen;

        // Kept on the component too: a script reload mid-play wipes WorldRequest's statics, and without the
        // character nothing could be saved for the rest of the session. Serialized fields survive the reload.
        [SerializeField, HideInInspector] private string character;
        [SerializeField, HideInInspector] private int seed;
        [SerializeField, HideInInspector] private int generatorVersion;
        [SerializeField, HideInInspector] private bool built;

        private void OnEnable()
        {
            if (built && !WorldRequest.Active) WorldRequest.Set(string.IsNullOrEmpty(character) ? null : character, seed, newGame: false, generatorVersion);
        }

        private void Awake()
        {
            if (greybox == null) greybox = FindAnyObjectByType<PittGreybox>();
            if (!WorldRequest.Active)
            {
                if (!devQuickStart)
                {
                    redirected = true;
                    SceneManager.LoadScene(WorldRequest.MenuScene);
                    return;
                }
                WorldRequest.Set(null, greybox.seed, newGame: true);
            }
            character = WorldRequest.Character;
            seed = WorldRequest.Seed;
            generatorVersion = WorldRequest.GeneratorVersion;
            BuildScreen();
        }

        private IEnumerator Start()
        {
            if (redirected) yield break;
            // Let the loading screen draw before the first piece of work.
            yield return null;

            // The generator stocks businesses by item name, before the save system has set up the registry.
            if (SaveRegistry.Instance == null) SaveRegistry.Instance = greybox.itemRegistry;
            // A frame's worth of work at a time, then let the bar redraw.
            var frame = Stopwatch.StartNew();
            foreach (float progress in PittGreyboxGenerator.BuildSteps(greybox, WorldRequest.Seed, WorldRequest.GeneratorVersion))
            {
                SetBar(progress * BuildShare);
                if (frame.ElapsedMilliseconds < 30) continue;
                yield return null;
                frame.Restart();
            }
            built = true;
            // No baked lighting: only the lamps near the camera are live.
            PittGreyboxGenerator.LastBuilt.gameObject.AddComponent<LightCuller>();

            // The game scene, on top. Its objects wake up with the city there; the player is placed before they start.
            SceneManager.sceneLoaded += OnGameSceneLoaded;
            var load = SceneManager.LoadSceneAsync(WorldRequest.GameScene, LoadSceneMode.Additive);
            while (!load.isDone)
            {
                SetBar(BuildShare + load.progress * (0.95f - BuildShare) / 0.9f);
                yield return null;
            }
            SceneManager.SetActiveScene(SceneManager.GetSceneByName(WorldRequest.GameScene));
            if (loadingCamera != null) loadingCamera.gameObject.SetActive(false);

            // Every system's Start, and a save being put back (two frames after the save system starts).
            for (int i = 0; i < 3; i++)
            {
                SetBar(0.95f + 0.05f * (i + 1) / 3f);
                yield return null;
            }
            if (WorldRequest.IsNewGame && SaveSystem.Instance != null) SaveSystem.Instance.Save(SaveSystem.AutoSlot, quiet: true);
            WorldRequest.Started();
            Destroy(screen);
        }

        /// <summary>
        /// Straight after the game scene's objects wake up, before any of them start: the player and their
        /// respawn point go to the city's start, or, for a new character, inside their own property.
        /// </summary>
        private void OnGameSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name != WorldRequest.GameScene) return;
            SceneManager.sceneLoaded -= OnGameSceneLoaded;
            var facing = Quaternion.Euler(0f, greybox.startYaw, 0f);
            foreach (var root in scene.GetRootGameObjects())
                if (root.name == "PlayerSpawn" || root.CompareTag("Player"))
                    root.transform.SetPositionAndRotation(greybox.startPosition, facing);
            var controller = FindAnyObjectByType<FirstPersonController>();
            if (controller != null) controller.SetLook(greybox.startYaw, 0f);
            if (WorldRequest.IsNewGame) PlaceAtHome(controller);
        }

        /// <summary>Inside the player's own property, a couple of steps in from the door, turned round to face it.</summary>
        private static void PlaceAtHome(FirstPersonController controller)
        {
            Properties.Property home = null;
            foreach (var p in Properties.Property.All)
                if (p.Owned && (home == null || p.Id == "Home")) home = p;
            if (home == null || controller == null) return;

            float yaw = home.InsideYaw;
            var inward = new Vector3(Mathf.Sin(yaw * Mathf.Deg2Rad), 0f, Mathf.Cos(yaw * Mathf.Deg2Rad));
            var cc = controller.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;
            controller.transform.position = home.InsidePoint + inward * 1.5f;
            controller.SetLook(yaw + 180f, 0f);
            if (cc != null) cc.enabled = true;
            Properties.Property.LastVisited = home;
        }

        // ---------------------------------------------------------------- loading screen

        private void SetBar(float value)
        {
            if (bar != null) bar.fillAmount = Mathf.Clamp01(value);
        }

        /// <summary>"Loading" over a bar that fills as the work gets done.</summary>
        private void BuildScreen()
        {
            screen = new GameObject("LoadingScreen", typeof(Canvas), typeof(CanvasScaler));
            screen.transform.SetParent(transform, false);
            var canvas = screen.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 200;
            var scaler = screen.GetComponent<CanvasScaler>();
            ThePitt.UI.UIScale.Apply(scaler);

            var backdrop = Rect("Backdrop", screen.transform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            backdrop.gameObject.AddComponent<Image>().color = new Color(0.015f, 0.012f, 0.012f, 1f);

            var label = Rect("Label", screen.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 30f), new Vector2(600f, 60f));
            var text = label.gameObject.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = 34;
            text.fontStyle = FontStyle.Bold;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = new Color(0.94f, 0.91f, 0.85f);
            text.text = "LOADING";

            var track = Rect("Bar", screen.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -22f), new Vector2(560f, 10f));
            track.gameObject.AddComponent<Image>().color = new Color(0.16f, 0.15f, 0.15f, 1f);
            var fill = Rect("Fill", track, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            bar = fill.gameObject.AddComponent<Image>();
            bar.color = new Color(1f, 0.66f, 0.3f);
            // A filled image needs a sprite to fill; a plain white one.
            bar.sprite = Sprite.Create(Texture2D.whiteTexture, new UnityEngine.Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f));
            bar.type = Image.Type.Filled;
            bar.fillMethod = Image.FillMethod.Horizontal;
            bar.fillOrigin = 0;
            bar.fillAmount = 0f;
        }

        private static RectTransform Rect(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax, Vector2 position, Vector2 size)
        {
            var rt = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.anchoredPosition = position;
            rt.sizeDelta = size;
            return rt;
        }
    }
}
