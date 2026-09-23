/*
┌────────────────────────────┐
│　Description: 工具声明特性
│　Remark: 附带循环检测阈值与幂等标记
│　ClassName: ToolAttribute
└────────────────────────────┘
*/

using System;

namespace LLM.Runtime
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class AgentToolAttribute : Attribute
    {
        public string Name { get; }
        public string Description { get; }

        /// <summary>
        /// 同一签名重复调用多少次后开始告警。
        /// 0 = 继承 profile 的 GlobalRepeatLimit；-1 = 关闭该工具的 L2 检测。
        /// </summary>
        public int RepeatLimit { get; set; } = 0;

        /// <summary>
        /// 同工具名本轮次数上限（换参也计，NameCap）。0 = 继承 PerNameToolCallLimit；-1 = 关闭。
        /// 与 RepeatLimit（同参）正交。
        /// </summary>
        public int NameRepeatLimit { get; set; } = 0;

        /// <summary>
        /// [Tool] 默认按无副作用对待。标 false 后同一签名第二次起一律拦截，
        /// 阈值恒为 1，忽略 RepeatLimit 与 GlobalRepeatLimit，且不提供关闭开关。
        /// </summary>
        public bool Idempotent { get; set; } = true;

        public AgentToolAttribute(string name = null, string description = "")
        {
            Name = name;
            Description = description;
        }
    }
}
