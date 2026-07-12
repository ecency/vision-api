using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EcencyApi.Infrastructure;

/// <summary>
/// Port of src/server/helper.ts apiRequest / promoted-entries helpers.
/// PRIVATE_API_AUTH is a base64-encoded JSON object of extra headers; when it
/// can't be decoded the request fails like the Node version does.
/// </summary>
public static class ApiClient
{
    public sealed class ApiAuthException : Exception
    {
        public ApiAuthException() : base("Api auth couldn't be create!") { }
    }

    private static Dictionary<string, string>? MakeApiAuth()
    {
        var encoded = Config.PrivateApiAuth.Trim();
        if (encoded.Length == 0)
        {
            return null;
        }

        try
        {
            var buffer = B64u.DecodeLenient(encoded);
            if (buffer == null || buffer.Length == 0)
            {
                return null;
            }

            var parsed = JsonNode.Parse(Encoding.UTF8.GetString(buffer));
            if (parsed is not JsonObject obj)
            {
                return null;
            }

            var headers = new Dictionary<string, string>();
            foreach (var kv in obj)
            {
                headers[kv.Key] = kv.Value switch
                {
                    JsonValue v when JsVal.TryGetStringLenient(v, out var s) => s,
                    null => "null",
                    _ => kv.Value.ToJsonString(),
                };
            }
            return headers;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// apiRequest(endpoint, method, extraHeaders, payload, params, timeout).
    /// Throws ApiAuthException when PRIVATE_API_AUTH is unusable (Node rejects
    /// the promise, which pipe() turns into a 500).
    /// </summary>
    public static Task<UpstreamResponse> ApiRequest(
        string endpoint,
        HttpMethod method,
        IEnumerable<KeyValuePair<string, string>>? extraHeaders = null,
        JsonNode? payload = null,
        IEnumerable<KeyValuePair<string, string?>>? query = null,
        int timeoutMs = Upstream.DefaultTimeoutMs)
    {
        var apiAuth = MakeApiAuth();
        if (apiAuth == null)
        {
            Console.Error.WriteLine("Api auth couldn't be create!");
            throw new ApiAuthException();
        }

        var url = $"{Config.PrivateApiAddr}/{endpoint}";

        var headers = new List<KeyValuePair<string, string>>();
        foreach (var kv in apiAuth)
        {
            headers.Add(kv);
        }
        if (extraHeaders != null)
        {
            headers.AddRange(extraHeaders);
        }

        return Upstream.BaseApiRequest(url, method, headers, payload, query, timeoutMs);
    }

    /// <summary>fetchPromotedEntries + getPromotedEntries (5-minute cached, shuffled).</summary>
    public static async Task<JsonArray> GetPromotedEntries(int limit, int shortContent)
    {
        var cacheKey = $"promotedentries-{shortContent}-{limit}";
        var promoted = MemCache.Get<JsonArray>(cacheKey);

        if (promoted == null)
        {
            try
            {
                promoted = await FetchPromotedEntries(limit, shortContent);
                if (promoted.Count > 0)
                {
                    MemCache.Set(cacheKey, promoted, 300);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"warn: failed to fetch promoted {e.Message}");
                promoted = new JsonArray();
            }
        }

        Shuffle(promoted);
        return promoted;
    }

    private static async Task<JsonArray> FetchPromotedEntries(int limit, int shortContent)
    {
        var resp = await ApiRequest(
            $"promoted-posts?limit={limit}&short_content={shortContent}", HttpMethod.Get);

        if (resp.Json is not JsonArray list)
        {
            return new JsonArray();
        }

        // random pick 18 (matches list.sort(() => Math.random() - 0.5).filter(i < 18))
        Shuffle(list);

        var result = new JsonArray();
        var taken = 0;
        foreach (var item in list.ToArray())
        {
            if (taken >= 18) break;
            if (item is JsonObject o && o.TryGetPropertyValue("post_data", out var postData) && postData != null)
            {
                o.Remove("post_data");
                result.Add(postData);
                taken++;
            }
            else
            {
                // items without post_data are filtered out but still consumed a slot
                // in the Node version's filter(x, i => i < 18) BEFORE the map — mirror that:
                taken++;
            }
        }

        return result;
    }

    private static void Shuffle(JsonArray arr)
    {
        // Fisher-Yates; Node uses sort(() => Math.random() - 0.5), which is a
        // (biased) shuffle — randomness is the observable behavior, not the bias.
        var items = arr.ToArray();
        foreach (var item in items)
        {
            arr.Remove(item);
        }
        var rng = Random.Shared;
        for (var i = items.Length - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
        foreach (var item in items)
        {
            arr.Add(item);
        }
    }
}
