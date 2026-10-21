using ThePitt.Factions;
using UnityEngine;

namespace ThePitt.Dev
{
    /// <summary>
    /// The dev world's rep buttons: a column per gang with its name, +10 and -10. The gangs only exist once the game
    /// scene has loaded on top of the city, so the buttons go up then, built in whatever gangs the game has.
    /// Laid out along the board's right, facing its forward.
    /// </summary>
    public class DevRepBoard : MonoBehaviour
    {
        [SerializeField] private Material plus;
        [SerializeField] private Material minus;
        [SerializeField, Min(0.5f)] private float spacing = 1.9f;

        public void Setup(Material plusPad, Material minusPad)
        {
            plus = plusPad;
            minus = minusPad;
        }

        private void Update()
        {
            var manager = FactionManager.Instance;
            if (manager == null || manager.Factions.Count == 0) return;
            float x = 0f;
            foreach (var f in manager.Factions)
            {
                if (f.IsPlayer || f.definition == null) continue;
                DevLabel.Create(transform, new Vector3(x, 2.35f, 0.05f), f.Name.ToUpperInvariant(), 0.025f, f.definition.Color);
                DevButton.Create(transform, new Vector3(x, 1.6f, 0f), "REP +10", plus, DevButton.Kind.Standing, 10f, f.definition.name);
                DevButton.Create(transform, new Vector3(x, 0.95f, 0f), "REP -10", minus, DevButton.Kind.Standing, -10f, f.definition.name);
                x += spacing;
            }
            enabled = false;
        }
    }
}
