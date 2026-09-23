/*
┌────────────────────────────┐
│　Description: 在线 LLM 会话
│　Remark: 系统提示词/上下文注入 + 流式
│　　　　　 + Tool Calling 多轮循环 + 历史压缩
│　ClassName: LLMSession
└────────────────────────────┘
*/

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Cysharp.Text;
using Cysharp.Threading.Tasks;
using LLM.Runtime.Agent;

namespace LLM.Runtime
{
    /// <summary>
    /// 一次对话会话：持有历史、注入内容与压缩状态。
    /// 同一会话同一时刻只允许一个进行中的请求（异步延续都在主线程，故不加锁）。
    /// </summary>
    public class LLMSession
    {
        private readonly StringBuilder answerBuilder = new();
        private readonly List<string> contextBlocks = new();
        private readonly List<LLMTool> extraTools = new();
        private readonly ContextManager contextManager;
        private readonly AgentToolCallAssembler assembler = new();

        private Action<LLMStreamChunk> onChunk;
        private int promptCharCount;
        private bool busy;

        public string SessionId { get; }

        public string SystemPrompt { get; set; }

        public float Temperature { get; set; } = 0.8f;

        public int MaxTokens { get; set; } = 1024;

        /// <summary>关闭后不向模型声明任何工具，ExtraTools 一并屏蔽</summary>
        public bool EnableTools { get; set; } = true;

        /// <summary>tool_call 的落地处。默认同步跑 ToolRegistry；内核换成带循环检测与异步动作的执行器</summary>
        public IAgentToolExecutor ToolExecutor { get; set; } = new SyncAgentToolRegistryExecutor();

        /// <summary>
        /// 逐工具可见性门。null = 全部放行。声明期同时过滤注册表工具与 ExtraTools，
        /// 具体控制面由宿主（AgentHost）实现，会话与内核不感知宿主类型。
        /// </summary>
        public IAgentToolGate ToolGate { get; set; }

        /// <summary>
        /// 注册表之外的工具声明（[AgentAction] 走这里，绝不能进 ToolRegistry）。
        /// 可直接 Add/Clear，不整体赋值以免和 BuildRequest 的复制语义打架。
        /// </summary>
        public List<LLMTool> ExtraTools => extraTools;

        public ContextManager Context => contextManager;

        public LLMSession(string sessionId, string systemPrompt = null,
            CompressionConfig_SO compressionConfig = null)
        {
            SessionId = sessionId;
            SystemPrompt = systemPrompt;
            contextManager = new ContextManager(compressionConfig, sessionId);
        }

        /// <summary>
        /// 注入一块长期上下文（世界信息、角色设定、当前状态等）。
        /// 按调用顺序拼在系统提示词之后，ClearContext 前每轮请求都带上。
        /// </summary>
        public void AddContext(string block)
        {
            if (string.IsNullOrEmpty(block)) return;
            contextBlocks.Add(block);
        }

        public void ClearContext()
        {
            contextBlocks.Clear();
        }

        /// <summary>
        /// 发起一轮问答。传入 onChunkCallback 走流式，每个增量回调一次；
        /// 工具往返轮次里的文本同样累积，最终作为完整回答返回并写入历史。
        /// writeHistory=false 时不落历史——内核要在确认代际之后自己决定写不写。
        /// ADR-023：无工具往返次数总闸；防循环见 LoopGuard NameCap/L2，成本见墙钟。
        /// </summary>
        public async UniTask<string> AskAsync(string userText,
            Action<LLMStreamChunk> onChunkCallback = null,
            string ephemeralContext = null,
            bool writeHistory = true,
            CancellationToken ct = default)
        {
            if (busy)
                throw new InvalidOperationException(
                    ZString.Format("[LLMSession] {0} 正在处理上一个请求", SessionId));

            busy = true;
            onChunk = onChunkCallback;
            answerBuilder.Clear();

            try
            {
                var messages = contextManager.GetHistoryMessages();
                messages.Add(new LLMMessage("user", userText ?? ""));

                string answer = await RunToolLoopAsync(messages, ephemeralContext, ct);
                if (writeHistory)
                    contextManager.AddRound(userText, answer);
                return answer;
            }
            finally
            {
                busy = false;
                onChunk = null;
            }
        }

        private async UniTask<string> RunToolLoopAsync(
            List<LLMMessage> messages, string ephemeralContext, CancellationToken ct)
        {
            for (;;)
            {
                var toolCalls = await SendRoundAsync(messages, ephemeralContext, ct);
                if (toolCalls.Count == 0)
                    return answerBuilder.ToString();

                if (await AppendToolResultsAsync(messages, toolCalls, ct))
                    return answerBuilder.ToString();
            }
        }

