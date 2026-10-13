using ThePitt.Interaction;
using UnityEngine;
using UnityEngine.UI;

namespace ThePitt.UI
{
    /// <summary>Crosshair plus the "[E] Pick up ..." prompt under it.</summary>
    public class InteractionPromptUI : MonoBehaviour
    {
        [SerializeField] private PlayerInteractor interactor;
        [SerializeField] private Text label;
        [SerializeField] private Graphic crosshair;
        [SerializeField] private string keyHint = "[E]";
        [SerializeField] private Color activeColor = new Color(0.92f, 0.9f, 0.86f);
        [SerializeField] private Color disabledColor = new Color(0.55f, 0.52f, 0.5f);

        private void OnEnable()
        {
            if (interactor != null) interactor.PromptChanged += Show;
            Show(null, false);
        }

        private void OnDisable()
        {
            if (interactor != null) interactor.PromptChanged -= Show;
        }

        private void Show(string prompt, bool canInteract)
        {
            bool visible = !string.IsNullOrEmpty(prompt);
            label.enabled = visible;
            if (visible)
            {
                label.text = canInteract ? $"{keyHint} {prompt}" : prompt;
                label.color = canInteract ? activeColor : disabledColor;
            }
            if (crosshair != null) crosshair.color = visible && canInteract ? activeColor : new Color(1f, 1f, 1f, 0.5f);
        }
    }
}
