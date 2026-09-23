/*
 * ContextPruning — 旧 tool result 修剪工具（移植自 Reasonix prune.go）
 *
 * Reasonix 设计要点：
 * - stale tool result 是"免费"的上下文压缩：文件可以重读、命令可以重跑
 * - 先 snip（保留头尾）-> 再需要时 prune（完全替换为占位符）
 * - 修剪发生在 summary compaction 之前，保护 pinned 头部和最近尾部
 */

using System;
using System.Collections.Generic;
using Cysharp.Text;
using UnityEngine.Pool;

namespace LLM.Runtime
{
    public enum ToolResultMaintenanceMode
    {
        Snip,
        Prune
    }

    public struct SnipStrategy
    {
        public int HeadLines;
        public int TailLines;
        public int HeadChars;
        public int TailChars;

        public static SnipStrategy ReadOnly => new()
        {
            HeadLines = 80, TailLines = 12, HeadChars = 10000, TailChars = 2000
        };
    }

    public struct PruneStats
    {
        public int Results;
        public int SavedChars;
    }

    public static class ContextPruning
    {
        private const string SnippedMarker = "[snipped tool result - ";
        private const string PrunedMarker = "[elided tool result - ";
        private const int DefaultMinPruneBytes = 1024;

        public static PruneStats SnipStaleToolResults(
            List<LLMMessage> messages, int tailBudgetTokens = 4096,
            int minPruneBytes = DefaultMinPruneBytes, float tokPerChar = 0.25f)
        {
            return MaintainStaleToolResults(messages, ToolResultMaintenanceMode.Snip,
                tailBudgetTokens, minPruneBytes, tokPerChar);
        }

        public static PruneStats PruneStaleToolResults(
            List<LLMMessage> messages, int tailBudgetTokens = 4096,
            int minPruneBytes = DefaultMinPruneBytes, float tokPerChar = 0.25f)
        {
            return MaintainStaleToolResults(messages, ToolResultMaintenanceMode.Prune,
                tailBudgetTokens, minPruneBytes, tokPerChar);
        }

        public static bool ShouldMaintainToolResult(LLMMessage msg, ToolResultMaintenanceMode mode,
            int minPruneBytes = DefaultMinPruneBytes)
        {
            if (msg == null || msg.Role != "tool") return false;
            if (string.IsNullOrEmpty(msg.Content)) return false;
            if (msg.Content.StartsWith(PrunedMarker)) return false;

            if (mode == ToolResultMaintenanceMode.Snip)
                return msg.Content.Length >= minPruneBytes && !msg.Content.StartsWith(SnippedMarker);

            if (msg.Content.StartsWith(SnippedMarker)) return true;
            return msg.Content.Length >= minPruneBytes;
        }

        public static int FindTailStartByToken(
            List<LLMMessage> messages, int head, int budgetTokens,
            int minRecentKeep = 2, float tokPerChar = 0.25f)
        {
            int start = messages.Count;
            int acc = 0;
            for (int i = messages.Count - 1; i > head; i--)
            {
                int c = (int)((messages[i]?.Content?.Length ?? 0) * tokPerChar);
                if (messages.Count - i > minRecentKeep && acc + c > budgetTokens) break;
                acc += c;
                start = i;
            }
            return start;
        }

        private static PruneStats MaintainStaleToolResults(
            List<LLMMessage> messages, ToolResultMaintenanceMode mode,
            int tailBudgetTokens, int minPruneBytes, float tokPerChar)
        {
            var stats = new PruneStats();
            if (messages == null || messages.Count == 0) return stats;

            // 修复：跳过所有连续的 system 消息（原版只找第一个非 system，可能误伤首轮 user 消息）
            int head = 0;
            for (int i = 0; i < messages.Count; i++)
            {
                if (messages[i] != null && messages[i].Role == "system")
                    head = i + 1;
                else
                    break;
            }

            int tailStart = FindTailStartByToken(messages, head, tailBudgetTokens, 2, tokPerChar);

            for (int i = head; i < tailStart && i < messages.Count; i++)
            {
                var msg = messages[i];
                if (!ShouldMaintainToolResult(msg, mode, minPruneBytes)) continue;

                string original = msg.Content;
                string replacement = mode == ToolResultMaintenanceMode.Prune
                    ? ZString.Format("{0}{1}, {2} chars; re-run the tool if needed again]",
                        PrunedMarker, msg.ToolCallId ?? "unknown", original.Length)
                    : SnipToolResult(msg);

                if (replacement != original)
                {
                    stats.SavedChars += original.Length - replacement.Length;
                    msg.Content = replacement;
                    stats.Results++;
                }
            }

            return stats;
        }

        private static string SnipToolResult(LLMMessage msg)
        {
            string content = msg.Content;
            string[] lines = content.Split('\n');
            var strategy = SnipStrategy.ReadOnly;

            if (lines.Length <= strategy.HeadLines + strategy.TailLines)
            {
                int headChars = Math.Min(strategy.HeadChars, content.Length / 2);
                int tailChars = Math.Min(strategy.TailChars, content.Length / 4);
                string head = content.Length > headChars ? content.Substring(0, headChars) : content;
                string tail = content.Length > tailChars ? content.Substring(content.Length - tailChars) : "";
                int omitted = content.Length - headChars - tailChars;
                return ZString.Format("{0}{1}, {2} chars]\n{3}\n[... {4} chars omitted ...]\n{5}",
                    SnippedMarker, msg.ToolCallId ?? "tool", content.Length, head, omitted, tail);
            }

            var headLines = ListPool<string>.Get();
            var tailLines = ListPool<string>.Get();
            for (int i = 0; i < Math.Min(strategy.HeadLines, lines.Length); i++) headLines.Add(lines[i]);
            for (int i = Math.Max(0, lines.Length - strategy.TailLines); i < lines.Length; i++) tailLines.Add(lines[i]);
            int omittedLines = lines.Length - headLines.Count - tailLines.Count;

            // 中途抛异常只会让这两个实例少回一次池（下次多分配一个），不会把已归还的列表发给别人用
            string snipped = ZString.Concat(
                ZString.Format("{0}{1}, {2} chars]\n", SnippedMarker, msg.ToolCallId ?? "tool", content.Length),
                ZString.Join("\n", headLines),
                ZString.Format("\n[... {0} lines omitted ...]\n", omittedLines),
                ZString.Join("\n", tailLines));
            ListPool<string>.Release(headLines);
            ListPool<string>.Release(tailLines);
            return snipped;
        }
    }
}