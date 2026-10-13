using ThePitt.Core;
using UnityEngine;
using UnityEngine.UI;

namespace ThePitt.UI
{
    /// <summary>Shows the in-game time as a 24-hour HH:MM clock.</summary>
    public class ClockUI : MonoBehaviour
    {
        [SerializeField] private Text label;

        private int shownMinute = -1;

        private void Reset()
        {
            label = GetComponent<Text>();
        }

        private void Update()
        {
            var time = TimeManager.Instance;
            if (time == null || label == null) return;

            int minute = time.Hour * 60 + time.Minute;
            if (minute == shownMinute) return;
            shownMinute = minute;
            label.text = $"{time.Hour:00}:{time.Minute:00}";
        }
    }
}
