#if UNITY_EDITOR
using System;
using UnityEngine;

[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public class MaterialPassAttribute : PropertyAttribute { }
#endif
