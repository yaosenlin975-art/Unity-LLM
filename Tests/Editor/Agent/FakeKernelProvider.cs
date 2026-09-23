/*
 * Agent 测试脚手架 — 脚本化内核 Provider
 * 流式（内核真路径），onChunk 全部内联发出后返回已完成的 UniTask：
 * 整轮同步收敛，EditMode 没有帧循环也能测
 */

using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using LLM.Runtime;

namespace LLM.Tests.Editor.Agent
{
    public readonly struct FakeCall
    {
        public readonly string Name;
        public readonly string Args;

        public FakeCall(string name, string args)
        {
            Name = name;
            Args = args;
        }
    }

    public sealed class FakeKernelProvider : ILLMProvider
    {
        private enum EScriptKind
        {
            Answer,
            ToolCalls,
            Fail
        }

        private readonly struct ScriptEntry
        {
            public readonly EScriptKind Kind;
            public readonly string Content;
            public readonly string Message;
            public readonly FakeCall[] Calls;

            private ScriptEntry(EScriptKind kind, string content, string message, FakeCall[] calls)
            {
                Kind = kind;
                Content = content;
                Message = message;
                Calls = calls;
            }

            public static ScriptEntry Answer(string content)
            {
                return new ScriptEntry(EScriptKind.Answer, content, null, null);
            }

            public static ScriptEntry ToolCalls(string content, FakeCall[] calls)
            {
                return new ScriptEntry(EScriptKind.ToolCalls, content, null, calls);
            }

            public static ScriptEntry Fail(string message)
            {
                return new ScriptEntry(EScriptKind.Fail, null, message, null);
            }
        }

        private readonly List<ScriptEntry> script = new();

        public FakeKernelProvider(string name)
        {
            ProviderName = name;
        }

        public string ProviderName { get; }
        public bool IsAvailable => true;
        public bool SupportsStreaming => true;
        public bool SupportsToolCalling => true;

        public int RequestCount { get; private set; }
        public readonly List<LLMRequest> Requests = new();

        public void EnqueueAnswer(string content)
        {
            script.Add(ScriptEntry.Answer(content));
        }

        public void EnqueueToolCalls(params FakeCall[] calls)
        {
            script.Add(ScriptEntry.ToolCalls(null, calls));
        }

        public void EnqueueContentAndToolCalls(string content, params FakeCall[] calls)
        {
            script.Add(ScriptEntry.ToolCalls(content, calls));
        }

        public void EnqueueFailure(string message)
        {
            script.Add(ScriptEntry.Fail(message));
        }

        /// <summary>换场景时清脚本与请求记录；已排队的流没有回滚，清完再起新轮</summary>
        public void Clear()
        {
            script.Clear();
            Requests.Clear();
            RequestCount = 0;
        }

        public UniTask<LLMResponse> CompleteAsync(LLMRequest request, CancellationToken ct)
            => throw new NotSupportedException("内核走流式，非流式路径不在脚手架范围内");

        public UniTask CompleteStreamAsync(LLMRequest request,
            Action<LLMStreamChunk> onChunk, CancellationToken ct)
        {
            // 真实 HTTP 在 ct 取消时会抛 OCE（dispatcher 只是不吞 OCE，不主动查）：
            // 脚手架必须同款，否则墙钟超时类用例的取消会被同步假流静默吞掉
            ct.ThrowIfCancellationRequested();

            Requests.Add(request);
            int index = RequestCount;
            RequestCount++;

            // 脚本用尽后重复最后一条：模拟模型不肯收口（供往返耗尽类用例复用）
            var entry = script[Math.Min(index, script.Count - 1)];

            if (entry.Kind == EScriptKind.Fail)
            {
                throw new Exception(entry.Message);
            }

            if (entry.Kind == EScriptKind.ToolCalls)
            {
                if (!string.IsNullOrEmpty(entry.Content))
                    onChunk(new LLMStreamChunk(entry.Content));

                for (int i = 0; i < entry.Calls.Length; i++)
                {
                    onChunk(new LLMStreamChunk("", new ToolCallDelta(
                        i, "call_" + i, entry.Calls[i].Name, entry.Calls[i].Args)));
                }
            }
            else
            {
                onChunk(new LLMStreamChunk(entry.Content));
            }

            // usage 独立 chunk（choices 为空），isDone 是最后不带 token 的那条
            onChunk(new LLMStreamChunk("", default, false, 12, 4, 3));
            onChunk(new LLMStreamChunk("", default, true));
            return default;
        }

        public int EstimateTokens(string text) => 0;
    }
}
