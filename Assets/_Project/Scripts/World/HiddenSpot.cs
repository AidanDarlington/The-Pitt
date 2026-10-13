using UnityEngine;

namespace ThePitt.World
{
    /// <summary>
    /// Marks a hidden back room (reached through a hole in an alley wall). Placeholder for
    /// black markets, dead drops and stashes.
    /// </summary>
    public class HiddenSpot : MonoBehaviour
    {
        [SerializeField] private string district;
        [SerializeField] private Vector3 size = new(4f, 3f, 4f);

        public string District => district;

        public void Setup(string districtName, Vector3 roomSize)
        {
            district = districtName;
            size = roomSize;
        }

        private void OnDrawGizmos()
        {
            Gizmos.color = new Color(1f, 0.2f, 0.8f, 0.6f);
            Gizmos.DrawWireCube(transform.position + Vector3.up * size.y / 2f, size);
        }
    }
}
