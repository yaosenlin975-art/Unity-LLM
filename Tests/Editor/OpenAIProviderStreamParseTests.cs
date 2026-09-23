/*
┌────────────────────────────┐
│　Description: OpenAIProvider 流式解析测试
│　Remark: 网关把不适用字段写成 JSON null
│　　　　　 时不得抛异常掐断整条流
│　ClassName: OpenAIProviderStreamParseTests
└────────────────────────────┘
*/

using System.Collections.Generic;
using System.Reflection;
using LLM.Runtime;
using NUnit.Framework;

namespace LLM.Tests.Editor
{
    [TestFixture]
    public class OpenAIProviderStreamParseTests
    {
        private static readonly MethodInfo ParseMethod = typeof(OpenAIProvider).GetMethod(
            "ParseStreamChunk", BindingFlags.Instance | BindingFlags.NonPublic);

        private static List<LLMStreamChunk> Parse(string data)
        {
            var provider = new OpenAIProvider("k", "m", "https://example.invalid/v1", "t");
            return (List<LLMStreamChunk>)ParseMethod.Invoke(provider, new object[] { data });
        }

        [Test]
        public void UsageChunk_NullPromptTokensDetails_ReadsZeroInsteadOfThrowing()
        {
            var chunks = Parse("{\"choices\":[],\"usage\":{\"prompt_tokens\":120," +
                               "\"completion_tokens\":8,\"prompt_tokens_details\":null}}");

            Assert.AreEqual(1, chunks.Count);
            Assert.AreEqual(120, chunks[0].PromptTokens);
            Assert.AreEqual(0, chunks[0].CacheHitTokens,
                "JValue(null) 不是 C# null，对它取子节点会抛 InvalidOperationException");
        }

        [Test]
        public void NullUsageAndNullDelta_AreIgnored()
        {
            Assert.AreEqual(0, Parse("{\"choices\":[],\"usage\":null}").Count);
            Assert.AreEqual(0, Parse("{\"choices\":[{\"delta\":null,\"finish_reason\":null}]}").Count);
        }

        [Test]
        public void CachedTokens_StillReadWhenPresent()
        {
            var chunks = Parse("{\"choices\":[],\"usage\":{\"prompt_tokens\":120," +
                               "\"completion_tokens\":8,\"prompt_tokens_details\":{\"cached_tokens\":96}}}");

            Assert.AreEqual(96, chunks[0].CacheHitTokens);
        }

        [Test]
        public void ToolCallDelta_KeepsIndexIdNameAndArguments()
        {
            var chunks = Parse(@"{""choices"":[{""delta"":{""tool_calls"":[{""index"":0,""id"":""c1""," +
                               @"""function"":{""name"":""write_fact"",""arguments"":""{\""key\"":\""a\""}""}}]}," +
                               @"""finish_reason"":""tool_calls""}]}");

            Assert.AreEqual(1, chunks.Count);
            Assert.AreEqual(0, chunks[0].ToolCallDelta.Index);
            Assert.AreEqual("c1", chunks[0].ToolCallDelta.Id);
            Assert.AreEqual("write_fact", chunks[0].ToolCallDelta.Name);
            Assert.AreEqual("{\"key\":\"a\"}", chunks[0].ToolCallDelta.ArgumentsDelta);
            Assert.IsTrue(chunks[0].IsDone, "finish_reason=tool_calls 要落在最后一条上，否则收尾判不到");
        }

        [Test]
        public void ContentWithFinishReason_EmitsOneDoneChunk()
        {
            var chunks = Parse("{\"choices\":[{\"delta\":{\"content\":\"好\"},\"finish_reason\":\"stop\"}]}");

            Assert.AreEqual(1, chunks.Count);
            Assert.AreEqual("好", chunks[0].ContentDelta);
            Assert.IsTrue(chunks[0].IsDone);
        }
    }
}
