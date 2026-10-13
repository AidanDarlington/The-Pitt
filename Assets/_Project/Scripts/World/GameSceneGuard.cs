using ThePitt.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ThePitt.World
{
    /// <summary>
    /// The game scene only works on top of a built city (the World scene loads it). Opened on its own (pressing Play
    /// on it in the editor), it switches everything else off before it can wake up and goes through the World scene.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public class GameSceneGuard : MonoBehaviour
    {
        private void Awake()
        {
            if (PittGreybox.Current != null) return;
            foreach (var root in gameObject.scene.GetRootGameObjects())
                if (root != gameObject) root.SetActive(false);
            SceneManager.LoadScene(WorldRequest.WorldScene);
        }
    }
}
