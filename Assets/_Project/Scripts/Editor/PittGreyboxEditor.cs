using ThePitt.World.Generation;
using UnityEditor;
using UnityEngine;

namespace ThePitt.World.EditorTools
{
    /// <summary>
    /// Inspector for the greybox settings. The game builds its own city from each character's seed; Generate here
    /// only shows a seed in the editor (not saved with the scene). Refresh Assets remakes the shared materials.
    /// </summary>
    [CustomEditor(typeof(PittGreybox))]
    public class PittGreyboxEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var greybox = (PittGreybox)target;
            bool playing = EditorApplication.isPlayingOrWillChangePlaymode;

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(playing))
            {
                if (GUILayout.Button("Generate (preview)", GUILayout.Height(30))) Preview(greybox, greybox.seed);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("New Seed + Generate"))
                    {
                        Undo.RecordObject(greybox, "New Greybox Seed");
                        Preview(greybox, Random.Range(1, 999999));
                    }
                    if (GUILayout.Button("Clear")) PittGreyboxGenerator.Clear(greybox);
                }
                if (GUILayout.Button("Refresh Assets")) PittWorldAssets.Refresh(greybox);
            }
        }

        private static void Preview(PittGreybox greybox, int seed)
        {
            PittWorldAssets.Refresh(greybox);
            PittGreyboxGenerator.Build(greybox, seed);
            EditorUtility.SetDirty(greybox);
        }
    }
}
