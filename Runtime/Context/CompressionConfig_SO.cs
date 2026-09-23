/*
┌────────────────────────────┐
│　Description: 上下文阈值与压缩配置
│　Remark: 轮数模式 / token 模式二选一，
│　　　　　 token 模式由 ContextWindowTokens>0 启用
│　ClassName: CompressionConfig_SO
└────────────────────────────┘
*/

using UnityEngine;

namespace LLM.Runtime
{
    [CreateAssetMenu(fileName = "CompressionConfig_SO", menuName = "LLM/CompressionConfig_SO")]
    public class CompressionConfig_SO : ScriptableObject
    {
        [Header("上下文窗口")]
        [Tooltip("上下文窗口 token 上限（0=不限制，改用轮数模式）")]
        public int ContextWindowTokens = 0;

        [Tooltip("滑动窗口保留轮数（仅在轮数模式下生效）")]
        [Range(3, 20)]
        public int SlidingWindowRounds = 6;

        [Tooltip("压缩触发阈值倍数（轮数模式）")]
        [Range(1.5f, 3f)]
        public float CompressThresholdMultiplier = 2f;

        [Header("三级阈值（token 模式）")]
        [Tooltip("软阈值比例：只报告上下文增长，不触发压缩")]
        [Range(0.3f, 0.7f)]
        public float SoftCompactRatio = 0.5f;

        [Tooltip("触发比例：估算 prompt token 达到此比例时压缩")]
        [Range(0.6f, 0.85f)]
        public float CompactRatio = 0.8f;

        [Tooltip("强制比例：达到此比例时即使折叠不经济也压缩")]
        [Range(0.85f, 0.95f)]
        public float ForceCompactRatio = 0.9f;

        [Tooltip("尾部上限：保留的最近对话不超过窗口的此比例")]
        [Range(0.3f, 0.7f)]
        public float CompactTargetRatio = 0.5f;

        [Header("尾部保留")]
        [Tooltip("最近对话的 token 预算（从新到旧逐轮累加）")]
        public int TailTokenBudget = 4096;

        [Tooltip("最少保留最近 N 轮对话")]
        [Range(1, 5)]
        public int MinRecentKeep = 2;

        [Tooltip("至少累积 N 轮才允许压缩")]
        [Range(2, 5)]
        public int MinCompactRounds = 2;

        [Tooltip("折叠区 token 低于此值则跳过压缩，避免无效折叠")]
        public int MinFoldTokens = 200;

        [Header("LLM 摘要")]
        [Tooltip("是否调用 LLM 生成摘要（关闭则使用机械降级）")]
        public bool EnableLLMCompression = true;

        [Tooltip("生成摘要用哪个 Provider 注册名（留空=沿用默认 Provider）。想用小模型压缩就填它的名字")]
        public string CompressionProviderName = "";

        [Tooltip("摘要最大 Token 数")]
        [Range(200, 800)]
        public int CompressionMaxTokens = 500;

        [Tooltip("摘要请求超时（秒）")]
        [Range(5f, 30f)]
        public float CompressionTimeoutSeconds = 15f;

        [Tooltip("摘要温度（越低越确定）")]
        [Range(0f, 1f)]
        public float CompressionTemperature = 0.3f;

        [Header("修剪与归档")]
        [Tooltip("旧回复超过此字符数才做占位修剪")]
        public int MinPruneBytes = 512;

        [Tooltip("压缩前是否把原始轮次归档到 persistentDataPath")]
        public bool EnableArchive = true;

        [Tooltip("输出压缩详细日志")]
        public bool EnableCompressionDebug = false;

        public int CompressThreshold => Mathf.CeilToInt(SlidingWindowRounds * CompressThresholdMultiplier);

        public bool UseTokenMode => ContextWindowTokens > 0;
    }
}
