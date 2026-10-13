using ThePitt.Interaction;
using UnityEngine;

namespace ThePitt.Properties
{
    /// <summary>The counter at a Watcher outpost: where utility caps and room permits are bought.</summary>
    public class WatcherDesk : MonoBehaviour, IInteractable
    {
        public string GetPrompt(PlayerInteractor interactor) => "Speak to the Watcher · utilities and permits";
        public bool CanInteract(PlayerInteractor interactor) => WatcherUI.Instance != null && !WatcherUI.Instance.IsOpen;
        public void Interact(PlayerInteractor interactor) => WatcherUI.Instance.Open(interactor);
    }
}