        /// <summary>发一轮请求，返回已拼接完整的工具调用；为空表示模型已直接作答</summary>
        private async UniTask<List<LLMToolCall>> SendRoundAsync(
            List<LLMMessage> messages, string ephemeralContext, CancellationToken ct)
        {
            assembler.Reset();
            var request = BuildRequest(messages, ephemeralContext);
            var dispatcher = LLMDispatcher.GetInstance();

            if (onChunk == null)
            {
                var response = await dispatcher.EnqueueAsync(request, ct);
                if (!string.IsNullOrEmpty(response.Content))
                    answerBuilder.Append(response.Content);

                contextManager.FeedActualTokenCount(response.PromptTokens, promptCharCount);
                return response.ToolCalls;
            }

            await dispatcher.EnqueueStreamAsync(request, HandleChunk, ct);
            return assembler.GetCompletedCalls();
        }

        private void HandleChunk(LLMStreamChunk chunk)
        {
            if (!string.IsNullOrEmpty(chunk.ContentDelta))
            {
                answerBuilder.Append(chunk.ContentDelta);
                onChunk?.Invoke(chunk);
            }

            var delta = chunk.ToolCallDelta;
            if (delta.Id != null || delta.Name != null || delta.ArgumentsDelta != null)
                assembler.Append(delta);

            if (chunk.PromptTokens > 0 || chunk.CompletionTokens > 0)
                contextManager.FeedActualTokenCount(chunk.PromptTokens, promptCharCount);

            // done 标记只在没有正文的那条 chunk 上补投：带 content 的收尾 chunk 上面已经投过一次，
            // 再投一次会让消费端（内核攒字、宿主刷气泡）把最后一段文字重复追加
            if (chunk.IsDone && string.IsNullOrEmpty(chunk.ContentDelta))
                onChunk?.Invoke(chunk);
        }

        private LLMRequest BuildRequest(List<LLMMessage> messages, string ephemeralContext)
        {
            var request = new LLMRequest
            {
                SessionId = SessionId,
                SystemPrompt = SystemPrompt,
                Messages = messages,
                Temperature = Temperature,
                MaxTokens = MaxTokens
            };

            for (int i = 0; i < contextBlocks.Count; i++)
                request.AddContextBlock(contextBlocks[i]);

            if (!string.IsNullOrEmpty(ephemeralContext))
                request.AddContextBlock(ephemeralContext);

            if (EnableTools)
            {
                // 全局静态工具、成员/动作声明统一过门并按名去重；
                // ToLLMTools 返回缓存 List，过滤时重建新 List，避免污染缓存
                var declared = new List<LLMTool>();
                var seen = new HashSet<string>();

                var registryTools = AgentToolRegistry.ToLLMTools();
                for (int i = 0; i < registryTools.Count; i++)
                {
                    var tool = registryTools[i];
                    if (!IsToolAllowed(tool.Function.Name)) continue;
                    if (!seen.Add(tool.Function.Name)) continue;
                    declared.Add(tool);
                }

                for (int i = 0; i < extraTools.Count; i++)
                {
                    var tool = extraTools[i];
                    if (!IsToolAllowed(tool.Function.Name)) continue;
                    if (!seen.Add(tool.Function.Name)) continue;
                    declared.Add(tool);
                }

                request.Tools = declared;
            }

            ContextPruning.SnipStaleToolResults(messages, contextManager.TailTokenBudget);
            promptCharCount = CountPromptChars(request, out int systemChars);
            contextManager.SetFixedOverheadChars(systemChars);
            return request;
        }

        private bool IsToolAllowed(string toolId)
        {
            return ToolGate == null || ToolGate.IsToolEnabled(toolId);
        }

        /// <summary>回填同批 tool_call 的结果；返回 true 表示执行器判了 AbortTurn，本轮就此收尾</summary>
        private async UniTask<bool> AppendToolResultsAsync(
            List<LLMMessage> messages, List<LLMToolCall> toolCalls, CancellationToken ct)
        {
            messages.Add(new LLMMessage("assistant", "")
            {
                ToolCalls = toolCalls.ToArray()
            });

            for (int i = 0; i < toolCalls.Count; i++)
            {
                var call = toolCalls[i];
                var result = await ToolExecutor.ExecuteAsync(call.Name, call.Arguments, ct);

                // 整轮已被判作废，同批剩余调用一律不执行也不回填（回填了也没有下一次请求去消费）
                if (result.AbortTurn) return true;

                // 注入点是公开 API，Content 为 null 会让 provider 收到 "content": null 直接 400
                messages.Add(new LLMMessage("tool", result.Content ?? "", call.Id));
            }

            return false;
        }

        private static int CountPromptChars(LLMRequest request, out int systemChars)
        {
            systemChars = request.BuildSystemContent()?.Length ?? 0;
            int chars = systemChars;

            for (int i = 0; i < request.Messages.Count; i++)
                chars += (request.Messages[i]?.Content?.Length ?? 0) + 4;

            return chars;
        }
    }
}
