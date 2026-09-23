/*
┌────────────────────────────┐
│　Description: 存储实现选型标记特性
│　Remark: PropertyAttribute 是 UnityEngine 的类型，所以特性留在
│　　　　　 运行期程序集，不需要 #if UNITY_EDITOR；抽屉在 LLM.Editor
│　ClassName: StoreTypeSelectAttribute
└────────────────────────────┘
*/

using System;
using UnityEngine;

namespace LLM.Runtime
{
    /// <summary>标在"存实现类 FullName"的字符串字段上，由 LLM.Editor 的抽屉画成下拉。</summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class StoreTypeSelectAttribute : PropertyAttribute
    {
        /// <summary>该字段允许选择的实现必须实现的接口。</summary>
        public Type RequiredInterface { get; }

        public StoreTypeSelectAttribute(Type requiredInterface)
        {
            RequiredInterface = requiredInterface;
        }
    }
}
