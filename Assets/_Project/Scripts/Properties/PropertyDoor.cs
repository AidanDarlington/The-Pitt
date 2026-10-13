using System.Collections;
using ThePitt.Audio;
using ThePitt.Economy;
using ThePitt.Interaction;
using ThePitt.Player;
using UnityEngine;
using UnityEngine.UI;

namespace ThePitt.Properties
{
    /// <summary>
    /// A property's door. The city door buys the place while it's for sale, then takes the player inside;
    /// the door in the room takes them back out. Going through is a quick fade to black and back.
    /// </summary>
    public class PropertyDoor : MonoBehaviour, IInteractable
    {
        [SerializeField] private Property property;
        [Tooltip("The door inside the room, leading back out to the city.")]
        [SerializeField] private bool isExit;

        private static bool travelling;

        /// <summary>The door on the city wall. Called by the generator.</summary>
        public void SetupEntrance(Property owner, Vector3 point, Vector2 normal)
        {
            property = owner;
            isExit = false;
            owner.SetEntrance(point, normal);
        }

        /// <summary>The door in the room. Called by the generator; the player arrives just in front of it.</summary>
        public void SetupExit(Property owner)
        {
            property = owner;
            isExit = true;
            var inward = transform.forward;
            inward.y = 0f;
            owner.SetArrival(transform.position - Vector3.up * (transform.position.y - owner.Centre.y) + inward.normalized * 1.3f,
                Mathf.Atan2(inward.x, inward.z) * Mathf.Rad2Deg);
        }

        public string GetPrompt(PlayerInteractor interactor)
        {
            if (property == null) return null;
            if (isExit) return "Leave · back to the street";
            if (property.Owned) return $"Enter {property.DisplayName}";
            var wallet = interactor.GetComponent<PlayerWallet>();
            int missing = wallet != null ? property.Price - wallet.Money : 0;
            return missing > 0
                ? $"{property.DisplayName} · for sale, {property.Price} scrip (need {missing} more)"
                : $"Buy {property.DisplayName} · {property.Price} scrip";
        }

        public bool CanInteract(PlayerInteractor interactor)
        {
            if (property == null || travelling) return false;
            if (isExit || property.Owned) return true;
            var wallet = interactor.GetComponent<PlayerWallet>();
            return wallet != null && wallet.Money >= property.Price;
        }

        public void Interact(PlayerInteractor interactor)
        {
            if (!isExit && !property.Owned)
            {
                if (property.TryBuy(interactor.GetComponent<PlayerWallet>()))
                {
                    AudioManager.Play2D(AudioManager.Library?.craftDone, 0.7f);
                    Production.Business.Of(property)?.AnnounceBought();
                }
                return;
            }
            AudioManager.Play2D(AudioManager.Library?.click, 0.5f);
            if (!isExit) Property.LastVisited = property;
            var to = isExit ? property.OutsidePoint : property.InsidePoint;
            float yaw = isExit ? property.OutsideYaw : property.InsideYaw;
            interactor.StartCoroutine(Travel(interactor.transform, to, yaw));
        }

        // ---------------------------------------------------------------- going through

        private static Image fade;

        private static IEnumerator Travel(Transform player, Vector3 to, float yaw)
        {
            travelling = true;
            var controller = player.GetComponent<FirstPersonController>();
            if (controller != null) controller.InputEnabled = false;
            var image = Fade();

            yield return FadeTo(image, 1f, 0.18f);
            var cc = player.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;
            player.position = to;
            if (controller != null) controller.SetLook(yaw, 0f);
            else player.rotation = Quaternion.Euler(0f, yaw, 0f);
            if (cc != null) cc.enabled = true;
            yield return null;
            yield return FadeTo(image, 0f, 0.25f);

            if (controller != null) controller.InputEnabled = true;
            travelling = false;
        }

        private static IEnumerator FadeTo(Image image, float alpha, float seconds)
        {
            float from = image.color.a;
            for (float t = 0f; t < seconds; t += Time.unscaledDeltaTime)
            {
                image.color = new Color(0f, 0f, 0f, Mathf.Lerp(from, alpha, t / seconds));
                yield return null;
            }
            image.color = new Color(0f, 0f, 0f, alpha);
        }

        /// <summary>A full-screen black overlay, made on first use.</summary>
        private static Image Fade()
        {
            if (fade != null) return fade;
            var canvasGo = new GameObject("DoorFade", typeof(Canvas));
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 200;
            var imageGo = new GameObject("Black", typeof(RectTransform), typeof(Image));
            imageGo.transform.SetParent(canvasGo.transform, false);
            var rt = (RectTransform)imageGo.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            fade = imageGo.GetComponent<Image>();
            fade.raycastTarget = false;
            fade.color = new Color(0f, 0f, 0f, 0f);
            return fade;
        }

        // Statics survive between play sessions when domain reload is off in the editor.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            travelling = false;
            fade = null;
        }
    }
}
