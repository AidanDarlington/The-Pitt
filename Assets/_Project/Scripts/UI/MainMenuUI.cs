using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using ThePitt.Core;
using ThePitt.Player;
using ThePitt.World;
using ThePitt.World.Generation;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace ThePitt.UI
{
    /// <summary>
    /// The title screen the game opens on (its own scene): Continue (the newest save, when there is one),
    /// New Game, Load Game (pick a character's save), Options and Quit.
    /// New Game lays out a world from a random seed and shows its map; Generate New World rolls another until the
    /// player likes one, then a Character Name (required, and the name of their save folder) and Start build that
    /// world in the game scene and put them in it. Builds itself.
    /// With <see cref="inGame"/> set it's the pause menu instead: Esc during play opens the same left-hand panel over
    /// the greyed-out, paused game, with Return to Game, Save Game, Load Game, Options, Main Menu and Quit (the last
    /// two ask to save first if the player hasn't since opening the menu). Esc backs out of a dialog or page, then
    /// returns to the game like Return to Game.
    /// </summary>
    public class MainMenuUI : MonoBehaviour
    {
        [Tooltip("The greybox settings prefab; a copy lays out the worlds shown on the New Game screen.")]
        [SerializeField] private PittGreybox worldPrefab;
        [Tooltip("The pause menu in the game scene rather than the title screen.")]
        [SerializeField] private bool inGame;

        public const string VolumeKey = "Options.Volume";
        public const string FullscreenKey = "Options.Fullscreen";
        private const int MaxNameLength = 24;

        private static readonly Color Accent = new(1f, 0.66f, 0.3f);
        private static readonly Color Ink = new(0.94f, 0.91f, 0.85f);
        private static readonly Color Muted = new(0.6f, 0.58f, 0.55f);
        private static readonly Color Warning = new(1f, 0.45f, 0.35f);
        private static readonly Color ButtonColour = new(0.09f, 0.09f, 0.1f, 0.92f);
        private static readonly Color PanelColour = new(0.03f, 0.03f, 0.035f, 0.88f);

        private Font font;
        private RectTransform root, buttonColumn, page, dialog;

        // Pause menu.
        private FirstPersonController controller;
        private PlacementController placement;
        private BuildModeController buildMode;
        private bool freeLastFrame, savedThisVisit;
        public bool IsPaused => inGame && root != null && root.gameObject.activeSelf;

        // New Game screen.
        private PittGreybox preview;
        private int seed;
        private RawImage map;
        private RectTransform mapLabels;
        private Text seedText, nameProblem;
        private InputField nameField;
        private Button startButton, rollButton;

        private void Awake()
        {
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (inGame)
            {
                controller = FindAnyObjectByType<FirstPersonController>();
                placement = FindAnyObjectByType<PlacementController>();
                buildMode = FindAnyObjectByType<BuildModeController>();
                return;
            }
            AudioListener.volume = PlayerPrefs.GetFloat(VolumeKey, 1f);
            Screen.fullScreen = PlayerPrefs.GetInt(FullscreenKey, Screen.fullScreen ? 1 : 0) == 1;
            Time.timeScale = 1f;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void Start()
        {
            Build();
            if (inGame) root.gameObject.SetActive(false);
            else ShowMain();
        }

        private void Update()
        {
            if (!inGame) return;
            bool escape = Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame;
            if (!escape) return;
            if (IsPaused) Back();
            // Only when Esc isn't already closing something else (inventory, map, a trade, placing a structure…):
            // those all had the player's input off, or were busy, the frame before.
            else if (freeLastFrame) Pause();
        }

        private void LateUpdate()
        {
            if (!inGame) return;
            freeLastFrame = !IsPaused && (controller == null || controller.InputEnabled) &&
                            (placement == null || (!placement.IsPlacing && !placement.IsMoving)) &&
                            (buildMode == null || !buildMode.IsActive);
        }

        // ---------------------------------------------------------------- pause menu

        private void Pause()
        {
            savedThisVisit = false;
            root.gameObject.SetActive(true);
            ShowMain();
            Time.timeScale = 0f;
            AudioListener.pause = true;
            if (controller != null) controller.InputEnabled = false;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void Resume()
        {
            CloseDialog();
            ClosePage();
            root.gameObject.SetActive(false);
            Unpause();
            if (controller != null) controller.InputEnabled = true;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        private static void Unpause()
        {
            Time.timeScale = 1f;
            AudioListener.pause = false;
        }

        /// <summary>Esc: shut a dialog, then a page, then the menu.</summary>
        private void Back()
        {
            if (dialog != null) CloseDialog();
            else if (page.gameObject.activeSelf) ClosePage();
            else Resume();
        }

        private void SaveNow()
        {
            if (SaveSystem.Instance == null || !SaveSystem.CanSave) return;
            SaveSystem.Instance.Save(SaveSystem.QuickSlot);
            savedThisVisit = true;
            ShowMain();
        }

        private void ReturnToMainMenu()
        {
            Unpause();
            SceneManager.LoadScene(WorldRequest.MenuScene);
        }

        /// <summary>
        /// Leaving the game (back to the menu, or quitting) asks to save first, unless they already have since
        /// opening the pause menu (or there's no character to save for).
        /// </summary>
        private void LeaveGame(string question, System.Action leave)
        {
            if (savedThisVisit || !SaveSystem.CanSave || SaveSystem.Instance == null)
            {
                leave();
                return;
            }
            Dialog(question, "You haven't saved since opening this menu.",
                ("SAVE", () =>
                {
                    SaveNow();
                    leave();
                }, false),
                ("DON'T SAVE", () =>
                {
                    // Not even the usual autosave on the way out.
                    SaveSystem.Suspended = true;
                    leave();
                }, true),
                ("CANCEL", CloseDialog, false));
        }

        // ---------------------------------------------------------------- pages

        private void ShowMain()
        {
            Clear(buttonColumn);
            ClosePage();
            if (inGame)
            {
                MenuButton(buttonColumn, "RETURN TO GAME", null, Resume);
                MenuButton(buttonColumn, "SAVE GAME", null, SaveNow, SaveSystem.CanSave && SaveSystem.Instance != null);
                bool anySaves = SaveSystem.AllSaves().Count > 0;
                MenuButton(buttonColumn, "LOAD GAME", anySaves ? null : "No saves", ShowLoad, anySaves);
                MenuButton(buttonColumn, "OPTIONS", null, ShowOptions);
                MenuButton(buttonColumn, "MAIN MENU", null, () => LeaveGame("SAVE BEFORE RETURNING TO THE MENU?", ReturnToMainMenu));
                MenuButton(buttonColumn, "QUIT GAME", null, () => LeaveGame("SAVE BEFORE QUITTING?", Quit));
                return;
            }
            var newest = NewestLoadable();
            if (newest != null)
            {
                var save = newest.Value;
                MenuButton(buttonColumn, "CONTINUE", $"{save.character} · Day {save.data.day}", () => Load(save.data));
            }
            MenuButton(buttonColumn, "NEW GAME", null, ShowNewGame);
            bool any = SaveSystem.AllSaves().Count > 0;
            MenuButton(buttonColumn, "LOAD GAME", any ? null : "No saves", ShowLoad, any);
            MenuButton(buttonColumn, "OPTIONS", null, ShowOptions);
            MenuButton(buttonColumn, "QUIT GAME", null, Quit);
        }

        private static SaveSystem.SaveInfo? NewestLoadable()
        {
            foreach (var save in SaveSystem.AllSaves())
                if (save.Loadable) return save;
            return null;
        }

        private void ShowLoad()
        {
            OpenPage("LOAD GAME", 620f);
            // Grouped by character, the character with the most recent save first.
            var order = new List<string>();
            var saves = SaveSystem.AllSaves();
            foreach (var s in saves)
                if (!order.Contains(s.character)) order.Add(s.character);
            foreach (var character in order)
            {
                var heading = NewText(page, character.ToUpperInvariant(), 22, Accent, FontStyle.Bold);
                heading.gameObject.AddComponent<LayoutElement>().preferredHeight = 36f;
                foreach (var save in saves)
                {
                    if (save.character != character) continue;
                    var data = save.data;
                    int hour = Mathf.FloorToInt(data.timeOfDay), minute = Mathf.FloorToInt((data.timeOfDay - hour) * 60f);
                    string detail = save.Loadable
                        ? $"Day {data.day} · {hour:00}:{minute:00} · saved {save.written:yyyy-MM-dd HH:mm}"
                        : "Made with an older world generator · can't be loaded";
                    var row = MenuButton(page, SlotName(save.slot).ToUpperInvariant(), detail, () => Load(data), save.Loadable);
                    var target = save;
                    DeleteButton(row, () => ConfirmDelete(target));
                }
            }
            MenuButton(page, "BACK", null, ClosePage);
        }

        /// <summary>An X at the right-hand end of a save's row.</summary>
        private void DeleteButton(Button row, System.Action onClick)
        {
            var rt = NewRect("Delete", row.transform);
            rt.anchorMin = new Vector2(1f, 0f);
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(1f, 0.5f);
            rt.sizeDelta = new Vector2(64f, 0f);
            var image = rt.gameObject.AddComponent<Image>();
            image.color = new Color(0.14f, 0.07f, 0.07f, 0.95f);
            var button = rt.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            var colours = button.colors;
            colours.highlightedColor = new Color(2.4f, 1.3f, 1.2f);
            colours.pressedColor = new Color(3f, 1.5f, 1.3f);
            button.colors = colours;
            button.onClick.AddListener(() => onClick());
            // Full strength even on a faded row: an old save that can't be loaded can still be deleted.
            rt.gameObject.AddComponent<CanvasGroup>().ignoreParentGroups = true;
            var x = NewText(rt, "X", 26, Warning, FontStyle.Bold);
            Fill(x.rectTransform);
            x.alignment = TextAnchor.MiddleCenter;
            // Keep the row's own label clear of it.
            foreach (Transform child in row.transform)
                if (child != rt && child.TryGetComponent<Text>(out var label)) label.rectTransform.offsetMax = new Vector2(-72f, label.rectTransform.offsetMax.y);
        }

        /// <summary>Asks before deleting: a box over everything with Delete and Cancel.</summary>
        private void ConfirmDelete(SaveSystem.SaveInfo save)
        {
            Dialog("DELETE SAVE?", $"{save.character} · {SlotName(save.slot)} · Day {save.data.day}\nThis can't be undone.",
                ("CANCEL", CloseDialog, false),
                ("DELETE", () =>
                {
                    SaveSystem.DeleteSave(save.character, save.slot);
                    CloseDialog();
                    // Continue and Load Game may have changed; back to the main list if nothing's left to load.
                    ShowMain();
                    if (SaveSystem.AllSaves().Count > 0) ShowLoad();
                }, true));
        }

        /// <summary>A box over everything (the rest dimmed) with a title, a message and a row of buttons.</summary>
        private void Dialog(string title, string message, params (string label, System.Action action, bool danger)[] choices)
        {
            CloseDialog();
            var cover = Fill(NewRect("Dialog", root));
            cover.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.7f);
            dialog = cover;
            var box = NewRect("Box", cover);
            box.anchorMin = box.anchorMax = box.pivot = new Vector2(0.5f, 0.5f);
            box.sizeDelta = new Vector2(Mathf.Max(620f, choices.Length * 250f), 260f);
            box.gameObject.AddComponent<Image>().color = new Color(0.06f, 0.06f, 0.07f, 1f);
            var layout = box.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(32, 32, 26, 28);
            layout.spacing = 14f;
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            var heading = NewText(box, title, 28, Warning, FontStyle.Bold);
            heading.gameObject.AddComponent<LayoutElement>().preferredHeight = 40f;
            var body = NewText(box, message, 19, Ink, FontStyle.Normal);
            body.gameObject.AddComponent<LayoutElement>().preferredHeight = 60f;

            var row = NewRect("Buttons", box);
            row.gameObject.AddComponent<LayoutElement>().preferredHeight = 60f;
            var buttons = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            buttons.spacing = 14f;
            buttons.childControlWidth = buttons.childControlHeight = true;
            buttons.childForceExpandWidth = buttons.childForceExpandHeight = true;
            foreach (var (label, action, danger) in choices)
            {
                var button = MenuButton(row, label, null, action);
                if (danger) button.GetComponent<Image>().color = new Color(0.3f, 0.08f, 0.07f, 0.95f);
            }
        }

        private void CloseDialog()
        {
            if (dialog != null) Destroy(dialog.gameObject);
            dialog = null;
        }

        private void ShowOptions()
        {
            OpenPage("OPTIONS", 620f);
            Slider(page, "VOLUME", 0f, 1f, PlayerPrefs.GetFloat(VolumeKey, 1f), v =>
            {
                PlayerPrefs.SetFloat(VolumeKey, v);
                AudioListener.volume = v;
            }, v => $"{Mathf.RoundToInt(v * 100f)}%");
            Slider(page, "MOUSE SENSITIVITY", 0.25f, 3f, PlayerPrefs.GetFloat(FirstPersonController.SensitivityKey, 1f),
                v =>
                {
                    PlayerPrefs.SetFloat(FirstPersonController.SensitivityKey, v);
                    if (controller != null) controller.SensitivityScale = v;
                }, v => $"{v:0.00}x");
            Slider(page, "UI SCALE", UIScale.Min, UIScale.Max, UIScale.Value, UIScale.Set,
                v => $"{Mathf.RoundToInt(v * 100f)}%");
            Toggle(page, "FULLSCREEN", Screen.fullScreen, on =>
            {
                PlayerPrefs.SetInt(FullscreenKey, on ? 1 : 0);
                Screen.fullScreen = on;
            });
            MenuButton(page, "BACK", null, () =>
            {
                PlayerPrefs.Save();
                ClosePage();
            });
        }

        private static string SlotName(string slot) => slot == SaveSystem.QuickSlot ? "Quicksave" : slot == SaveSystem.AutoSlot ? "Autosave" : slot;

        // ---------------------------------------------------------------- new game

        private void ShowNewGame()
        {
            OpenPage("NEW GAME", 1160f);
            var body = NewRect("Body", page);
            body.gameObject.AddComponent<LayoutElement>().preferredHeight = 700f;

            // The map on the left, everything else beside it.
            var frame = NewRect("Map", body);
            frame.anchorMin = frame.anchorMax = frame.pivot = new Vector2(0f, 1f);
            frame.sizeDelta = new Vector2(680f, 680f);
            frame.gameObject.AddComponent<Image>().color = new Color(0.01f, 0.01f, 0.012f, 1f);
            map = Fill(NewRect("Image", frame)).gameObject.AddComponent<RawImage>();
            map.raycastTarget = false;
            mapLabels = Fill(NewRect("Labels", frame));

            var side = NewRect("Controls", body);
            side.anchorMin = side.anchorMax = side.pivot = new Vector2(0f, 1f);
            side.anchoredPosition = new Vector2(710f, 0f);
            side.sizeDelta = new Vector2(394f, 0f);
            var layout = side.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 12f;
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            side.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            seedText = NewText(side, "", 18, Muted, FontStyle.Normal);
            seedText.gameObject.AddComponent<LayoutElement>().preferredHeight = 28f;
            rollButton = MenuButton(side, "GENERATE NEW WORLD", null, () => StartCoroutine(Roll()));

            var label = NewText(side, "CHARACTER NAME", 20, Ink, FontStyle.Bold);
            label.gameObject.AddComponent<LayoutElement>().preferredHeight = 44f;
            label.alignment = TextAnchor.LowerLeft;
            nameField = NameField(side);
            nameProblem = NewText(side, "", 16, Warning, FontStyle.Normal);
            nameProblem.gameObject.AddComponent<LayoutElement>().preferredHeight = 26f;
            startButton = MenuButton(side, "START", null, StartGame);
            MenuButton(side, "BACK", null, ClosePage);
            nameField.onValueChanged.AddListener(_ => CheckName());
            CheckName();

            StartCoroutine(Roll());
        }

        /// <summary>A fresh seed, laid out and drawn. Takes a moment, so the screen says so first.</summary>
        private IEnumerator Roll()
        {
            SetUsable(rollButton, false);
            seedText.text = "Laying out a new city…";
            yield return null;
            yield return null;
            if (preview == null)
            {
                // A copy of the settings to lay worlds out on, kept switched off.
                var holder = new GameObject("WorldPreview");
                holder.SetActive(false);
                preview = Instantiate(worldPrefab, holder.transform);
            }
            seed = Random.Range(1, 999999);
            PittGreyboxGenerator.PlanLayout(preview, seed);
            map.texture = preview.mapTexture;
            DrawLabels();
            seedText.text = $"World seed {seed}";
            SetUsable(rollButton, true);
        }

        /// <summary>District names and the scrap pit, placed as on the in-game map.</summary>
        private void DrawLabels()
        {
            Clear(mapLabels);
            for (int i = 0; i < preview.districts.Count; i++)
            {
                var d = preview.districts[i];
                var palette = preview.palettes[d.palette % preview.palettes.Length];
                MapLabel(d.name.ToUpperInvariant(), preview.DistrictLabelPoint(i), palette.accent, 20);
            }
            MapLabel("SCRAP PIT", preview.pitCenter, new Color(0.85f, 0.7f, 0.5f), 14);
        }

        private void MapLabel(string text, Vector2 world, Color color, int size)
        {
            var label = NewText(mapLabels, text, size, color, FontStyle.Bold);
            label.alignment = TextAnchor.MiddleCenter;
            label.gameObject.AddComponent<Outline>().effectColor = new Color(0f, 0f, 0f, 0.8f);
            var rt = label.rectTransform;
            var uv = (world + Vector2.one * preview.HalfSize) / preview.citySize;
            rt.anchorMin = rt.anchorMax = uv;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(260f, 30f);
            rt.anchoredPosition = Vector2.zero;
        }

        /// <summary>Required, a sensible length, plain characters (it names a folder), and not taken.</summary>
        private string NameProblem(string name)
        {
            if (name.Length == 0) return "Your character needs a name.";
            if (name.Length > MaxNameLength) return $"At most {MaxNameLength} characters.";
            if (!Regex.IsMatch(name, @"^[A-Za-z0-9][A-Za-z0-9 '\-]*$")) return "Letters, numbers, spaces, ' and - only.";
            if (SaveSystem.CharacterExists(name)) return $"There's already a character called {name}.";
            return null;
        }

        private void CheckName()
        {
            string problem = NameProblem(nameField.text.Trim());
            // Don't nag before they've typed anything; Start just stays off.
            nameProblem.text = nameField.text.Length == 0 ? "" : problem ?? "";
            SetUsable(startButton, problem == null);
        }

        private void StartGame()
        {
            string name = nameField.text.Trim();
            if (NameProblem(name) != null) return;
            StartCoroutine(Loading(() => SaveSystem.StartNewGame(name, seed)));
        }

        private void Load(SaveData data) => StartCoroutine(Loading(() =>
        {
            Unpause();
            SaveSystem.StartFromSave(data);
        }));

        /// <summary>Puts up the loading screen, lets it draw, then starts building (which holds the frame until done).</summary>
        private IEnumerator Loading(System.Action go)
        {
            var cover = Fill(NewRect("Loading", root));
            cover.gameObject.AddComponent<Image>().color = new Color(0.015f, 0.012f, 0.012f, 1f);
            // The same as the World scene's loading screen, which takes over (and fills the bar) a moment later.
            var text = NewText(cover, "LOADING", 34, Ink, FontStyle.Bold);
            text.alignment = TextAnchor.MiddleCenter;
            var tr = text.rectTransform;
            tr.anchorMin = tr.anchorMax = tr.pivot = new Vector2(0.5f, 0.5f);
            tr.anchoredPosition = new Vector2(0f, 30f);
            tr.sizeDelta = new Vector2(600f, 60f);
            var track = NewRect("Bar", cover);
            track.anchorMin = track.anchorMax = track.pivot = new Vector2(0.5f, 0.5f);
            track.anchoredPosition = new Vector2(0f, -22f);
            track.sizeDelta = new Vector2(560f, 10f);
            track.gameObject.AddComponent<Image>().color = new Color(0.16f, 0.15f, 0.15f, 1f);
            yield return null;
            yield return new WaitForEndOfFrame();
            go();
        }

        private void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        private void OpenPage(string title, float width)
        {
            Clear(page);
            page.sizeDelta = new Vector2(width, 0f);
            page.gameObject.SetActive(true);
            var heading = NewText(page, title, 30, Accent, FontStyle.Bold);
            heading.gameObject.AddComponent<LayoutElement>().preferredHeight = 52f;
        }

        private void ClosePage()
        {
            Clear(page);
            page.gameObject.SetActive(false);
        }

        // ---------------------------------------------------------------- building

        private void Build()
        {
            var go = new GameObject("MainMenu", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            go.transform.SetParent(transform, false);
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = go.GetComponent<CanvasScaler>();
            UIScale.Apply(scaler);
            root = (RectTransform)go.transform;
            if (inGame)
            {
                // Over the HUD (under the save toast), with the paused game greyed out behind the panel.
                canvas.sortingOrder = 50;
                var grey = Fill(NewRect("Grey", root)).gameObject.AddComponent<Image>();
                grey.color = new Color(0.1f, 0.1f, 0.11f, 0.72f);
            }

            var side = NewRect("Side", root);
            side.anchorMin = Vector2.zero;
            side.anchorMax = new Vector2(0f, 1f);
            side.pivot = new Vector2(0f, 0.5f);
            side.sizeDelta = new Vector2(620f, 0f);
            side.gameObject.AddComponent<Image>().color = PanelColour;
            var edge = NewRect("Edge", side);
            edge.anchorMin = new Vector2(1f, 0f);
            edge.anchorMax = Vector2.one;
            edge.pivot = new Vector2(1f, 0.5f);
            edge.sizeDelta = new Vector2(4f, 0f);
            edge.gameObject.AddComponent<Image>().color = Accent;

            var title = NewText(root, "THE PITT", 84, Ink, FontStyle.Bold);
            Place(title.rectTransform, new Vector2(110f, -150f), new Vector2(500f, 100f));

            buttonColumn = Column("Buttons", root, new Vector2(110f, -290f), 400f);
            page = Column("Page", root, new Vector2(680f, -110f), 620f);
            page.gameObject.AddComponent<Image>().color = PanelColour;
            page.GetComponent<VerticalLayoutGroup>().padding = new RectOffset(28, 28, 22, 26);
            page.gameObject.SetActive(false);
        }

        private RectTransform Column(string name, Transform parent, Vector2 topLeft, float width)
        {
            var rt = NewRect(name, parent);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = topLeft;
            rt.sizeDelta = new Vector2(width, 0f);
            var layout = rt.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 12f;
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            rt.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            return rt;
        }

        private Button MenuButton(RectTransform parent, string label, string detail, System.Action onClick, bool interactable = true)
        {
            var rt = NewRect(label, parent);
            rt.gameObject.AddComponent<LayoutElement>().preferredHeight = detail != null ? 78f : 60f;
            var image = rt.gameObject.AddComponent<Image>();
            image.color = ButtonColour;
            var button = rt.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            var colours = button.colors;
            colours.highlightedColor = new Color(2.2f, 1.9f, 1.5f);
            colours.pressedColor = new Color(2.8f, 2.3f, 1.6f);
            colours.selectedColor = Color.white;
            colours.disabledColor = new Color(1f, 1f, 1f, 0.45f);
            colours.colorMultiplier = 1f;
            button.colors = colours;
            rt.gameObject.AddComponent<CanvasGroup>();
            SetUsable(button, interactable);
            button.onClick.AddListener(() => onClick());

            var bar = NewRect("Bar", rt);
            bar.anchorMin = Vector2.zero;
            bar.anchorMax = new Vector2(0f, 1f);
            bar.pivot = new Vector2(0f, 0.5f);
            bar.sizeDelta = new Vector2(4f, 0f);
            bar.gameObject.AddComponent<Image>().color = Accent;

            var text = NewText(rt, label, 26, Ink, FontStyle.Bold);
            var tr = Fill(text.rectTransform);
            tr.offsetMin = new Vector2(24f, detail != null ? 30f : 0f);
            if (detail == null) return button;
            var sub = NewText(rt, detail, 17, Accent, FontStyle.Normal);
            var sr = Fill(sub.rectTransform);
            sr.offsetMin = new Vector2(24f, 10f);
            sr.offsetMax = new Vector2(0f, -44f);
            return button;
        }

        /// <summary>A button that can't be used is plainly faded, text and all.</summary>
        private static void SetUsable(Button button, bool usable)
        {
            button.interactable = usable;
            button.GetComponent<CanvasGroup>().alpha = usable ? 1f : 0.35f;
        }

        private InputField NameField(RectTransform parent)
        {
            var rt = NewRect("CharacterName", parent);
            rt.gameObject.AddComponent<LayoutElement>().preferredHeight = 56f;
            var image = rt.gameObject.AddComponent<Image>();
            image.color = new Color(0.14f, 0.13f, 0.13f, 1f);
            var field = rt.gameObject.AddComponent<InputField>();
            field.targetGraphic = image;
            field.characterLimit = MaxNameLength + 8;
            field.lineType = InputField.LineType.SingleLine;

            var text = NewText(rt, "", 24, Ink, FontStyle.Bold);
            text.supportRichText = false;
            var tr = Fill(text.rectTransform);
            tr.offsetMin = new Vector2(16f, 0f);
            tr.offsetMax = new Vector2(-16f, 0f);
            var placeholder = NewText(rt, "Who are you?", 24, Muted, FontStyle.Italic);
            var pr = Fill(placeholder.rectTransform);
            pr.offsetMin = new Vector2(16f, 0f);
            pr.offsetMax = new Vector2(-16f, 0f);
            field.textComponent = text;
            field.placeholder = placeholder;
            return field;
        }

        private void Slider(RectTransform parent, string label, float min, float max, float value,
            System.Action<float> changed, System.Func<float, string> format)
        {
            var row = NewRect(label, parent);
            row.gameObject.AddComponent<LayoutElement>().preferredHeight = 70f;
            var title = NewText(row, label, 20, Ink, FontStyle.Bold);
            StretchTop(title.rectTransform, 30f);
            var readout = NewText(row, format(value), 20, Accent, FontStyle.Bold);
            StretchTop(readout.rectTransform, 30f);
            readout.alignment = TextAnchor.MiddleRight;

            var track = NewRect("Slider", row);
            track.anchorMin = new Vector2(0f, 0f);
            track.anchorMax = new Vector2(1f, 0f);
            track.pivot = new Vector2(0.5f, 0f);
            track.anchoredPosition = new Vector2(0f, 8f);
            track.sizeDelta = new Vector2(0f, 22f);
            var background = Fill(NewRect("Background", track));
            background.offsetMin = new Vector2(0f, 7f);
            background.offsetMax = new Vector2(0f, -7f);
            background.gameObject.AddComponent<Image>().color = new Color(0.16f, 0.16f, 0.17f);
            var fillArea = Fill(NewRect("Fill Area", track));
            fillArea.offsetMin = new Vector2(0f, 7f);
            fillArea.offsetMax = new Vector2(0f, -7f);
            var fill = Fill(NewRect("Fill", fillArea));
            fill.gameObject.AddComponent<Image>().color = Accent;
            var handleArea = Fill(NewRect("Handle Area", track));
            var handle = NewRect("Handle", handleArea);
            handle.sizeDelta = new Vector2(14f, 0f);
            var handleImage = handle.gameObject.AddComponent<Image>();
            handleImage.color = Ink;

            var slider = track.gameObject.AddComponent<UnityEngine.UI.Slider>();
            slider.fillRect = fill;
            slider.handleRect = handle;
            slider.targetGraphic = handleImage;
            slider.minValue = min;
            slider.maxValue = max;
            slider.value = value;
            slider.onValueChanged.AddListener(v =>
            {
                readout.text = format(v);
                changed(v);
            });
        }

        private void Toggle(RectTransform parent, string label, bool value, System.Action<bool> changed)
        {
            bool on = value;
            var row = NewRect(label, parent);
            row.gameObject.AddComponent<LayoutElement>().preferredHeight = 44f;
            var title = NewText(row, label, 20, Ink, FontStyle.Bold);
            Fill(title.rectTransform);
            var state = NewText(row, on ? "ON" : "OFF", 20, Accent, FontStyle.Bold);
            Fill(state.rectTransform);
            state.alignment = TextAnchor.MiddleRight;
            var image = row.gameObject.AddComponent<Image>();
            image.color = Color.clear;
            var button = row.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(() =>
            {
                on = !on;
                state.text = on ? "ON" : "OFF";
                changed(on);
            });
        }

        private Text NewText(Transform parent, string text, int size, Color color, FontStyle style)
        {
            var t = NewRect("Text", parent).gameObject.AddComponent<Text>();
            t.font = font;
            t.fontSize = size;
            t.fontStyle = style;
            t.color = color;
            t.text = text;
            t.alignment = TextAnchor.MiddleLeft;
            t.raycastTarget = false;
            return t;
        }

        private static void Clear(RectTransform rt)
        {
            if (rt == null) return;
            foreach (Transform child in rt) Destroy(child.gameObject);
        }

        private static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        private static RectTransform Fill(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            return rt;
        }

        private static void Place(RectTransform rt, Vector2 topLeft, Vector2 size)
        {
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = topLeft;
            rt.sizeDelta = size;
        }

        private static void StretchTop(RectTransform rt, float height)
        {
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 1f);
            rt.offsetMin = new Vector2(0f, -height);
            rt.offsetMax = Vector2.zero;
        }
    }
}
