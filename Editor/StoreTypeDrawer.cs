/*
┌────────────────────────────┐
│　Description: 存储实现类名的下拉抽屉
│　Remark: 按 attribute 匹配（CustomPropertyDrawer 登记到特性类型），
│　　　　　 只接管带 [StoreTypeSelect] 的字段；配置值丢失时保留原串
│　ClassName: StoreTypeDrawer
└────────────────────────────┘
*/

using System;
using Cysharp.Text;
using LLM.Runtime;
using UnityEditor;
using UnityEngine;

namespace LLM.Editor
{
    [CustomPropertyDrawer(typeof(StoreTypeSelectAttribute))]
    public sealed class StoreTypeDrawer : PropertyDrawer
    {
        private const string k_none = "（不持久化）";

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            var attr = (StoreTypeSelectAttribute)attribute;
            var candidates = StoreTypePicker.Collect(attr.RequiredInterface);

            var values = new string[candidates.Count + 1];
            var labels = new string[candidates.Count + 1];
            values[0] = string.Empty;
            labels[0] = k_none;
            for (int i = 0; i < candidates.Count; i++)
            {
                values[i + 1] = candidates[i].FullName;
                labels[i + 1] = candidates[i].DisplayName;
            }

            var current = property.stringValue ?? string.Empty;
            int index = Array.IndexOf(values, current);

            // 类型被删或改名：多列一项并保留原串，不在下拉里静默改写用户的配置
            // 空白串按"未配置"算（与装配根同一口径），不该渲染成"丢失"
            if (index < 0 && !string.IsNullOrWhiteSpace(current))
            {
                Array.Resize(ref values, values.Length + 1);
                Array.Resize(ref labels, labels.Length + 1);
                values[^1] = current;
                labels[^1] = ZString.Concat(current, "（丢失）");
                index = values.Length - 1;
            }

            if (index < 0) index = 0;

            int picked;
            using (var change = new EditorGUI.ChangeCheckScope())
            {
                picked = EditorGUI.Popup(position, label.text, index, labels);
                if (!change.changed) return;
            }

            property.stringValue = values[picked];
            property.serializedObject.ApplyModifiedProperties();
            EditorUtility.SetDirty(property.serializedObject.targetObject);
        }
    }
}
