#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

[CustomPropertyDrawer(typeof(MaterialPassAttribute))]
public class MaterialPassAttributeDrawer : PropertyDrawer
{
    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        string materialPropertyPath = property.propertyPath.Substring(0, property.propertyPath.LastIndexOf(".") + 1) + "passMaterial";
        SerializedProperty materialProperty = property.serializedObject.FindProperty(materialPropertyPath);
        Material material = materialProperty.objectReferenceValue as Material;

        List<string> selectablePasses;
        bool isMaterialValid = material != null;
        selectablePasses = isMaterialValid ? GetPassIndexStringEntries(material) : new List<string>() { "No material" };
        property.intValue = EditorGUI.Popup(position, "Pass Index", property.intValue, selectablePasses.ToArray());
        property.serializedObject.ApplyModifiedProperties();
    }

    private List<string> GetPassIndexStringEntries(Material material)
    {
        List<string> passIndexEntries = new List<string>();
        for (int i = 0; i < material.passCount; ++i)
        {
            // "Name of a pass (index)" - "PassAlpha (1)"
            string entry = $"{material.GetPassName(i)} ({i})";
            passIndexEntries.Add(entry);
        }

        return passIndexEntries;
    }
}
#endif
