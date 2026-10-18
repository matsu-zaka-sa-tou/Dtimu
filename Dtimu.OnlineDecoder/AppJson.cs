using System.Text.Json.Serialization;

namespace Dtimu.OnlineDecoder;

// camelCase 输出：播放页 JS 读 s.state / s.progress / s.downloadTotal
// （上轮改为显式传 JsonTypeInfo 后丢失了 Web 默认命名策略，这里补回）
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StatusDto))]
internal partial class AppJson : JsonSerializerContext { }
