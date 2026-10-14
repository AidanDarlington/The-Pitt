using ThePitt.Combat;
using ThePitt.Items;
using ThePitt.Player;
using UnityEngine;
using UnityEngine.UI;

namespace ThePitt.UI
{
    /// <summary>
    /// Fighting feedback, built at runtime on its own canvas: what's in hand and how many rounds, a hit
    /// marker round the crosshair (red on a kill), and a red flash when the player is hurt.
    /// </summary>
    public class CombatHUD : MonoBehaviour
    {
        [SerializeField] private PlayerCombat combat;
        [SerializeField] private PlayerVitals vitals;
        [SerializeField, Min(0f)] private float hitMarkerTime = 0.15f;
        [SerializeField, Range(0f, 1f)] private float hurtFlashAlpha = 0.35f;

        private Text weaponLabel;
        private Image hurtFlash;
        private readonly Image[] marker = new Image[4];
        private float markerUntil;
        private bool markerKill;
        private float hurtAlpha;

        private void Awake()
        {
            if (combat == null) combat = GetComponent<PlayerCombat>();
            if (vitals == null) vitals = GetComponent<PlayerVitals>();
            Build();
        }

        private void OnEnable()
        {
            if (combat != null)
            {
                combat.Changed += Refresh;
                combat.Hit += OnHit;
            }
            if (vitals != null) vitals.Hurt += OnHurt;
        }

        private void OnDisable()
        {
            if (combat != null)
            {
                combat.Changed -= Refresh;
                combat.Hit -= OnHit;
            }
            if (vitals != null) vitals.Hurt -= OnHurt;
        }

        private void Start() => Refresh();

        private void OnHit(bool killed)
        {
            markerKill = killed;
            markerUntil = Time.time + hitMarkerTime * (killed ? 2.5f : 1f);
        }

        private void OnHurt(DamageInfo info) => hurtAlpha = hurtFlashAlpha;

        private void Update()
        {
            // Its canvas can go first while the scene is being torn down (leaving play mode, loading a save).
            if (hurtFlash == null || marker[0] == null) return;
            bool showMarker = Time.time < markerUntil;
            var markerColor = markerKill ? new Color(0.95f, 0.2f, 0.15f) : new Color(1f, 1f, 1f, 0.9f);
            foreach (var m in marker)
            {
                m.enabled = showMarker;
                m.color = markerColor;
            }

            hurtAlpha = Mathf.MoveTowards(hurtAlpha, 0f, Time.deltaTime * 1.2f);
            hurtFlash.enabled = hurtAlpha > 0.001f;
            hurtFlash.color = new Color(0.6f, 0f, 0f, hurtAlpha);

            if (combat != null && combat.IsReloading) Refresh();
        }

        private void Refresh()
        {
            if (combat == null || weaponLabel == null) return;
            var w = combat.Equipped;
            if (combat.HeldUsable != null)
            {
                var held = combat.HeldUsable;
                string tier = held.HasQuality ? $" <color=#B0A890>({combat.HeldUsableQuality})</color>" : "";
                weaponLabel.text = $"{held.DisplayName}{tier}   <color=#B0A890>LMB {Hotbar.UseVerb(held)}</color>";
                return;
            }
            if (w == null)
            {
                weaponLabel.text = combat.HandsDown ? "" : "Fists";
                return;
            }
            string name = w.HasQuality ? $"{w.DisplayName} <color=#B0A890>({combat.EquippedQuality})</color>" : w.DisplayName;
            if (w.WeaponType != WeaponType.Firearm)
            {
                weaponLabel.text = name;
                return;
            }
            string rounds = combat.IsReloading ? "reloading…" : $"{combat.LoadedRounds}/{w.MagazineSize}";
            weaponLabel.text = $"{name}   {rounds}   <color=#B0A890>Ammo {combat.SpareRounds}</color>";
        }

        private void Build()
        {
            var canvasGo = new GameObject("CombatHUD", typeof(Canvas), typeof(CanvasScaler));
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 50;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            UIScale.Apply(scaler);
            var root = canvasGo.transform;

            hurtFlash = NewImage(root, "HurtFlash", Vector2.zero, Vector2.zero);
            var rt = hurtFlash.rectTransform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            hurtFlash.enabled = false;

            // Four short ticks in an X round the crosshair.
            for (int i = 0; i < 4; i++)
            {
                float angle = 45f + i * 90f;
                var dir = Quaternion.Euler(0f, 0f, angle) * Vector3.right;
                marker[i] = NewImage(root, "HitMarker", (Vector2)(dir * 14f), new Vector2(12f, 3f));
                marker[i].rectTransform.anchorMin = marker[i].rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
                marker[i].rectTransform.localRotation = Quaternion.Euler(0f, 0f, angle);
                marker[i].enabled = false;
            }

            var labelGo = new GameObject("Weapon", typeof(RectTransform), typeof(Text));
            labelGo.transform.SetParent(root, false);
            var lr = (RectTransform)labelGo.transform;
            lr.anchorMin = lr.anchorMax = new Vector2(0.5f, 0f);
            lr.pivot = new Vector2(0.5f, 0f);
            // Just above the hotbar.
            lr.anchoredPosition = new Vector2(0f, 100f);
            lr.sizeDelta = new Vector2(900f, 36f);
            weaponLabel = labelGo.GetComponent<Text>();
            weaponLabel.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            weaponLabel.fontSize = 24;
            weaponLabel.alignment = TextAnchor.MiddleCenter;
            weaponLabel.color = new Color(0.92f, 0.9f, 0.85f);
            weaponLabel.raycastTarget = false;
            weaponLabel.supportRichText = true;
        }

        private static Image NewImage(Transform parent, string name, Vector2 position, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchoredPosition = position;
            rt.sizeDelta = size;
            var image = go.GetComponent<Image>();
            image.raycastTarget = false;
            return image;
        }
    }
}
