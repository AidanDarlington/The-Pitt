using ThePitt.Core;
using ThePitt.Economy;
using ThePitt.Factions;
using ThePitt.Interaction;
using UnityEngine;

namespace ThePitt.Dev
{
    /// <summary>
    /// A button on the dev world's console: gives or takes scrip, moves a gang's standing, or winds the clock on,
    /// so things can be tested without playing up to them.
    /// </summary>
    public class DevButton : MonoBehaviour, IInteractable
    {
        public enum Kind { Scrip, Standing, Hours }

        [SerializeField] private Kind kind;
        [SerializeField] private float amount;
        [Tooltip("The gang's definition asset name, for Standing.")]
        [SerializeField] private string faction;

        private PlayerWallet wallet;
        private Renderer face;
        private float flashUntil;

        public void Setup(Kind newKind, float newAmount, string factionId = null)
        {
            kind = newKind;
            amount = newAmount;
            faction = factionId;
        }

        private void Awake()
        {
            face = GetComponent<Renderer>();
            enabled = false;
        }

        // Only runs while lit up after a press.
        private void Update()
        {
            if (Time.unscaledTime < flashUntil) return;
            if (face != null) face.enabled = true;
            enabled = false;
        }

        private PlayerWallet Wallet => wallet != null ? wallet : wallet = FindAnyObjectByType<PlayerWallet>();

        private FactionManager.FactionState Gang()
        {
            var manager = FactionManager.Instance;
            if (manager == null) return null;
            foreach (var f in manager.Factions)
                if (f.definition != null && f.definition.name == faction) return f;
            return null;
        }

        private string Signed => amount > 0f ? $"+{amount:0}" : $"{amount:0}";

        public string GetPrompt(PlayerInteractor interactor)
        {
            switch (kind)
            {
                case Kind.Scrip:
                    return $"Scrip {Signed} (have {(Wallet != null ? Wallet.Money : 0):N0})";
                case Kind.Standing:
                    var gang = Gang();
                    if (gang == null) return "Gang not found";
                    return $"{gang.Name} rep {Signed} (now {gang.standing:0}, {FactionManager.Instance.RelationTo(gang)})";
                default:
                    return $"Wind the clock on {amount:0} hour{(Mathf.Approximately(amount, 1f) ? "" : "s")}";
            }
        }

        public bool CanInteract(PlayerInteractor interactor) => kind switch
        {
            Kind.Scrip => Wallet != null && (amount > 0f || Wallet.Money > 0),
            Kind.Standing => Gang() is { Alive: true },
            _ => TimeManager.Instance != null,
        };

        public void Interact(PlayerInteractor interactor)
        {
            switch (kind)
            {
                case Kind.Scrip:
                    int scrip = Mathf.RoundToInt(amount);
                    if (scrip > 0) Wallet.Add(scrip);
                    else Wallet.TrySpend(Mathf.Min(-scrip, Wallet.Money));
                    break;
                case Kind.Standing:
                    FactionManager.Instance.ChangeStanding(Gang(), amount);
                    break;
                default:
                    TimeManager.Instance.Advance(amount);
                    break;
            }
            Audio.AudioManager.Play2D(Audio.AudioManager.Library?.click, 0.5f);
            // A blink, so the press registers.
            if (face != null) face.enabled = false;
            flashUntil = Time.unscaledTime + 0.08f;
            enabled = true;
        }

        /// <summary>A labelled button on a wall: a glowing pad with its caption above it, facing along the parent's forward.</summary>
        public static DevButton Create(Transform parent, Vector3 localPosition, string caption, Material pad, Kind kind, float amount, string factionId = null)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "DevButton_" + caption;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localScale = new Vector3(0.8f, 0.4f, 0.08f);
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = pad;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            DevLabel.Create(parent, localPosition + new Vector3(0f, 0.36f, 0.05f), caption, 0.02f);
            var button = go.AddComponent<DevButton>();
            button.Setup(kind, amount, factionId);
            return button;
        }
    }

    /// <summary>Floating text for signposting the dev world.</summary>
    public static class DevLabel
    {
        private static Font font;
        private static Material material;

        /// <summary>Text centred at <paramref name="localPosition"/>, reading along the parent's right and facing its forward.</summary>
        public static TextMesh Create(Transform parent, Vector3 localPosition, string text, float size, Color? color = null)
        {
            if (font == null) font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            // The font's own material draws over everything; this one stays behind walls.
            if (material == null)
            {
                var shader = Shader.Find("ThePitt/WorldText");
                material = shader != null ? new Material(shader) { mainTexture = font.material.mainTexture } : font.material;
                // The font's texture is remade when it grows to fit new characters.
                Font.textureRebuilt += f =>
                {
                    if (f == font && material != null) material.mainTexture = f.material.mainTexture;
                };
            }
            var go = new GameObject("Label");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            // TextMesh reads toward its own -Z; turn it to face the parent's forward.
            go.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            var mesh = go.AddComponent<TextMesh>();
            mesh.font = font;
            mesh.text = text;
            mesh.fontSize = 64;
            mesh.characterSize = size;
            mesh.anchor = TextAnchor.MiddleCenter;
            mesh.alignment = TextAlignment.Center;
            mesh.color = color ?? new Color(0.94f, 0.91f, 0.85f);
            go.GetComponent<MeshRenderer>().sharedMaterial = material;
            return mesh;
        }
    }
}
