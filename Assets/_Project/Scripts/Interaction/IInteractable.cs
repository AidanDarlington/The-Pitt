namespace ThePitt.Interaction
{
    /// <summary>Anything the player can look at and press Interact on.</summary>
    public interface IInteractable
    {
        /// <summary>Text shown after the key hint, e.g. "Pick up Scrap Metal".</summary>
        string GetPrompt(PlayerInteractor interactor);

        /// <summary>False greys out the prompt (e.g. inventory full, plant not ready).</summary>
        bool CanInteract(PlayerInteractor interactor);

        void Interact(PlayerInteractor interactor);
    }
}
