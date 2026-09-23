/*
┌────────────────────────────┐
│　Description: Runtime 测试命令白名单特性
│　Remark: 命令必须显式声明稳定 ID
│　ClassName: RuntimeTestCommandAttribute
└────────────────────────────┘
*/

using System;

namespace LLM.Runtime.RuntimeTesting
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class RuntimeTestCommandAttribute : Attribute
    {
        public string Id { get; }
        public string Description { get; }

        public RuntimeTestCommandAttribute(string id, string description = "")
        {
            Id = id;
            Description = description;
        }
    }
}
