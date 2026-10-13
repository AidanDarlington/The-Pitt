#if UNITY_EDITOR
using System.Collections;
using System.IO;
using ThePitt.Player;
using UnityEngine;

namespace ThePitt.Dev
{
    /// <summary>
    /// Editor-only: records the menu's background flythrough. Added to the game scene in play mode, it takes over
    /// the main camera, hides the HUD and the player, and flies one slow circle above the rooftops, saving one
    /// frame per game frame at a fixed rate (so flicker and district fades play at true speed). The frames are
    /// then encoded into a looping video for the menu. The first and last frames meet, so it loops seamlessly.
    /// </summary>
    public class MenuFlythroughCapture : MonoBehaviour
    {
        public string outputFolder;
        public int framesPerSecond = 30;
        public float seconds = 45f;
        [Tooltip("Radius of the circle flown around the middle of the city.")]
        public float radius = 105f;
        [Tooltip("Height above the tallest roof under the path.")]
        public float clearance = 10f;
        [Tooltip("Fly at exactly this height instead (0 = clear the roofs automatically).")]
        public float fixedHeight;
        [Tooltip("How far ahead along the circle the camera looks, in radians, and how far in toward the middle.")]
        public float lookAhead = 0.55f;
        public float lookInward = 0.45f;

        public bool Done { get; private set; }
        public int FramesWritten { get; private set; }

        private Camera cam;
        private float height;
        private int frame;
        private int total;

        private void Start()
        {
            cam = Camera.main;
            // The player stays put and out of shot; the camera flies free.
            var fpc = FindAnyObjectByType<FirstPersonController>();
            if (fpc != null)
            {
                fpc.enabled = false;
                foreach (var r in fpc.GetComponentsInChildren<Renderer>()) r.enabled = false;
            }
            cam.transform.SetParent(null, true);
            foreach (var canvas in FindObjectsByType<Canvas>(FindObjectsInactive.Exclude))
                if (canvas.isRootCanvas) canvas.enabled = false;

            // High enough to clear every roof on the circle.
            float top = 0f;
            for (int i = 0; i < 360; i++)
            {
                float a = i * Mathf.Deg2Rad;
                var p = new Vector3(Mathf.Cos(a) * radius, 400f, Mathf.Sin(a) * radius);
                if (Physics.SphereCast(p, 4f, Vector3.down, out var hit, 500f)) top = Mathf.Max(top, hit.point.y);
            }
            height = fixedHeight > 0f ? fixedHeight : top + clearance;

            total = Mathf.RoundToInt(seconds * framesPerSecond);
            Directory.CreateDirectory(outputFolder);
            Time.captureFramerate = framesPerSecond;
            Pose(0f);
            StartCoroutine(Record());
        }

        private void LateUpdate()
        {
            if (!Done) Pose((float)frame / total);
        }

        private void Pose(float t)
        {
            float a = t * Mathf.PI * 2f;
            var pos = new Vector3(Mathf.Cos(a) * radius, height, Mathf.Sin(a) * radius);
            float b = a + lookAhead;
            var target = new Vector3(Mathf.Cos(b) * radius * (1f - lookInward), 0f, Mathf.Sin(b) * radius * (1f - lookInward));
            cam.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(target - pos));
        }

        private IEnumerator Record()
        {
            // A couple of seconds first, for the district colours and lamps to settle.
            for (int i = 0; i < framesPerSecond * 2; i++) yield return null;
            while (frame < total)
            {
                yield return new WaitForEndOfFrame();
                var shot = ScreenCapture.CaptureScreenshotAsTexture();
                File.WriteAllBytes(Path.Combine(outputFolder, $"{frame:0000}.jpg"), shot.EncodeToJPG(92));
                Destroy(shot);
                frame++;
                FramesWritten = frame;
            }
            Time.captureFramerate = 0;
            Done = true;
        }
    }
}
#endif
