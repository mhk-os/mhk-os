using Newtonsoft.Json.Linq;
using UnityEditor;

namespace MHK.Editor.Sdk
{
    /// <summary>
    /// Serialized properties as JSON and back, exactly as Unity stores them (strings, numbers, booleans, arrays and
    /// nested structs). The project settings kit carries player settings this way.
    /// </summary>
    public static class SerializedPropertyJson
    {
        /// <summary>The value of <paramref name="p"/> and everything under it.</summary>
        public static JToken ToJson(SerializedProperty p)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.String: return p.stringValue;
                case SerializedPropertyType.Boolean: return p.boolValue;
                case SerializedPropertyType.Integer: return p.longValue;
                case SerializedPropertyType.Enum: return p.intValue;
                case SerializedPropertyType.Float: return p.doubleValue;
            }
            if (p.isArray)
            {
                var array = new JArray();
                for (var i = 0; i < p.arraySize; i++) array.Add(ToJson(p.GetArrayElementAtIndex(i)));
                return array;
            }
            var obj = new JObject();
            var it = p.Copy();
            var end = p.GetEndProperty();
            if (!it.Next(true)) return obj;
            do
            {
                if (SerializedProperty.EqualContents(it, end)) break;
                obj[it.name] = ToJson(it);
            } while (it.Next(false));
            return obj;
        }

        /// <summary>Sets <paramref name="p"/> (and everything under it) to <paramref name="value"/>. Unknown struct fields are skipped.</summary>
        public static void FromJson(SerializedProperty p, JToken value)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.String: p.stringValue = (string)value; return;
                case SerializedPropertyType.Boolean: p.boolValue = (bool)value; return;
                case SerializedPropertyType.Integer: p.longValue = (long)value; return;
                case SerializedPropertyType.Enum: p.intValue = (int)value; return;
                case SerializedPropertyType.Float: p.doubleValue = (double)value; return;
            }
            if (p.isArray)
            {
                var array = (JArray)value;
                p.arraySize = array.Count;
                for (var i = 0; i < array.Count; i++) FromJson(p.GetArrayElementAtIndex(i), array[i]);
                return;
            }
            foreach (var field in (JObject)value)
            {
                var child = p.FindPropertyRelative(field.Key);
                if (child != null) FromJson(child, field.Value);
            }
        }
    }
}
