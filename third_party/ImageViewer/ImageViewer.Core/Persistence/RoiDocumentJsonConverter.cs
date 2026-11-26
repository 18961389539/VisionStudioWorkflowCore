using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImageViewer.Core.Persistence
{
    /// <summary>
    /// 会话文档中 ROI 载荷的读写转换器。
    /// Chinese: 写出时始终输出嵌套对象；读取时同时接受嵌套对象（新格式）与转义字符串（历史格式），
    /// 以保证改造前保存的会话文件仍然可以打开。
    /// English: Always writes the ROI payload as a nested object. On read it accepts both the nested object
    /// (current format) and an escaped JSON string (legacy format) so pre-existing session files keep working.
    /// </summary>
    internal sealed class RoiDocumentJsonConverter : JsonConverter<RoiDocument>
    {
        public override RoiDocument? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                return JsonSerializer.Deserialize(ref reader, ImageViewer.Core.Persistence.ImageViewerCoreJsonSerializationContext.Default.RoiDocument);
            }

            string? legacyJson = reader.GetString();
            return string.IsNullOrWhiteSpace(legacyJson)
                ? null
                : JsonSerializer.Deserialize(legacyJson, ImageViewer.Core.Persistence.ImageViewerCoreJsonSerializationContext.Default.RoiDocument);
        }

        public override void Write(Utf8JsonWriter writer, RoiDocument value, JsonSerializerOptions options)
        {
            JsonSerializer.Serialize(writer, value, ImageViewer.Core.Persistence.ImageViewerCoreJsonSerializationContext.Default.RoiDocument);
        }
    }
}
