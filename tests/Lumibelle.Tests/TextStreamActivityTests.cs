using System.ClientModel.Primitives;
using lumibelle.Services.AI;
using Microsoft.Extensions.AI;
using OpenAI.Chat;

namespace Lumibelle.Tests;

public class TextStreamActivityTests
{
    [Theory]
    [InlineData("reasoning", "Thinking", true)]
    [InlineData("reasoning_content", "Thinking", true)]
    [InlineData("reasoning", "", false)]
    [InlineData("content", "Hello", false)]
    public void SdkExtensionFieldsAreActivityOnly(string field, string text, bool expected)
    {
        var json = "{\"id\":\"test\",\"object\":\"chat.completion.chunk\",\"created\":123,\"model\":\"test\",\"choices\":[{\"index\":0,\"delta\":{\"" + field + "\":\"" + text + "\"}}]}";
        var raw = ModelReaderWriter.Read<StreamingChatCompletionUpdate>(BinaryData.FromString(json))!;
        var response = new ChatResponseUpdate(ChatRole.Assistant, "") { RawRepresentation = raw };
        Assert.Equal(expected, TextStreamActivity.Reasoning(response));
        Assert.Empty(response.Text);
    }
}
