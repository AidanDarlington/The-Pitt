using System.Collections.Generic;
using ThePitt.Factions;
using ThePitt.Player;
using ThePitt.World;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace ThePitt.UI
{
    /// <summary>
    /// Full-screen top-down map of The Pitt (toggle with the Map action), with the player's position and
    /// facing as a green arrow. Three views, picked with the tabs above the legend, each showing only its own
    /// layer: the map itself with district and place names, the properties, and the factions (gang turf and
    /// the Watchers). Opens on whichever view was picked last.
    /// </summary>
    public class MapUI : MonoBehaviour
    {
        [SerializeField] private InputActionAsset actions;
        [SerializeField] private PittGreybox greybox;
        [SerializeField] private DistrictAtmosphere atmosphere;
        [SerializeField] private Transform player;
        [SerializeField] private FirstPersonController controller;
        [SerializeField] private GameObject panel;
        [SerializeField] private RawImage mapImage;
        [SerializeField] private RectTransform playerMarker;
        [SerializeField] private RectTransform labelLayer;
        [Tooltip("Inactive Text used as the template for map labels.")]
        [SerializeField] private Text labelTemplate;
        [SerializeField] private Text headerLabel;
        [Tooltip("Legend text on the right; rewritten for each view. The view tabs sit above it.")]
        [SerializeField] private Text legend;
        [Tooltip("Hidden while the map is open. Found automatically if empty.")]
        [SerializeField] private HotbarUI hotbarUI;
        [Tooltip("Opacity of the gang territory tint over the map.")]
        [SerializeField, Range(0f, 1f)] private float territoryAlpha = 0.35f;

        public enum View { Map, Properties, Factions }

        private const string ViewKey = "MapView";
        private static readonly Color PlayerColor = new(0.35f, 1f, 0.4f);

        private InputAction toggle;
        private bool labelsBuilt;
        private View view;
        private RectTransform districtLayer, propertyLayer, factionLayer;
        private readonly List<(View view, Image background, Text text)> tabs = new();
        private readonly List<GameObject> labels = new();
        private readonly List<GameObject> factionLabels = new();
        private readonly List<GameObject> propertyLabels = new();
        private RawImage territoryImage;
        // Zoom: the map scales inside a clipping viewport where it used to sit; the scroll wheel zooms toward
        // the cursor and dragging pans. Labels and the player arrow stay the same size on screen.
        private const float MaxZoom = 5f, ZoomStep = 1.25f;
        private RectTransform viewport;
        private RectMask2D viewportMask;
        private float zoom = 1f;
        private bool dragging;
        private Texture2D territoryTexture;
        private bool territoryDirty = true;

        public bool IsOpen => panel.activeSelf;

        private void Awake()
        {
            // The city lives in the World scene; no reference can reach across.
            if (greybox == null) greybox = PittGreybox.Current;
            toggle = actions.FindActionMap("Player", true).FindAction("Map", true);
            labelTemplate.gameObject.SetActive(false);
            panel.SetActive(false);
            if (hotbarUI == null) hotbarUI = FindAnyObjectByType<HotbarUI>();
            view = (View)Mathf.Clamp(PlayerPrefs.GetInt(ViewKey, 0), 0, 2);
            BuildTabs();
            MakeArrow();
            BuildViewport();
        }

        private void Start()
        {
            if (FactionManager.Instance != null) FactionManager.Instance.TerritoryChanged += MarkTerritoryDirty;
        }

        private void OnDestroy()
        {
            if (FactionManager.Instance != null) FactionManager.Instance.TerritoryChanged -= MarkTerritoryDirty;
            if (territoryTexture != null) Destroy(territoryTexture);
        }

        private void MarkTerritoryDirty()
        {
            territoryDirty = true;
            if (IsOpen) RefreshTerritory();
        }

        private void Update()
        {
            bool canOpen = controller == null || controller.InputEnabled;
            if (toggle.WasPressedThisFrame() && (IsOpen || canOpen)) SetOpen(!IsOpen);
            else if (IsOpen && Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame) SetOpen(false);

            if (IsOpen)
            {
                UpdateMarker();
                UpdateZoom();
            }
        }

        public void SetOpen(bool open)
        {
            if (open && !labelsBuilt) BuildLabels();
            if (open && territoryDirty) RefreshTerritory();
            if (open) RefreshPropertyLabels();
            // Opens on the view picked last.
            if (open) SetView(view);
            panel.SetActive(open);
            if (hotbarUI != null) hotbarUI.SetVisible(!open);
            // The map takes over like the inventory: no walking or looking while it's up.
            if (controller != null) controller.InputEnabled = !open;
            Cursor.lockState = open ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = open;
            if (open)
            {
                mapImage.texture = greybox.mapTexture;
                string here = atmosphere != null ? atmosphere.CurrentDistrictName : "";
                headerLabel.text = string.IsNullOrEmpty(here) ? "THE PITT" : $"THE PITT  ·  {here.ToUpperInvariant()}";
                var turf = FactionManager.Instance != null && player != null ? FactionManager.Instance.OwnerAt(player.position) : null;
                if (turf != null) headerLabel.text += $"  ·  {turf.Name.ToUpperInvariant()} TURF";
                UpdateMarker();
                // Zoomed in: open on the player.
                dragging = false;
                SetPan(-playerMarker.anchoredPosition * zoom);
                ApplyZoom();
            }
        }

        // ---------------------------------------------------------------- zoom

        /// <summary>Puts the map inside a viewport of its own size and place, which clips it once zoomed in.</summary>
        private void BuildViewport()
        {
            var map = mapImage.rectTransform;
            var go = new GameObject("MapViewport", typeof(RectTransform), typeof(RectMask2D));
            viewport = (RectTransform)go.transform;
            viewport.SetParent(map.parent, false);
            viewport.SetSiblingIndex(map.GetSiblingIndex());
            viewport.anchorMin = map.anchorMin;
            viewport.anchorMax = map.anchorMax;
            viewport.pivot = map.pivot;
            viewport.anchoredPosition = map.anchoredPosition;
            viewport.sizeDelta = map.sizeDelta;
            viewportMask = go.GetComponent<RectMask2D>();
            map.SetParent(viewport, false);
            map.anchorMin = map.anchorMax = map.pivot = new Vector2(0.5f, 0.5f);
            map.anchoredPosition = Vector2.zero;
            // Catches the scroll and drag over the map.
            mapImage.raycastTarget = true;
        }

        private void UpdateZoom()
        {
            var mouse = Mouse.current;
            if (mouse == null || viewport == null) return;
            var canvas = viewport.GetComponentInParent<Canvas>();
            var cam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            var screen = mouse.position.ReadValue();
            bool over = RectTransformUtility.RectangleContainsScreenPoint(viewport, screen, cam);

            float scroll = mouse.scroll.ReadValue().y;
            if (over && Mathf.Abs(scroll) > 0.01f)
            {
                float next = Mathf.Clamp(scroll > 0f ? zoom * ZoomStep : zoom / ZoomStep, 1f, MaxZoom);
                if (!Mathf.Approximately(next, zoom))
                {
                    // Keep the point under the cursor where it is.
                    RectTransformUtility.ScreenPointToLocalPointInRectangle(viewport, screen, cam, out var local);
                    var onMap = (local - mapImage.rectTransform.anchoredPosition) / zoom;
                    zoom = next;
                    SetPan(local - onMap * zoom);
                    ApplyZoom();
                }
            }

            if (mouse.leftButton.wasPressedThisFrame) dragging = over && zoom > 1f;
            if (!mouse.leftButton.isPressed) dragging = false;
            if (dragging) SetPan(mapImage.rectTransform.anchoredPosition + mouse.delta.ReadValue() / canvas.scaleFactor);
        }

        /// <summary>Moves the map in its viewport, never so far that an edge comes away from the side.</summary>
        private void SetPan(Vector2 pan)
        {
            var limit = viewport.rect.size * (zoom - 1f) / 2f;
            mapImage.rectTransform.anchoredPosition = new Vector2(
                Mathf.Clamp(pan.x, -limit.x, limit.x), Mathf.Clamp(pan.y, -limit.y, limit.y));
        }

        private void ApplyZoom()
        {
            mapImage.rectTransform.localScale = Vector3.one * zoom;
            // At full size the Watchers' labels hang past the map's edge: only clip once zoomed.
            viewportMask.enabled = zoom > 1.001f;
            var counter = Vector3.one / zoom;
            foreach (var text in labelLayer.GetComponentsInChildren<Text>(true)) text.rectTransform.localScale = counter;
            playerMarker.localScale = counter;
            SetPan(mapImage.rectTransform.anchoredPosition);
        }

        private Vector2 MapPosition(Vector2 world)
        {
            var rect = mapImage.rectTransform.rect;
            var uv = (world + Vector2.one * greybox.HalfSize) / greybox.citySize;
            return new Vector2((uv.x - 0.5f) * rect.width, (uv.y - 0.5f) * rect.height);
        }

        private void UpdateMarker()
        {
            if (player == null) return;
            // Inside a property (off the map): shown at its door.
            var at = player.position;
            foreach (var p in Properties.Property.All)
                if (p.Contains(at, -0.5f)) at = p.Entrance;
            playerMarker.anchoredPosition = MapPosition(new Vector2(at.x, at.z));
            // North (+Z) is up on the map; the marker points the way the player faces.
            playerMarker.localRotation = Quaternion.Euler(0f, 0f, -player.eulerAngles.y);
        }

        /// <summary>Turns the marker into a green arrow, with a dark one behind it so it shows on light streets.</summary>
        private void MakeArrow()
        {
            if (playerMarker == null) return;
            var sprite = ArrowSprite();
            var halo = playerMarker.Find("Halo") as RectTransform;
            var body = playerMarker.Find("Body") as RectTransform;
            var facing = playerMarker.Find("Facing");
            if (facing != null) facing.gameObject.SetActive(false);
            if (halo != null && halo.TryGetComponent<Image>(out var haloImage))
            {
                haloImage.sprite = sprite;
                haloImage.color = new Color(0f, 0f, 0f, 0.85f);
                halo.anchoredPosition = Vector2.zero;
                halo.localRotation = Quaternion.identity;
                halo.sizeDelta = new Vector2(30f, 30f);
            }
            if (body != null && body.TryGetComponent<Image>(out var bodyImage))
            {
                bodyImage.sprite = sprite;
                bodyImage.color = PlayerColor;
                body.anchoredPosition = Vector2.zero;
                // The old diamond was a square turned 45°.
                body.localRotation = Quaternion.identity;
                body.sizeDelta = new Vector2(22f, 22f);
            }
        }

        /// <summary>A navigation arrow pointing up: a triangle with a notch cut into its base.</summary>
        private static Sprite ArrowSprite()
        {
            const int size = 64;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            Vector2 tip = new(0.5f, 0.97f), left = new(0.1f, 0.05f), notch = new(0.5f, 0.3f), right = new(0.9f, 0.05f);
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                // 4x4 samples per pixel for smooth edges.
                int inside = 0;
                for (int sy = 0; sy < 4; sy++)
                for (int sx = 0; sx < 4; sx++)
                {
                    var p = new Vector2((x + (sx + 0.5f) / 4f) / size, (y + (sy + 0.5f) / 4f) / size);
                    if (InTriangle(p, tip, left, notch) || InTriangle(p, tip, notch, right)) inside++;
                }
                px[y * size + x] = new Color32(255, 255, 255, (byte)(inside * 255 / 16));
            }
            tex.SetPixels32(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        }

        private static bool InTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = Cross(p, a, b), d2 = Cross(p, b, c), d3 = Cross(p, c, a);
            bool neg = d1 < 0f || d2 < 0f || d3 < 0f, pos = d1 > 0f || d2 > 0f || d3 > 0f;
            return !(neg && pos);
        }

        private static float Cross(Vector2 p, Vector2 a, Vector2 b) => (p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y);

        private void BuildLabels()
        {
            labelsBuilt = true;
            // Everything on the layer but the template: this session's labels, and any left over from an older
            // city that got saved into the scene.
            foreach (Transform child in labelLayer)
                if (child != labelTemplate.transform) Destroy(child.gameObject);
            labels.Clear();
            factionLabels.Clear();
            propertyLabels.Clear();
            districtLayer = NewLayer("Districts");
            propertyLayer = NewLayer("Properties");
            factionLayer = NewLayer("Factions");

            for (int i = 0; i < greybox.districts.Count; i++)
            {
                var d = greybox.districts[i];
                var palette = greybox.palettes[d.palette % greybox.palettes.Length];
                AddLabel(d.name.ToUpperInvariant(), greybox.DistrictLabelPoint(i), palette.accent, 22, districtLayer);
            }
            AddLabel("SCRAP PIT", greybox.pitCenter, new Color(0.85f, 0.7f, 0.5f), 15, districtLayer);
            // The Watchers' outposts hang off the edge, beyond the map itself: their platforms and bridges in
            // white, on every view. Their name is faction information.
            foreach (var o in greybox.watcherOutposts)
            {
                AddMarker(o.rect, new Color(0.92f, 0.95f, 1f, 0.95f));
                AddMarker(o.bridge, new Color(0.92f, 0.95f, 1f, 0.8f));
                // Off to the side of the platform, clear of the map: outward for the side outposts, along the
                // edge for the top and bottom ones. Either way it reads away from the platform.
                var side = Mathf.Abs(o.inward.x) > 0f ? -o.inward : new Vector2(o.inward.y, -o.inward.x);
                var label = AddLabel("◆ WATCHERS", o.rect.center + side * (o.rect.width / 2f + 3f), new Color(0.85f, 0.93f, 1f), 13, factionLayer);
                var text = label.GetComponent<Text>();
                text.alignment = side.x > 0f ? TextAnchor.MiddleLeft : TextAnchor.MiddleRight;
                text.rectTransform.pivot = new Vector2(side.x > 0f ? 0f : 1f, 0.5f);
                text.rectTransform.sizeDelta = new Vector2(160f, text.rectTransform.sizeDelta.y);
            }
            // Only the lowest of the big terraces gets a name, so the map doesn't fill with labels.
            PittGreybox.Plateau? sink = null;
            foreach (var z in greybox.terraces)
            {
                if (z.rect.width * z.rect.height < 1200f || z.rect.Contains(greybox.pitCenter)) continue;
                if (z.height < -3f && (sink == null || z.height < sink.Value.height)) sink = z;
            }
            if (sink != null) AddLabel("THE SINK", sink.Value.rect.center, new Color(0.9f, 0.9f, 0.85f), 13, districtLayer);
            foreach (var sh in greybox.sumpShafts)
                AddLabel("▼ SUMP", sh.center + Vector2.down * 6f, new Color(0.95f, 0.85f, 0.4f), 13, districtLayer);
        }

        private RectTransform NewLayer(string name)
        {
            var layer = new GameObject(name, typeof(RectTransform)).transform as RectTransform;
            layer.SetParent(labelLayer, false);
            layer.anchorMin = layer.anchorMax = new Vector2(0.5f, 0.5f);
            layer.sizeDelta = Vector2.zero;
            return layer;
        }

        // ---------------------------------------------------------------- views

        public void SetView(View next)
        {
            if (next != view) PlayerPrefs.SetInt(ViewKey, (int)next);
            view = next;
            if (districtLayer != null) districtLayer.gameObject.SetActive(view == View.Map);
            if (propertyLayer != null) propertyLayer.gameObject.SetActive(view == View.Properties);
            if (factionLayer != null) factionLayer.gameObject.SetActive(view == View.Factions);
            if (territoryImage != null) territoryImage.gameObject.SetActive(view == View.Factions);
            foreach (var (v, background, text) in tabs)
            {
                bool on = v == view;
                background.color = on ? new Color(0.9f, 0.88f, 0.84f, 0.95f) : new Color(0.08f, 0.08f, 0.09f, 0.85f);
                text.color = on ? new Color(0.06f, 0.06f, 0.07f) : new Color(0.85f, 0.83f, 0.8f);
            }
            if (legend != null) legend.text = LegendText(view);
        }

        /// <summary>One tab per view, stacked full-width in a column above the legend, so no label wraps.</summary>
        private void BuildTabs()
        {
            if (legend == null) return;
            var lr = legend.rectTransform;
            const float tabHeight = 36f, gap = 6f;
            var row = new GameObject("ViewTabs", typeof(RectTransform), typeof(VerticalLayoutGroup)).transform as RectTransform;
            row.SetParent(lr.parent, false);
            row.anchorMin = row.anchorMax = lr.anchorMin;
            row.pivot = new Vector2(lr.pivot.x, 0f);
            float legendTop = lr.anchoredPosition.y + lr.sizeDelta.y * (1f - lr.pivot.y);
            row.anchoredPosition = new Vector2(lr.anchoredPosition.x, legendTop + 16f);
            row.sizeDelta = new Vector2(lr.sizeDelta.x, tabHeight * 3f + gap * 2f);
            var layout = row.GetComponent<VerticalLayoutGroup>();
            layout.spacing = gap;
            layout.childControlWidth = layout.childControlHeight = true;
            layout.childForceExpandWidth = layout.childForceExpandHeight = true;

            foreach (var (v, title) in new[] { (View.Map, "MAP"), (View.Properties, "PROPERTIES"), (View.Factions, "FACTIONS") })
            {
                var button = new GameObject(title, typeof(RectTransform), typeof(Image), typeof(Button));
                button.transform.SetParent(row, false);
                var background = button.GetComponent<Image>();
                var text = new GameObject("Text", typeof(RectTransform), typeof(Text)).GetComponent<Text>();
                text.rectTransform.SetParent(button.transform, false);
                text.rectTransform.anchorMin = Vector2.zero;
                text.rectTransform.anchorMax = Vector2.one;
                text.rectTransform.offsetMin = text.rectTransform.offsetMax = Vector2.zero;
                text.font = labelTemplate.font;
                text.fontSize = 16;
                text.fontStyle = FontStyle.Bold;
                text.alignment = TextAnchor.MiddleCenter;
                text.raycastTarget = false;
                text.text = title;
                var target = v;
                button.GetComponent<Button>().onClick.AddListener(() => SetView(target));
                tabs.Add((v, background, text));
            }
        }

        /// <summary>The selected view's own keys first, then the streets and buildings key every view shares.</summary>
        private static string LegendText(View view)
        {
            var sb = new System.Text.StringBuilder("<b>LEGEND</b>\n\n");
            string you = $"<color=#{ColorUtility.ToHtmlStringRGB(PlayerColor)}>▲</color>  You\n";
            switch (view)
            {
                case View.Properties:
                    sb.Append($"<color=#{ColorUtility.ToHtmlStringRGB(OwnedColor)}>{OwnedKey}</color>  Your property\n");
                    sb.Append($"<color=#{ColorUtility.ToHtmlStringRGB(ForSaleColor)}>{ForSaleKey}</color>  For sale\n");
                    sb.Append(you).Append('\n');
                    break;
                case View.Factions:
                    sb.Append("<color=#C0B8A8>■</color>  Gang territory (tinted)\n");
                    sb.Append("<color=#F2F7FF>■</color>  Watcher ground\n");
                    sb.Append("<color=#ECF2FF>■</color>  Watcher outposts\n");
                    sb.Append(you).Append('\n');
                    break;
                default:
                    sb.Append(you);
                    sb.Append("───  District border\n");
                    sb.Append("<color=#F2D966>▼</color>  Sump shaft\n\n");
                    break;
            }
            sb.Append("<color=#D8D4CC>■</color>  Thoroughfares\n");
            sb.Append("<color=#B0ACA4>■</color>  Secondary alleys\n");
            sb.Append("<color=#8A8680>■</color>  Small alleys\n");
            sb.Append("<color=#141414>■</color>  Buildings\n");
            sb.Append("<color=#C8A060>■</color>  Raised catwalks\n");
            sb.Append("<color=#806040>■</color>  Scrap pit\n");
            if (view != View.Factions) sb.Append("<color=#ECF2FF>■</color>  Watcher outposts\n");
            if (view != View.Map) sb.Append("───  District border\n");
            sb.Append("\n<color=#8A8680>Scroll to zoom · drag to pan\nM / Esc to close</color>");
            return sb.ToString();
        }

        private static readonly Color OwnedColor = new(0.4f, 0.9f, 0.4f);
        private static readonly Color ForSaleColor = new(1f, 0.45f, 0.35f);
        private const string OwnedKey = "●";
        private const string ForSaleKey = "■";

        /// <summary>
        /// Marks every property with its key from the legend: the player's own, and the ones for sale with their
        /// price. Businesses say what they are.
        /// </summary>
        private void RefreshPropertyLabels()
        {
            foreach (var l in propertyLabels) Destroy(l);
            propertyLabels.Clear();
            foreach (var p in Properties.Property.All)
            {
                var business = Production.Business.Of(p);
                string what = business != null ? business.Name.ToUpperInvariant() : null;
                string text = p.Owned ? (what != null ? $"{OwnedKey} {what}" : OwnedKey)
                    : what != null ? $"{ForSaleKey} {what} · {p.Price}" : $"{ForSaleKey} {p.Price}";
                // Right on its door.
                var label = AddLabel(text, new Vector2(p.Entrance.x, p.Entrance.z),
                    p.Owned ? OwnedColor : ForSaleColor, p.Owned ? 20 : 13, propertyLayer);
                if (p.Owned) label.GetComponent<Text>().fontStyle = FontStyle.Bold;
                labels.Remove(label);
                propertyLabels.Add(label);
            }
        }

        /// <summary>Repaints the territory tint and moves each gang's label to the middle of its biggest holding.</summary>
        private void RefreshTerritory()
        {
            territoryDirty = false;
            var factions = FactionManager.Instance;
            if (factions == null || factions.CellCount == 0) return;

            int res = factions.Resolution;
            if (territoryTexture == null || territoryTexture.width != res)
            {
                if (territoryTexture != null) Destroy(territoryTexture);
                territoryTexture = new Texture2D(res, res, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                };
            }
            if (territoryImage == null)
            {
                // Laid over the map image, under the labels and the player marker.
                var go = new GameObject("Territory", typeof(RectTransform), typeof(RawImage));
                var rt = (RectTransform)go.transform;
                rt.SetParent(mapImage.transform, false);
                rt.SetAsFirstSibling();
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = rt.offsetMax = Vector2.zero;
                territoryImage = go.GetComponent<RawImage>();
                territoryImage.raycastTarget = false;
                territoryImage.gameObject.SetActive(view == View.Factions);
            }

            // The grid can overhang the city edge; scale the image so cells line up with the map.
            float covered = res * factions.CellSize / greybox.citySize;
            territoryImage.rectTransform.anchorMax = new Vector2(covered, covered);

            var px = new Color32[res * res];
            for (int c = 0; c < px.Length; c++)
            {
                if (factions.IsReserved(c))
                {
                    px[c] = new Color(0.95f, 0.97f, 1f, territoryAlpha + 0.15f);
                    continue;
                }
                var o = factions.OwnerOfCell(c);
                if (o == null) continue;
                var col = o.definition.Color;
                col.a = territoryAlpha;
                px[c] = col;
            }
            territoryTexture.SetPixels32(px);
            territoryTexture.Apply();
            territoryImage.texture = territoryTexture;

            foreach (var l in factionLabels) Destroy(l);
            factionLabels.Clear();
            foreach (var f in factions.Factions)
            {
                if (!f.Alive) continue;
                var label = AddLabel(f.Name.ToUpperInvariant(), LargestHoldingCentre(factions, f) + Vector2.down * 8f,
                    f.definition.Color, 14, factionLayer);
                labels.Remove(label);
                factionLabels.Add(label);
            }
        }

        /// <summary>Centre of the biggest connected patch of a gang's cells, so outposts don't drag the label off.</summary>
        private static Vector2 LargestHoldingCentre(FactionManager factions, FactionManager.FactionState f)
        {
            int res = factions.Resolution;
            var seen = new bool[factions.CellCount];
            var stack = new Stack<int>();
            Vector2 best = Vector2.zero;
            int bestCount = 0;
            for (int start = 0; start < seen.Length; start++)
            {
                if (seen[start] || factions.OwnerOfCell(start) != f) continue;
                Vector2 sum = Vector2.zero;
                int count = 0;
                seen[start] = true;
                stack.Push(start);
                while (stack.Count > 0)
                {
                    int c = stack.Pop();
                    sum += factions.CellCenter(c);
                    count++;
                    int x = c % res, y = c / res;
                    Visit(x - 1, y); Visit(x + 1, y); Visit(x, y - 1); Visit(x, y + 1);
                }
                if (count > bestCount)
                {
                    bestCount = count;
                    best = sum / count;
                }
            }
            return best;

            void Visit(int x, int y)
            {
                if (x < 0 || y < 0 || x >= res || y >= res) return;
                int n = y * res + x;
                if (seen[n] || factions.OwnerOfCell(n) != f) return;
                seen[n] = true;
                stack.Push(n);
            }
        }

        /// <summary>A filled box on the map covering a world rect, drawn under the labels.</summary>
        private void AddMarker(Rect world, Color color)
        {
            var go = new GameObject("Marker", typeof(RectTransform), typeof(Image));
            var rt = (RectTransform)go.transform;
            rt.SetParent(labelLayer, false);
            rt.SetAsFirstSibling();
            var a = MapPosition(world.min);
            var b = MapPosition(world.max);
            rt.anchoredPosition = (a + b) / 2f;
            rt.sizeDelta = new Vector2(Mathf.Abs(b.x - a.x), Mathf.Abs(b.y - a.y));
            var image = go.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            labels.Add(go);
        }

        private GameObject AddLabel(string text, Vector2 world, Color color, int size, RectTransform layer)
        {
            var label = Instantiate(labelTemplate, layer);
            label.gameObject.SetActive(true);
            label.text = text;
            label.color = color;
            label.fontSize = size;
            label.rectTransform.anchoredPosition = MapPosition(world);
            labels.Add(label.gameObject);
            return label.gameObject;
        }
    }
}
