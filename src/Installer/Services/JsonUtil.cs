using System.Text.Json;
using System.Text.Json.Serialization;

namespace BigWalkVRInstaller.Services
{
    public static class JsonUtil
    {
        // the manifest and record models are plain public fields, and the manifest carries
        // keys we do not model, so both have to be tolerated
        static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            IncludeFields = true,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            WriteIndented = false,
        };

        public static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

        public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    }
}
