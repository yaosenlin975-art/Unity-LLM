/*
┌────────────────────────────┐
│　Description: Runtime 性能采样摘要
│　Remark: 纯计算，空窗口明确不可用
│　ClassName: RuntimePerformanceSummary
└────────────────────────────┘
*/

using System;
using System.Collections.Generic;

namespace LLM.Runtime.RuntimeTesting
{
    public readonly struct RuntimePerformanceSummary
    {
        public bool Available { get; }
        public int Count { get; }
        public float Average { get; }
        public float P95 { get; }
        public float P99 { get; }
        public float Max { get; }

        private RuntimePerformanceSummary(bool available, int count, float average, float p95, float p99, float max)
        {
            Available = available;
            Count = count;
            Average = average;
            P95 = p95;
            P99 = p99;
            Max = max;
        }

        public static RuntimePerformanceSummary Calculate(IReadOnlyList<float> samples)
        {
            if (samples == null || samples.Count == 0)
                return new RuntimePerformanceSummary(false, 0, 0f, 0f, 0f, 0f);

            var sorted = new float[samples.Count];
            float total = 0f;
            for (int i = 0; i < samples.Count; i++)
            {
                sorted[i] = samples[i];
                total += sorted[i];
            }

            Array.Sort(sorted);
            return new RuntimePerformanceSummary(
                true,
                sorted.Length,
                total / sorted.Length,
                Percentile(sorted, 0.95f),
                Percentile(sorted, 0.99f),
                sorted[sorted.Length - 1]);
        }

        private static float Percentile(float[] sorted, float percentile)
        {
            int rank = (int)Math.Ceiling(sorted.Length * percentile);
            int index = Math.Max(0, Math.Min(sorted.Length - 1, rank - 1));
            return sorted[index];
        }
    }

    public sealed class RuntimePerformanceCapture
    {
        private readonly List<float> samples = new();

        public bool IsCapturing { get; private set; }

        public int Count => samples.Count;

        public void Start()
        {
            samples.Clear();
            IsCapturing = true;
        }

        public void AddSample(float value)
        {
            if (IsCapturing) samples.Add(value);
        }

        public RuntimePerformanceSummary Stop()
        {
            IsCapturing = false;
            return RuntimePerformanceSummary.Calculate(samples);
        }
    }
}
