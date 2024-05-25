using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace jKnepel.SimpleUnityNetworking.Logging
{
    [CreateAssetMenu(fileName = "LoggerConfiguration", menuName = "SimpleUnityNetworking/LoggerConfiguration")]
    public class LoggerConfiguration : ScriptableObject
    {
        public LoggerSettings Settings = new();

        public Logger GetLogger()
        {
            return new(new()
            {
                PrintToConsole = Settings.PrintToConsole,
                PrintLog = Settings.PrintLog,
                PrintWarning = Settings.PrintWarning,
                PrintError = Settings.PrintError
            });
        }
    }
    
#if UNITY_EDITOR
    [CustomEditor(typeof(LoggerConfiguration), true)]
    public class LoggerConfigurationEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(serializedObject.FindProperty("Settings"), new GUIContent("Settings:"));
            EditorGUI.indentLevel--;
            
            serializedObject.ApplyModifiedProperties();
        }
    }
#endif
}
